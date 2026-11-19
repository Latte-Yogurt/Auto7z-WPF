namespace Auto7z_WPF.Services
{
    /// <summary>
    /// BLAKE2b-256（32 字节输出）。
    ///
    /// 摘要长度参与参数块，所以它不是「BLAKE2b-512 的前 32 字节」——
    /// 那是另一个完全不同的值。两者必须各成一个算法，各有各的后缀与别名。
    /// </summary>
    public sealed class Blake2b256Core : Blake2bFamilyCore
    {
        public Blake2b256Core() : base(32) { }
    }
}
