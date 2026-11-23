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

        /// <summary>
        /// 单实例监听线程的确定性观察点：在**通过停止标志检查、创建管道之前**被调用。
        ///
        /// 为什么需要它：BUG-04 的竞态窗口在「通过检查」与「发布管道」之间，
        /// 宽度是微秒级。压力测试（连续启动几十次、连接几十次）**撞不出来** ——
        /// 实测修复前后都通过，那种测试只能证明「没崩」，不能证明「窗口被关掉了」。
        /// 只有在这里开一个固定的缝，让测试把监听线程精确停在窗口中间，
        /// 再在此时调用 Dispose，才能区分两种实现：
        ///   · 有生命周期锁：Dispose 看到 `_running=false` 生效，监听线程随即退出；
        ///   · 没有锁（旧实现）：Dispose 读到 `_currentPipe` 为 null 后直接返回，
        ///     监听线程随后创建管道并永久阻塞在 WaitForConnection。
        ///
        /// 用「私有字段 + 显式读写方法」而不是公开可写字段：
        /// 公开字段从没被产品代码赋值，编译器会报 CS0649，把「0 警告」的交付标准打破；
        /// 而抑制警告又会把将来真的忘了赋值的字段一起藏起来。
        /// </summary>
        private static Action _beforePipeCreate;

        /// <summary>测试侧安装观察点；传 null 表示卸载。</summary>
        internal static void SetBeforePipeCreate(Action hook)
        {
            _beforePipeCreate = hook;
        }

        internal static void InvokeBeforePipeCreate()
        {
            Action hook = _beforePipeCreate;
            if (hook != null) hook();
        }

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
