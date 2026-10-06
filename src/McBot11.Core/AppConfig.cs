using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McBot11.Core
{
    /// <summary>config.json 根对象。</summary>
    public sealed class AppConfig
    {
        [JsonPropertyName("minecraft")]
        public MinecraftSection Minecraft { get; set; } = new MinecraftSection();

        [JsonPropertyName("onebot")]
        public OneBotSection OneBot { get; set; } = new OneBotSection();

        [JsonPropertyName("routing")]
        public RoutingSection Routing { get; set; } = new RoutingSection();

        [JsonPropertyName("format")]
        public FormatSection Format { get; set; } = new FormatSection();

        [JsonPropertyName("customCommands")]
        public CustomCommandsSection CustomCommands { get; set; } = new CustomCommandsSection();

        [JsonPropertyName("commandPermissions")]
        public CommandPermissionsSection CommandPermissions { get; set; } = new CommandPermissionsSection();
    }

    /// <summary>Minecraft 连接与认证配置。</summary>
    public sealed class MinecraftSection
    {
        [JsonPropertyName("host")] public string Host { get; set; } = "127.0.0.1";
        [JsonPropertyName("port")] public int Port { get; set; } = 25565;
        [JsonPropertyName("username")] public string Username { get; set; } = "MyBot";
        /// <summary>协议版本；null = 自动（status 查询）。</summary>
        [JsonPropertyName("protocolVersion")] public int? ProtocolVersion { get; set; }
        [JsonPropertyName("authType")] public string AuthType { get; set; } = "offline";
        /// <summary>账号池（config\accounts.json）中当前选中的账号 ID；空则回退单账号兼容配置。</summary>
        [JsonPropertyName("currentAccountId")] public string? CurrentAccountId { get; set; }
        [JsonPropertyName("auth")] public AuthSection Auth { get; set; } = new AuthSection();
        [JsonPropertyName("autoReconnect")] public bool AutoReconnect { get; set; } = true;
        [JsonPropertyName("reconnectIntervalSeconds")] public int ReconnectIntervalSeconds { get; set; } = 10;
    }

    public sealed class AuthSection
    {
        [JsonPropertyName("offline")] public OfflineAuthSection Offline { get; set; } = new OfflineAuthSection();
        [JsonPropertyName("mojang")] public MojangAuthSection Mojang { get; set; } = new MojangAuthSection();
        [JsonPropertyName("microsoft")] public MicrosoftAuthSection Microsoft { get; set; } = new MicrosoftAuthSection();
        [JsonPropertyName("yggdrasil")] public YggdrasilAuthSection Yggdrasil { get; set; } = new YggdrasilAuthSection();
    }

    public sealed class OfflineAuthSection
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "MyBot";
    }

    public sealed class MojangAuthSection
    {
        [JsonPropertyName("email")] public string Email { get; set; } = "";
        [JsonPropertyName("password")] public string Password { get; set; } = "";
    }

    public sealed class MicrosoftAuthSection
    {
        [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    }

    public sealed class YggdrasilAuthSection
    {
        [JsonPropertyName("serverRoot")] public string ServerRoot { get; set; } = "https://littleskin.cn/api/yggdrasil";
        [JsonPropertyName("email")] public string Email { get; set; } = "";
        [JsonPropertyName("password")] public string Password { get; set; } = "";
    }

    /// <summary>OneBot 连接配置。</summary>
    public sealed class OneBotSection
    {
        [JsonPropertyName("wsUrl")] public string WsUrl { get; set; } = "ws://127.0.0.1:3001";
        [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("reconnectIntervalSeconds")] public int ReconnectIntervalSeconds { get; set; } = 5;
    }

    /// <summary>消息路由配置。</summary>
    public sealed class RoutingSection
    {
        [JsonPropertyName("mcToQQ")] public McToQqSection McToQq { get; set; } = new McToQqSection();
        [JsonPropertyName("qqToMc")] public QqToMcSection QqToMc { get; set; } = new QqToMcSection();
    }

    public sealed class McToQqSection
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("targetGroups")] public List<long> TargetGroups { get; set; } = new List<long>();
        [JsonPropertyName("targetPrivate")] public List<long> TargetPrivate { get; set; } = new List<long>();
        [JsonPropertyName("includeBotSelfMessage")] public bool IncludeBotSelfMessage { get; set; }
    }

    public sealed class QqToMcSection
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("allowGroups")] public List<long> AllowGroups { get; set; } = new List<long>();
        [JsonPropertyName("allowPrivate")] public List<long> AllowPrivate { get; set; } = new List<long>();
        [JsonPropertyName("requireAtInGroup")] public bool RequireAtInGroup { get; set; } = true;
    }

    /// <summary>消息格式模板。</summary>
    public sealed class FormatSection
    {
        /// <summary>QQ→MC 新格式（默认）：({QQ名字}) {消息}。</summary>
        public const string DefaultQqToMcTemplate = "({qqName}) {message}";
        /// <summary>旧格式（已废弃，仅用于自动迁移）：&lt;{botName}&gt; ({qqName}) ({gameName}) {message}。</summary>
        public const string LegacyQqToMcTemplate = "<{botName}> ({qqName}) ({gameName}) {message}";

        [JsonPropertyName("qqToMcTemplate")] public string QqToMcTemplate { get; set; } = DefaultQqToMcTemplate;
        [JsonPropertyName("mcToQqTemplate")] public string McToQqTemplate { get; set; } = "[MC] <{mcName}> {message}";
        /// <summary>系统消息模板（与玩家消息模板分开）。</summary>
        [JsonPropertyName("systemMessageTemplate")] public string SystemMessageTemplate { get; set; } = "[MC] <Server> {message}";
    }

    /// <summary>进服自动执行的自定义命令。</summary>
    public sealed class CustomCommandsSection
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        /// <summary>命令文件路径（相对路径基于 D:\mcbot11 根目录）。</summary>
        [JsonPropertyName("filePath")] public string FilePath { get; set; } = "config/commands.txt";
        [JsonPropertyName("executeOnJoin")] public bool ExecuteOnJoin { get; set; } = true;
        /// <summary>每条命令之间的执行间隔（毫秒）。</summary>
        [JsonPropertyName("commandExecuteIntervalMs")] public int CommandExecuteIntervalMs { get; set; } = 1000;
        /// <summary>命令前缀（程序发送时自动补全，允许文件内已带 /）。</summary>
        [JsonPropertyName("commandPrefix")] public string CommandPrefix { get; set; } = "/";
    }

    /// <summary>QQ 远程执行 MC 命令的权限（安全白名单，默认全空 = 无人可执行）。</summary>
    public sealed class CommandPermissionsSection
    {
        [JsonPropertyName("allowCommandGroups")] public List<long> AllowCommandGroups { get; set; } = new List<long>();
        [JsonPropertyName("allowCommandPrivate")] public List<long> AllowCommandPrivate { get; set; } = new List<long>();
        /// <summary>执行成功后回复发送者。</summary>
        [JsonPropertyName("replyOnExecute")] public bool ReplyOnExecute { get; set; } = true;
        /// <summary>失败/无权限/空命令时回复发送者。</summary>
        [JsonPropertyName("replyOnFailure")] public bool ReplyOnFailure { get; set; } = true;
    }

    /// <summary>配置加载/保存；文件不存在时生成默认配置（首次运行）。</summary>
    public static class ConfigStore
    {
        public static readonly string RootDir = @"D:\mcbot11";
        public static readonly string ConfigDir = Path.Combine(RootDir, "config");
        public static readonly string LogDir = Path.Combine(RootDir, "logs");
        public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
        public static readonly string TokensPath = Path.Combine(ConfigDir, "tokens.json");
        /// <summary>第三方登录账号池。</summary>
        public static readonly string AccountsPath = Path.Combine(ConfigDir, "accounts.json");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>确保根目录结构存在。</summary>
        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(LogDir);
        }

        /// <summary>加载配置；不存在则创建默认，解析失败则备份后重建默认。</summary>
        public static AppConfig Load()
        {
            EnsureDirectories();
            if (!File.Exists(ConfigPath))
            {
                var fresh = new AppConfig();
                Save(fresh);
                return fresh;
            }
            try
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg == null) throw new JsonException("配置为空");
                if (MigrateLegacyFormat(cfg)) Save(cfg);
                return cfg;
            }
            catch (Exception ex)
            {
                string backup = ConfigPath + ".broken." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss");
                try { File.Copy(ConfigPath, backup, overwrite: true); } catch { }
                throw new CoreException($"config.json 解析失败（已备份到 {backup}）: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 旧配置自动迁移：QQ→MC 模板若仍是旧格式（含 {botName}/{gameName} 或等于旧默认），
        /// 统一迁移到新默认 "({qqName}) {message}"（MC 服务器会自动加 &lt;假人名&gt; 前缀）。
        /// </summary>
        private static bool MigrateLegacyFormat(AppConfig cfg)
        {
            string t = cfg.Format.QqToMcTemplate ?? "";
            if (t.Length == 0 ||
                t.Contains("{gameName}", StringComparison.Ordinal) ||
                t.Contains("{botName}", StringComparison.Ordinal) ||
                t.Equals(FormatSection.LegacyQqToMcTemplate, StringComparison.Ordinal))
            {
                cfg.Format.QqToMcTemplate = FormatSection.DefaultQqToMcTemplate;
                return true;
            }
            return false;
        }

        /// <summary>保存配置（原子写：临时文件 + 替换）。</summary>
        public static void Save(AppConfig cfg)
        {
            EnsureDirectories();
            string json = JsonSerializer.Serialize(cfg, Options);
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }

    /// <summary>Core 层异常。</summary>
    public sealed class CoreException : Exception
    {
        public CoreException(string message) : base(message) { }
        public CoreException(string message, Exception inner) : base(message, inner) { }
    }
}
