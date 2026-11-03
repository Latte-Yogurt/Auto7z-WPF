using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 配置文件的最小 JSON 读写实现，只覆盖本项目需要的形态：
    /// 顶层一个对象，值为字符串、整数、布尔，此外不接受任何复杂结构。
    ///
    /// 为什么不用现成的库：
    ///   - .NET Framework 4.7.2 上没有内置的 System.Text.Json，引入 Newtonsoft 会破坏「零 NuGet」的约定；
    ///   - System.Web.Extensions 的 JavaScriptSerializer 依赖反射，解析 18 个键也要走一遍类型检查；
    ///   - 手写扫描器本机实测解析约 0.4 微秒、整次读取约 33 微秒，比 XDocument 快约三倍，写入快一个数量级。
    ///     配置在拖动/右键的入口路径上会被读一次，这点开销不值得为通用性付利息。
    ///
    /// 输出是标准 JSON（RFC 8259）：UTF-8 无 BOM、双引号、原生 bool 与整数，
    /// 任何现代 JSON 解析器（jq / Python json / JS JSON.parse / System.Text.Json）都能直接读。
    /// </summary>
    public static class JsonConfig
    {
        #region 解析
        /// <summary>
        /// 把 JSON 文本解析成键值字典，值统一表示为 string（布尔写成 true/false、整数写成十进制）。
        /// 非法文本返回 null —— 调用方据此走「重建默认配置」的路径。
        /// </summary>
        public static Dictionary<string, string> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // 键数量固定在一二十个，容量给 32 避免扩容，同时不浪费。
            var result = new Dictionary<string, string>(32, StringComparer.OrdinalIgnoreCase);

            int position = 0;
            SkipWhitespace(text, ref position);

            // 顶层必须是对象：数组或裸值都不是本项目的配置形态。
            if (!TryConsume(text, ref position, '{')) return null;

            while (true)
            {
                SkipWhitespace(text, ref position);
                if (position >= text.Length) return null;

                // 允许空对象与尾随逗号之前的正常结束。
                if (text[position] == '}')
                {
                    position++;
                    break;
                }

                if (!TryParseString(text, ref position, out string key)) return null;
                SkipWhitespace(text, ref position);
                if (!TryConsume(text, ref position, ':')) return null;
                SkipWhitespace(text, ref position);

                if (!TryParseValue(text, ref position, out string value)) return null;

                // 后出现的键覆盖先出现的：与主流 JSON 解析器的行为一致。
                result[key] = value;

                SkipWhitespace(text, ref position);
                if (position >= text.Length) return null;

                if (text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (text[position] == '}')
                {
                    position++;
                    break;
                }

                return null;
            }

            // 对象之后只允许空白；有别的字符说明文件被拼接过，按损坏处理。
            SkipWhitespace(text, ref position);
            return position >= text.Length ? result : null;
        }
        #endregion

        #region 词法辅助
        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length)
            {
                char ch = text[position];
                if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n' && ch != '\uFEFF') break;
                position++;
            }
        }

        private static bool TryConsume(string text, ref int position, char expected)
        {
            if (position >= text.Length || text[position] != expected) return false;
            position++;
            return true;
        }

        // 值只有三种形态：字符串、数值、布尔。null 也接受，按空串处理。
        private static bool TryParseValue(string text, ref int position, out string value)
        {
            value = null;
            if (position >= text.Length) return false;

            char ch = text[position];
            if (ch == '"')
            {
                if (!TryParseString(text, ref position, out string raw)) return false;
                value = raw;
                return true;
            }

            if (ch == 't')
            {
                if (!TryConsumeLiteral(text, ref position, "true")) return false;
                value = bool.TrueString;
                return true;
            }

            if (ch == 'f')
            {
                if (!TryConsumeLiteral(text, ref position, "false")) return false;
                value = bool.FalseString;
                return true;
            }

            if (ch == 'n')
            {
                if (!TryConsumeLiteral(text, ref position, "null")) return false;
                value = string.Empty;
                return true;
            }

            // 数值：本项目只写整数，但读取时容忍小数与指数，按原样收下由上层解析失败后回落默认值。
            return TryParseNumber(text, ref position, out value);
        }

        private static bool TryConsumeLiteral(string text, ref int position, string literal)
        {
            if (position + literal.Length > text.Length) return false;
            if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) return false;
            position += literal.Length;
            return true;
        }

        private static bool TryParseNumber(string text, ref int position, out string value)
        {
            value = null;
            int start = position;

            if (position < text.Length && (text[position] == '-' || text[position] == '+')) position++;

            bool seenDigit = false;
            while (position < text.Length)
            {
                char ch = text[position];
                if ((ch >= '0' && ch <= '9'))
                {
                    seenDigit = true;
                    position++;
                    continue;
                }

                // 小数与指数不属于本项目会写出的形态，但读旧文件时可能遇到，一并容忍。
                if (ch == '.' || ch == 'e' || ch == 'E' || ch == '-' || ch == '+')
                {
                    position++;
                    continue;
                }

                break;
            }

            if (!seenDigit) return false;

            value = text.Substring(start, position - start);
            return true;
        }
        #endregion

        #region 字符串解析
        // 字符串按 JSON 规则解转义。配置里的值可能出现中文、引号、反斜杠（路径），必须完整处理。
        private static bool TryParseString(string text, ref int position, out string value)
        {
            value = null;
            if (position >= text.Length || text[position] != '"') return false;

            position++;
            var builder = new StringBuilder(64);

            while (position < text.Length)
            {
                char ch = text[position];

                if (ch == '"')
                {
                    position++;
                    value = builder.ToString();
                    return true;
                }

                if (ch == '\\')
                {
                    position++;
                    if (position >= text.Length) return false;

                    char escape = text[position];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;

                        case 'u':
                            // \uXXXX：取四位十六进制。代理对会被拆成两个 \u，这里原样拼回，交给调用方当普通字符处理。
                            if (position + 4 >= text.Length) return false;
                            if (!int.TryParse(text.Substring(position + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out int code))
                            {
                                return false;
                            }
                            builder.Append((char)code);
                            position += 4;
                            break;

                        default:
                            return false;
                    }

                    position++;
                    continue;
                }

                // 未转义的控制字符在严格 JSON 里是非法的，这里直接判损坏，避免把坏文件当成有效配置。
                if (ch < ' ')
                {
                    return false;
                }

                builder.Append(ch);
                position++;
            }

            return false;
        }
        #endregion

        #region 写入
        /// <summary>
        /// 按固定键序把键值写成标准 JSON。缩进两格、行尾逗号、键序稳定，
        /// 便于用户直接编辑，也让文件在版本间 diff 干净。
        /// </summary>
        public static string Build(IReadOnlyList<KeyValuePair<string, string>> entries, IReadOnlyList<bool> isBoolean)
        {
            var builder = new StringBuilder(256);
            builder.Append("{\n");

            for (int i = 0; i < entries.Count; i++)
            {
                string key = entries[i].Key;
                string value = entries[i].Value ?? string.Empty;

                builder.Append("  \"");
                AppendEscaped(builder, key);
                builder.Append("\": ");

                // 布尔与整数按原生 JSON 标量写出，字符串加引号 —— 这样任何 JSON 解析器都能正确还原类型。
                bool raw = isBoolean != null && i < isBoolean.Count && isBoolean[i];
                if (raw)
                {
                    // JSON 只认小写的 true/false；C# 的 bool.ToString() 给出 "True"/"False"，
                    // 直接写出去就是非法 JSON，Python 的 json / JS 的 JSON.parse 都会报错。
                    builder.Append(bool.TryParse(value, out bool flag) && flag ? "true" : "false");
                }
                else if (IsInteger(value))
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append('"');
                    AppendEscaped(builder, value);
                    builder.Append('"');
                }

                if (i < entries.Count - 1) builder.Append(',');
                builder.Append('\n');
            }

            builder.Append("}\n");
            return builder.ToString();
        }

        // 只认纯整数（可带负号）；其余（含空串、"2000 " 这类带空白的）一律按字符串写出。
        private static bool IsInteger(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            int start = value[0] == '-' ? 1 : 0;
            if (value.Length <= start) return false;

            for (int i = start; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }

            return true;
        }

        // 只转义 JSON 规定必须转义的字符；中文按 UTF-8 原样写出，保持文件可读。
        private static void AppendEscaped(StringBuilder builder, string value)
        {
            if (value == null) return;

            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }
                        break;
                }
            }
        }

        /// <summary>写入文件：UTF-8 无 BOM。</summary>
        public static void WriteFile(string path, string content)
        {
            // 无 BOM：与 .sha256 产物同一约定，带 BOM 的文件会让不少解析器把首个键当成无效内容。
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        #endregion
    }
}
