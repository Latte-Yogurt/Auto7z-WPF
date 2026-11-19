using System.Security.Cryptography;

namespace Auto7z_WPF.Services
{
    /// <summary>MD5（16 字节输出）。框架自带的不是加密安全的摘要，校验文件完整性仍然够用。</summary>
    public sealed class Md5Core : FrameworkCore
    {
        protected override HashAlgorithm CreateAlgorithm()
        {
            return MD5.Create();
        }
    }
}
