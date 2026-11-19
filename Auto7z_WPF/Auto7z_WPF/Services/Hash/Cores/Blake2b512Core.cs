namespace Auto7z_WPF.Services
{
    /// <summary>
    /// BLAKE2b-512（64 字节输出）。BLAKE2b 的完整摘要，校验记录里最常见的 BLAKE2b 形态。
    /// </summary>
    public sealed class Blake2b512Core : Blake2bFamilyCore
    {
        public Blake2b512Core() : base(64) { }
    }
}
