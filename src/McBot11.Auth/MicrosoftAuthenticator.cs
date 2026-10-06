using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McBot11.Auth
{
    /// <summary>
    /// Microsoft 账号认证（纯 HttpClient 自研，不依赖 MSAL）。
    /// 流程：Azure OAuth2 device code → Xbox Live user auth → XSTS →
    /// Minecraft login_with_xbox → entitlements 校验 → profile 取档案。
    /// 支持 refresh_token 续期；不保存密码，仅缓存 token。
    /// </summary>
    public sealed class MicrosoftAuthenticator : IAuthenticator
    {
        /// <summary>内置默认客户端 ID（Minecraft 启动器公开 ID，支持 device code）。</summary>
        public const string DefaultClientId = "00000000402b5328";

        private const string Scope = "XboxLive.signin offline_access";
        private const string AzureDeviceCode = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string AzureToken = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string XblAuth = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsAuth = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string McLogin = "https://api.minecraftservices.com/authentication/login_with_xbox";
        private const string McEntitlements = "https://api.minecraftservices.com/entitlements/mcstore/";
        private const string McProfile = "https://api.minecraftservices.com/minecraft/profile";

        /// <summary>device code 显示提示（UI 呈验码/链接）。参数：(message 或结构化字段)。</summary>
        public event Action<string>? DeviceCodePrompt;

        private readonly string _clientId;
        private readonly ITokenCache? _cache;
        private readonly string _cacheKey;

        public MicrosoftAuthenticator(string? clientId = null, ITokenCache? cache = null)
        {
            _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId;
            _cache = cache;
            _cacheKey = "microsoft|" + _clientId;
        }

        public async Task<SessionProfile> AuthenticateAsync(AccountConfig account, CancellationToken ct = default)
        {
            // 1) refresh token 来源：配置优先，其次 tokens.json 缓存
            string? refreshToken = account.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
                refreshToken = _cache?.Load(_cacheKey)?.RefreshToken;

            // 2) 尝试静默续期
            if (!string.IsNullOrEmpty(refreshToken))
            {
                try
                {
                    var refreshed = await RefreshAsync(refreshToken, ct).ConfigureAwait(false);
                    account.AccessToken = refreshed.Access;
                    if (!string.IsNullOrEmpty(refreshed.Refresh))
                    {
                        account.RefreshToken = refreshed.Refresh;
                        SaveTokenCache(account, refreshed.Refresh);
                    }
                    account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
                    return await CompleteAsync(account, ct).ConfigureAwait(false);
                }
                catch (AuthException ex)
                {
                    // refresh token 已失效，回落到 device code 流程
                    System.Diagnostics.Debug.WriteLine("refresh failed: " + ex.Message);
                    _cache?.Remove(_cacheKey);
                }
            }

            // 2) device code 交互流程
            var (dcStatus, dc) = await AuthHttp.PostJsonAsync(AzureDeviceCode, new
            {
                client_id = _clientId,
                scope = Scope
            }, ct).ConfigureAwait(false);
            if (dcStatus != 200)
                throw new AuthException($"device code 请求失败 (HTTP {dcStatus}): {AuthHttp.ExtractError(dc.GetRawText())}");

            string deviceCode = dc.GetProperty("device_code").GetString() ?? "";
            int interval = dc.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out int i) ? i : 5;
            int expiresIn = dc.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int e) ? e : 900;
            string message = dc.TryGetProperty("message", out var mg) && mg.ValueKind == JsonValueKind.String
                ? mg.GetString()!
                : "打开 " + (dc.TryGetProperty("verification_uri", out var vu) ? vu.GetString() : "https://microsoft.com/link")
                  + " 输入代码 " + (dc.TryGetProperty("user_code", out var uc) ? uc.GetString() : "");
            DeviceCodePrompt?.Invoke(message);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                var (st, tok) = await AuthHttp.PostJsonAsync(AzureToken, new
                {
                    client_id = _clientId,
                    grant_type = "urn:ietf:params:oauth:grant-type:device_code",
                    device_code = deviceCode
                }, ct).ConfigureAwait(false);

                if (st == 200)
                {
                    string access = tok.GetProperty("access_token").GetString() ?? "";
                    string? refresh = tok.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                    int expires = tok.TryGetProperty("expires_in", out var ex2) && ex2.TryGetInt32(out int x2) ? x2 : 3600;
                    if (access.Length == 0) throw new AuthException("Microsoft token 响应缺少 access_token");
                    account.AccessToken = access;
                    account.RefreshToken = refresh;
                    account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires);
                    if (!string.IsNullOrEmpty(refresh)) SaveTokenCache(account, refresh);
                    return await CompleteAsync(account, ct).ConfigureAwait(false);
                }

                string err = "";
                if (tok.ValueKind == JsonValueKind.Object && tok.TryGetProperty("error", out var eEl))
                    err = eEl.GetString() ?? "";
                switch (err)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "expired_token":
                        throw new AuthException("device code 已过期，请重试");
                    case "authorization_declined":
                        throw new AuthException("用户在浏览器中拒绝了授权");
                    default:
                        throw new AuthException($"Microsoft token 轮询失败 (HTTP {st}): {AuthHttp.ExtractError(tok.GetRawText())}");
                }
            }
            throw new AuthException("device code 流程超时");
        }

        private sealed record RefreshResult(string Access, string? Refresh, int ExpiresIn);

        /// <summary>把 refresh token 写入 tokens.json（不写密码）。</summary>
        private void SaveTokenCache(AccountConfig account, string refreshToken)
        {
            _cache?.Save(_cacheKey, new CachedToken
            {
                RefreshToken = refreshToken,
                AccessToken = account.AccessToken ?? "",
                Account = account.Username ?? "",
                Root = _clientId
            });
        }

        private async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            var (st, tok) = await AuthHttp.PostJsonAsync(AzureToken, new
            {
                client_id = _clientId,
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                scope = Scope
            }, ct).ConfigureAwait(false);
            if (st != 200)
                throw new AuthException($"refresh 失败 (HTTP {st}): {AuthHttp.ExtractError(tok.GetRawText())}");
            string access = tok.GetProperty("access_token").GetString() ?? "";
            string? refresh = tok.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            int expires = tok.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out int x) ? x : 3600;
            return new RefreshResult(access, refresh, expires);
        }

        /// <summary>已完成 Azure 授权后：Xbox → Minecraft → 档案。</summary>
        private async Task<SessionProfile> CompleteAsync(AccountConfig account, CancellationToken ct)
        {
            string azureToken = account.AccessToken!;

            // 3) Xbox Live user auth（RPS ticket: d=<azure access token>）
            var (xblStatus, xbl) = await AuthHttp.PostJsonAsync(XblAuth, new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + azureToken
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            }, ct).ConfigureAwait(false);
            if (xblStatus != 200)
                throw new AuthException($"Xbox Live 认证失败 (HTTP {xblStatus}): {AuthHttp.ExtractError(xbl.GetRawText())}");
            string xblToken = xbl.GetProperty("Token").GetString() ?? "";
            string uhs = xbl.GetProperty("DisplayClaims").GetProperty("uhs")[0].GetString() ?? "";

            // 4) XSTS 授权
            var (xstsStatus, xsts) = await AuthHttp.PostJsonAsync(XstsAuth, new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            }, ct).ConfigureAwait(false);
            if (xstsStatus != 200)
                throw new AuthException($"XSTS 授权失败 (HTTP {xstsStatus}): {AuthHttp.ExtractError(xsts.GetRawText())}（若 XErr=2148916233 通常表示 Xbox 账号未完成登录/未成年限制）");
            string xstsToken = xsts.GetProperty("Token").GetString() ?? "";

            // 5) Minecraft 服务登录
            var (mcStatus, mc) = await AuthHttp.PostJsonAsync(McLogin, new
            {
                identityToken = "XBL3.0 x=" + uhs + ";" + xstsToken
            }, ct).ConfigureAwait(false);
            if (mcStatus != 200)
                throw new AuthException($"Minecraft 服务认证失败 (HTTP {mcStatus}): {AuthHttp.ExtractError(mc.GetRawText())}");
            string mcToken = mc.GetProperty("access_token").GetString() ?? "";
            if (mcToken.Length == 0) throw new AuthException("Minecraft access_token 为空");

            // 6) 权益校验（可选提示）
            try
            {
                var (entStatus, ent) = await AuthHttp.GetJsonAsync(McEntitlements, ct).ConfigureAwait(false);
                if (entStatus == 200 && ent.TryGetProperty("items", out var items) &&
                    items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0)
                    throw new AuthException("该 Microsoft 账号没有 Minecraft 权益（未购买或未领取）");
            }
            catch (AuthException) { throw; }
            catch { /* 权益接口失败不阻断，档案接口会兜底 */ }

            // 7) 游戏档案
            var (pStatus, prof) = await AuthHttp.GetJsonAsync(McProfile, ct).ConfigureAwait(false);
            if (pStatus == 404)
                throw new AuthException("该账号尚未创建 Minecraft 游戏档案（或权益未生效）");
            if (pStatus != 200)
                throw new AuthException($"获取游戏档案失败 (HTTP {pStatus}): {AuthHttp.ExtractError(prof.GetRawText())}");

            string idHex = prof.GetProperty("id").GetString() ?? "";
            string name = prof.GetProperty("name").GetString() ?? "";
            if (!UuidUtil.TryParse(idHex, out Guid uuid))
                throw new AuthException("游戏档案 UUID 解析失败: " + idHex);
            if (!string.IsNullOrWhiteSpace(account.Username) &&
                !string.Equals(account.Username, name, StringComparison.OrdinalIgnoreCase))
            {
                // 配置名与档案名不一致时以档案名为准（正版档案名不可改）
            }

            // 注：account.AccessToken 保留 Azure access token（供 refresh 判定），
            // Minecraft access token 仅放入返回的 SessionProfile（每次连接重新获取，避免缓存过期 token）。
            account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(3600);

            return new SessionProfile
            {
                Username = name,
                Uuid = uuid,
                AccessToken = mcToken,
                AuthKind = "msa"
            };
        }
    }
}
