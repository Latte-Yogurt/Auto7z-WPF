using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 单实例控制：第一个进程占住互斥量与命名管道，后来的进程把路径转交给它。
    /// </summary>
    public class SingleInstanceService
    {
        #region 通道命名
        // 两个可执行体共用同一互斥名与管道名：它们读写同一份配置、处理同一批任务，
        // 只要有一个已经在跑，另一个就该把路径交出去而不是另起一份。
        private const string MutexName = "Local\\Auto7z_WPF_SingleInstance";

        private const string PipeName = "Auto7z_WPF_InstancePipe";

        private const char PathSeparator = '\n';

        private Mutex _mutex;
        private volatile Thread _listener;
        private volatile bool _running;

        /// <summary>
        /// 监听线程的**生命周期锁**。管道创建、当前管道发布、停止标志、当前管道关闭
        /// 这四件事全部在这把锁里完成 —— 缺了它就会出现 BUG-04 的窗口：
        ///
        ///   线程 B：通过 `while (_running)` 检查 → 被抢占
        ///   线程 A：`_running = false`；读 `_currentPipe` 得 **null**；Join 等待
        ///   线程 B：恢复 → 新建管道 → 发布 → 阻塞在 `WaitForConnection()`
        ///
        /// 结果：没人关这个新建的管道，线程永久阻塞，Join 超时后互斥量照样被释放，
        /// 新旧两个实例就同时监听同名管道了。
        ///
        /// 加了锁之后，线程 B 在发布管道时必须重新取锁并复查 `_running`，
        /// 此时 `_running` 已为 false，它关掉刚建的管道直接退出，窗口消失。
        /// </summary>
        private readonly object _lifecycleLock = new object();

        /// <summary>
        /// 监听线程当前阻塞在其上的服务端管道。`Dispose()` 靠关闭它来打断
        /// `WaitForConnection()` —— 设 `_running = false` 唤不醒一个阻塞调用。
        /// **只在 `_lifecycleLock` 内读写。**
        /// </summary>
        private NamedPipeServerStream _currentPipe;

        /// <summary>
        /// Dispose 里等待监听线程退出的上限。
        ///
        /// 正常路径上，关掉 `_currentPipe` 之后线程会在几毫秒内退出；
        /// 这个超时只是兜底，防止无法打断的极端情形把关闭流程卡死。
        /// 早先这里是 1000ms 且**没有**主动打断，等 1 秒纯属白等。
        /// </summary>
        private const int ListenerJoinTimeoutMs = 2000;

        /// <summary>
        /// 第一次 Join 超时后的第二次机会。
        ///
        /// 走到这里说明监听线程在「通过检查」与「发布管道」之间被卡住了 ——
        /// 生命周期锁下这条路已经走不通（发布前必然复查停止标志），
        /// 但真出现时不能直接放行：线程还活着就释放互斥量，
        /// 等于让新实例和一个还在占着管道名的旧线程并存。
        /// 再关一次当前管道、再等一次，然后才放手。
        /// </summary>
        private const int SecondJoinTimeoutMs = 1000;
        #endregion

        #region 路径交接
        // 交接里必须带上模式开关，否则「右键解压」会变成「压缩」。
        //
        // 这是真实缺陷：外壳扩展以 -uz 启动第二个进程，第二个进程发现已有实例在跑，
        // 就把路径交给它然后自己退出。若交接只传路径，-uz 就在这一步丢了 ——
        // 接收方按自己的界面模式处理，默认或压缩模式下就成了压缩。
        // 症状极具误导性：菜单发的是 -uz、日志显示进程正常、产物却是压缩包。
        private const string ModeMarker = "AUTO7Z_MODE=";

        // 一次性把「模式 + 换行分隔的路径」写出去。头部放模式标记，其后是路径；
        // 新接收方认得标记，老接收方（没有标记）按「未指定模式」处理。
        private static string BuildPayload(string[] paths, string mode)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(mode))
            {
                sb.Append(ModeMarker).Append(mode).Append(PathSeparator);
            }

            sb.Append(string.Join(PathSeparator.ToString(), paths));
            return sb.ToString();
        }

        /// <summary>把路径发给已在运行的实例，对方接收成功返回 true。</summary>
        /// <param name="paths">要处理的路径。</param>
        /// <param name="mode">模式开关（-a / -z / -uz），null 表示沿用对方当前模式。</param>
        public static bool SendToRunningInstance(string[] paths, string mode = null)
        {
            if (paths == null || paths.Length == 0) return false;

            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    // 对方可能刚抢到互斥量、还没把监听建好，所以留 5 秒而不是立刻放弃。
                    pipe.Connect(5000);

                    // 路径里可能带空格、引号与中文，用换行分隔整体写出，既不拼命令行也不受参数个数限制。
                    byte[] data = Encoding.UTF8.GetBytes(BuildPayload(paths, mode));
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        #endregion

        #region 互斥量与监听
        /// <summary>尝试成为唯一实例，抢到互斥量返回 true。</summary>
        public bool TryAcquire()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // 没抢到就必须把刚建出来的句柄放掉，否则本进程会握着一个不属于它的互斥量。
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }

        /// <summary>启动后台监听线程，收到一批路径与模式就回调。</summary>
        /// <param name="onFiles">收到时触发，第二参数为模式开关，可能为 null。</param>
        public void StartListener(Action<string[], string> onFiles)
        {
            if (onFiles == null) return;

            // 置位也必须走生命周期锁：否则「Dispose 在 StartListener 之前到达」会让
            // 监听线程带着一个已复位的标志启动，Dispose 等不到它，互斥量却已释放。
            lock (_lifecycleLock)
            {
                _running = true;
            }

            // 后台线程：主程序退出时不该被它拖住。
            var listener = new Thread(() => ListenLoop(onFiles))
            {
                IsBackground = true,
                Name = "Auto7z 单实例监听"
            };
            _listener = listener;
            listener.Start();
        }

        private void ListenLoop(Action<string[], string> onFiles)
        {
            while (_running)
            {
                NamedPipeServerStream pipe = null;
                try
                {
#if DEBUG
                    // 确定性测试挂点：让测试把线程精确定位在「通过检查之后、创建管道之前」。
                    // 只有在那里调用 Dispose，才能区分「有生命周期锁」与「没有」。
                    TestHooks.InvokeBeforePipeCreate();
#endif
                    // 每轮都新建一个服务端管道：一个实例只服务一次连接，
                    // 复用同一个会让第二个客户端永远等不到接应。
                    pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    bool stopRequested;
                    lock (_lifecycleLock)
                    {
                        // **发布管道必须与复查停止标志在同一把锁里。**
                        //
                        // 早先是裸赋值 `_currentPipe = pipe;`，没有复查 ——
                        // 于是 Dispose 可能在「赋值」之前读到 null 并放走线程，
                        // 线程随后带着一个没人关的管道阻塞到永远（BUG-04）。
                        //
                        // 现在：Dispose 也要取这把锁才能设 `_running=false` 并读 `_currentPipe`，
                        // 因此两者必然形成先后 —— 要么线程先发布（Dispose 能看到并关掉它），
                        // 要么 Dispose 先完成（线程在这里看到 `_running=false` 并自己关掉）。
                        // 没有第三种可能。
                        if (!_running)
                        {
                            stopRequested = true;
                        }
                        else
                        {
                            _currentPipe = pipe;
                            stopRequested = false;
                        }
                    }

                    if (stopRequested)
                    {
                        // 已经不需要监听了：把刚建的管道关掉再退出，别留着占管道名。
                        try { pipe.Dispose(); } catch { /* 有意忽略：刚建出来的管道 */ }
                        return;
                    }

                    // 把当前管道登记下来，让 Dispose 能主动打断等待。
                    //
                    // **不能只靠 `_running` 标志。** `WaitForConnection()` 是阻塞调用，
                    // 设标志不会唤醒它；早先 `Dispose()` 只写 `_running = false` 再 `Join(1000)`，
                    // 1 秒后放弃 —— 线程仍挂在 WaitForConnection 上。
                    // 它是后台线程、不会阻止进程退出，但会在关闭期间继续占着管道名，
                    // 于是「关闭后立即重启」的新实例可能抢不到管道。
                    pipe.WaitForConnection();

                    // 唤醒后立刻复查：可能是 Dispose 关掉管道才让我们出来的。
                    if (!_running) return;

                    using (var reader = new StreamReader(pipe, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        var paths = new List<string>();
                        string mode = null;

                        foreach (string line in text.Split(PathSeparator))
                        {
                            string trimmed = line.Trim();

                            // 头部可能带模式标记：认出来就取走，不当路径。
                            if (trimmed.StartsWith(ModeMarker, StringComparison.OrdinalIgnoreCase))
                            {
                                string value = trimmed.Substring(ModeMarker.Length).Trim();
                                if (value.Length > 0) mode = value;
                                continue;
                            }

                            if (trimmed.Length > 0) paths.Add(trimmed);
                        }

                        if (paths.Count > 0) onFiles(paths.ToArray(), mode);
                    }
                }
                catch (Exception)
                {
                    // 管道被占用或连接中途断掉都会走到这里，稍等再重建，免得在异常状态下空转把 CPU 打满。
                    // Dispose 关闭管道也会走到这里 —— 下面的 _running 复查会直接退出循环。
                    if (!_running) return;
                    try { Thread.Sleep(200); } catch { /* 有意忽略：重试间隔，线程中止无需处理 */ }
                }
                finally
                {
                    lock (_lifecycleLock)
                    {
                        // 只清「确实是自己」的那一条：Dispose 可能已经把 _currentPipe 关掉，
                        // 而下一轮尚未发布新管道，此时别把 null 之外的别人写回去。
                        if (ReferenceEquals(_currentPipe, pipe)) _currentPipe = null;
                    }
                    try { if (pipe != null) pipe.Dispose(); } catch { /* 有意忽略：管道可能已被 Dispose 关闭 */ }
                }
            }
        }

        /// <summary>
        /// 停止监听并释放互斥量。
        ///
        /// **必须主动打断 WaitForConnection。** 三种做法里只有「关掉管道」是可靠的：
        ///   · 设 `_running = false`：阻塞中的 WaitForConnection 不会醒，线程留在原地；
        ///   · `CancellationToken`：NamedPipeServerStream 的同步 WaitForConnection 不接受令牌；
        ///   · 关掉当前管道实例：等待立刻以 ObjectDisposedException 返回，线程随即退出。
        /// 关闭顺序也重要 —— 先关管道让线程退出，再释放互斥量。反过来的话，
        /// 新实例可能在旧监听线程还没退出时抢到互斥量，两边同时监听同名管道。
        /// </summary>
        public void Dispose()
        {
            NamedPipeServerStream pipe;

            // **停止标志与管道落空必须原子。**
            //
            // 早先是两句裸写：`_running = false;` 然后 `pipe = _currentPipe;`。
            // 中间那个空档就是 BUG-04：监听线程此时正好通过 while 检查，
            // 随后发布一个 Dispose 看不见的新管道，永久阻塞在 WaitForConnection。
            //
            // 现在两句都在 `_lifecycleLock` 内，而监听线程发布管道也取同一把锁并复查
            // `_running` —— 于是两者只能一先一后，不存在「标志设了但管道还没发布」的交错。
            lock (_lifecycleLock)
            {
                _running = false;

                // 主动打断：关掉监听线程当前阻塞的那个管道。
                pipe = _currentPipe;
                _currentPipe = null;
            }

            if (pipe != null)
            {
                try { pipe.Dispose(); } catch { /* 有意忽略：线程可能刚好自己退出，管道已被释放 */ }
            }

            // 现在 Join 才有意义：线程会因为上面那次关闭而退出。
            // 仍然保留超时兜底 —— 万一遇到无法打断的情形，不能让关闭流程卡死。
            bool exited = JoinListener(ListenerJoinTimeoutMs);
            if (!exited)
            {
                // 兜底还没死：线程可能是在「取锁之前」被抢占的，等它拿到锁时会看到
                // `_running == false` 并自行关闭刚建的管道。再补一次打断 + 等待。
                InterruptCurrentPipe();
                exited = JoinListener(SecondJoinTimeoutMs);
            }

            // **监听线程确实退出了才释放互斥量。**
            // 早先 Join 超时后照样释放 —— 旧线程仍在占管道名，新实例随即抢到互斥量，
            // 两个实例同时监听，交接必然错乱。现在超时就根本不释放：本进程没有可用的
            // 单实例权，让它安静退出，比制造「两个实例都以为自己唯一」的假象安全。
            if (!exited) return;

            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception)
            {
                // 没拿到过互斥体时释放会失败，忽略
            }
        }

        /// <summary>等待监听线程退出；没有线程（从未启动）视为已退出。</summary>
        /// <param name="timeoutMs">等待上限。</param>
        /// <returns>线程已退出返回 true。</returns>
        private bool JoinListener(int timeoutMs)
        {
            Thread listener = _listener;
            if (listener == null) return true;

            try
            {
                return listener.Join(timeoutMs);
            }
            catch
            {
                // 线程已退出或从未启动。
                return true;
            }
        }

        /// <summary>再关一次当前管道，用于 Join 超时后的兜底打断。</summary>
        private void InterruptCurrentPipe()
        {
            NamedPipeServerStream pipe;
            lock (_lifecycleLock)
            {
                pipe = _currentPipe;
                _currentPipe = null;
            }

            if (pipe != null)
            {
                try { pipe.Dispose(); } catch { /* 有意忽略：可能已被线程自己释放 */ }
            }
        }
        #endregion
    }
}
