#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""编译 testtools\cs 下的全部 C# 检查工具。

每个工具一个目录，目录里有 <名字>.cs。
编译产物放在**各自目录里**（<名字>.exe），不往别处扔 ——
免得测试跑到一半发现二进制不见了，或者几个工具互相覆盖。

用法:
    python build_all.py              # 编译全部
    python build_all.py shellcheck   # 只编译指定的一个或几个
    python build_all.py --clean      # 先删掉旧的 .exe 再编译

依赖:
    csc.exe  (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe)
    .NET Framework 4.7.2 参考程序集
"""
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

CSC = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe")
REF_ROOT = Path(r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2")

REFS = [
    REF_ROOT / "PresentationFramework.dll",
    REF_ROOT / "PresentationCore.dll",
    REF_ROOT / "WindowsBase.dll",
    REF_ROOT / "System.Xaml.dll",
]

# 这些工具是纯控制台程序，不需要 WPF 引用；
# 带上也没坏处，少一个依赖少一个坑，统一都带。
OPT = "/nologo"

# 编译出来的 exe 要放在哪：工具自己的目录
def target_dir(tool_dir):
    return tool_dir


def find_tools():
    """找出所有含 <名字>.cs 的子目录。"""
    out = []
    for d in sorted(HERE.iterdir()):
        if not d.is_dir():
            continue
        cs = d / (d.name + ".cs")
        if cs.exists():
            out.append((d.name, d, cs))
    return out


def compile_one(name, tool_dir, src):
    """编译一个工具。返回 (成功, 输出)。"""
    exe = target_dir(tool_dir) / (name + ".exe")
    args = [str(CSC), OPT, "/out:" + str(exe)]
    for r in REFS:
        if not r.exists():
            return False, "缺少参考程序集: %s" % r
        args.append("/r:" + str(r))
    args.append(str(src))

    r = subprocess.run(args, capture_output=True, text=True, encoding="gbk", errors="replace")
    out = (r.stdout or "") + (r.stderr or "")
    if r.returncode != 0:
        return False, out
    return True, out


def clean_one(tool_dir):
    """删掉旧的 .exe 和 .txt（工具自己的输出）。"""
    removed = []
    for pat in ("*.exe", "*.txt"):
        for f in tool_dir.glob(pat):
            try:
                f.unlink()
                removed.append(f.name)
            except OSError:
                pass
    return removed


def main():
    argv = sys.argv[1:]
    do_clean = "--clean" in argv
    wanted = [a for a in argv if not a.startswith("-")]

    tools = find_tools()
    if wanted:
        tools = [t for t in tools if t[0] in wanted]
        missing = set(wanted) - {t[0] for t in tools}
        if missing:
            print("找不到工具: %s" % ", ".join(sorted(missing)))
            return 2

    if not tools:
        print("没有可编译的工具")
        return 2

    print("=== 编译测试工具 (%d 个) ===" % len(tools))
    print("csc: %s" % CSC)
    print("")

    ok = 0
    failed = []
    for name, tool_dir, src in tools:
        if do_clean:
            removed = clean_one(tool_dir)
            if removed:
                print("  [清理] %s: 删掉 %s" % (name, ", ".join(removed)))

        success, out = compile_one(name, tool_dir, src)
        exe = target_dir(tool_dir) / (name + ".exe")
        if success and exe.exists():
            ok += 1
            print("  [成功] %-12s -> %s (%d 字节)" % (name, exe.relative_to(HERE), exe.stat().st_size))
        else:
            failed.append(name)
            print("  [失败] %s" % name)
            for line in out.splitlines():
                if line.strip():
                    print("         " + line)

    print("")
    print("编译结果: %d 成功, %d 失败" % (ok, len(failed)))
    if failed:
        print("失败的工具: %s" % ", ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
