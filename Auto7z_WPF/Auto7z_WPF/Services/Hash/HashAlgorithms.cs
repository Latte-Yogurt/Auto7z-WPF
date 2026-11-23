using System;
using System.Collections.Generic;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 一个算法目录项：标识、显示名、摘要位数、内核工厂，以及能把它认出来的后缀与别名。
    /// </summary>
    public class HashAlgorithmInfo
    {
        /// <summary>规范标识，全部小写，例如 sha256、sha3-256、crc32。</summary>
        public string Id { get; set; }

        /// <summary>界面显示名，例如 SHA-256。</summary>
        public string DisplayName { get; set; }

        /// <summary>摘要字节数。</summary>
        public int DigestBytes { get; set; }

        /// <summary>摘要的十六进制字符数（DigestBytes * 2），也是校验记录里值的长度。</summary>
        public int HexLength { get { return DigestBytes * 2; } }

        /// <summary>新建一个可复用的增量内核。</summary>
        public Func<IHashCore> CreateCore { get; set; }

        // 下面两个集合属性加了一道「永不返回 null」的守门：
        //
        // 它们原本是裸的 `{ get; set; }`，而同一个类里 `Extension` 属性已经写了
        // `if (Extensions == null ...) return ".checksum";` 这样的判空 ——
        // 同一份数据的两个使用点，一个防了、一个没防，是典型的不一致。
        //
        // 具体风险在 BuildCatalog 的静态构造里：
        //     foreach (string alias in info.Aliases)      // Aliases 为 null → NRE
        //     foreach (string extension in info.Extensions) // Extensions 为 null → NRE
        // 静态构造抛异常会变成 TypeInitializationException，之后**每一次**访问
        // HashAlgorithms 都失败（哪怕改回正确值，进程内已无法恢复）——
        // 这种失败模式极难排查，代价远高于两行判空。
        //
        // 当前 18 个 Simple() 调用点都传了数组，所以不可达；但从「公开可写属性」
        // 的定义推不出这个保证 —— 谁都能写 info.Aliases = null。
        // 与其在每个 foreach 里判空（容易漏），不如让读取端永远拿到一个空集合。
        private List<string> _extensions = new List<string>();
        private HashSet<string> _aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>能把这个算法认出来的文件后缀，全部小写且带点，例如 .sha256。</summary>
        public List<string> Extensions
        {
            get { return _extensions ?? (_extensions = new List<string>()); }
            set { _extensions = value ?? new List<string>(); }
        }

        /// <summary>
        /// 写校验记录时用的首选后缀，取 Extensions 的第一项。
        /// 必须按「算法」而不是「算法族」取：SHA3-256 要写 .sha3-256 而不是 .sha256，
        /// 写错了文件传回来时会被按 SHA-256 认，校验必然对不上。目录里每个算法的首选后缀唯一。
        /// </summary>
        public string Extension
        {
            get
            {
                List<string> extensions = Extensions;
                if (extensions.Count == 0) return ".checksum";
                return extensions[0];
            }
        }

        /// <summary>名字别名，全部小写且已去掉分隔符，sha256 与 sha-256 都能命中。</summary>
        public HashSet<string> Aliases
        {
            get { return _aliases ?? (_aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
            set { _aliases = value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// 算法目录。设计目标是最宽容的兼容性：
    /// 1. 覆盖 .NET Framework 自带算法与自实现算法；
    /// 2. 同一算法接受多种后缀与写法（.sha256 / .sha256sum / .sha2，名字侧接受 SHA-256 / sha256 / SHA2-256）；
    /// 3. 后缀认不出时按值的长度推断候选，比对时任一个命中即通过，
    ///    避免因为分不清 SHA-256 与 SHA3-256 就误报失败。
    /// </summary>
    public static class HashAlgorithms
    {
        private static readonly List<HashAlgorithmInfo> _all = BuildCatalog();

        private static readonly Dictionary<string, HashAlgorithmInfo> _byId =
            new Dictionary<string, HashAlgorithmInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, HashAlgorithmInfo> _byAlias =
            new Dictionary<string, HashAlgorithmInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, HashAlgorithmInfo> _byExtension =
            new Dictionary<string, HashAlgorithmInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<int, List<HashAlgorithmInfo>> _byHexLength =
            new Dictionary<int, List<HashAlgorithmInfo>>();

        private static readonly List<string> _extensionKeys = new List<string>();

        static HashAlgorithms()
        {
            foreach (HashAlgorithmInfo info in _all)
            {
                _byId[info.Id] = info;

                foreach (string alias in info.Aliases)
                {
                    _byAlias[NormalizeName(alias)] = info;
                }

                foreach (string extension in info.Extensions)
                {
                    _byExtension[extension] = info;
                    if (!_extensionKeys.Contains(extension)) _extensionKeys.Add(extension);
                }

                List<HashAlgorithmInfo> sameLength;
                if (!_byHexLength.TryGetValue(info.HexLength, out sameLength))
                {
                    sameLength = new List<HashAlgorithmInfo>();
                    _byHexLength[info.HexLength] = sameLength;
                }
                sameLength.Add(info);
            }

            _extensionKeys.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        /// <summary>目录里的全部算法，顺序即界面下拉框的顺序。</summary>
        public static IList<HashAlgorithmInfo> All
        {
            get { return _all; }
        }

        /// <summary>
        /// 默认算法：调用方没有指定算法时的兜底。
        ///
        /// **要换默认算法，改下面 getter 里用的 <see cref="DefaultAlgorithmId"/> 常量即可**，
        /// 不要在这里另写 <c>_byId["其它标识"]</c>。全工程的「默认是谁」只由那一个常量决定，
        /// 其余消费者都引用本属性、不再各自写死字符串：
        ///   - AppConfig.ChecksumAlgorithm 的字段初值
        ///   - ConfigService 读取缺失键时的兜底、以及 DefaultValueFor 的返回值
        ///   - Auto7zViewModel._checksumAlgorithmId 的字段初值
        ///   - HashingService.Calculate 的 algorithm 为空时的回落
        /// 改完记得同步 testtools 里 hashprobe 的标准值表所对应的默认算法断言（若默认值影响它）。
        ///
        /// 标识必须存在于 <see cref="All"/> 中：写错会在**首次访问**时抛 KeyNotFoundException，
        /// 而不是静默退回某个算法 —— 宁可启动就炸，也不要悄悄算出另一种摘要。
        /// </summary>
        public static HashAlgorithmInfo Default
        {
            // ★ 改默认算法就改 DefaultAlgorithmId 这个常量（见下方定义处），本行不用动。
            //   合法取值 = BuildCatalog 里每个条目的 id，共 18 个：
            //     md5 / sha1 / sha224 / sha256 / sha384 / sha512 /
            //     sha512-224 / sha512-256 /
            //     sha3-224 / sha3-256 / sha3-384 / sha3-512 /
            //     blake2s-256 / blake2b-256 / blake2b-512 / blake3 /
            //     crc32 / crc64
            //   填了不在列表里的值不会静默回落：首次访问本属性时直接抛 KeyNotFoundException。
            get { return _byId[DefaultAlgorithmId]; }
        }

        /// <summary>
        /// 默认算法的标识（小写，与 <see cref="BuildCatalog"/> 里的 id 完全一致）。
        ///
        /// **这就是全工程唯一需要改的一行：换默认算法 = 改这个字符串。**
        /// 抽成常量的目的，是让「默认算法」这件事有一个具名、可全局搜索的锚点
        /// （搜 <c>DefaultAlgorithmId</c> 即可定位），而不是散落在多个文件里的字面量。
        ///
        /// 改法：
        ///   1. 把右边的 "sha256" 换成目标算法的 id（合法值与 18 项清单见上方
        ///      <see cref="Default"/> 的 getter 注释）；
        ///   2. 重新编译即可 —— AppConfig、ConfigService、Auto7zViewModel、HashingService
        ///      都引用本常量，会自动跟随，不需要逐个文件改；
        ///   3. 跑 testtools 的 hashprobe 与 run_regress 复验。
        ///
        /// 注意：本常量只在 <see cref="All"/> 构建完成后被读取，填错值时表现为
        /// 首次访问 <see cref="Default"/> 抛 KeyNotFoundException，而不是编译错误。
        /// </summary>
        private const string DefaultAlgorithmId = "sha256"; // ← 改默认算法就改这一行

        /// <summary>按标识取算法；找不到返回 null。</summary>
        public static HashAlgorithmInfo ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            HashAlgorithmInfo info;
            return _byId.TryGetValue(id.Trim(), out info) ? info : null;
        }

        /// <summary>按名字或别名取算法；找不到返回 null。</summary>
        public static HashAlgorithmInfo ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string key = NormalizeName(name);
            if (key.Length == 0) return null;

            HashAlgorithmInfo info;
            if (_byAlias.TryGetValue(key, out info)) return info;
            return _byId.TryGetValue(name.Trim(), out info) ? info : null;
        }

        /// <summary>把名字归一化：去掉分隔符与大小写，便于别名匹配。</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (c == '-' || c == '_' || c == ' ' || c == '.' || c == '/') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>构造算法目录。顺序即界面下拉框的顺序，改动会直接改变用户看到的列表。</summary>
        private static List<HashAlgorithmInfo> BuildCatalog()
        {
            var list = new List<HashAlgorithmInfo>();

            list.Add(Simple("md5", "MD5", 16,
                () => new Md5Core(),
                new[] { ".md5", ".md5sum" },
                new[] { "md5", "md-5", "md5sum" }));

            list.Add(Simple("sha1", "SHA-1", 20,
                () => new Sha1Core(),
                new[] { ".sha1", ".sha1sum", ".sha-1" },
                new[] { "sha1", "sha-1", "sha1sum" }));

            list.Add(Simple("sha224", "SHA-224", 28,
                () => new Sha224Core(),
                new[] { ".sha224", ".sha224sum", ".sha2-224" },
                new[] { "sha224", "sha-224", "sha2-224", "sha2_224" }));

            list.Add(Simple("sha256", "SHA-256", 32,
                () => new Sha256Core(),
                new[] { ".sha256", ".sha256sum", ".sha2", ".sha2-256" },
                new[] { "sha256", "sha-256", "sha2-256", "sha256sum", "sha2" }));

            list.Add(Simple("sha384", "SHA-384", 48,
                () => new Sha384Core(),
                new[] { ".sha384", ".sha384sum", ".sha2-384" },
                new[] { "sha384", "sha-384", "sha2-384" }));

            list.Add(Simple("sha512", "SHA-512", 64,
                () => new Sha512Core(),
                new[] { ".sha512", ".sha512sum", ".sha2-512" },
                new[] { "sha512", "sha-512", "sha2-512", "sha512sum" }));

            list.Add(Simple("sha512-224", "SHA-512/224", 28,
                () => new Sha512T224Core(),
                new[] { ".sha512-224", ".sha512_224", ".sha512t224" },
                new[] { "sha512-224", "sha512/224", "sha512t224", "sha512_224" }));

            list.Add(Simple("sha512-256", "SHA-512/256", 32,
                () => new Sha512T256Core(),
                new[] { ".sha512-256", ".sha512_256", ".sha512t256" },
                new[] { "sha512-256", "sha512/256", "sha512t256", "sha512_256" }));

            list.Add(Simple("sha3-224", "SHA3-224", 28,
                () => new Sha3_224Core(),
                new[] { ".sha3-224", ".sha3_224", ".sha3224" },
                new[] { "sha3-224", "sha3_224", "sha3224", "sha3-224sum" }));

            list.Add(Simple("sha3-256", "SHA3-256", 32,
                () => new Sha3_256Core(),
                new[] { ".sha3-256", ".sha3_256", ".sha3256", ".sha3" },
                new[] { "sha3-256", "sha3_256", "sha3256", "sha3", "sha3-256sum" }));

            list.Add(Simple("sha3-384", "SHA3-384", 48,
                () => new Sha3_384Core(),
                new[] { ".sha3-384", ".sha3_384", ".sha3384" },
                new[] { "sha3-384", "sha3_384", "sha3384", "sha3-384sum" }));

            list.Add(Simple("sha3-512", "SHA3-512", 64,
                () => new Sha3_512Core(),
                new[] { ".sha3-512", ".sha3_512", ".sha3512" },
                new[] { "sha3-512", "sha3_512", "sha3512", "sha3-512sum" }));

            list.Add(Simple("blake2s-256", "BLAKE2s-256", 32,
                () => new Blake2sCore(),
                new[] { ".blake2s", ".blake2s-256", ".b2s" },
                new[] { "blake2s", "blake2s-256", "b2s" }));

            list.Add(Simple("blake2b-256", "BLAKE2b-256", 32,
                () => new Blake2b256Core(),
                new[] { ".blake2b-256", ".blake2b256", ".b2b-256" },
                new[] { "blake2b-256", "blake2b256", "b2b-256" }));

            list.Add(Simple("blake2b-512", "BLAKE2b-512", 64,
                () => new Blake2b512Core(),
                new[] { ".blake2b", ".blake2b-512", ".b2b", ".blake2" },
                new[] { "blake2b", "blake2b-512", "b2b", "blake2" }));

            // BLAKE3 只有 32 字节这一种输出，不像 BLAKE2 有 b/s 两支与可配长度，
            // 所以首选后缀必须是独有的 .blake3，绝不能与 .blake2b / .b2b 混用：
            // 后缀是读回时判定算法的唯一线索，混用会让 BLAKE3 的值按 BLAKE2b 认。
            list.Add(Simple("blake3", "BLAKE3", 32,
                () => new Blake3Core(),
                new[] { ".blake3", ".b3", ".blake3sum" },
                new[] { "blake3", "blake-3", "b3", "blake3sum" }));

            list.Add(Simple("crc32", "CRC-32", 4,
                () => new Crc32Core(),
                new[] { ".crc32", ".crc", ".sfv" },
                new[] { "crc32", "crc-32", "crc", "sfv" }));

            list.Add(Simple("crc64", "CRC-64", 8,
                () => new Crc64Core(),
                new[] { ".crc64", ".crc-64" },
                new[] { "crc64", "crc-64" }));

            return list;
        }

        /// <summary>构造一个内置算法的目录项。</summary>
        private static HashAlgorithmInfo Simple(string id, string displayName, int digestBytes,
            Func<IHashCore> core, string[] extensions, string[] aliases)
        {
            var info = new HashAlgorithmInfo
            {
                Id = id,
                DisplayName = displayName,
                DigestBytes = digestBytes,
                CreateCore = core,
                // `new List<string>(null)` 会抛 ArgumentNullException。
                // 属性 setter 现在会把 null 变成空集合，但这里是构造实参、走不到 setter，
                // 所以仍要在这里给出定义：缺后缀/别名只是「少了点信息」，
                // 不该让整个静态构造崩掉（静态构造一旦失败，进程内再也用不了本类）。
                Extensions = extensions == null
                    ? new List<string>()
                    : new List<string>(extensions),
                Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };

            if (aliases != null)
            {
                foreach (string alias in aliases)
                {
                    info.Aliases.Add(alias);
                }
            }

            return info;
        }
    }
}
