using System.Text;

namespace TCPStudio.Services
{
    /// <summary>
    /// 数据编解码：hex / ascii / utf8 / base64 与字节数组互转。
    /// 存储无损（原始字节 Base64），展示文本按当时编码解码。
    /// </summary>
    public static class PayloadCodec
    {
        /// <summary>文本 -> 待发送字节。格式错误抛 FormatException。</summary>
        public static byte[] Encode(string text, string encoding, string? newLine)
        {
            text ??= "";
            byte[] data = (encoding ?? "hex").ToLowerInvariant() switch
            {
                "hex" => FromHex(text),
                "base64" => Convert.FromBase64String(text.Trim()),
                "ascii" => Encoding.ASCII.GetBytes(WithLine(text, newLine)),
                _ => Encoding.UTF8.GetBytes(WithLine(text, newLine)),
            };
            // hex/base64 等按字节追加行尾
            if (string.Equals(newLine, "crlf", StringComparison.OrdinalIgnoreCase))
            {
                data = [.. data, 0x0D, 0x0A];
            }
            return data;
        }

        /// <summary>字节 -> 展示文本（hex 为空格分隔大写）。</summary>
        public static string DecodeText(byte[] data, string encoding)
        {
            return (encoding ?? "hex").ToLowerInvariant() switch
            {
                "hex" => ToHexDisplay(data),
                "base64" => Convert.ToBase64String(data),
                "ascii" => Encoding.ASCII.GetString(data),
                _ => Encoding.UTF8.GetString(data),
            };
        }

        public static string ToHexDisplay(byte[] data)
            => string.Join(' ', data.Select(b => b.ToString("X2")));

        /// <summary>解析 hex：自动剔除空格/逗号/换行/0x 前缀，长度必须为偶数。</summary>
        public static byte[] FromHex(string hex)
        {
            var cleaned = new string(hex.Where(char.IsAsciiHexDigit).ToArray());
            if (cleaned.Length % 2 != 0)
            {
                throw new FormatException("HEX 数据长度必须为偶数");
            }
            if (cleaned.Length == 0)
            {
                return [];
            }
            var bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        private static string WithLine(string text, string? newLine)
        {
            return string.Equals(newLine, "crlf", StringComparison.OrdinalIgnoreCase)
                ? text + "\r\n"
                : text;
        }
    }
}
