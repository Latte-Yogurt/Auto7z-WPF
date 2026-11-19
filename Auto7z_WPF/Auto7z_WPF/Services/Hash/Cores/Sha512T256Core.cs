namespace Auto7z_WPF.Services
{
    /// <summary>SHA-512/256（32 字节输出）。</summary>
    public sealed class Sha512T256Core : Sha512FamilyCore
    {
        public Sha512T256Core()
            : base(new ulong[]
            {
                0x22312194FC2BF72CUL, 0x9F555FA3C84C64C2UL, 0x2393B86B6F53B151UL, 0x963877195940EABDUL,
                0x96283EE2A88EFFE3UL, 0xBE5E1E2553863992UL, 0x2B0199FC2C85B8AAUL, 0x0EB72DDC81C52CA2UL
            })
        {
        }

        protected override int OutputBytes { get { return 32; } }
    }
}
