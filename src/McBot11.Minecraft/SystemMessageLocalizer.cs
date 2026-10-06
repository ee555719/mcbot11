using System;
using System.Collections.Generic;
using System.Text;
using McBot11.Protocol.Text;

namespace McBot11.Minecraft
{
    /// <summary>
    /// 系统消息翻译键 → 中文本地化（独立映射类，便于扩展）。
    /// 覆盖 system_chat / disguised_chat 中常见的 translate 键；
    /// 未知键原样输出（可带参数），绝不抛异常。
    /// </summary>
    public static class SystemMessageLocalizer
    {
        /// <summary>翻译键映射表（可继续扩展）。</summary>
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 需求必覆盖的三个键
            ["multiplayer.player.joined"] = "有玩家加入了游戏",
            ["death.attack.genericKill"] = "有玩家死亡了",
            ["multiplayer.player.left"] = "有玩家退出了游戏",
            // 同族常见键（扩展）
            ["multiplayer.player.reconnected"] = "有玩家重新加入了游戏",
            ["multiplayer.disconnect.disconnected"] = "玩家断开了连接",
        };

        /// <summary>
        /// 翻译键本地化。template 支持 %s / %1$s / {0} 占位符按序替换；
        /// 未知键原样返回（参数非空时附加在括号内）。永不抛异常。
        /// </summary>
        public static string Localize(string? key, IReadOnlyList<string> args)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                if (Map.TryGetValue(key!, out string? template))
                    return ApplyPlaceholders(template, args);
                // 未知翻译键：原样输出（带参数）
                if (args == null || args.Count == 0) return key!;
                var sb = new StringBuilder(key);
                sb.Append('(');
                for (int i = 0; i < args.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(args[i]);
                }
                sb.Append(')');
                return sb.ToString();
            }
            catch
            {
                return key!; // 绝不因本地化失败而抛异常
            }
        }

        /// <summary>
        /// 从 system_chat / disguised_chat 的 NBT 组件本地化为纯文本：
        /// 含 translate 键则走映射；否则回退为常规组件纯文本渲染。
        /// </summary>
        public static string FromComponent(object? node)
        {
            try
            {
                if (node is Dictionary<string, object> dict &&
                    dict.TryGetValue("translate", out var tr) && tr is string key)
                {
                    var args = new List<string>();
                    if (dict.TryGetValue("with", out var withObj) && withObj is List<object> withList)
                    {
                        foreach (object? a in withList)
                            args.Add(TextComponent.ToPlainText(a));
                    }
                    return Localize(key, args);
                }
                return TextComponent.ToPlainText(node);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>占位符替换：按序消耗式 %s / %1$s..%9$s / %n$s，支持 {0}..{9} 与 %% 转义。</summary>
        private static string ApplyPlaceholders(string template, IReadOnlyList<string> args)
        {
            if (args == null || args.Count == 0) return template;
            var sb = new StringBuilder(template.Length + 16);
            int plainIdx = 0;
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '{' && i + 1 < template.Length && i + 2 < template.Length &&
                    char.IsDigit(template[i + 1]) && template[i + 2] == '}')
                {
                    int idx = template[i + 1] - '0';
                    sb.Append(idx < args.Count ? args[idx] : string.Empty);
                    i += 2;
                    continue;
                }
                if (c == '%' && i + 1 < template.Length)
                {
                    char n = template[i + 1];
                    if (n == 's')
                    {
                        sb.Append(plainIdx < args.Count ? args[plainIdx++] : string.Empty);
                        i++;
                        continue;
                    }
                    if (n >= '1' && n <= '9' && i + 3 < template.Length &&
                        template[i + 2] == '$' && template[i + 3] == 's')
                    {
                        int idx = n - '1';
                        sb.Append(idx < args.Count ? args[idx] : string.Empty);
                        i += 3;
                        continue;
                    }
                    if (n == '%')
                    {
                        sb.Append('%');
                        i++;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
