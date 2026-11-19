using System;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// SHA-2 家族的 32 位字变体。SHA-256 用框架自带的，这里只补框架缺的 SHA-224，
    /// 两者共用同一套压缩函数与常量表。
    /// </summary>
    public sealed class Sha224Core : IHashCore
    {
        private const int BlockBytes = 64;

        private static readonly uint[] _k =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        private static readonly uint[] _initial =
        {
            0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939,
            0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4
        };

        private readonly uint[] _h = new uint[8];
        private readonly byte[] _block = new byte[BlockBytes];
        private readonly uint[] _w = new uint[64];
        private int _blockLength;
        private long _totalLength;

        public Sha224Core()
        {
            Initialize();
        }

        public void Initialize()
        {
            Array.Copy(_initial, _h, 8);
            _blockLength = 0;
            _totalLength = 0;
            Array.Clear(_block, 0, _block.Length);
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            _totalLength += count;

            while (count > 0)
            {
                int take = BlockBytes - _blockLength;
                if (take > count) take = count;

                Buffer.BlockCopy(buffer, offset, _block, _blockLength, take);
                _blockLength += take;
                offset += take;
                count -= take;

                if (_blockLength == BlockBytes)
                {
                    Compress(_block, 0);
                    _blockLength = 0;
                }
            }
        }

        public byte[] Finish()
        {
            long bitLength = _totalLength * 8;

            _block[_blockLength++] = 0x80;
            if (_blockLength > BlockBytes - 8)
            {
                while (_blockLength < BlockBytes) _block[_blockLength++] = 0;
                Compress(_block, 0);
                _blockLength = 0;
            }

            while (_blockLength < BlockBytes - 8) _block[_blockLength++] = 0;

            for (int i = 0; i < 8; i++)
            {
                _block[BlockBytes - 8 + i] = (byte)((bitLength >> (56 - 8 * i)) & 0xFF);
            }

            Compress(_block, 0);
            _blockLength = 0;

            // SHA-224 只输出 7 个字（28 字节），第 8 个字 _h[7] 丢弃。
            var output = new byte[28];
            for (int i = 0; i < 7; i++)
            {
                output[i * 4] = (byte)((_h[i] >> 24) & 0xFF);
                output[i * 4 + 1] = (byte)((_h[i] >> 16) & 0xFF);
                output[i * 4 + 2] = (byte)((_h[i] >> 8) & 0xFF);
                output[i * 4 + 3] = (byte)(_h[i] & 0xFF);
            }

            Array.Copy(_initial, _h, 8);
            return output;
        }

        private void Compress(byte[] block, int offset)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = offset + i * 4;
                _w[i] = ((uint)block[at] << 24)
                    | ((uint)block[at + 1] << 16)
                    | ((uint)block[at + 2] << 8)
                    | (uint)block[at + 3];
            }

            for (int i = 16; i < 64; i++)
            {
                uint s0 = Rotate(_w[i - 15], 7) ^ Rotate(_w[i - 15], 18) ^ (_w[i - 15] >> 3);
                uint s1 = Rotate(_w[i - 2], 17) ^ Rotate(_w[i - 2], 19) ^ (_w[i - 2] >> 10);
                _w[i] = _w[i - 16] + s0 + _w[i - 7] + s1;
            }

            uint a = _h[0], b = _h[1], c = _h[2], d = _h[3];
            uint e = _h[4], f = _h[5], g = _h[6], h = _h[7];

            for (int i = 0; i < 64; i++)
            {
                uint s1 = Rotate(e, 6) ^ Rotate(e, 11) ^ Rotate(e, 25);
                uint ch = (e & f) ^ ((~e) & g);
                uint temp1 = h + s1 + ch + _k[i] + _w[i];
                uint s0 = Rotate(a, 2) ^ Rotate(a, 13) ^ Rotate(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint temp2 = s0 + maj;

                h = g;
                g = f;
                f = e;
                e = d + temp1;
                d = c;
                c = b;
                b = a;
                a = temp1 + temp2;
            }

            _h[0] += a;
            _h[1] += b;
            _h[2] += c;
            _h[3] += d;
            _h[4] += e;
            _h[5] += f;
            _h[6] += g;
            _h[7] += h;
        }

        private static uint Rotate(uint value, int bits)
        {
            return (value >> bits) | (value << (32 - bits));
        }
    }
}
