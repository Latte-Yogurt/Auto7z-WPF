using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace KillWaitProbe
{
    /// <summary>
    /// 验证 SevenZipService.KillAllRunningProcesses() 是否真的「等待 7z 退出」
    /// 及其耗时是否有界。对应审查报告 P1-3。
    ///
    /// ## 两次踩坑记录，都是同一个根因
    ///
    /// 产品的进程跟踪表 `_runningProcesses` 是 **static 字段**，只活在
    /// 「加载了产品程序集的那个进程」里。
    ///
    ///   坑 1：用 PowerShell 另起进程调 KillAllRunningProcesses → 表是空的，
    ///         函数立刻返回，7z 一个没杀。
    ///   坑 2：在本探针里 Process.Start 一个 Auto7z.exe 子进程 → 7z 归**子进程**
    ///         的静态表管，本进程的表仍然是空的，结果完全一样。
    ///
    /// 正确的做法是在**本进程内**直接调用产品的 ExecuteCommand 把 7z 起起来，
    /// 这样 raise 起来的 7z 由本进程的静态表跟踪，再调清理才有意义。
    /// 这个探针现在走的就是这条路径。
    ///
    /// ## 退出码
    /// 0 = 全部通过；1 = 有失败；2 = 环境/接口问题。
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static string _exePath;

        private static void Record(string name, bool ok, string detail)
        {
            Console.WriteLine("  {0} {1,-46} {2}", ok ? "[通过]" : "[失败]", name, detail);
            if (!ok) _failures++;
        }

        private static int Main(string[] args)
        {
            _exePath = args.Length > 0 ? args[0] : null;
            if (_exePath == null || !File.Exists(_exePath))
            {
                Console.Error.WriteLine("用法: killwait.exe <Auto7z.exe 路径>");
                return 2;
            }

            Console.WriteLine(new string('=', 82));
            Console.WriteLine("关闭时等待 7z 退出（P1-3）");
            Console.WriteLine("被测程序: " + _exePath);
            Console.WriteLine(new string('=', 82));

            var asm = Assembly.LoadFrom(_exePath);
            var svcType = asm.GetType("Auto7z_WPF.Services.SevenZipService");
            if (svcType == null)
            {
                Console.Error.WriteLine("找不到 SevenZipService");
                return 2;
            }

            var killAll = svcType.GetMethod("KillAllRunningProcesses",
                BindingFlags.Public | BindingFlags.Static);
            if (killAll == null)
            {
                Console.Error.WriteLine("找不到 KillAllRunningProcesses");
                return 2;
            }

            // ---------- 一、空闲路径 ----------
            Console.WriteLine();
            Console.WriteLine("=== 一、空闲时调用 ===");
            int before = Count7z();
            var sw = Stopwatch.StartNew();
            killAll.Invoke(null, null);
            sw.Stop();
            Record("空闲时清理立即返回", sw.ElapsedMilliseconds < 3000, sw.ElapsedMilliseconds + " ms");
            Record("空闲时清理不产生副作用", Count7z() <= before,
                "7z 数量 " + before + " -> " + Count7z());

            // ---------- 二、有 7z 运行时 ----------
            Console.WriteLine();
            Console.WriteLine("=== 二、7z 运行中调用清理 ===");

            string tempRoot = Path.Combine(Path.GetTempPath(),
                "Auto7zTest_killwait_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string src = Path.Combine(tempRoot, "src");
                string data = Path.Combine(tempRoot, "data");
                string outDir = Path.Combine(tempRoot, "out");
                Directory.CreateDirectory(outDir);
                MakeCorpus(src, 1200, 65536);

                // 产品的数据目录要在压缩前设好，7z.exe 会释放到那里
                Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", data);

                var svc = Activator.CreateInstance(svcType);

                // 必须先把 7z.exe / 7z.dll 释放到数据目录，否则 ExecuteCommand
                // 起来后会因为找不到 exe 立刻失败，我们根本观察不到 7z 进程。
                var ensure = svcType.GetMethod("EnsureComponents");
                object[] ensureArgs = { null };
                bool componentsReady = false;
                if (ensure != null)
                {
                    try { componentsReady = (bool)ensure.Invoke(svc, ensureArgs); }
                    catch (Exception) { componentsReady = false; }
                }
                Record("引擎组件已就位", componentsReady,
                    componentsReady ? "7z.exe / 7z.dll 已释放"
                                    : "EnsureComponents 失败: " + Describe(ensureArgs));

                var exec = svcType.GetMethod("ExecuteCommand");

                string archive = Path.Combine(outDir, "big.7z");
                string arguments = "a -aoa -y -t7z \"" + archive + "\" \"" + src + "\"";

                // 在后台线程上跑压缩，主线程等 7z 出现后调清理
                Exception execError = null;
                var done = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {
                    try
                    {
                        exec.Invoke(svc, new object[]
                        {
                            arguments,
                            (Action<int>)null,
                            CancellationToken.None,
                            (TimeSpan?)null
                        });
                    }
                    catch (Exception ex)
                    {
                        execError = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                thread.IsBackground = true;

                int baseline = Count7z();
                thread.Start();

                // 用「跟踪表」而不是「系统进程表」判断可以下手了。
                // 为什么不用 Count7z：7z 从启动到被 TrackProcess 之间有个窗口，
                // 而任务收尾时 UntrackProcess 又发生在进程真正退出之前。
                // 只看系统进程表会在那个窗口里抢跑 —— 等我们调用清理时，
                // 跟踪表可能已经空了（实测踩到过，表现为「跟踪表为空」）。
                // 跟踪表非空才是「此刻调用清理有意义」的准确信号。
                bool saw = false;
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    if (CountTracked(svcType) > 0) { saw = true; break; }
                    if (done.IsSet) break;
                    Thread.Sleep(5);
                }

                if (!saw)
                {
                    // 没观察到就说明这条路径**没有测到**。
                    // 早先我写成「记为通过」，等于又造了一个假绿 ——
                    // 与 P1-2 里批评的「跳过算通过」是同一个错误。
                    Record("7z 运行中调用清理", false,
                        done.IsSet ? "任务在观察前已结束（语料过快），无法测到等待行为"
                                   : "90 秒内跟踪表始终为空，无法测到等待行为");
                }
                else
                {
                    Record("ExecuteCommand 确实起了 7z 子进程", true, "跟踪表非空");

                    // 先确认**跟踪表里真的有进程**。
                    int tracked = CountTracked(svcType);
                    Record("调用前跟踪表中有该 7z", tracked > 0,
                        tracked > 0 ? "跟踪 " + tracked + " 个"
                                    : "跟踪表为空 —— 本次未真正验证等待行为");

                    // 关键时序：必须在 7z 仍被跟踪时调用清理。
                    sw.Restart();
                    killAll.Invoke(null, null);
                    sw.Stop();

                    int trackedAfter = CountTracked(svcType);
                    Record("清理后跟踪表已清空", trackedAfter == 0,
                        "剩余 " + trackedAfter + " 个");

                    // 清理返回后，被跟踪的 7z 必须已经不在系统里。
                    // 给一个极短的余量（50ms）容纳操作系统回收进程表条目的延迟 ——
                    // 这不是放宽断言，KillAll 内部的 WaitForExit 已经等过了，
                    // 这里只是避免与计数本身的瞬时窗口较劲。
                    int after = Count7z();
                    if (after > baseline)
                    {
                        Thread.Sleep(50);
                        after = Count7z();
                    }
                    Record("清理返回后无残留 7z", after <= baseline,
                        after <= baseline ? "无残留" : "仍有 " + (after - baseline) + " 个");
                    Record("清理耗时有界（<=5s）", sw.ElapsedMilliseconds <= 5000,
                        sw.ElapsedMilliseconds + " ms");

                    Console.WriteLine("         （等待耗时 {0} ms，供参考）", sw.ElapsedMilliseconds);
                }

                done.Wait(60000);
                if (execError != null)
                {
                    Console.WriteLine("         注: ExecuteCommand 返回异常（预期，进程被杀）："
                        + execError.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                Record("7z 运行中调用清理", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                foreach (var p in Process.GetProcessesByName("7z"))
                {
                    try { p.Kill(); p.WaitForExit(5000); p.Dispose(); } catch (Exception) { }
                }
                try { Directory.Delete(tempRoot, true); } catch (Exception) { }
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 82));
            Console.WriteLine("结果: " + (_failures == 0 ? "全部通过" : _failures + " 项失败"));
            Console.WriteLine(new string('=', 82));
            return _failures == 0 ? 0 : 1;
        }

        private static int Count7z()
        {
            try { return Process.GetProcessesByName("7z").Length; }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// 读产品私有的 static 跟踪表 _runningProcesses 的条目数。
        ///
        /// 为什么必须读它：只看「系统里还有没有 7z.exe」无法区分
        ///   (a) 清理真的杀了并等到了退出，与
        ///   (b) 跟踪表本来就是空的、清理什么都没做。
        /// 两者在文件系统/进程表上的最终状态可能相同，但结论完全相反。
        /// </summary>
        private static int CountTracked(Type svcType)
        {
            try
            {
                var f = svcType.GetField("_runningProcesses",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null) return -1;
                var coll = f.GetValue(null) as System.Collections.ICollection;
                return coll == null ? -1 : coll.Count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string Describe(object[] args)
        {
            if (args == null || args.Length == 0 || args[0] == null) return "(无错误信息)";
            var ex = args[0] as Exception;
            return ex == null ? args[0].ToString() : ex.GetType().Name + ": " + ex.Message;
        }

        private static void MakeCorpus(string dir, int count, int size)
        {
            Directory.CreateDirectory(dir);
            var rng = new Random(12345);
            for (int i = 0; i < count; i++)
            {
                string sub = Path.Combine(dir, "d" + (i % 20).ToString("00"));
                Directory.CreateDirectory(sub);
                var buf = new byte[size];
                rng.NextBytes(buf);
                File.WriteAllBytes(Path.Combine(sub, "f" + i.ToString("0000") + ".bin"), buf);
            }
        }
    }
}
