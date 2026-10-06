using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace McBot11.Protocol.Crypto
{
    /// <summary>登录会话加密相关静态工具。</summary>
    public static class LoginCrypto
    {
        /// <summary>
        /// 计算 session 服务器哈希（"serverId" 哈希）：
        /// SHA1(serverId + 共享密钥 + 服务器公钥)，按 Java BigInteger 十六进制格式输出
        /// （无前导零、负数带 "-" 前缀）。用于 Mojang session 服务器 join。
        /// </summary>
        public static string ComputeServerIdHash(string serverId, byte[] sharedSecret, byte[] serverPublicKey)
        {
            using var sha1 = SHA1.Create();
            var ms = new System.IO.MemoryStream();
            byte[] idBytes = Encoding.UTF8.GetBytes(serverId);
            ms.Write(idBytes, 0, idBytes.Length);
            ms.Write(sharedSecret, 0, sharedSecret.Length);
            ms.Write(serverPublicKey, 0, serverPublicKey.Length);
            byte[] digest = sha1.ComputeHash(ms.ToArray());
            var value = new BigInteger(digest, isUnsigned: false, isBigEndian: true);
            string magnitude = BigInteger.Abs(value).ToString("x", CultureInfo.InvariantCulture);
            magnitude = magnitude.TrimStart('0');
            if (magnitude.Length == 0) magnitude = "0";
            return (value.Sign < 0 ? "-" : "") + magnitude;
        }

        /// <summary>用服务器公钥（SPKI DER）加密共享密钥或校验令牌（RSA/ECB/PKCS1Padding）。</summary>
        public static byte[] RsaEncrypt(byte[] data, byte[] spkiPublicKey)
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spkiPublicKey, out _);
            return rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
        }

        /// <summary>生成 16 字节 AES 共享密钥。</summary>
        public static byte[] GenerateSharedSecret()
        {
            var secret = new byte[16];
            RandomNumberGenerator.Fill(secret);
            return secret;
        }
    }
}
