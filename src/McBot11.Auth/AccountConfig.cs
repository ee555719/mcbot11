using System;
using System.Threading;
using System.Threading.Tasks;

namespace McBot11.Auth
{
    /// <summary>认证方式。</summary>
    public enum AuthType
    {
        /// <summary>离线模式（offline，UUID 由玩家名派生）。</summary>
        Offline,
        /// <summary>Microsoft 账号（device code + Xbox Live + Minecraft 服务）。</summary>
        Microsoft,
        /// <summary>Mojang 账号（legacy yggdrasil，authserver.mojang.com）。</summary>
        Mojang,
        /// <summary>第三方 Yggdrasil 服务（LittleSkin 等，协议服务器根地址可配）。</summary>
        Yggdrasil
    }

    /// <summary>config.json 中的账号配置条目。</summary>
    public sealed class AccountConfig
    {
        /// <summary>类型：offline / microsoft / mojang / yggdrasil。</summary>
        public string Type { get; set; } = "offline";

        /// <summary>游戏内玩家名（offline 必填；microsoft 可留空取档案名）。</summary>
        public string Username { get; set; } = "";

        /// <summary>可选固定 UUID（36 位带连字符或 32 位十六进制）。offline 默认按玩家名派生。</summary>
        public string? Uuid { get; set; }

        /// <summary>mojang/yggdrasil 登录密码（仅登录时使用，不写入日志）。</summary>
        public string? Password { get; set; }

        /// <summary>yggdrasil 协议服务器根地址（如 https://littleskin.cn/api/yggdrasil）。</summary>
        public string? YggdrasilRoot { get; set; }

        /// <summary>Microsoft OAuth 客户端 ID（留空用内置默认）。</summary>
        public string? MicrosoftClientId { get; set; }

        /// <summary>缓存的 access token（自动刷新，允许持久化；不保存密码）。</summary>
        public string? AccessToken { get; set; }

        /// <summary>缓存的 refresh token。</summary>
        public string? RefreshToken { get; set; }

        /// <summary>access token 过期时间（ISO 8601，UTC）。</summary>
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>
        /// token 缓存键覆盖（账号池按账号 ID 存取；为空时沿用默认 root|username）。
        /// </summary>
        public string? CacheKeyOverride { get; set; }

        /// <summary>
        /// 已选定角色 UUID（32 位无横线；账号池 accounts.json 的 selectedProfileId）。
        /// 认证成功后若 availableProfiles 中存在该角色，则以其作为本次登录使用的角色。
        /// </summary>
        public string? SelectedProfileId { get; set; }

        /// <summary>
        /// 已选定角色名（accounts.json 的 selectedProfileName）。
        /// refresh 请求体的 selectedProfile 需要 id+name 同时给出（需求 3.1），
        /// 角色不在缓存列表时用它兜底构造绑定目标。
        /// </summary>
        public string? SelectedProfileName { get; set; }
    }

    /// <summary>Yggdrasil 角色档案（availableProfiles 数组元素）。</summary>
    public sealed class AuthProfile
    {
        /// <summary>角色 UUID（32 位无横线）。</summary>
        public string Id { get; set; } = "";
        /// <summary>角色名。</summary>
        public string Name { get; set; } = "";

        public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    /// <summary>认证结果：登录会话档案（供 MC 登录与 session join 使用）。</summary>
    public sealed class SessionProfile
    {
        public string Username { get; set; } = "";
        public Guid Uuid { get; set; }
        public string AccessToken { get; set; } = "";
        /// <summary>认证类型字符串（offline / msa / mojang / yggdrasil）。</summary>
        public string AuthKind { get; set; } = "offline";

        /// <summary>
        /// authenticate/refresh 返回的全部可选角色（availableProfiles）；
        /// yggdrasil 多角色选择用，offline/microsoft 视实现而定（可能为空）。
        /// </summary>
        public System.Collections.Generic.List<AuthProfile> AvailableProfiles { get; set; }
            = new System.Collections.Generic.List<AuthProfile>();

        /// <summary>UUID 十六进制（无连字符），用于 session join 的 selectedProfile。</summary>
        public string UuidHex => Uuid.ToString("N");
    }

    /// <summary>认证器接口。</summary>
    public interface IAuthenticator
    {
        Task<SessionProfile> AuthenticateAsync(AccountConfig account, CancellationToken ct = default);
    }

    /// <summary>缓存的登录 token（config\tokens.json 条目；不含密码）。</summary>
    public sealed class CachedToken
    {
        /// <summary>accessToken。</summary>
        public string AccessToken { get; set; } = "";
        /// <summary>refresh token（microsoft OAuth 或 yggdrasil 客户端会话）。</summary>
        public string? RefreshToken { get; set; }
        /// <summary>yggdrasil clientToken。</summary>
        public string? ClientToken { get; set; }
        /// <summary>选中的角色 UUID（十六进制）。</summary>
        public string? ProfileId { get; set; }
        /// <summary>选中的角色名。</summary>
        public string? ProfileName { get; set; }
        /// <summary>
        /// 最近一次 authenticate/refresh 返回的可选角色列表（availableProfiles），
        /// 用于离线展示与多角色选择；超过 1 小时视为过期需重新获取。
        /// </summary>
        public System.Collections.Generic.List<AuthProfile>? AvailableProfiles { get; set; }
        /// <summary>认证服务器根地址。</summary>
        public string? Root { get; set; }
        /// <summary>账号标识（用户名/邮箱）。</summary>
        public string Account { get; set; } = "";
        /// <summary>保存时间（ISO 8601）。</summary>
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>token 缓存接口（实现：文件缓存 config\tokens.json）。</summary>
    public interface ITokenCache
    {
        CachedToken? Load(string key);
        void Save(string key, CachedToken token);
        void Remove(string key);
    }
}
