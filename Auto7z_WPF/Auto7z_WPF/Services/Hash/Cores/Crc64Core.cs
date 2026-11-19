namespace Auto7z_WPF.Services
{
    /// <summary>
    /// CRC-64/XZ：反射多项式 0xC96C5795D7870F42，初值全 1，输出前再异或全 1，8 字节大端。
    /// 与 CRC-32 一样用查表实现。
    /// </summary>
    public sealed class Crc64Core : IHashCore
    {
        private const ulong Polynomial = 0xC96C5795D7870F42UL;

        private static readonly ulong[] _table = BuildTable();

        private ulong _crc;

        public Crc64Core()
        {
            Initialize();
        }

        public void Initialize()
        {
            _crc = 0xFFFFFFFFFFFFFFFFUL;
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            ulong crc = _crc;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc = _table[(int)((crc ^ buffer[i]) & 0xFF)] ^ (crc >> 8);
            }

            _crc = crc;
        }

        private static ulong[] BuildTable()
        {
            var table = new ulong[256];
            for (ulong i = 0; i < 256; i++)
            {
                ulong value = i;
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
            ulong value = _crc ^ 0xFFFFFFFFFFFFFFFFUL;
            _crc = 0xFFFFFFFFFFFFFFFFUL;

            return new[]
            {
                (byte)((value >> 56) & 0xFF),
                (byte)((value >> 48) & 0xFF),
                (byte)((value >> 40) & 0xFF),
                (byte)((value >> 32) & 0xFF),
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }
    }
}
