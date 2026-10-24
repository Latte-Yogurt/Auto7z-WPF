#if DEBUG
using System;
using System.Collections.Generic;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 测试注入入口（仅 Debug 构建）。
    ///
    /// 为什么需要它
    /// ------------
    /// 「把文件拖到主窗口」这条路径由 Explorer 发起 OLE 拖放，自动化工具既发不出
    /// 合成拖拽（主窗口是 AllowsTransparency 的透明窗），也没法跨进程调用窗口上的
    /// IDropTarget（那个指针只在目标进程里有效）。所以留一个接口级入口：
    /// 直接把路径交给正在运行的主窗口，走的是与投放**完全相同**的 EnqueueFiles 路径。
    ///
    /// 为什么用条件编译
    /// ----------------
    /// 这是测试设施，不是产品功能。整个类与调用点都包在 #if DEBUG 里，
    /// Release 构建里不会编译进去，最终产物中不存在 --feed 这个开关。
    /// </summary>
    internal static class TestHooks
    {
        /// <summary>测试注入开关。</summary>
        public const string FeedSwitch = "--feed";

        /// <summary>
        /// 解析 "--feed path1 path2 ..."。
        /// </summary>
        /// <param name="args">命令行参数。</param>
        /// <param name="paths">注入的路径列表。</param>
        /// <returns>是否请求了注入。</returns>
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
                if (requested) collected.Add(arg);
            }

            if (!requested) return false;
            paths = collected.ToArray();
            return true;
        }
    }
}
#endif
