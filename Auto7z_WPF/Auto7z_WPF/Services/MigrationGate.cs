using System;
using System.Diagnostics;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 数据目录搬迁期间的**读写隔离闸门**。
    ///
    /// ## 它要解决的问题
    ///
    /// 搬迁的语义是「把一整棵数据目录挪到另一处」，而日志、配置、任务状态
    /// 三个写入方随时可能在搬的过程中往**源目录**里写东西：
    ///
    ///   1. 迁移开始复制 `Auto7z_Issue_2026-11-20.log`
    ///   2. 后台线程正好写日志，追加了一行
    ///   3. 复制结束，源文件被删除
    ///   4. 目标文件只包含复制时读到的那部分 —— **第 2 步写的那行永久消失了**
    ///
    /// 配置与任务状态更糟：它们写的是「当前状态」，丢掉一次更新可能让
    /// TaskState 停在 active、或让用户刚改的压缩级别回到旧值。
    ///
    /// ## 为什么「先查布尔再写」不够（BUG-01）
    ///
    /// 第一版闸门是一个布尔标志：写入方 `if (MigrationGate.IsClosed) return;`，
    /// 搬迁方 `Enter()` 把它置真。看起来严谨，实际**没有建立任何先后关系**：
    ///
    ///     线程 A：检查 IsClosed → false
    ///     线程 A：取 _writeLock
    ///     线程 A：再检查 IsClosed → false
    ///     线程 A：**被挂起**（还没打开文件）
    ///     线程 B：Enter() → 关闭闸门 → 开始复制 → 复制完 → 删除源文件
    ///     线程 A：恢复 → 打开**已被删除的**源文件写入
    ///
    /// 那一行写进了一个「已经被搬走、正在被删掉」的 inode。
    /// 问题的本质是**检查与动手之间不是原子的**，加多少次检查都只是把窗口变窄。
    ///
    /// ## 现在的做法：读写锁
    ///
    ///   · 普通写入：取**读锁**，持有到文件写完关掉为止，然后释放
    ///   · 数据搬迁：取**写锁**，等所有已开始的写入结束才拿到
    ///
    /// 于是先后关系是真的被建立起来了，而不是「大概率没撞上」：
    ///
    ///     所有已开始的写入完成 ──► 搬迁取得独占 ──► 复制 / 提交 / 删除 ──► 释放 ──► 恢复写入
    ///
    /// 写锁只在「所有读锁持有者都退出」后才授予，这是 ReaderWriterLockSlim 的语义，
    /// 不是我们靠时间差凑出来的。
    ///
    /// ## 为什么写入方还会「放弃」而不是无限等
    ///
    /// 搬迁可能持续数秒。若写入方无限等待，一个后台日志线程就能把界面卡住数秒
    /// （配置保存、状态写盘都在 UI 线程上）。而这些写入都是「下一次还会再写」的类型，
    /// 丢掉一次不影响最终状态：配置有 AutoSave，日志本来是尽力而为，
    /// 搬迁期间本来就不该有任务在跑。
    ///
    /// 所以 <see cref="TryEnterWrite"/> 只等 <see cref="WriterWaitMilliseconds"/> 毫秒：
    ///
    ///   · 没有搬迁时：`TryEnterReadLock` 立即成功，**零延迟**（不是每次都等 100ms）
    ///   · 搬迁刚开始、写入方同时进来：最多等 100ms，避免误判成「正在搬迁」而白白丢弃
    ///   · 搬迁进行中：等满 100ms 后放弃本次写入
    ///
    /// ## 同一线程上的重入（搬迁事务）
    ///
    /// 搬迁的完整语义不止「搬文件」，还包括**把模式切换提交上去**
    /// （改 `AppPaths.IsPortable`、重置日志路径）。这两步之间若开闸，
    /// 后台线程会按**旧模式**把日志写回刚被删掉的旧目录，下次启动又被判成便携模式
    /// —— 用户点了「取消便携」，重启却回到便携（BUG-03）。
    ///
    /// 所以闸门必须能被外层调用方**提前持有**并跨过模式提交。而搬迁类内部也需要
    /// 自己持一次闸（它是对外公开的，不能假设调用方一定包了事务）。
    /// `ReaderWriterLockSlim` 默认不允许递归，因此这里用线程本地深度做重入：
    /// 同一线程第二次进入只加计数，真正加解锁只发生在 0↔1 的边界上。
    ///
    /// **代价（如实写明）**：`SuspendWrites` 的进入与释放必须在**同一个线程**上配对。
    /// 搬迁是同步的 UI 线程操作，不存在跨线程持有的路径；若将来把它改成
    /// async/await，必须先重新设计这一层（`ThreadStatic` 深度会失效）。
    /// </summary>
    public static class MigrationGate
    {
        private static readonly ReaderWriterLockSlim _gate =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        /// <summary>
        /// 写入方等锁的上限（毫秒）。
        ///
        /// 取值理由：够长，能吃掉「搬迁刚要开始」与「写入正要开始」撞在一起的那点抖动；
        /// 够短，搬迁期间即使有写入方排队，界面也最多卡这么久。
        /// </summary>
        private const int WriterWaitMilliseconds = 100;

        // 当前线程持有写锁（= 正在执行搬迁）的重入深度。0 表示本线程没有持搬迁闸。
        [ThreadStatic] private static int _suspendDepth;

        // 当前线程持有读锁（= 正在写入）的重入深度。0 表示本线程没有持写入闸。
        [ThreadStatic] private static int _writerDepth;

        // 供只读查询与日志使用；真正的互斥由 _gate 保证，它只是「现在是否有搬迁」的快照。
        private static volatile bool _closed;

        /// <summary>
        /// 闸门是否关闭（即：当前正处于搬迁中）。
        ///
        /// **不要用它来做写入前的判断** —— 那正是 BUG-01 的成因。
        /// 写入方必须走 <see cref="TryEnterWrite"/>，让锁本身建立先后关系。
        /// 这个属性只用于诊断输出与「现在要不要提示用户」一类的旁观判断。
        /// </summary>
        public static bool IsClosed
        {
            get { return _closed; }
        }

        /// <summary>
        /// 写入方进入隔离区。**必须与 <see cref="ExitWrite"/> 成对调用。**
        ///
        /// 返回 false 表示「本次写入应当放弃」：搬迁正在持有独占，或本线程正在执行搬迁
        /// （后者若等待就会自己等自己，永远不返回）。
        /// </summary>
        /// <returns>成功取得读锁返回 true；应当放弃本次写入返回 false。</returns>
        public static bool TryEnterWrite()
        {
            // 本线程正在搬迁：它自己的写入必须放弃，否则 write 锁在等自己。
            if (_suspendDepth > 0) return false;

            // 同线程重入：外层已经持读锁，只加深度。
            // 这一支同时防住 LockRecursionPolicy.NoRecursion 的递归异常。
            if (_writerDepth > 0)
            {
                _writerDepth++;
                return true;
            }

            if (!_gate.TryEnterReadLock(WriterWaitMilliseconds)) return false;

            _writerDepth = 1;
            return true;
        }

        /// <summary>写入方离开隔离区。未持有或重复调用都是空操作，不会抛。</summary>
        public static void ExitWrite()
        {
            if (_writerDepth <= 0) return;

            _writerDepth--;
            if (_writerDepth == 0) _gate.ExitReadLock();
        }

        /// <summary>
        /// 搬迁方进入独占区：等所有已开始的写入完成后，独占到本作用域结束。
        ///
        /// 同一线程可重入（外层事务 + 搬迁类内部各持一次），只在最外层真正加解锁。
        /// 返回值可用于 `using`，异常路径下也保证释放 ——
        /// 搬迁中途抛异常却忘了开闸，会让此后所有日志静默消失，
        /// 那种故障极难排查（程序看起来「正常」，只是什么都没有记录）。
        /// </summary>
        /// <returns>释放时自动退出独占的作用域。</returns>
        public static IDisposable SuspendWrites()
        {
            if (_suspendDepth == 0)
            {
                // 这一句会阻塞到所有读锁持有者退出 —— 这就是「先完成已有写入」的落点。
                _gate.EnterWriteLock();
                _closed = true;
            }

            _suspendDepth++;
            return new SuspendScope();
        }

        private sealed class SuspendScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (_suspendDepth <= 0)
                {
                    // 跨线程释放（或用错线程）：正确做法本应是同一线程配对。
                    // 这里仍然把锁放掉，避免「闸门永久关闭、此后所有写入静默消失」，
                    // 但要留下痕迹 —— 静默的成功比一次异常更难查。
                    Debug.Fail("MigrationGate.SuspendWrites 在另一个线程上被释放");
                    try
                    {
                        _gate.ExitWriteLock();
                        _closed = false;
                    }
                    catch (SynchronizationLockException)
                    {
                        // 确实不是本线程持有的锁：只能保持现状，由调用方修正配对方式。
                    }
                    return;
                }

                _suspendDepth--;
                if (_suspendDepth > 0) return;

                _closed = false;
                _gate.ExitWriteLock();
            }
        }
    }
}
