using System;
using System.Text;

namespace McBot11.Protocol.IO
{
    /// <summary>
    /// Minecraft 协议整数/字节数组读取器（大端序）。
    /// 覆盖 VarInt/VarLong、有符号与无符号定长整数、浮点、字符串（VarInt 长度前缀）、UUID、
    /// VarInt 长度前缀字节数组等协议原语。一次性承载单个包的负载数据，无 IO。
    /// </summary>
    public sealed class PacketReader
    {
        private readonly byte[] _buf;
        private int _pos;
        private readonly int _end;

        public PacketReader(byte[] buf) : this(buf, 0, buf?.Length ?? 0) { }

        public PacketReader(byte[] buf, int offset, int length)
        {
            _buf = buf ?? throw new ArgumentNullException(nameof(buf));
            if (offset < 0 || length < 0 || offset + length > buf.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            _pos = offset;
            _end = offset + length;
        }

        /// <summary>当前位置（相对缓冲区起始）。</summary>
        public int Position
        {
            get => _pos;
            set
            {
                if (value < 0 || value > _end) throw new ProtocolException("PacketReader 位置越界: " + value);
                _pos = value;
            }
        }

        /// <summary>剩余未读字节数。</summary>
        public int Remaining => _end - _pos;

        private void Require(int count)
        {
            if (count < 0 || Remaining < count)
                throw new ProtocolException($"包数据不足：需要 {count} 字节，剩余 {Remaining} 字节");
        }

        public byte PeekByte()
        {
            Require(1);
            return _buf[_pos];
        }

        public byte ReadByte()
        {
            Require(1);
            return _buf[_pos++];
        }

        public bool ReadBoolean() => ReadByte() != 0;

        public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

        public short ReadInt16()
        {
            Require(2);
            int v = (_buf[_pos] << 8) | _buf[_pos + 1];
            _pos += 2;
            return unchecked((short)v);
        }

        public ushort ReadUInt16()
        {
            Require(2);
            int v = (_buf[_pos] << 8) | _buf[_pos + 1];
            _pos += 2;
            return (ushort)v;
        }

        public int ReadInt32()
        {
            Require(4);
            int v = (_buf[_pos] << 24) | (_buf[_pos + 1] << 16) | (_buf[_pos + 2] << 8) | _buf[_pos + 3];
            _pos += 4;
            return v;
        }

        public long ReadInt64()
        {
            Require(8);
            long v = ((long)_buf[_pos] << 56) | ((long)_buf[_pos + 1] << 48) | ((long)_buf[_pos + 2] << 40) |
                     ((long)_buf[_pos + 3] << 32) | ((long)_buf[_pos + 4] << 24) | ((long)_buf[_pos + 5] << 16) |
                     ((long)_buf[_pos + 6] << 8) | _buf[_pos + 7];
            _pos += 8;
            return v;
        }

        public float ReadSingle()
        {
            int bits = ReadInt32();
            return BitConverter.Int32BitsToSingle(bits);
        }

        public double ReadDouble()
        {
            long bits = ReadInt64();
            return BitConverter.Int64BitsToDouble(bits);
        }

        /// <summary>读取 VarInt（最多 5 字节）。</summary>
        public int ReadVarInt()
        {
            int value = 0;
            int shift = 0;
            while (true)
            {
                byte b = ReadByte();
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 35) throw new ProtocolException("VarInt 超过 5 字节");
            }
            return value;
        }

        /// <summary>读取 VarLong（最多 10 字节）。</summary>
        public long ReadVarLong()
        {
            long value = 0;
            int shift = 0;
            while (true)
            {
                byte b = ReadByte();
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 70) throw new ProtocolException("VarLong 超过 10 字节");
            }
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            var result = new byte[count];
            Array.Copy(_buf, _pos, result, 0, count);
            _pos += count;
            return result;
        }

        public void Skip(int count)
        {
            Require(count);
            _pos += count;
        }

        /// <summary>读取 VarInt 长度前缀的字节数组。</summary>
        public byte[] ReadVarBytes(int maxLength)
        {
            int len = ReadVarInt();
            if (len < 0 || len > maxLength)
                throw new ProtocolException($"字节数组长度非法: {len} (上限 {maxLength})");
            return ReadBytes(len);
        }

        /// <summary>
        /// 读取协议字符串：VarInt 字节长度前缀 + UTF-8。
        /// maxChars 为字符数上限（协议定义如 16/256/32767）。
        /// </summary>
        public string ReadString(int maxChars = 32767)
        {
            int byteLen = ReadVarInt();
            int maxBytes = maxChars * 3 + 3;
            if (byteLen < 0 || byteLen > maxBytes)
                throw new ProtocolException($"字符串字节长度非法: {byteLen} (上限 {maxBytes})");
            Require(byteLen);
            string s;
            try
            {
                s = Encoding.UTF8.GetString(_buf, _pos, byteLen);
            }
            catch (ArgumentException ex)
            {
                throw new ProtocolException("字符串 UTF-8 解码失败", ex);
            }
            _pos += byteLen;
            if (s.Length > maxChars)
                throw new ProtocolException($"字符串超长: {s.Length} > {maxChars}");
            return s;
        }

        /// <summary>读取 16 字节大端 UUID。</summary>
        public Guid ReadUuid()
        {
            byte[] raw = ReadBytes(16);
            string hex = Convert.ToHexString(raw).ToLowerInvariant();
            return new Guid(hex);
        }

        /// <summary>读取 NBT 字符串（无符号大端 2 字节长度前缀，非 VarInt）。</summary>
        public string ReadNbtString()
        {
            int len = ReadUInt16();
            Require(len);
            string s;
            try
            {
                s = Encoding.UTF8.GetString(_buf, _pos, len);
            }
            catch (ArgumentException ex)
            {
                throw new ProtocolException("NBT 字符串 UTF-8 解码失败", ex);
            }
            _pos += len;
            return s;
        }

        /// <summary>读取剩余全部字节。</summary>
        public byte[] ReadRemaining()
        {
            int n = Remaining;
            return ReadBytes(n);
        }
    }
}
