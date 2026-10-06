using System.Threading;
using System.Threading.Tasks;

namespace McBot11.Auth
{
    /// <summary>
    /// session join：向 sessionserver 登记 "serverId → 玩家" 映射，供服务器加密阶段验证。
    /// 路径策略：authlib-injector 标准 /sessionserver/session/minecraft/join 优先
    /// （Mojang 反向），404/405 时回退 /session/minecraft/join。离线模式无需 join。
    /// </summary>
    public static class SessionJoiner
    {
        public static async Task JoinAsync(string joinRoot, SessionProfile profile, string serverId, bool mojangPreferred = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(joinRoot))
                throw new AuthException("session join 根地址为空");
            string root = joinRoot.TrimEnd('/');
            string primary = mojangPreferred ? root + "/session/minecraft/join" : root + "/sessionserver/session/minecraft/join";
            string fallback = mojangPreferred ? root + "/sessionserver/session/minecraft/join" : root + "/session/minecraft/join";
            var payload = new
            {
                accessToken = profile.AccessToken,
                selectedProfile = profile.UuidHex,
                serverId
            };
            var (status, body) = await AuthHttp.PostRawWithFallbackAsync(primary, fallback, payload, ct).ConfigureAwait(false);
            if (status != 204 && status != 200)
                throw new AuthException($"session join 失败 (HTTP {status}): {AuthHttp.ExtractError(body)}");
        }
    }
}
