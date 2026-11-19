using System;
using System.Security.Cryptography;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// .NET 框架自带算法的适配基类：把 <see cref="HashAlgorithm"/> 的
    /// TransformBlock / TransformFinalBlock 包装成 IHashCore 的增量接口。
    ///
    /// 为什么要有这层适配而不是自研：框架里这几个实现已经过验证，
    /// 重写只会引入风险，哈希内核的正确性没有「我们自己写会更好」的空间。
    ///
    /// 为什么每个算法还是各占一个类：算法身份写在类型里（Md5Core 就是 MD5），
    /// 而不是藏在一个闭包委托里。这样看代码时不必追到工厂那一行才知道
    /// 某个类到底算的是哪个算法，删改某个算法也不必担心动到别人。
    /// 真正共用的只有「怎么喂数据、怎么收尾」这段与算法无关的管道。
    /// </summary>
    public abstract class FrameworkCore : IHashCore
    {
        private HashAlgorithm _algorithm;

        protected FrameworkCore()
        {
            Initialize();
        }

        /// <summary>造一个全新的框架算法实例。每个子类只实现这一句。</summary>
        protected abstract HashAlgorithm CreateAlgorithm();

        /// <summary>
        /// 每次重置都新建算法实例：HashAlgorithm 在 TransformFinalBlock 之后状态就已结束，
        /// 复用同一对象容易在「算完一个文件再算下一个」时踩到；新建开销相比读盘可以忽略。
        /// </summary>
        public void Initialize()
        {
            if (_algorithm != null)
            {
                _algorithm.Dispose();
            }
            _algorithm = CreateAlgorithm();
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;
            _algorithm.TransformBlock(buffer, offset, count, null, 0);
        }

        /// <summary>收尾并取摘要。返回的数组是副本，调用方可以随意保存。</summary>
        public byte[] Finish()
        {
            _algorithm.TransformFinalBlock(new byte[0], 0, 0);
            byte[] result = _algorithm.Hash;
            return result == null ? new byte[0] : (byte[])result.Clone();
        }
    }
}
