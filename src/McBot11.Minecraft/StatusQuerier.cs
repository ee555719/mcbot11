using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McBot11.Logging;
using McBot11.Protocol;
using McBot11.Protocol.IO;
using McBot11.Protocol.Net;

namespace McBot11.Minecraft
{
    /// <summary>
    /// Status 阶段查询：握手 → status_request → 读 version.protocol → 关闭。
    /// 用于协议版本留空时自动识别服务器版本（协议 767~777 范围校验）。
    /// </summary>
    public static class StatusQuerier
    {
        public static async Task<int> ResolveAsync(string host, int port, IAppLogger log, CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var frames = new FrameReader();

            // 握手（status 分支；protocol 字段在 status 阶段被服务器忽略）
            var hs = new PacketWriter();
            hs.WriteVarInt(PacketIds.GetId(767, PacketState.Handshake, PacketDirection.ToServer, PacketName.Intention));
            hs.WriteVarInt(767);
            hs.WriteString(host.Length > 255 ? host.Substring(0, 255) : host, 255);
            hs.WriteInt16(unchecked((short)port));
            hs.WriteVarInt(1); // next = status
            await WriteAsync(stream, FrameEncoder.Encode(hs.ToArray(), -1), timeout.Token).ConfigureAwait(false);

            var req = new PacketWriter();
            req.WriteVarInt(PacketIds.GetId(767, PacketState.Status, PacketDirection.ToServer, PacketName.StatusRequest));
            await WriteAsync(stream, FrameEncoder.Encode(req.ToArray(), -1), timeout.Token).ConfigureAwait(false);

            var buf = new byte[8192];
            while (true)
            {
                int n = await stream.ReadAsync(buf, timeout.Token).ConfigureAwait(false);
                if (n == 0) throw new ProtocolException("status 查询时服务器关闭连接");
                frames.Append(buf, 0, n);
                while (frames.TryTake(out byte[] packet))
                {
                    var r = new PacketReader(packet);
                    int id = r.ReadVarInt();
                    if (!PacketIds.TryGetName(767, PacketState.Status, PacketDirection.ToClient, id, out var name))
                        continue;
                    if (name == PacketName.StatusResponse)
                    {
                        string json = r.ReadString(32767 * 4);
                        int protocol = ParseProtocol(json);
                        log.Info("Status", $"服务器 {host}:{port} 版本协议 = {protocol}");
                        return protocol;
                    }
                }
            }
        }

        private static int ParseProtocol(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("version", out var ver) ||
                !ver.TryGetProperty("protocol", out var p) || !p.TryGetInt32(out int protocol))
                throw new ProtocolException("status 响应缺少 version.protocol");
            return protocol;
        }

        private static async Task WriteAsync(System.IO.Stream stream, byte[] data, CancellationToken ct)
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
