using System;
using System.Security.Cryptography;
using System.Text;

namespace McBot11.Auth
{
    /// <summary>UUID 工具：离线 UUID 派生与解析。</summary>
    public static class UuidUtil
    {
        /// <summary>
        /// 生成离线 UUID（等价 Java UUID.nameUUIDFromBytes("OfflinePlayer:" + name)）：
        /// MD5 → version 3、RFC 4122 variant。
        /// </summary>
        public static Guid OfflineUuid(string name)
        {
            byte[] input = Encoding.UTF8.GetBytes("OfflinePlayer:" + name);
            byte[] hash = MD5.HashData(input);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant 2
            string hex = Convert.ToHexString(hash).ToLowerInvariant();
            return new Guid(hex);
        }

        /// <summary>解析 UUID 字符串（带连字符/无连字符均可），失败时返回 false。</summary>
        public static bool TryParse(string? text, out Guid uuid)
        {
            uuid = Guid.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string compact = text.Replace("-", "").Trim();
            if (compact.Length != 32) return false;
            try
            {
                uuid = new Guid(compact);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>按配置与认证类型确定最终 UUID。</summary>
        public static Guid Resolve(string? configured, AuthType type, string username, Guid? fromServer = null)
        {
            if (fromServer is Guid g && g != Guid.Empty) return g;
            if (TryParse(configured, out Guid parsed)) return parsed;
            if (type == AuthType.Offline) return OfflineUuid(username);
            return fromServer ?? OfflineUuid(username);
        }
    }
}
