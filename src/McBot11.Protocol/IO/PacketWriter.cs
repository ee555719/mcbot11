using System;
using System.Text;

namespace McBot11.Protocol.IO
{
    /// <summary>
    /// Minecraft 协议写入器（大端序）。可增长缓冲，一次构造一个包的负载数据。
    /// </summary>
    public sealed class PacketWriter
    {
        private byte[] _buf;
        private int _len;

        public PacketWriter(int capacity = 256)
        {
            _buf = new byte[Math.Max(16, capacity)];
            _len = 0;
        }

        /// <summary>已写入的字节数。</summary>
        public int Length => _len;

        private void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int n = _buf.Length;
            while (n < _len + extra) n *= 2;
            Array.Resize(ref _buf, n);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buf[_len++] = value;
        }

        public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteInt16(short value)
        {
            Ensure(2);
            _buf[_len++] = (byte)(value >> 8);
            _buf[_len++] = (byte)value;
        }

        public void WriteInt32(int value)
        {
            Ensure(4);
            _buf[_len++] = (byte)(value >> 24);
            _buf[_len++] = (byte)(value >> 16);
            _buf[_len++] = (byte)(value >> 8);
            _buf[_len++] = (byte)value;
        }

        public void WriteInt64(long value)
        {
            Ensure(8);
            _buf[_len++] = (byte)(value >> 56);
            _buf[_len++] = (byte)(value >> 48);
            _buf[_len++] = (byte)(value >> 40);
            _buf[_len++] = (byte)(value >> 32);
            _buf[_len++] = (byte)(value >> 24);
            _buf[_len++] = (byte)(value >> 16);
            _buf[_len++] = (byte)(value >> 8);
            _buf[_len++] = (byte)value;
        }

        public void WriteSingle(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            WriteInt32(bits);
        }

        public void WriteDouble(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            WriteInt64(bits);
        }

        /// <summary>写入 VarInt（1~5 字节）。</summary>
        public void WriteVarInt(int value)
        {
            uint v = (uint)value;
            Ensure(5);
            while ((v & ~0x7Fu) != 0)
            {
                _buf[_len++] = (byte)((v & 0x7F) | 0x80);
                v >>= 7;
            }
            _buf[_len++] = (byte)v;
        }

        /// <summary>写入 VarLong（1~10 字节）。</summary>
        public void WriteVarLong(long value)
        {
            ulong v = (ulong)value;
            Ensure(10);
            while ((v & ~0x7FUL) != 0)
            {
                _buf[_len++] = (byte)((v & 0x7F) | 0x80);
                v >>= 7;
            }
            _buf[_len++] = (byte)v;
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            Ensure(data.Length);
            data.CopyTo(_buf.AsSpan(_len));
            _len += data.Length;
        }

        /// <summary>写入 VarInt 长度前缀的字节数组。</summary>
        public void WriteVarBytes(ReadOnlySpan<byte> data)
        {
            WriteVarInt(data.Length);
            WriteBytes(data);
        }

        /// <summary>
        /// 写入协议字符串：VarInt 字节长度前缀 + UTF-8。
        /// maxChars 为字符数上限；超出抛出 ProtocolException（由调用方先做截断）。
        /// </summary>
        public void WriteString(string value, int maxChars = 32767)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length > maxChars)
                throw new ProtocolException($"字符串超长: {value.Length} > {maxChars}");
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > maxChars * 3 + 3)
                throw new ProtocolException($"字符串字节超长: {bytes.Length}");
            WriteVarInt(bytes.Length);
            WriteBytes(bytes);
        }

        /// <summary>写入 16 字节大端 UUID。</summary>
        public void WriteUuid(Guid value)
        {
            byte[] raw = Convert.FromHexString(value.ToString("N"));
            WriteBytes(raw);
        }

        /// <summary>输出全部已写字节的副本。</summary>
        public byte[] ToArray()
        {
            var result = new byte[_len];
            Array.Copy(_buf, result, _len);
            return result;
        }

        /// <summary>重置写入位置（复用实例）。</summary>
        public void Reset() => _len = 0;
    }
}
