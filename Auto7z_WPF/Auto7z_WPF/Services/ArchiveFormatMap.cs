using System;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 压缩格式与命令行文本之间的换算，以及按文件头识别压缩包类型。
    /// </summary>
    public static class ArchiveFormatMap
    {
        #region 格式换算
        public static string ToSwitchValue(ArchiveFormat format)
        {
            // 只有这三种能作为输出格式：-t 开关写死 7z/zip/tar 时就这么多，
            // gz/bz2/xz/zst/rar 之类要配置字典与附加参数，不在本程序的输出范围里。
            switch (format)
            {
                case ArchiveFormat.Zip: return "zip";
                case ArchiveFormat.Tar: return "tar";
                case ArchiveFormat.SevenZip:
                default: return "7z";
            }
        }

        public static string ToExtension(ArchiveFormat format)
        {
            return "." + ToSwitchValue(format);
        }

        /// <summary>把配置里读到的格式文本还原成枚举，无法识别返回 null。</summary>
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
        #endregion

        #region 魔数识别
        /// <summary>按文件头判断压缩包类型，无法识别返回 null。</summary>
        public static string DetectByMagic(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            try
            {
                // 共享读：文件可能正被别的程序占用，独占打开会在这里误判成「不是压缩包」。
                using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    // 取 262 字节是因为 tar 的特征串 ustar 落在偏移 257 处，
                    // 只读开头几个字节就会把它漏掉。
                    var header = new byte[262];
                    int read = fs.Read(header, 0, header.Length);

                    if (read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
                        return "7z";

                    if (read >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 && header[4] == 0x1A && header[5] == 0x07)
                        return "rar";

                    if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07))
                        return "zip";

                    if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B)
                        return "gz";

                    if (read >= 3 && header[0] == 0x42 && header[1] == 0x5A && header[2] == 0x68)
                        return "bz2";

                    if (read >= 5 && header[0] == 0xFD && header[1] == 0x37 && header[2] == 0x7A && header[3] == 0x58 && header[4] == 0x5A)
                        return "xz";

                    if (read >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
                        return "zst";

                    if (read >= 4 && header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x43 && header[3] == 0x46)
                        return "cab";

                    if (read >= 4 && header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x57 && header[3] == 0x49)
                        return "wim";

                    if (read >= 262 && header[257] == 0x75 && header[258] == 0x73 && header[259] == 0x74 && header[260] == 0x61 && header[261] == 0x72)
                        return "tar";

                    return null;
                }
            }
            catch
            {
                // 文件不存在、没有读权限、或读取途中被删，一律按无法识别处理，
                // 让调用方退回按扩展名判断，而不是把异常抛到任务流程里中断整个任务。
                return null;
            }
        }
        #endregion
    }
}
