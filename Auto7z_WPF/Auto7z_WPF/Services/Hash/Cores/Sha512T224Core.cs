namespace Auto7z_WPF.Services
{
    /// <summary>SHA-512/224（28 字节输出）。</summary>
    public sealed class Sha512T224Core : Sha512FamilyCore
    {
        public Sha512T224Core()
            : base(new ulong[]
            {
                0x8C3D37C819544DA2UL, 0x73E1996689DCD4D6UL, 0x1DFAB7AE32FF9C82UL, 0x679DD514582F9FCFUL,
                0x0F6D2B697BD44DA8UL, 0x77E36F7304C48942UL, 0x3F9D85A86A1D36C8UL, 0x1112E6AD91D692A1UL
            })
        {
        }

        protected override int OutputBytes { get { return 28; } }
    }
}
