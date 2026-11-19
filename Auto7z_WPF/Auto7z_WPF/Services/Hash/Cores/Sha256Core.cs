using System.Security.Cryptography;

namespace Auto7z_WPF.Services
{
    /// <summary>SHA-256（32 字节输出）。默认算法，也是校验记录里最常见的一种。</summary>
    public sealed class Sha256Core : FrameworkCore
    {
        protected override HashAlgorithm CreateAlgorithm()
        {
            return SHA256.Create();
        }
    }
}
