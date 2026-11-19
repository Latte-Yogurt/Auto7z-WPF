namespace Auto7z_WPF.Services
{
    /// <summary>SHA3-384（48 字节输出，速率 104）。</summary>
    public sealed class Sha3_384Core : Sha3Core
    {
        public Sha3_384Core() : base(104) { }
        protected override int OutputBytes { get { return 48; } }
    }
}
