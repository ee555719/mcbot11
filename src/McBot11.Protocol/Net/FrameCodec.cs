using System;
using McBot11.Protocol.IO;

namespace McBot11.Protocol.Net
{
    /// <summary>
    /// 无 IO 的协议帧编解码器（Varint21 帧 + 可选 zlib 压缩）。
    /// FrameReader 累积 TCP 字节流（加密层之后）并产出解压后的包负载；
    /// FrameEncoder 将包负载编码为可发送的帧字节（加密层之前）。
    /// 未启用压缩时：[VarInt 长度][负载]；
    /// 启用压缩后：[VarInt 帧长][VarInt 未压缩长度][0x00 + 原始数据 | zlib 数据]。
    /// 帧长上限 2^21-1（与 vanilla Varint21FrameDecoder 一致）。
    /// </summary>
    public sealed class FrameReader
    {
        /// <summary>最大帧负载长度（Varint21 上限 2^21-1）。</summary>
        public const int MaxFrameLength = (1 << 21) - 1;

        private byte[] _buf = new byte[8192];
        private int _len;

        /// <summary>压缩阈值；-1 表示未启用压缩。</summary>
        public int CompressionThreshold { get; set; } = -1;

        /// <summary>追加原始字节（解密后、解帧前）。</summary>
        public void Append(byte[] data, int offset, int count)
        {
            if (_len + count > _buf.Length)
            {
                int n = _buf.Length;
                while (n < _len + count) n *= 2;
                Array.Resize(ref _buf, n);
            }
            Array.Copy(data, offset, _buf, _len, count);
            _len += count;
        }

        /// <summary>尝试取出一个完整包（已按阈值解压）；数据不足时返回 false。</summary>
        public bool TryTake(out byte[] packet)
        {
            packet = null!;
            if (!TryReadVarInt(_buf, 0, _len, out int frameLen, out int lenSize))
                return false;
            if (frameLen < 0 || frameLen > MaxFrameLength)
                throw new ProtocolException($"帧长度非法: {frameLen}");
            if (_len < lenSize + frameLen)
                return false;

            int bodyStart = lenSize;
            int bodyEnd = lenSize + frameLen;
            byte[] body = new byte[frameLen];
            Array.Copy(_buf, bodyStart, body, 0, frameLen);

            if (CompressionThreshold < 0)
            {
                packet = body;
            }
            else
            {
                int dataLen;
                int dataOffset;
                try
                {
                    var br = new PacketReader(body);
                    dataLen = br.ReadVarInt();
                    dataOffset = br.Position;
                }
                catch (ProtocolException ex)
                {
                    throw new ProtocolException("压缩帧头部解析失败", ex);
                }
                if (dataLen == 0)
                {
                    packet = new byte[frameLen - dataOffset];
                    Array.Copy(body, dataOffset, packet, 0, packet.Length);
                }
                else
                {
                    if (dataLen < 0 || dataLen > MaxFrameLength)
                        throw new ProtocolException($"解压后长度非法: {dataLen}");
                    int zLen = frameLen - dataOffset;
                    if (zLen <= 0) throw new ProtocolException("压缩帧数据为空");
                    packet = Zlib.Decode(body, dataOffset, zLen, dataLen);
                }
            }

            int consumed = bodyEnd;
            int remain = _len - consumed;
            if (remain > 0) Array.Copy(_buf, consumed, _buf, 0, remain);
            _len = remain;
            return true;
        }

        internal static bool TryReadVarInt(byte[] buf, int offset, int length, out int value, out int size)
        {
            value = 0;
            size = 0;
            int shift = 0;
            for (int i = 0; i < Math.Min(length, 5); i++)
            {
                byte b = buf[offset + i];
                value |= (b & 0x7F) << shift;
                size = i + 1;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift >= 35) throw new ProtocolException("帧长 VarInt 超过 5 字节");
            }
            return false;
        }
    }

    /// <summary>包负载 → 帧字节。</summary>
    public static class FrameEncoder
    {
        /// <summary>编码一个包为可发送帧。threshold &lt; 0 表示未启用压缩。</summary>
        public static byte[] Encode(byte[] packet, int threshold)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (threshold < 0)
            {
                var raw = new PacketWriter(packet.Length + 5);
                if (packet.Length > FrameReader.MaxFrameLength)
                    throw new ProtocolException($"包长度超限: {packet.Length}");
                raw.WriteVarInt(packet.Length);
                raw.WriteBytes(packet);
                return raw.ToArray();
            }

            var body = new PacketWriter(packet.Length + 10);
            if (packet.Length >= threshold && packet.Length <= FrameReader.MaxFrameLength)
            {
                byte[] z = Zlib.Encode(packet);
                if (z.Length + 5 < packet.Length) // 压缩后更小才使用压缩
                {
                    body.WriteVarInt(packet.Length);
                    body.WriteBytes(z);
                }
                else
                {
                    body.WriteVarInt(0);
                    body.WriteBytes(packet);
                }
            }
            else
            {
                body.WriteVarInt(0);
                body.WriteBytes(packet);
            }
            byte[] bodyBytes = body.ToArray();
            if (bodyBytes.Length > FrameReader.MaxFrameLength)
                throw new ProtocolException($"帧长度超限: {bodyBytes.Length}");
            var w = new PacketWriter(bodyBytes.Length + 5);
            w.WriteVarInt(bodyBytes.Length);
            w.WriteBytes(bodyBytes);
            return w.ToArray();
        }
    }
}
