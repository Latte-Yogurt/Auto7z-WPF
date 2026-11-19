using System.Security.Cryptography;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// SHA-512（64 字节输出）。走框架实现，不是自研 ——
    /// 这里只有一个类，所以「SHA-512 用的是哪份代码」没有歧义。
    /// SHA-512/224 与 SHA-512/256 框架没有，那两个见 Sha512T224Core / Sha512T256Core。
    /// </summary>
    public sealed class Sha512Core : FrameworkCore
    {
        protected override HashAlgorithm CreateAlgorithm()
        {
            return SHA512.Create();
        }
    }
}
