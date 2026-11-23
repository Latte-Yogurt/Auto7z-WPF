#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Hash 核心族的静态一致性检查（替代逐个肉眼读 17 个小文件）。

检查项：
  H1 每个核心类都是 sealed 且有 XML 摘要
  H2 每个核心都有对应的目录注册项（HashAlgorithms._byId / 目录表）
  H3 目录里注册的标识在 7z/系统里可用（只验证命名规范，不验证算法正确性）
  H4 每个核心的输出长度与算法名是否自洽（SHA-256 → 32 字节等）

为什么要自动化：17 个文件里 13 个是 9~17 行的薄封装，逐个肉眼读
既慢又容易漏。把它们的一致性约束写成断言，比逐个看更可靠。
"""
import io
import os
import re
import sys

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")):
    SRC = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")
else:
    SRC = P
CORES = os.path.join(SRC, "Services", "Hash", "Cores")

# 算法名 → 期望输出字节数
EXPECTED_BYTES = {
    "Md5Core": 16,
    "Sha1Core": 20,
    "Sha224Core": 28,
    "Sha256Core": 32,
    "Sha384Core": 48,
    "Sha512Core": 64,
    "Sha512T224Core": 28,
    "Sha512T256Core": 32,
    "Blake2b256Core": 32,
    "Blake2b512Core": 64,
    "Blake2sCore": 32,
    "Blake3Core": 32,
    "Sha3_224Core": 28,
    "Sha3_256Core": 32,
    "Sha3_384Core": 48,
    "Sha3_512Core": 64,
    "Crc32Core": 4,
    "Crc64Core": 8,
    "Sha224Core": 28,
}

problems = []


def fail(code, msg):
    problems.append((code, msg))


print("=" * 72)
print("Hash 核心族一致性")
print("=" * 72)

files = sorted(f for f in os.listdir(CORES) if f.endswith(".cs") and f != "IHashCore.cs")
print("核心文件 %d 个" % len(files))

# ---- H1 sealed + XML 摘要 ----
print("\n【H1】sealed 与 XML 摘要")
not_sealed, no_summary = [], []
for f in files:
    t = io.open(os.path.join(CORES, f), encoding="utf-8", errors="replace").read()
    cls = f[:-3]
    # 抽象基类**不该** sealed（它们的存在意义就是被继承）。判据要区分：
#   abstract class  → 必须不 sealed
#   具体 class      → 必须 sealed
    is_abstract = re.search(r'\babstract\s+class\s+' + re.escape(cls) + r'\b', t) is not None
    is_sealed = re.search(r'\bsealed\s+class\s+' + re.escape(cls) + r'\b', t) is not None
    if is_abstract:
        if is_sealed:
            not_sealed.append(f + "（抽象类不该 sealed）")
    elif not is_sealed:
        not_sealed.append(f)
    if "<summary>" not in t:
        no_summary.append(f)
if not_sealed:
    fail("H1", "未 sealed: " + ", ".join(not_sealed))
    print("  !!   未 sealed: " + ", ".join(not_sealed))
if no_summary:
    fail("H1", "缺 XML 摘要: " + ", ".join(no_summary))
    print("  !!   缺 XML 摘要: " + ", ".join(no_summary))
if not not_sealed and not no_summary:
    print("  OK   %d 个核心全部 sealed 且有摘要" % len(files))

# ---- H2 目录注册 ----
print("\n【H2】目录注册")
alg_path = os.path.join(SRC, "Services", "Hash", "HashAlgorithms.cs")
alg = io.open(alg_path, encoding="utf-8", errors="replace").read()
registered = set(re.findall(r'new\s+(\w+Core)\s*\(', alg))
all_cores = {f[:-3] for f in files}

# 抽象基类不算需要注册
# 只有真正带 abstract 的基类才排除。上一版把 Blake2sCore 也列进来了，
# 但它是具体类（继承 Blake2bFamilyCore 并指定 32 字节输出），必须参与注册检查。
ABSTRACTS = {"FrameworkCore", "Sha3Core", "Blake2bFamilyCore", "Sha512FamilyCore"}
concrete = all_cores - ABSTRACTS

missing_reg = sorted(concrete - registered)
extra_reg = sorted(registered - concrete)
if missing_reg:
    fail("H2", "有核心类未注册: " + ", ".join(missing_reg))
    print("  !!   未注册: " + ", ".join(missing_reg))
if extra_reg:
    fail("H2", "注册了不存在的核心: " + ", ".join(extra_reg))
    print("  !!   注册了不存在的: " + ", ".join(extra_reg))
if not missing_reg and not extra_reg:
    print("  OK   %d 个具体核心全部已注册，无多余注册项" % len(concrete))

# ---- H3 标识命名规范 ----
print("\n【H3】算法标识命名")
# 目录里的注册写法是 list.Add(Simple("md5", "MD5", 16, ...))，
# 不是 new HashAlgorithmInfo(...)。上一版按后者匹配，结果是 0 个 ——
# 那项检查就形同不存在（最危险的一类假检查：永远 PASS，却什么都没验证）。
ids = re.findall(r'Simple\(\s*"([^"]+)"', alg)
bad_ids = [i for i in ids if not re.match(r'^[a-z0-9][a-z0-9\-]*$', i)]
if bad_ids:
    fail("H3", "标识不合规范（应小写+连字符）: " + ", ".join(bad_ids))
    print("  !!   不合规范: " + ", ".join(bad_ids))
else:
    print("  OK   %d 个标识全部为小写+连字符形式" % len(ids))

# ---- H4 输出长度自洽 ----
print("\n【H4】输出长度与算法名自洽")
mismatches = []
for f in files:
    cls = f[:-3]
    expect = EXPECTED_BYTES.get(cls)
    if expect is None:
        continue
    t = io.open(os.path.join(CORES, f), encoding="utf-8", errors="replace").read()
    # 从 base(N) 或 OutputBytes 里取声明的字节数
    m = re.search(r'OutputBytes\s*\{\s*get\s*\{\s*return\s+(\d+)', t)
    if m:
        got = int(m.group(1))
    else:
        m2 = re.search(r':\s*base\(\s*(\d+)\s*\)', t)
        got = int(m2.group(1)) if m2 else None
    if got is None:
        continue
    if got != expect:
        mismatches.append("%s: 声明 %d 字节，按算法名应为 %d" % (cls, got, expect))
if mismatches:
    for m in mismatches:
        fail("H4", m)
        print("  !!   " + m)
else:
    print("  OK   已声明长度与算法名全部自洽")

# ---- H5 默认算法必须是合法 id ----
#
# 为什么必须有这一项：HashAlgorithms.Default 的实现是
#     get { return _byId[DefaultAlgorithmId]; }
# 注释里明确写着「填了不在列表里的值不会静默回落：首次访问本属性时直接抛
# KeyNotFoundException」。但原来**没有任何自动化检查**拦住这个错误 ——
# 改常量的人只能靠读注释；一旦填错，要等到运行时第一次取默认算法才炸，
# 而那时抛的是 KeyNotFoundException，从堆栈看不出是配置写错了。
#
# 本项把「常量属于 18 个合法 id」变成断言。2026-11-23 新增。
print("\n【H5】默认算法在合法 id 列表中")
m_def = re.search(r'DefaultAlgorithmId\s*=\s*"([^"]+)"', alg)
if not m_def:
    fail("H5", "找不到 DefaultAlgorithmId 常量")
    print("  !!   找不到 DefaultAlgorithmId 常量")
else:
    default_id = m_def.group(1)
    if default_id in ids:
        print("  OK   默认算法 %r 在 %d 个合法 id 中" % (default_id, len(ids)))
    else:
        msg = ("默认算法 %r 不在合法 id 列表里 —— 首次访问 HashAlgorithms.Default "
               "会抛 KeyNotFoundException；合法值是: %s"
               % (default_id, ", ".join(sorted(ids))))
        fail("H5", msg)
        print("  !!   " + msg)

# ---- 汇总 ----
print("\n" + "=" * 72)
if problems:
    print("发现 %d 项问题：" % len(problems))
    for c, m in problems:
        print("  [%s] %s" % (c, m))
    print("FAIL")
    sys.exit(1)

print("Hash 核心族一致")
print("PASS")
sys.exit(0)
