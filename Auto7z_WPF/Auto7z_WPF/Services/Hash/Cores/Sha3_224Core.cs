namespace Auto7z_WPF.Services
{
    /// <summary>SHA3-224（28 字节输出，速率 144）。</summary>
    public sealed class Sha3_224Core : Sha3Core
    {
        public Sha3_224Core() : base(144) { }
        protected override int OutputBytes { get { return 28; } }
    }
}
