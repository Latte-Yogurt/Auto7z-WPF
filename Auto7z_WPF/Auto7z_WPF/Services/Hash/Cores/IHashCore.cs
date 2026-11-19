namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 增量哈希内核：分块喂数据，最后取结果。实现必须可复用（Initialize 之后能重新开始）。
    /// </summary>
    public interface IHashCore
    {
        /// <summary>重置到初始状态。</summary>
        void Initialize();

        /// <summary>喂入一段数据。count 为 0 时什么都不做。</summary>
        void Append(byte[] buffer, int offset, int count);

        /// <summary>结束并返回原始摘要字节。</summary>
        byte[] Finish();
    }
}
