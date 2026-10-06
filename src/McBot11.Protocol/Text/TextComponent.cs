using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace McBot11.Protocol.Text
{
    /// <summary>
    /// 聊天组件 → 纯文本。
    /// 支持两种载体：
    /// 1) 网络 NBT 组件（system_chat/disguised_chat/disconnect 等，Dictionary/List 结构）；
    /// 2) JSON 字符串（login_disconnect 的 reason 等）。
    /// 渲染规则：text 字段直接追加；translate 按 with 参数替换 %s / %n$s；
    /// extra 递归；无参数时回退显示 translate 键名。selector/key 按原文追加。
    /// </summary>
    public static class TextComponent
    {
        /// <summary>把 NBT 组件对象平铺为纯文本。</summary>
        public static string ToPlainText(object? node)
        {
            if (node == null) return string.Empty;
            switch (node)
            {
                case string s:
                    return s;
                case Dictionary<string, object> dict:
                {
                    var sb = new StringBuilder();
                    AppendDict(sb, dict);
                    return sb.ToString();
                }
                case List<object> list:
                {
                    var sb = new StringBuilder();
                    foreach (var item in list) sb.Append(ToPlainText(item));
                    return sb.ToString();
                }
                case int i:
                    return i.ToString();
                case long l:
                    return l.ToString();
                case float f:
                    return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return string.Empty;
            }
        }

        private static void AppendDict(StringBuilder sb, Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("text", out var text))
            {
                sb.Append(ToPlainText(text));
            }
            else if (dict.TryGetValue("translate", out var tr))
            {
                string key = tr as string ?? string.Empty;
                var args = new List<string>();
                if (dict.TryGetValue("with", out var with) && with is List<object> withList)
                {
                    foreach (var a in withList) args.Add(ToPlainText(a));
                }
                sb.Append(FormatTranslate(key, args));
            }
            else if (dict.TryGetValue("selector", out var sel))
            {
                sb.Append(sel as string ?? string.Empty);
            }
            else if (dict.TryGetValue("key", out var keyTag))
            {
                sb.Append(keyTag as string ?? string.Empty);
            }
            else if (dict.TryGetValue("score", out var scoreObj) && scoreObj is Dictionary<string, object> score)
            {
                if (score.TryGetValue("value", out var val)) sb.Append(ToPlainText(val));
            }

            if (dict.TryGetValue("extra", out var extra) && extra is List<object> extras)
            {
                foreach (var item in extras) sb.Append(ToPlainText(item));
            }
        }

        /// <summary>替换翻译键中的 %1$s..%9$s 与 %s 占位符（%% 转义为字面 %）。</summary>
        private static string FormatTranslate(string key, List<string> args)
        {
            if (args.Count == 0) return key;
            var sb = new StringBuilder(key.Length + 16);
            int plainIdx = 0;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c == '%' && i + 1 < key.Length)
                {
                    char n = key[i + 1];
                    if (n == 's')
                    {
                        sb.Append(plainIdx < args.Count ? args[plainIdx++] : string.Empty);
                        i++;
                        continue;
                    }
                    if (n >= '1' && n <= '9' && i + 3 < key.Length && key[i + 2] == '$' && key[i + 3] == 's')
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

        /// <summary>
        /// 解析协议字符串形式的聊天组件（JSON）为纯文本；
        /// 非 JSON（以 { [ " 开头）时按原文返回。
        /// </summary>
        public static string JsonToPlainText(string json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;
            char c0 = json[0];
            if (c0 != '{' && c0 != '[' && c0 != '"') return json;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ToPlainText(ConvertElement(doc.RootElement));
            }
            catch (JsonException)
            {
                return json;
            }
        }

        private static object? ConvertElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt32(out int i)) return i;
                    if (el.TryGetInt64(out long l)) return l;
                    return el.GetDouble();
                case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var p in el.EnumerateObject())
                    {
                        var v = ConvertElement(p.Value);
                        if (v != null) dict[p.Name] = v;
                    }
                    return dict;
                }
                case JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (var item in el.EnumerateArray())
                    {
                        var v = ConvertElement(item);
                        if (v != null) list.Add(v);
                    }
                    return list;
                }
                default:
                    return null;
            }
        }
    }
}
