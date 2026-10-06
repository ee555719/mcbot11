using System;
using System.Security.Cryptography;

namespace McBot11.Protocol.Crypto
{
    /// <summary>
    /// AES-128-CFB8 流密码（Java Cipher "AES/CFB8/NoPadding" 的自研实现）。
    /// Minecraft 以共享密钥（16 字节）同时作为 AES 密钥与 CFB8 反馈寄存器初值（IV）。
    /// .NET 无内建 CFB8 模式，这里用 AES-ECB 单块加密手写 8 位分段 CFB：
    ///   密钥流字节 o = AES(SR)[0]；加密 c = p ^ o；解密 p = c ^ o；
    ///   每字节后寄存器左移 1 字节并填入密文（加密与解密均填密文）。
    /// 实例在整个 TCP 流上保持状态（跨包连续），与 Java 端行为一致。
    /// </summary>
    public sealed class Cfb8Cipher : IDisposable
    {
        private readonly ICryptoTransform _aes;
        private readonly byte[] _sr = new byte[16];
        private readonly byte[] _ks = new byte[16];
        private bool _disposed;

        private Cfb8Cipher(byte[] key)
        {
            if (key == null || key.Length != 16)
                throw new ArgumentException("CFB8 密钥必须为 16 字节", nameof(key));
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.BlockSize = 128;
            _aes = aes.CreateEncryptor();
            Array.Copy(key, _sr, 16);
        }

        public static Cfb8Cipher Create(byte[] key) => new Cfb8Cipher(key);

        /// <summary>对缓冲区原地加/解密（同一实现，操作可逆）。</summary>
        public void Apply(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Cfb8Cipher));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            for (int i = 0; i < count; i++)
            {
                _aes.TransformBlock(_sr, 0, 16, _ks, 0);
                byte o = _ks[0];
                byte b = buffer[offset + i];
                buffer[offset + i] = (byte)(b ^ o);
                Array.Copy(_sr, 1, _sr, 0, 15);
                _sr[15] = buffer[offset + i];
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _aes.Dispose();
            Array.Clear(_sr);
            Array.Clear(_ks);
        }
    }
}
