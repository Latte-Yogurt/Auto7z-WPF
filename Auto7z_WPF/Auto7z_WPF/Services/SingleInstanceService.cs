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
        private Thread _listener;
        private volatile bool _running;
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

        /// <summary>启动后台监听线程，收到一批路径就回调。</summary>
        /// <param name="onFiles">收到路径时触发（参数为路径数组）。</param>
        public void StartListener(Action<string[]> onFiles)
        {
            if (onFiles == null) return;
            // 老签名忽略模式：把收到的模式丢掉，行为与之前一致。
            StartListener((paths, mode) => onFiles(paths));
        }

        /// <summary>启动后台监听线程，收到一批路径与模式就回调。</summary>
        /// <param name="onFiles">收到时触发，第二参数为模式开关，可能为 null。</param>
        public void StartListener(Action<string[], string> onFiles)
        {
            if (onFiles == null) return;

            _running = true;

            // 后台线程：主程序退出时不该被它拖住。
            _listener = new Thread(() => ListenLoop(onFiles))
            {
                IsBackground = true,
                Name = "Auto7z 单实例监听"
            };
            _listener.Start();
        }

        private void ListenLoop(Action<string[], string> onFiles)
        {
            while (_running)
            {
                try
                {
                    // 每轮都新建一个服务端管道：一个实例只服务一次连接，
                    // 复用同一个会让第二个客户端永远等不到接应。
                    using (var pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        pipe.WaitForConnection();
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
                }
                catch (Exception)
                {
                    // 管道被占用或连接中途断掉都会走到这里，稍等再重建，免得在异常状态下空转把 CPU 打满。
                    try { Thread.Sleep(200); } catch { /* 有意忽略：重试间隔，线程中止无需处理 */ }
                }
            }
        }

        /// <summary>停止监听并释放互斥量。</summary>
        public void Dispose()
        {
            _running = false;

            try { _listener?.Join(1000); } catch { /* 有意忽略：监听线程已退出或从未启动，等不到也无妨 */ }

            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
            catch (Exception)
            {
                // 没拿到过互斥体时释放会失败，忽略
            }
        }
        #endregion
    }
}
