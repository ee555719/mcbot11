using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace McBot11.Auth
{
    /// <summary>账号池条目（config\accounts.json；第三方 Yggdrasil 账号）。</summary>
    public sealed class AccountEntry
    {
        /// <summary>本地唯一 ID（GUID）。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>认证类型，账号池当前固定为 yggdrasil。</summary>
        public string AuthType { get; set; } = "yggdrasil";

        /// <summary>认证服务器根地址（如 https://littleskin.cn/api/yggdrasil）。</summary>
        public string ServerRoot { get; set; } = "";

        /// <summary>登录邮箱 / 用户名。</summary>
        public string Email { get; set; } = "";

        /// <summary>展示名称（默认取邮箱，可自定义）。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// 密码：DPAPI(CurrentUser) 加密后的 Base64；选择「不保存密码」时为空字符串。
        /// 永不落盘明文、永不写入日志。
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>上次登录成功时间（UTC ISO 8601）。</summary>
        public DateTimeOffset? LastLoginTime { get; set; }

        /// <summary>上次登录成功时的角色名。</summary>
        public string? LastProfileName { get; set; }

        /// <summary>上次登录成功时的角色 UUID（32 位十六进制）。</summary>
        public string? LastProfileUuid { get; set; }

        /// <summary>当前选定登录角色的 UUID（32 位无横线；多角色选择，持久化于 accounts.json）。</summary>
        public string? SelectedProfileId { get; set; }

        /// <summary>当前选定登录角色名（用于 UI 展示）。</summary>
        public string? SelectedProfileName { get; set; }

        /// <summary>下拉框显示文本（不序列化）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Label => string.IsNullOrWhiteSpace(DisplayName)
            ? (string.IsNullOrWhiteSpace(Email) ? Id : Email)
            : DisplayName + (string.IsNullOrWhiteSpace(Email) ? "" : "（" + Email + "）");

        /// <summary>读取解密后的密码（仅内存使用；解密失败返回 null）。</summary>
        public string? GetPassword() => SecureStore.Unprotect(Password);

        /// <summary>写入加密后的密码（空 = 不保存）。</summary>
        public void SetPassword(string? plain) => Password = SecureStore.Protect(plain);
    }

    /// <summary>账号池根对象（config\accounts.json）。</summary>
    public sealed class AccountPool
    {
        public List<AccountEntry> Accounts { get; set; } = new List<AccountEntry>();

        /// <summary>当前用于 MC 假人登录的账号 ID。</summary>
        public string? CurrentAccountId { get; set; }
    }

    /// <summary>
    /// 敏感字段加解密：Windows DPAPI（ProtectedData，CurrentUser 作用域 + 应用熵）。
    /// 账号密码与 tokens.json 的 accessToken/clientToken/refreshToken 均经此加密后落盘。
    /// </summary>
    public static class SecureStore
    {
        private static readonly byte[] AccountEntropy = Encoding.UTF8.GetBytes("McBot11.accounts.v1");
        private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("McBot11.tokens.v1");

        /// <summary>加密为 Base64；空值返回空串。</summary>
        public static string Protect(string? plain)
            => Encrypt(plain, AccountEntropy);

        /// <summary>解密；失败（非本机/非当前用户/数据损坏）返回 null。</summary>
        public static string? Unprotect(string? protectedBase64)
            => Decrypt(protectedBase64, AccountEntropy);

        /// <summary>加密 token（独立熵，与密码分开）。</summary>
        public static string ProtectToken(string? plain)
            => Encrypt(plain, TokenEntropy);

        /// <summary>解密 token；失败返回 null。</summary>
        public static string? UnprotectToken(string? protectedBase64)
            => Decrypt(protectedBase64, TokenEntropy);

        private static string Encrypt(string? plain, byte[] entropy)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
        }

        private static string? Decrypt(string? value, byte[] entropy)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                byte[] data = Convert.FromBase64String(value);
                byte[] plain = ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }
    }
}
