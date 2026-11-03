#if DEBUG
using System;
using System.Collections.Generic;

namespace Auto7z_WPF.Services
{
    // 整文件包在 #if DEBUG 里：Release 交付物里这个类型根本不存在，
    // 命令行也就不认识 --feed。测试侧反射不到它属于正确行为，不是回归。
    internal static class TestHooks
    {
        public const string FeedSwitch = "--feed";

        public static bool TryParseFeed(string[] args, out string[] paths)
        {
            paths = null;
            if (args == null || args.Length == 0) return false;

            var collected = new List<string>();
            bool requested = false;
            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (string.Equals(arg, FeedSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    requested = true;
                    continue;
                }
                // 只收 --feed 之后的参数：前面的仍归命令行解析，
                // 这样同一次启动不会因为测试开关而改变正常参数的语义。
                if (requested) collected.Add(arg);
            }

            if (!requested) return false;
            paths = collected.ToArray();
            return true;
        }
    }
}
#endif
