using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 单实例控制。
    ///
    /// 第二次启动时把要处理的文件交给已经在跑的那个实例，自己直接退出。
    /// 已经在跑的实例收到通知后把文件排进任务队列，一个一个来。
    ///
    /// 交接走的是命名管道，不落盘：
    /// 后来的实例把路径写进管道，第一个实例读完就接着处理。
    /// </summary>
    public class SingleInstanceService
    {
        /// <summary>命名互斥体的名字。带上用户标识，多个用户各开各的。</summary>
        private const string MutexName = "Local\\Auto7z_WPF_SingleInstance";

        /// <summary>交接用的管道名。</summary>
        private const string PipeName = "Auto7z_WPF_InstancePipe";

        /// <summary>管道里路径之间的分隔符。</summary>
        private const char PathSeparator = '\n';

        private Mutex _mutex;
        private Thread _listener;
        private volatile bool _running;

        /// <summary>
        /// 已有实例在跑时，用它把文件交过去。
        /// </summary>
        /// <param name="paths">要交给已有实例的文件路径。</param>
        /// <returns>交给成功返回 true。</returns>
        public static bool SendToRunningInstance(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;

            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    // 等第一个实例把管道建起来
                    pipe.Connect(5000);

                    byte[] data = Encoding.UTF8.GetBytes(string.Join(PathSeparator.ToString(), paths));
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                // 对方还没准备好就交不上，只能自己接着做
                return false;
            }
        }

        /// <summary>
        /// 尝试成为唯一实例。
        /// </summary>
        /// <returns>true 表示这是第一个实例，false 表示已经有实例在跑。</returns>
        public bool TryAcquire()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 开始监听其他实例送来的文件。
        /// </summary>
        /// <param name="onFiles">收到文件时的回调。</param>
        public void StartListener(Action<string[]> onFiles)
        {
            if (onFiles == null) return;

            _running = true;

            _listener = new Thread(() => ListenLoop(onFiles))
            {
                IsBackground = true,
                Name = "Auto7z 单实例监听"
            };
            _listener.Start();
        }

        /// <summary>
        /// 一直守着管道，有人送来文件就交给上层。
        /// </summary>
        private void ListenLoop(Action<string[]> onFiles)
        {
            while (_running)
            {
                try
                {
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
                    // 管道断了就重新建一个接着等
                    try { Thread.Sleep(200); } catch { }
                }
            }
        }

        /// <summary>释放占用的资源。</summary>
        public void Dispose()
        {
            _running = false;

            try { _listener?.Join(1000); } catch { }

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
    }
}
