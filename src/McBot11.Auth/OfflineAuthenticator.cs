using System;
using System.Threading;
using System.Threading.Tasks;

namespace McBot11.Auth
{
    /// <summary>离线模式认证：UUID 按玩家名派生，占位 access token。</summary>
    public sealed class OfflineAuthenticator : IAuthenticator
    {
        public Task<SessionProfile> AuthenticateAsync(AccountConfig account, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account.Username))
                throw new AuthException("offline 账号必须配置 Username");
            var profile = new SessionProfile
            {
                Username = account.Username.Trim(),
                Uuid = UuidUtil.Resolve(account.Uuid, AuthType.Offline, account.Username.Trim()),
                AccessToken = "0",
                AuthKind = "offline"
            };
            return Task.FromResult(profile);
        }
    }
}
