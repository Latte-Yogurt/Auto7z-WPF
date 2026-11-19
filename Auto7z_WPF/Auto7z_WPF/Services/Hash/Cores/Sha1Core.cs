using System.Security.Cryptography;

namespace Auto7z_WPF.Services
{
    /// <summary>SHA-1（20 字节输出）。已有碰撞攻击，仅用于与既有校验记录互通。</summary>
    public sealed class Sha1Core : FrameworkCore
    {
        protected override HashAlgorithm CreateAlgorithm()
        {
            return SHA1.Create();
        }
    }
}
