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
        /// <summary>把路径发给已在运行的实例，对方接收成功返回 true。</summary>
        public static bool SendToRunningInstance(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;

            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    // 对方可能刚抢到互斥量、还没把监听建好，所以留 5 秒而不是立刻放弃。
                    pipe.Connect(5000);

                    // 路径里可能带空格、引号与中文，用换行分隔整体写出，既不拼命令行也不受参数个数限制。
                    byte[] data = Encoding.UTF8.GetBytes(string.Join(PathSeparator.ToString(), paths));
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
        /// <param name="onFiles">收到路径时触发。</param>
        public void StartListener(Action<string[]> onFiles)
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

        private void ListenLoop(Action<string[]> onFiles)
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
                            foreach (string line in text.Split(PathSeparator))
                            {
                                string trimmed = line.Trim();
                                if (trimmed.Length > 0) paths.Add(trimmed);
                            }

                            if (paths.Count > 0) onFiles(paths.ToArray());
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
