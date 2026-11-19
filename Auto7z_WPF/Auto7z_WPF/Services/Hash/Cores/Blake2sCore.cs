using System;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// BLAKE2s（32 位字，最大 32 字节摘要），BLAKE2s-256 是这里的默认形态。
    ///
    /// 关键细节：最后一块要带 final 标志，不能提前压缩；数据长度只体现在最后一块的计数器里，
    /// 所以必须缓存一个块；参数块里含 digest 长度，摘要长度不同得到的值也不同，
    /// 不能算完再截断。
    /// </summary>
    public sealed class Blake2sCore : IHashCore
    {
        private const int BlockBytes = 64;
        private const int DigestBytes = 32;

        private static readonly uint[] _iv =
        {
            0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
            0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u
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

        private readonly uint[] _h = new uint[8];
        private readonly byte[] _block = new byte[BlockBytes];
        private readonly uint[] _m = new uint[16];
        private readonly uint[] _v = new uint[16];
        private long _counter;
        private int _blockLength;

        public Blake2sCore()
        {
            Initialize();
        }

        /// <summary>重置状态并写入参数块：digest 长度、key 长度 0、fanout 1、depth 1。</summary>
        public void Initialize()
        {
            Array.Copy(_iv, _h, 8);

            _h[0] ^= 0x01010000u ^ (uint)DigestBytes;

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

        /// <summary>
        /// 交出最后一块。若缓冲区已满，先补满的那一块不是最后一块，立刻压缩；
        /// 之后再按 final 标志压缩一次才取摘要。
        /// </summary>
        public byte[] Finish()
        {
            _counter += _blockLength;
            Compress(true);

            var output = new byte[DigestBytes];
            for (int i = 0; i < DigestBytes / 4; i++)
            {
                output[i * 4] = (byte)(_h[i] & 0xFF);
                output[i * 4 + 1] = (byte)((_h[i] >> 8) & 0xFF);
                output[i * 4 + 2] = (byte)((_h[i] >> 16) & 0xFF);
                output[i * 4 + 3] = (byte)((_h[i] >> 24) & 0xFF);
            }

            Array.Copy(_iv, _h, 8);
            _h[0] ^= 0x01010000u ^ (uint)DigestBytes;
            _counter = 0;
            _blockLength = 0;
            return output;
        }

        private void Compress(bool isLast)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = i * 4;
                _m[i] = (uint)(_block[at] | (_block[at + 1] << 8) | (_block[at + 2] << 16) | (_block[at + 3] << 24));
            }

            // 读完就必须清空：最后一块往往是不满的，规范要求空出来的部分补零。
            // 不在这里清，上一块残留的字节就会被当成这一块的尾巴参与压缩 ——
            // 实测只有长度恰好是 64 的倍数时才对，其余全错（64 字节 56f34e…、
            // 65 字节却算出 1555ec…，标准值是 1b53ee…）。
            // 初始化时也清过一次，所以只有第一块碰巧是对的，这个错很容易被漏掉。
            Array.Clear(_block, 0, _block.Length);

            for (int i = 0; i < 8; i++)
            {
                _v[i] = _h[i];
                _v[i + 8] = _iv[i];
            }

            _v[12] ^= (uint)(_counter & 0xFFFFFFFF);
            _v[13] ^= (uint)((_counter >> 32) & 0xFFFFFFFF);

            if (isLast) _v[14] = ~_v[14];

            for (int round = 0; round < 10; round++)
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

        private static void G(uint[] v, int a, int b, int c, int d, uint x, uint y)
        {
            v[a] = v[a] + v[b] + x;
            v[d] = RotateRight(v[d] ^ v[a], 16);
            v[c] = v[c] + v[d];
            v[b] = RotateRight(v[b] ^ v[c], 12);
            v[a] = v[a] + v[b] + y;
            v[d] = RotateRight(v[d] ^ v[a], 8);
            v[c] = v[c] + v[d];
            v[b] = RotateRight(v[b] ^ v[c], 7);
        }

        private static uint RotateRight(uint value, int bits)
        {
            return (value >> bits) | (value << (32 - bits));
        }
    }
}
