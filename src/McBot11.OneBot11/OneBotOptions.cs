using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace McBot11.OneBot11
{
    /// <summary>OneBot v11 正向 WebSocket 连接配置。</summary>
    public sealed class OneBotOptions
    {
        /// <summary>WebSocket 地址，如 ws://127.0.0.1:3001。</summary>
        public string Url { get; set; } = "ws://127.0.0.1:3001";

        /// <summary>访问令牌（配置后以 Authorization: Bearer 头携带）。</summary>
        public string? AccessToken { get; set; }

        /// <summary>断线后首次重连的起始延迟（毫秒，之后指数退避，上限 30 秒）。</summary>
        public int ReconnectIntervalMs { get; set; } = 5000;
    }

    /// <summary>群消息事件参数。</summary>
    public sealed class GroupMessageArgs : EventArgs
    {
        public long GroupId { get; init; }
        public long UserId { get; init; }
        /// <summary>发送者昵称。</summary>
        public string Nickname { get; init; } = "";
        /// <summary>群名片（优先于昵称）。</summary>
        public string Card { get; init; } = "";
        /// <summary>消息纯文本（CQ 码已还原为文本形式）。</summary>
        public string Text { get; init; } = "";
        /// <summary>原始消息（raw_message，含 CQ 码）。</summary>
        public string RawMessage { get; init; } = "";
        /// <summary>消息中被 @ 的 QQ 号列表。</summary>
        public IReadOnlyList<long> AtList { get; init; } = Array.Empty<long>();
        /// <summary>是否 @ 了机器人自身。</summary>
        public bool AtBot { get; init; }
        public long SelfId { get; init; }
    }

    /// <summary>私聊消息事件参数。</summary>
    public sealed class PrivateMessageArgs : EventArgs
    {
        public long UserId { get; init; }
        public string Nickname { get; init; } = "";
        public string Text { get; init; } = "";
        public string RawMessage { get; init; } = "";
        public long SelfId { get; init; }
    }

    /// <summary>OneBot 客户端状态。</summary>
    public enum OneBotState
    {
        Disconnected,
        Connecting,
        Connected
    }

    /// <summary>消息 JSON 数组元素 → 文本还原与 @ 提取。</summary>
    public static class OneBotMessageParser
    {
        public static (string Text, List<long> Ats) ParseMessageArray(JsonElement message)
        {
            var sb = new StringBuilder();
            var ats = new List<long>();
            if (message.ValueKind == JsonValueKind.String)
            {
                return (message.GetString() ?? "", ats);
            }
            if (message.ValueKind != JsonValueKind.Array) return ("", ats);
            foreach (var seg in message.EnumerateArray())
            {
                string type = seg.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (!seg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                switch (type)
                {
                    case "text":
                        if (data.TryGetProperty("text", out var txt))
                            sb.Append(txt.GetString());
                        break;
                    case "at":
                        if (data.TryGetProperty("qq", out var qq))
                        {
                            string qqStr = qq.ToString();
                            sb.Append('@').Append(qqStr);
                            if (long.TryParse(qqStr, out long qqn)) ats.Add(qqn);
                        }
                        break;
                    case "image":
                        sb.Append("[图片]");
                        break;
                    case "face":
                        sb.Append("[表情]");
                        break;
                    case "reply":
                        sb.Append("[回复]");
                        break;
                    default:
                        sb.Append('[').Append(type).Append(']');
                        break;
                }
            }
            return (sb.ToString(), ats);
        }

        /// <summary>从 raw_message 提取 @ 的 QQ 号（CQ 码形式 [CQ:at,qq=123]）。</summary>
        public static List<long> ParseAtFromRaw(string raw)
        {
            var ats = new List<long>();
            int pos = 0;
            while ((pos = raw.IndexOf("[CQ:at,qq=", pos, StringComparison.Ordinal)) >= 0)
            {
                int start = pos + "[CQ:at,qq=".Length;
                int end = raw.IndexOf(']', start);
                if (end < 0) break;
                if (long.TryParse(raw.Substring(start, end - start), out long qq))
                    ats.Add(qq);
                pos = end + 1;
            }
            return ats;
        }
    }
}
