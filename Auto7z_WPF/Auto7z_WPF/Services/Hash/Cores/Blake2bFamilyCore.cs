using System;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// BLAKE2b（64 位字）的共用机械。摘要长度参与参数块，所以
    /// BLAKE2b-256 与「BLAKE2b-512 截半」不是同一个值，必须分别构造。
    ///
    /// 这是抽象基类而不是可实例化的算法：具体算法各自成类
    /// （Blake2b256Core / Blake2b512Core），长度写在子类里。
    /// 与 Sha512FamilyCore、Sha3Core 同一套结构 ——
    /// 共用的是压缩函数与常量表，不是「哪个算法」。
    /// </summary>
    public abstract class Blake2bFamilyCore : IHashCore
    {
        private const int BlockBytes = 128;

        private static readonly ulong[] _iv =
        {
            0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL, 0x3C6EF372FE94F82BUL, 0xA54FF53A5F1D36F1UL,
            0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL, 0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL
        };

        private static readonly int[,] _sigma =
        {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
            { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
            { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
            { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
            { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
            { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
            { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
            { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
            { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 }
        };

        private readonly uint _digestBytes;
        private readonly ulong[] _h = new ulong[8];
        private readonly byte[] _block = new byte[BlockBytes];
        private readonly ulong[] _m = new ulong[16];
        private readonly ulong[] _v = new ulong[16];
        private long _counter;
        private int _blockLength;

        /// <summary>由具体算法给出摘要长度（BLAKE2b 支持 1-64 字节，这里只需 32 与 64）。</summary>
        protected Blake2bFamilyCore(int digestBytes)
        {
            if (digestBytes <= 0 || digestBytes > 64) throw new ArgumentOutOfRangeException("digestBytes");
            _digestBytes = (uint)digestBytes;
            Initialize();
        }

        public void Initialize()
        {
            Array.Copy(_iv, _h, 8);
            _h[0] ^= 0x01010000UL ^ _digestBytes;
            _counter = 0;
            _blockLength = 0;
            Array.Clear(_block, 0, _block.Length);
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            while (count > 0)
            {
                if (_blockLength == BlockBytes)
                {
                    _counter += BlockBytes;
                    Compress(false);
                    _blockLength = 0;
                }

                int take = BlockBytes - _blockLength;
                if (take > count) take = count;

                Buffer.BlockCopy(buffer, offset, _block, _blockLength, take);
                _blockLength += take;
                offset += take;
                count -= take;
            }
        }

        public byte[] Finish()
        {
            _counter += _blockLength;
            Compress(true);

            var output = new byte[_digestBytes];
            for (int i = 0; i < _digestBytes / 8; i++)
            {
                for (int b = 0; b < 8; b++)
                {
                    output[i * 8 + b] = (byte)((_h[i] >> (8 * b)) & 0xFF);
                }
            }

            int remainder = (int)_digestBytes % 8;
            if (remainder != 0)
            {
                ulong last = _h[_digestBytes / 8];
                for (int b = 0; b < remainder; b++)
                {
                    output[_digestBytes - remainder + b] = (byte)((last >> (8 * b)) & 0xFF);
                }
            }

            Array.Copy(_iv, _h, 8);
            _h[0] ^= 0x01010000UL ^ _digestBytes;
            _counter = 0;
            _blockLength = 0;
            return output;
        }

        private void Compress(bool isLast)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = i * 8;
                ulong word = 0;
                for (int b = 0; b < 8; b++)
                {
                    word |= (ulong)_block[at + b] << (8 * b);
                }
                _m[i] = word;
            }

            // 读完就必须清空：最后一块往往是不满的，规范要求空出来的部分补零。
            // 不在这里清，上一块残留的字节就会被当成这一块的尾巴参与压缩 ——
            // 实测只有长度恰好是 128 的倍数时才对，其余全错。
            // 初始化时也清过一次，所以只有第一块碰巧是对的，这个错很容易被漏掉。
            Array.Clear(_block, 0, _block.Length);

            for (int i = 0; i < 8; i++)
            {
                _v[i] = _h[i];
                _v[i + 8] = _iv[i];
            }

            _v[12] ^= (ulong)_counter;
            _v[13] ^= 0;

            if (isLast) _v[14] = ~_v[14];

            for (int round = 0; round < 12; round++)
            {
                int r = round % 10;

                G(_v, 0, 4, 8, 12, _m[_sigma[r, 0]], _m[_sigma[r, 1]]);
                G(_v, 1, 5, 9, 13, _m[_sigma[r, 2]], _m[_sigma[r, 3]]);
                G(_v, 2, 6, 10, 14, _m[_sigma[r, 4]], _m[_sigma[r, 5]]);
                G(_v, 3, 7, 11, 15, _m[_sigma[r, 6]], _m[_sigma[r, 7]]);

                G(_v, 0, 5, 10, 15, _m[_sigma[r, 8]], _m[_sigma[r, 9]]);
                G(_v, 1, 6, 11, 12, _m[_sigma[r, 10]], _m[_sigma[r, 11]]);
                G(_v, 2, 7, 8, 13, _m[_sigma[r, 12]], _m[_sigma[r, 13]]);
                G(_v, 3, 4, 9, 14, _m[_sigma[r, 14]], _m[_sigma[r, 15]]);
            }

            for (int i = 0; i < 8; i++)
            {
                _h[i] ^= _v[i] ^ _v[i + 8];
            }
        }

        private static void G(ulong[] v, int a, int b, int c, int d, ulong x, ulong y)
        {
            v[a] = v[a] + v[b] + x;
            v[d] = RotateRight(v[d] ^ v[a], 32);
            v[c] = v[c] + v[d];
            v[b] = RotateRight(v[b] ^ v[c], 24);
            v[a] = v[a] + v[b] + y;
            v[d] = RotateRight(v[d] ^ v[a], 16);
            v[c] = v[c] + v[d];
            v[b] = RotateRight(v[b] ^ v[c], 63);
        }

        private static ulong RotateRight(ulong value, int bits)
        {
            return (value >> bits) | (value << (64 - bits));
        }
    }
}
