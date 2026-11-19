namespace Auto7z_WPF.Services
{
    /// <summary>
    /// CRC-32/ISO-HDLC（zip / zlib / .sfv 用的那一个）：反射多项式 0xEDB88320，
    /// 初值 0xFFFFFFFF，输出前再异或 0xFFFFFFFF。
    /// 查表实现：逐位循环是每字节 8 次分支，查表每字节一次内存访问，实测差两个数量级
    /// （64MB 从 3.7 秒降到 0.04 秒量级）。表在类型初始化时算一次，之后只读。
    /// </summary>
    public sealed class Crc32Core : IHashCore
    {
        private const uint Polynomial = 0xEDB88320u;

        private static readonly uint[] _table = BuildTable();

        private long _crc;

        public Crc32Core()
        {
            Initialize();
        }

        public void Initialize()
        {
            _crc = 0xFFFFFFFFu;
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            uint crc = (uint)_crc;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc = _table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }

            _crc = crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                }
                table[i] = value;
            }
            return table;
        }

        public byte[] Finish()
        {
            uint value = (uint)_crc ^ 0xFFFFFFFFu;
            _crc = 0xFFFFFFFFu;

            return new[]
            {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }
    }
}
