using System;
using McBot11.Auth;

namespace McBot11.Minecraft
{
    /// <summary>Minecraft 客户端连接选项。</summary>
    public sealed class MinecraftOptions
    {
        /// <summary>服务器地址。</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>服务器端口。</summary>
        public int Port { get; set; } = 25565;

        /// <summary>协议版本（767~777）。0 = 自动（通过 status 查询服务器版本）。</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>账号配置。</summary>
        public AccountConfig Account { get; set; } = new AccountConfig();

        /// <summary>上报的品牌标识。</summary>
        public string Brand { get; set; } = "mcbot11";

        /// <summary>客户端语言（client information locale）。</summary>
        public string Locale { get; set; } = "zh_CN";

        /// <summary>视距（chunk）。</summary>
        public int ViewDistance { get; set; } = 8;

        /// <summary>token 缓存（config\tokens.json 的实现，由 Core 注入）。</summary>
        public McBot11.Auth.ITokenCache? TokenCache { get; set; }
    }

    /// <summary>聊天消息类别。</summary>
    public enum ChatKind
    {
        /// <summary>玩家聊天（player_chat，已签名广播）。</summary>
        Player,
        /// <summary>系统消息（system_chat，非 action bar）。</summary>
        System,
        /// <summary>伪装/代理消息（disguised_chat）。</summary>
        Disguised
    }

    /// <summary>聊天事件参数（纯文本已渲染）。</summary>
    public sealed class ChatEventArgs : EventArgs
    {
        public ChatKind Kind { get; init; }
        /// <summary>消息正文（纯文本）。</summary>
        public string Text { get; init; } = "";
        /// <summary>发送者 UUID（Player 类别时有值；否则为空）。</summary>
        public Guid SenderUuid { get; init; }
        /// <summary>发送者游戏名（已知时）。</summary>
        public string? SenderName { get; init; }
    }

    /// <summary>客户端对外状态。</summary>
    public enum ClientPhase
    {
        Disconnected,
        Connecting,
        Login,
        Configuration,
        Play
    }
}
