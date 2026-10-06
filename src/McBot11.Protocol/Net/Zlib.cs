using System;
using System.IO;
using System.IO.Compression;

namespace McBot11.Protocol.Net
{
    /// <summary>
    /// zlib（RFC 1950，含 zlib 头）编解码，基于 BCL 的 ZLibStream。
    /// 用于启用压缩后的协议帧负载；解压时强制精确长度校验。
    /// </summary>
    public static class Zlib
    {
        /// <summary>zlib 压缩。</summary>
        public static byte[] Encode(byte[] data)
        {
            using var ms = new MemoryStream(data.Length / 2 + 16);
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        /// <summary>zlib 解压，要求解压结果恰为 expectedLength 字节。</summary>
        public static byte[] Decode(byte[] data, int offset, int count, int expectedLength)
        {
            if (expectedLength < 0 || expectedLength > FrameReader.MaxFrameLength)
                throw new ProtocolException($"解压长度非法: {expectedLength}");
            var output = new byte[expectedLength];
            try
            {
                using var input = new MemoryStream(data, offset, count, writable: false);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                int total = 0;
                while (total < expectedLength)
                {
                    int n = z.Read(output, total, expectedLength - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total != expectedLength)
                    throw new ProtocolException($"解压长度不符：期望 {expectedLength}，实际 {total}");
                if (z.ReadByte() != -1)
                    throw new ProtocolException("解压数据超出期望长度");
                return output;
            }
            catch (InvalidDataException ex)
            {
                throw new ProtocolException("zlib 解压失败", ex);
            }
        }
    }
}
