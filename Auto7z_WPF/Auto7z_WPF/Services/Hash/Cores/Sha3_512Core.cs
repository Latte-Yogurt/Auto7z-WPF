namespace Auto7z_WPF.Services
{
    /// <summary>SHA3-512（64 字节输出，速率 72）。</summary>
    public sealed class Sha3_512Core : Sha3Core
    {
        public Sha3_512Core() : base(72) { }
        protected override int OutputBytes { get { return 64; } }
    }
}
