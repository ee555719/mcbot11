using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McBot11.Auth
{
    /// <summary>Yggdrasil 服务器元数据（GET / 根地址）。</summary>
    public sealed class YggdrasilMetadata
    {
        public string[] SkinDomains { get; set; } = Array.Empty<string>();
        public string SignaturePublicKey { get; set; } = "";
        public string ServerName { get; set; } = "";
    }

    /// <summary>Yggdrasil 元数据获取（GET {root}/）。</summary>
    public static class YggdrasilMetadataFetcher
    {
        public static async Task<YggdrasilMetadata> GetAsync(string root, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new AuthException("认证服务器根地址为空");
            var (status, json) = await AuthHttp.GetJsonAsync(root.TrimEnd('/') + "/", ct).ConfigureAwait(false);
            if (status != 200)
                throw new AuthException($"获取认证服务器元数据失败 (HTTP {status}): {AuthHttp.ExtractError(json.GetRawText())}");
            var meta = new YggdrasilMetadata();
            if (json.TryGetProperty("skinDomains", out var domains) && domains.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var d in domains.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.String)
                        list.Add(d.GetString() ?? "");
                meta.SkinDomains = list.ToArray();
            }
            if (json.TryGetProperty("signaturePublickey", out var pk) && pk.ValueKind == JsonValueKind.String)
                meta.SignaturePublicKey = pk.GetString() ?? "";
            if (json.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object &&
                m.TryGetProperty("serverName", out var sn) && sn.ValueKind == JsonValueKind.String)
                meta.ServerName = sn.GetString() ?? "";
            return meta;
        }
    }

    /// <summary>Yggdrasil token 绑定状态机（需求 3.2）。</summary>
    public enum TokenState
    {
        /// <summary>本地无 accessToken → 走 authenticate（需密码）。</summary>
        NoToken,
        /// <summary>有 token 但本地无法解析 selectedProfile → 不允许直接 refresh，必须 authenticate 拿 availableProfiles 给用户选。</summary>
        TokenUnbound,
        /// <summary>有 token 且本地可解析 selectedProfile → 走带 selectedProfile 的 refresh（保持一致性）。</summary>
        TokenBound
    }

    /// <summary>refresh 结果分类（需求 3.4 错误映射）。</summary>
    public enum RefreshOutcome
    {
        /// <summary>HTTP 200，token 换发成功。</summary>
        Success,
        /// <summary>HTTP 401/403 → token 失效，调用方应重新 authenticate。</summary>
        TokenInvalid,
        /// <summary>HTTP 400 + no profile assigned → 未绑定角色，调用方应触发选择角色流程。</summary>
        UnboundProfile,
        /// <summary>其它失败（如 404/解析异常），调用方回退密码登录。</summary>
        Failed
    }

    /// <summary>
    /// Yggdrasil 协议认证。
    /// 路径策略（authlib-injector 标准，需求指定）：
    ///   yggdrasil → 先 /authserver/authenticate、/sessionserver/session/minecraft/join（404 回退 /authenticate、/session/minecraft/join）；
    ///   mojang    → authserver.mojang.com 直接 /authenticate、sessionserver /session/minecraft/join（回退方向相反）。
    /// 流程：tokens.json 缓存 → validate → refresh → 密码登录；失败分别给出清晰错误。
    /// 多角色：authenticate/refresh 的 availableProfiles 全量缓存到 tokens.json，
    /// AccountConfig.SelectedProfileId 指定登录角色（1 小时缓存过期后 refresh 重新获取）。
    /// </summary>
    public sealed class YggdrasilAuthenticator : IAuthenticator
    {
        private readonly string _root;
        private readonly bool _mojang;
        private readonly ITokenCache? _cache;

        public YggdrasilAuthenticator(string? root, AuthType type, ITokenCache? cache = null)
        {
            _cache = cache;
            if (type == AuthType.Mojang)
            {
                _root = "https://authserver.mojang.com";
                _mojang = true;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(root))
                    throw new AuthException("yggdrasil 账号必须配置 YggdrasilRoot（认证服务器根地址）");
                _root = root.TrimEnd('/');
                _mojang = false;
            }
        }

        /// <summary>认证服务器根地址。</summary>
        public string Root => _root;

        /// <summary>token 缓存键：账号池按 ID（CacheKeyOverride），兼容单账号为 root|username。</summary>
        private string CacheKey(string username) => _mojang ? "mojang" : _root + "|" + username;

        private string CacheKey(AccountConfig account, string username)
            => !string.IsNullOrEmpty(account.CacheKeyOverride)
                ? account.CacheKeyOverride!
                : CacheKey(username);

        public async Task<SessionProfile> AuthenticateAsync(AccountConfig account, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account.Username))
                throw new AuthException("账号名/邮箱不能为空");
            string username = account.Username.Trim();
            string cacheKey = CacheKey(account, username);

            // 1) 缓存 token：validate → 有效即用
            CachedToken? cached = _cache?.Load(cacheKey);
            if (cached != null && !string.IsNullOrEmpty(cached.AccessToken))
            {
                if (await ValidateAsync(cached.AccessToken, cached.ClientToken, ct).ConfigureAwait(false))
                {
                    await ResolveSelectedAsync(account, cacheKey, cached, ct).ConfigureAwait(false);
                    await EnsureBoundAsync(cacheKey, cached, ct).ConfigureAwait(false);
                    return BuildProfile(cached, username);
                }

                // 2) refresh 复用：仅 TokenBound（可解析 selectedProfile）允许 refresh 且必须携带
                //    selectedProfile；TokenUnbound 按需求 3.2 不允许直接 refresh → 走密码 authenticate
                var target = ResolveBindTarget(account, cached);
                if (target != null)
                {
                    var outcome = await TryRefreshAsync(cached, cacheKey, target, ct).ConfigureAwait(false);
                    if (outcome == RefreshOutcome.Success)
                    {
                        await ResolveSelectedAsync(account, cacheKey, cached, ct).ConfigureAwait(false);
                        await EnsureBoundAsync(cacheKey, cached, ct).ConfigureAwait(false);
                        return BuildProfile(cached, username);
                    }
                    // refresh 失败（token 失效 / 未绑定角色 / 其它）→ 丢弃缓存，改走密码 authenticate
                    _cache?.Remove(cacheKey);
                    cached = null;
                }
            }

            // 3) 密码登录（内部解析 availableProfiles；随后按选定角色绑定 token）
            if (string.IsNullOrEmpty(account.Password))
                throw new AuthException("token 缓存无效、已过期或未绑定角色，且未配置密码，无法登录（请重新输入密码）");

            await LoginWithPasswordAsync(account, username, cacheKey, ct).ConfigureAwait(false);
            var reloaded = _cache?.Load(cacheKey);
            if (reloaded != null)
            {
                await ResolveSelectedAsync(account, cacheKey, reloaded, ct).ConfigureAwait(false);
                await EnsureBoundAsync(cacheKey, reloaded, ct).ConfigureAwait(false);
                return BuildProfile(reloaded, username);
            }
            throw new AuthException("认证成功但未写入 token 缓存，请重试");
        }

        /// <summary>
        /// 判断 token 绑定状态机（需求 3.2）：
        /// NoToken = 本地无 accessToken；
        /// TokenBound = 有 token 且本地可解析 selectedProfile（accounts.json 已选 或 tokens.json 已绑定）；
        /// TokenUnbound = 有 token 但无法解析 selectedProfile → 不允许直接 refresh。
        /// </summary>
        public static TokenState GetTokenState(CachedToken? cached, AccountConfig account)
        {
            if (cached == null || string.IsNullOrEmpty(cached.AccessToken)) return TokenState.NoToken;
            return ResolveBindTarget(account, cached) != null ? TokenState.TokenBound : TokenState.TokenUnbound;
        }

        /// <summary>
        /// 解析 refresh 应携带的 selectedProfile（id + name 必须同时给出）：
        /// 优先 accounts.json 已选角色（在 availableProfiles 中取名字，或用 SelectedProfileName 兜底），
        /// 其次 tokens.json 已绑定角色；无法解析出 name 时返回 null（= 未绑定状态，须走 authenticate）。
        /// </summary>
        public static AuthProfile? ResolveBindTarget(AccountConfig account, CachedToken? cached)
        {
            if (cached == null || string.IsNullOrEmpty(cached.AccessToken)) return null;
            string wanted = (account.SelectedProfileId ?? "").Trim();
            if (wanted.Length > 0)
            {
                var picked = FindProfile(cached.AvailableProfiles, wanted);
                if (picked != null) return picked;
                if (SameProfileId(cached.ProfileId, wanted) && !string.IsNullOrEmpty(cached.ProfileName))
                    return new AuthProfile { Id = cached.ProfileId!, Name = cached.ProfileName! };
                if (!string.IsNullOrEmpty(account.SelectedProfileName))
                    return new AuthProfile { Id = wanted.Replace("-", ""), Name = account.SelectedProfileName! };
                return null; // 已选角色不在列表且无名字可用 → 未绑定，交由 authenticate 刷新列表后重新选择
            }
            if (!string.IsNullOrEmpty(cached.ProfileId))
            {
                var inList = FindProfile(cached.AvailableProfiles, cached.ProfileId);
                if (inList != null) return inList;
                if (!string.IsNullOrEmpty(cached.ProfileName))
                    return new AuthProfile { Id = cached.ProfileId!, Name = cached.ProfileName! };
            }
            return null;
        }

        private static bool SameProfileId(string? a, string? b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
               string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// token 已有本地绑定则跳过；未绑定且有可选角色 → 绑定到已选角色（或第一个角色）。
        /// 保证 login_start / session join 使用的 selectedProfile 与服务端一致。
        /// </summary>
        private async Task EnsureBoundAsync(string cacheKey, CachedToken cached, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(cached.ProfileId)) return;
            var list = cached.AvailableProfiles;
            if (list == null || list.Count == 0) return;
            if (string.IsNullOrEmpty(cached.AccessToken)) return;
            await TryRefreshAsync(cached, cacheKey, list[0], ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(cached.ProfileId))
            {
                // 服务器未回 selectedProfile 时按本地绑定处理（下次 refresh 仍会携带角色）
                cached.ProfileId = list[0].Id;
                cached.ProfileName = list[0].Name;
                cached.SavedAt = DateTimeOffset.UtcNow;
                _cache?.Save(cacheKey, cached);
            }
        }

        /// <summary>
        /// 按 account.SelectedProfileId 解析登录角色：
        /// 已选角色与当前 token 绑定不一致 → refresh 携带 selectedProfile 重新绑定；
        /// 列表过期（>1 小时）→ refresh 重新获取 availableProfiles；
        /// 仍不存在（角色被删除）→ 抛出「已选角色已失效」提示重新选择。
        /// </summary>
        private async Task ResolveSelectedAsync(AccountConfig account, string cacheKey, CachedToken cached, CancellationToken ct)
        {
            string wanted = (account.SelectedProfileId ?? "").Trim();
            if (wanted.Length == 0 || cached == null) return;

            bool stale = (DateTimeOffset.UtcNow - cached.SavedAt) > TimeSpan.FromHours(1);
            var picked = FindProfile(cached.AvailableProfiles, wanted);
            bool boundToWanted = picked != null &&
                string.Equals((cached.ProfileId ?? "").Replace("-", ""), picked.Id.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

            if (!boundToWanted || stale || cached.AvailableProfiles == null || cached.AvailableProfiles.Count == 0)
            {
                // refresh：始终携带可解析的 selectedProfile（需求 3.1：带 id+name，避免未绑定 400）；
                // 无法解析绑定目标（TokenUnbound）→ 不允许直接 refresh，交由下方给出重新选择提示
                AuthProfile? bind = picked ?? ResolveBindTarget(account, cached);
                if (bind != null)
                    await TryRefreshAsync(cached, cacheKey, bind, ct).ConfigureAwait(false);
                picked = FindProfile(cached.AvailableProfiles, wanted);
            }

            if (picked == null)
            {
                if (cached.AvailableProfiles == null || cached.AvailableProfiles.Count == 0)
                    throw new AuthException("该账号没有可用角色，请先到皮肤站创建角色");
                throw new AuthException("已选角色已失效（不在最新 availableProfiles 中），请重新选择角色");
            }

            if (!string.Equals((cached.ProfileId ?? "").Replace("-", ""), picked.Id.Replace("-", ""), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(cached.ProfileName, picked.Name, StringComparison.Ordinal))
            {
                cached.ProfileId = picked.Id;
                cached.ProfileName = picked.Name;
                cached.SavedAt = DateTimeOffset.UtcNow;
                _cache?.Save(cacheKey, cached);
            }
        }

        private static AuthProfile? FindProfile(List<AuthProfile>? list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return null;
            string want = id.Replace("-", "");
            foreach (var p in list)
                if (string.Equals((p.Id ?? "").Replace("-", ""), want, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        /// <summary>
        /// 获取角色列表（需求 3.3 完整状态机）：
        /// NoToken        → authenticate（需密码）；
        /// TokenUnbound   → 不允许直接 refresh → authenticate 拿 availableProfiles 交给用户选择，
        ///                  选定后由 BindProfileAsync 立刻带 selectedProfile refresh 完成绑定；
        /// TokenBound     → 缓存 1 小时内先 validate，通过则直接用缓存；否则带 selectedProfile refresh；
        /// 返回空列表 = 该账号没有可用角色。
        /// </summary>
        public async Task<List<AuthProfile>> FetchProfilesAsync(
            AccountConfig account, bool forceRefresh = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account.Username))
                throw new AuthException("账号名/邮箱不能为空");
            string username = account.Username.Trim();
            string cacheKey = CacheKey(account, username);

            var cached = _cache?.Load(cacheKey);
            TokenState state = GetTokenState(cached, account);

            // ---- NoToken / TokenUnbound：走 authenticate（需密码），不直接 refresh ----
            if (state != TokenState.TokenBound)
            {
                if (string.IsNullOrEmpty(account.Password))
                {
                    throw new AuthException(state == TokenState.NoToken
                        ? "获取角色列表失败：未配置密码（请输入密码以获取角色列表）"
                        : "该账号未绑定角色，且未配置密码（请输入密码以获取角色列表）");
                }
                var profile = await LoginWithPasswordAsync(account, username, cacheKey, ct).ConfigureAwait(false);
                return profile.AvailableProfiles ?? new List<AuthProfile>();
            }

            // ---- TokenBound ----
            bool fresh = (DateTimeOffset.UtcNow - cached!.SavedAt) <= TimeSpan.FromHours(1);
            bool hasList = cached.AvailableProfiles != null && cached.AvailableProfiles.Count > 0;

            // 3.3.1 本地已有 selectedProfile 且 validate 通过 → 直接展示缓存的 availableProfiles
            if (!forceRefresh && fresh && hasList &&
                await ValidateAsync(cached.AccessToken, cached.ClientToken, ct).ConfigureAwait(false))
                return new List<AuthProfile>(cached.AvailableProfiles!);

            // 3.3.x 带 selectedProfile 的 refresh（需求 3.1/3.2 TokenBound）
            AuthProfile target = ResolveBindTarget(account, cached)!;
            RefreshOutcome outcome = await TryRefreshAsync(cached, cacheKey, target, ct).ConfigureAwait(false);
            if (outcome == RefreshOutcome.Success)
            {
                if (cached.AvailableProfiles != null && cached.AvailableProfiles.Count > 0)
                    return new List<AuthProfile>(cached.AvailableProfiles);
                if (fresh && hasList)
                    return new List<AuthProfile>(cached.AvailableProfiles!); // refresh 未回角色列表时保留旧列表
                return new List<AuthProfile>(); // refresh 成功但服务器没有可用角色
            }

            // 400 + no profile assigned（需求 3.4）→ 中文提示 + 触发选择角色流程（重新 authenticate）
            if (outcome == RefreshOutcome.UnboundProfile && !string.IsNullOrEmpty(account.Password))
            {
                var profile = await LoginWithPasswordAsync(account, username, cacheKey, ct).ConfigureAwait(false);
                return profile.AvailableProfiles ?? new List<AuthProfile>();
            }

            // 401/403 token 失效（需求 3.4）→ 重新 authenticate
            if (outcome == RefreshOutcome.TokenInvalid && !string.IsNullOrEmpty(account.Password))
            {
                _cache?.Remove(cacheKey);
                var profile = await LoginWithPasswordAsync(account, username, cacheKey, ct).ConfigureAwait(false);
                return profile.AvailableProfiles ?? new List<AuthProfile>();
            }

            // 其它失败（Failed）→ 有密码则重新 authenticate；无密码回退缓存或提示输入密码
            if (!string.IsNullOrEmpty(account.Password))
            {
                var profile = await LoginWithPasswordAsync(account, username, cacheKey, ct).ConfigureAwait(false);
                return profile.AvailableProfiles ?? new List<AuthProfile>();
            }
            if (fresh && hasList)
                return new List<AuthProfile>(cached.AvailableProfiles!);
            if (outcome == RefreshOutcome.UnboundProfile)
                throw new AuthException("该账号未绑定角色，请先选择角色后再登录（The access token has no profile assigned, please select a profile）；未配置密码（请输入密码以获取角色列表）");
            if (outcome == RefreshOutcome.TokenInvalid)
                throw new AuthException("登录凭据已失效，且未配置密码（请输入密码以重新登录）");
            throw new AuthException("token 缓存无效或已过期，且未配置密码（请输入密码以获取角色列表）");
        }

        /// <summary>
        /// 用户选定角色后立刻绑定（需求 3.3.3）：调一次带 selectedProfile 的 refresh，
        /// 让服务器完成 token ↔ 角色绑定，并把 selectedProfile 写入 tokens.json。
        /// 未登录（无 token）时抛错提示先走 authenticate。
        /// </summary>
        public async Task BindProfileAsync(AccountConfig account, AuthProfile profile, CancellationToken ct = default)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Id))
                throw new AuthException("绑定角色失败：角色不能为空");
            if (string.IsNullOrWhiteSpace(account.Username))
                throw new AuthException("账号名/邮箱不能为空");
            string cacheKey = CacheKey(account, account.Username.Trim());

            var cached = _cache?.Load(cacheKey);
            if (cached == null || string.IsNullOrEmpty(cached.AccessToken))
                throw new AuthException("该账号尚未登录（无有效 token），请先输入密码获取角色列表");

            RefreshOutcome outcome = await TryRefreshAsync(cached, cacheKey, profile, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case RefreshOutcome.Success:
                    return;
                case RefreshOutcome.TokenInvalid:
                    _cache?.Remove(cacheKey);
                    throw new AuthException("登录凭据已失效，绑定角色失败（请重新输入密码后再选择角色）");
                case RefreshOutcome.UnboundProfile:
                    throw new AuthException("该账号未绑定角色，绑定失败，请先选择角色后再登录（The access token has no profile assigned）");
                default:
                    throw new AuthException($"绑定角色失败（{profile.Name}），请稍后重试");
            }
        }

        /// <summary>
        /// 严格密码校验（添加/编辑账号的"验证并保存"用）：跳过 token 缓存，
        /// 直接调用 authenticate 校验邮箱与密码；成功后 token 与 availableProfiles 写入缓存。
        /// </summary>
        public async Task<SessionProfile> AuthenticateWithPasswordAsync(AccountConfig account, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account.Username))
                throw new AuthException("账号名/邮箱不能为空");
            if (string.IsNullOrEmpty(account.Password))
                throw new AuthException("未输入密码，无法验证");
            string username = account.Username.Trim();
            return await LoginWithPasswordAsync(account, username, CacheKey(account, username), ct).ConfigureAwait(false);
        }

        private SessionProfile BuildProfile(CachedToken token, string fallbackName)
        {
            if (!UuidUtil.TryParse(token.ProfileId, out Guid uuid))
                throw new AuthException("token 缓存缺少有效 selectedProfile，需重新登录");
            return new SessionProfile
            {
                Username = string.IsNullOrEmpty(token.ProfileName) ? fallbackName : token.ProfileName!,
                Uuid = uuid,
                AccessToken = token.AccessToken,
                AuthKind = _mojang ? "mojang" : "yggdrasil",
                AvailableProfiles = token.AvailableProfiles ?? new List<AuthProfile>()
            };
        }

        private async Task<SessionProfile> LoginWithPasswordAsync(AccountConfig account, string username, string cacheKey, CancellationToken ct)
        {
            string clientToken = Guid.NewGuid().ToString("N");
            var (status, json) = await AuthHttp.PostJsonWithFallbackAsync(
                AuthPath("/authserver/authenticate", "/authenticate"),
                AuthPathFallback("/authserver/authenticate", "/authenticate"), new
            {
                agent = new { name = "Minecraft", version = 1 },
                username,
                password = account.Password,
                clientToken,
                requestUser = false
            }, ct).ConfigureAwait(false);

            if (status != 200 && status != 204)
            {
                string detail = AuthHttp.ExtractError(json.GetRawText());
                string hint = status switch
                {
                    403 => "账号或密码错误，或该认证服务器拒绝了登录",
                    404 => "认证服务器接口不存在，请检查根地址",
                    _ => "认证服务器不可达或内部错误"
                };
                throw new AuthException($"Yggdrasil 登录失败 (HTTP {status}): {hint} - {detail}");
            }

            if (!json.TryGetProperty("accessToken", out var at))
                throw new AuthException("登录响应缺少 accessToken");
            string accessToken = at.GetString() ?? "";
            if (accessToken.Length == 0)
                throw new AuthException("登录响应 accessToken 为空");

            // availableProfiles：全部可选角色（多角色选择的数据源）
            List<AuthProfile> available = ParseAvailableProfiles(json);
            if (available.Count == 0)
                throw new AuthException("该账号没有可用角色，请先到皮肤站创建角色");

            // token 是否绑定以服务端 selectedProfile 为准（可能为空 = 未绑定，
            // 由 BindProfileAsync 在用户选定角色后通过带 selectedProfile 的 refresh 绑定；
            // 本地绝不伪造绑定 —— tokens.json 的 selectedProfile 只写服务端返回值）
            string? profileId = null, profileName = null;
            if (json.TryGetProperty("selectedProfile", out var sp) && sp.ValueKind == JsonValueKind.Object)
            {
                if (sp.TryGetProperty("id", out var idEl)) profileId = idEl.GetString();
                if (sp.TryGetProperty("name", out var nameEl)) profileName = nameEl.GetString();
            }

            // 展示用登录档案：优先已选定角色（仍在最新列表中时），其次服务端绑定，最后首角色；
            // 已选定角色失效时不抛错 —— 交由 UI 发现「已选角色不在列表」后重新弹窗选择
            string wanted = (account.SelectedProfileId ?? "").Trim();
            string? loginId = profileId, loginName = profileName;
            if (wanted.Length > 0)
            {
                var picked = FindProfile(available, wanted);
                if (picked != null)
                {
                    loginId = picked.Id;
                    loginName = picked.Name;
                }
            }
            if (loginId == null)
            {
                loginId = available[0].Id;
                loginName = available[0].Name;
            }

            _cache?.Save(cacheKey, new CachedToken
            {
                AccessToken = accessToken,
                ClientToken = clientToken,
                ProfileId = profileId,
                ProfileName = profileName,
                AvailableProfiles = available,
                Root = _root,
                Account = username
            });

            if (!UuidUtil.TryParse(loginId, out Guid uuid))
                throw new AuthException("selectedProfile UUID 解析失败: " + loginId);
            return new SessionProfile
            {
                Username = loginName ?? username,
                Uuid = uuid,
                AccessToken = accessToken,
                AuthKind = _mojang ? "mojang" : "yggdrasil",
                AvailableProfiles = available
            };
        }

        /// <summary>解析 authenticate/refresh 响应中的 availableProfiles 数组。</summary>
        private static List<AuthProfile> ParseAvailableProfiles(JsonElement json)
        {
            var list = new List<AuthProfile>();
            if (json.TryGetProperty("availableProfiles", out var ap) && ap.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ap.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string id = "", name = "";
                    if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        id = idEl.GetString() ?? "";
                    if (item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        name = nameEl.GetString() ?? "";
                    if (id.Length == 0) continue;
                    if (FindProfile(list, id) != null) continue;
                    list.Add(new AuthProfile { Id = id, Name = name });
                }
            }
            return list;
        }

        private async Task<bool> ValidateAsync(string accessToken, string? clientToken, CancellationToken ct)
        {
            try
            {
                var (status, _) = await AuthHttp.PostRawWithFallbackAsync(
                    AuthPath("/authserver/validate", "/validate"),
                    AuthPathFallback("/authserver/validate", "/validate"), new
                    {
                        accessToken,
                        clientToken
                    }, ct).ConfigureAwait(false);
                return status == 204 || status == 200;
            }
            catch (AuthException)
            {
                return false;
            }
        }

        /// <summary>
        /// POST /authserver/refresh（需求 3.1/3.4）。
        /// 请求体始终携带 selectedProfile（bindTo 为空时回退 token 本地绑定；两者皆无则省略字段），
        /// 并带 requestUser=true；返回 RefreshOutcome 分类：
        ///   200 → Success；401/403 → TokenInvalid；400 + no profile assigned → UnboundProfile；
        ///   5xx → 抛出「认证服务器故障，请稍后重试」；网络异常 → 抛出 AuthException；其它 → Failed。
        /// </summary>
        private async Task<RefreshOutcome> TryRefreshAsync(CachedToken token, string cacheKey, AuthProfile? bindTo, CancellationToken ct)
        {
            AuthProfile? target = bindTo;
            if (target == null && !string.IsNullOrEmpty(token.ProfileId) && !string.IsNullOrEmpty(token.ProfileName))
                target = new AuthProfile { Id = token.ProfileId!, Name = token.ProfileName! };

            object payload = target == null
                ? new { accessToken = token.AccessToken, clientToken = token.ClientToken, requestUser = true }
                : new
                {
                    accessToken = token.AccessToken,
                    clientToken = token.ClientToken,
                    requestUser = true,
                    selectedProfile = new { id = target.Id, name = target.Name }
                };
            var (status, json) = await AuthHttp.PostJsonWithFallbackAsync(
                AuthPath("/authserver/refresh", "/refresh"),
                AuthPathFallback("/authserver/refresh", "/refresh"), payload, ct).ConfigureAwait(false);

            if (status >= 500)
                throw new AuthException($"认证服务器故障，请稍后重试 (HTTP {status})");

            if (status != 200)
            {
                string err = AuthHttp.ExtractError(json.GetRawText());
                if (status == 401 || status == 403)
                    return RefreshOutcome.TokenInvalid;
                if (status == 400 && IsUnboundProfileError(err))
                    return RefreshOutcome.UnboundProfile;
                return RefreshOutcome.Failed;
            }

            if (json.TryGetProperty("accessToken", out var at))
                token.AccessToken = at.GetString() ?? token.AccessToken;
            if (json.TryGetProperty("clientToken", out var ctEl))
                token.ClientToken = ctEl.GetString() ?? token.ClientToken;
            if (json.TryGetProperty("selectedProfile", out var sp) && sp.ValueKind == JsonValueKind.Object)
            {
                if (sp.TryGetProperty("id", out var idEl)) token.ProfileId = idEl.GetString();
                if (sp.TryGetProperty("name", out var nmEl)) token.ProfileName = nmEl.GetString();
            }
            else if (target != null)
            {
                // 服务器未回传 selectedProfile 时按请求绑定的角色处理
                token.ProfileId = target.Id;
                token.ProfileName = target.Name;
            }
            var refreshed = ParseAvailableProfiles(json);
            if (refreshed.Count > 0)
                token.AvailableProfiles = refreshed;
            token.SavedAt = DateTimeOffset.UtcNow;
            _cache?.Save(cacheKey, token);
            return RefreshOutcome.Success;
        }

        /// <summary>识别「令牌未绑定角色」类 400（LittleSkin 中英文对照原文）。</summary>
        private static bool IsUnboundProfileError(string err)
            => err.Contains("未绑定角色", StringComparison.Ordinal) ||
               err.Contains("no profile assigned", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("has no profile", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("not bound to", StringComparison.OrdinalIgnoreCase);

        /// <summary>主路径：yggdrasil 用 authlib-injector 前缀，Mojang 无前缀。</summary>
        private string AuthPath(string authlibPath, string standardPath)
            => _root + (_mojang ? standardPath : authlibPath);

        /// <summary>回退路径（主路径 404/405 时尝试）。</summary>
        private string AuthPathFallback(string authlibPath, string standardPath)
            => _root + (_mojang ? authlibPath : standardPath);

        /// <summary>session join（按路径策略回退）。</summary>
        public Task JoinAsync(SessionProfile profile, string serverId, CancellationToken ct = default)
            => SessionJoiner.JoinAsync(_root, profile, serverId, _mojang, ct);
    }

    /// <summary>认证器工厂。</summary>
    public static class AuthenticatorFactory
    {
        public static IAuthenticator Create(AccountConfig account, ITokenCache? cache = null)
        {
            switch ((account.Type ?? "").Trim().ToLowerInvariant())
            {
                case "offline":
                    return new OfflineAuthenticator();
                case "microsoft":
                case "msa":
                    return new MicrosoftAuthenticator(account.MicrosoftClientId, cache);
                case "mojang":
                    return new YggdrasilAuthenticator(null, AuthType.Mojang, cache);
                case "yggdrasil":
                    return new YggdrasilAuthenticator(account.YggdrasilRoot, AuthType.Yggdrasil, cache);
                default:
                    throw new AuthException("未知账号类型: " + account.Type);
            }
        }

        /// <summary>该账号类型对应的 session join 根地址（offline 返回 null 表示无需 join）。</summary>
        public static string? GetJoinRoot(AccountConfig account)
        {
            switch ((account.Type ?? "").Trim().ToLowerInvariant())
            {
                case "offline":
                    return null;
                case "yggdrasil":
                    return account.YggdrasilRoot?.TrimEnd('/');
                case "mojang":
                    return "https://sessionserver.mojang.com";
                default:
                    return "https://sessionserver.mojang.com";
            }
        }
    }
}
