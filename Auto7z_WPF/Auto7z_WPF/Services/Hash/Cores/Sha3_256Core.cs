namespace Auto7z_WPF.Services
{
    /// <summary>SHA3-256（32 字节输出，速率 136）。</summary>
    public sealed class Sha3_256Core : Sha3Core
    {
        public Sha3_256Core() : base(136) { }
        protected override int OutputBytes { get { return 32; } }
    }
}
