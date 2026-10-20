using System;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 归档格式与 7-Zip 命令行参数之间的映射，集中处理大小写与 -t 开关。
    /// </summary>
    public static class ArchiveFormatMap
    {
        /// <summary>7-Zip 的 -t 开关值。</summary>
        public static string ToSwitchValue(ArchiveFormat format)
        {
            switch (format)
            {
                case ArchiveFormat.Zip: return "zip";
                case ArchiveFormat.Tar: return "tar";
                case ArchiveFormat.SevenZip:
                default: return "7z";
            }
        }

        /// <summary>归档文件扩展名（含点）。</summary>
        public static string ToExtension(ArchiveFormat format)
        {
            return "." + ToSwitchValue(format);
        }

        /// <summary>解析配置或用户输入中的格式文本，无法识别时返回 null。</summary>
        public static ArchiveFormat? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            switch (text.Trim().ToLowerInvariant())
            {
                case "7z": return ArchiveFormat.SevenZip;
                case "zip": return ArchiveFormat.Zip;
                case "tar": return ArchiveFormat.Tar;
                default: return null;
            }
        }

        /// <summary>
        /// 根据文件头魔数识别压缩格式（识别范围大于可压缩范围，用于自动模式判断）。
        /// </summary>
        public static string DetectByMagic(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            try
            {
                using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    var header = new byte[262];
                    int read = fs.Read(header, 0, header.Length);

                    // 7z: 37 7A BC AF 27 1C
                    if (read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
                        return "7z";

                    // rar: 52 61 72 21 1A 07（RAR4 / RAR5 共用前 7 字节）
                    if (read >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 && header[4] == 0x1A && header[5] == 0x07)
                        return "rar";

                    // zip: 50 4B 03 04 / 50 4B 05 06 / 50 4B 07 08
                    if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07))
                        return "zip";

                    // gzip: 1F 8B
                    if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B)
                        return "gz";

                    // bzip2: 42 5A 68
                    if (read >= 3 && header[0] == 0x42 && header[1] == 0x5A && header[2] == 0x68)
                        return "bz2";

                    // xz: FD 37 7A 58 5A
                    if (read >= 5 && header[0] == 0xFD && header[1] == 0x37 && header[2] == 0x7A && header[3] == 0x58 && header[4] == 0x5A)
                        return "xz";

                    // zstd: 28 B5 2F FD
                    if (read >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
                        return "zst";

                    // cab: 4D 53 43 46
                    if (read >= 4 && header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x43 && header[3] == 0x46)
                        return "cab";

                    // wim: 4D 53 57 49
                    if (read >= 4 && header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x57 && header[3] == 0x49)
                        return "wim";

                    // tar: 偏移 257 处为 "ustar"
                    if (read >= 262 && header[257] == 0x75 && header[258] == 0x73 && header[259] == 0x74 && header[260] == 0x61 && header[261] == 0x72)
                        return "tar";

                    return null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
