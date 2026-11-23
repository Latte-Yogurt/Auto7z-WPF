#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""跨语言常量一致性检查：C# 侧与 C++ 侧必须使用同一个 CLSID / Handler 名。

为什么单独一项（真实风险，不是理论问题）：

    ShellRegistrationService.cs 里写死了
        ShellClsid     = "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"
        ShellHandlerName = "Auto7z"
    而 ShellExtension.cpp 里是
        const CLSID kClassId = { 0x6C3F8A21, 0x4B7D, ... };
        const wchar_t kHandlerKeyName[] = L"Auto7z";

    这两处**没有任何编译期或运行期的关联**。改了一边忘了另一边，后果：
      - CLSID 不一致 → HasRegistryResidue() 查的键根本不是产品注册的那个键，
        于是「dll 缺失时如实上报残留」这条逻辑永远报「无残留」（假阴性），
        用户会看到「已注销」而注册表里其实还指着不存在的 dll。
      - Handler 名不一致 → 同理，四条 shellex 路径里三条查不到。

    这类缺陷**不会让任何测试失败**，只会让卸载不干净，而且极难归因。

检查方式：把 C# 的大括号 GUID 与 C++ 的 8 段字节展开后逐字段比对，
并核对 Handler 名字符串。任何不一致都判失败。
"""
import io
import os
import re
import sys

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")):
    ROOT = P
elif os.path.isdir(os.path.join(P, "Resources", "Languages")):
    ROOT = os.path.dirname(os.path.dirname(P))
else:
    ROOT = P

CS_FILE = os.path.join(ROOT, "Auto7z_WPF", "Auto7z_WPF", "Services", "ShellRegistrationService.cs")
CPP_FILE = os.path.join(ROOT, "Auto7zShell", "src", "ShellExtension.cpp")

problems = []


def read(p):
    try:
        return io.open(p, encoding="utf-8", errors="replace").read()
    except OSError:
        return ""


print("=" * 74)
print("跨语言常量一致性：CLSID / Handler 名")
print("=" * 74)

# ---------------------------------------------------------------- C# 侧
cs = read(CS_FILE)
if not cs:
    print("!! 找不到 %s" % CS_FILE)
    sys.exit(2)

cs_clsid_m = re.search(r'ShellClsid\s*=\s*"\{([0-9A-Fa-f\-]+)\}"', cs)
cs_handler_m = re.search(r'ShellHandlerName\s*=\s*"([^"]+)"', cs)

if not cs_clsid_m:
    problems.append("C# 侧找不到 ShellClsid")
    cs_clsid = None
else:
    cs_clsid = cs_clsid_m.group(1).upper()

if not cs_handler_m:
    problems.append("C# 侧找不到 ShellHandlerName")
    cs_handler = None
else:
    cs_handler = cs_handler_m.group(1)

print("C# 侧:")
print("  ShellClsid       = %s" % cs_clsid)
print("  ShellHandlerName = %s" % cs_handler)

# ---------------------------------------------------------------- C++ 侧
cpp = read(CPP_FILE)
if not cpp:
    print("!! 找不到 %s" % CPP_FILE)
    sys.exit(2)

# C++ 的 CLSID 是 8 个数值：Data1, Data2, Data3, Data4[0..7]
cpp_clsid_m = re.search(
    r'kClassId\s*=\s*\{\s*(0x[0-9A-Fa-f]+)\s*,\s*(0x[0-9A-Fa-f]+)\s*,\s*(0x[0-9A-Fa-f]+)\s*,'
    r'(.*?)\}', cpp, re.S)
cpp_clsid = None
if cpp_clsid_m:
    d1 = int(cpp_clsid_m.group(1), 16)
    d2 = int(cpp_clsid_m.group(2), 16)
    d3 = int(cpp_clsid_m.group(3), 16)
    rest = re.findall(r'0x([0-9A-Fa-f]+)', cpp_clsid_m.group(4))
    if len(rest) == 8:
        d4 = [int(x, 16) for x in rest]
        cpp_clsid = "%08X-%04X-%04X-%02X%02X-%s" % (
            d1, d2, d3, d4[0], d4[1],
            "".join("%02X" % b for b in d4[2:]))

cpp_handler_m = re.search(r'kHandlerKeyName\s*\[\s*\]\s*=\s*L"([^"]+)"', cpp)
cpp_handler = cpp_handler_m.group(1) if cpp_handler_m else None

print("")
print("C++ 侧:")
print("  kClassId         = %s" % (cpp_clsid or "(解析失败)"))
print("  kHandlerKeyName  = %s" % (cpp_handler or "(找不到)"))

# ---------------------------------------------------------------- 比对
print("")
print("=== 比对 ===")

if cs_clsid and cpp_clsid:
    if cs_clsid == cpp_clsid:
        print("  OK   CLSID 一致")
    else:
        problems.append("CLSID 不一致：C#=%s C++=%s" % (cs_clsid, cpp_clsid))
        print("  !!   CLSID 不一致")
        print("       C# : %s" % cs_clsid)
        print("       C++: %s" % cpp_clsid)
        print("       后果：HasRegistryResidue() 查的不是产品注册的那个键，")
        print("             会永远报「无残留」，把没清理干净报成已注销。")
else:
    problems.append("CLSID 无法比对（一侧解析失败）")

if cs_handler and cpp_handler:
    if cs_handler == cpp_handler:
        print("  OK   Handler 名一致")
    else:
        problems.append("Handler 名不一致：C#=%s C++=%s" % (cs_handler, cpp_handler))
        print("  !!   Handler 名不一致")
else:
    problems.append("Handler 名无法比对")

# ---------------------------------------------------------------- 附：四条 shellex 路径
print("")
print("=== 附带检查：C# 侧四条残留路径是否齐全 ===")
EXPECTED = [
    r"Software\Classes\CLSID\ + ShellClsid",
    r"Software\Classes\\\*\\shellex\\ContextMenuHandlers",
    r"Software\Classes\Directory\\shellex\\ContextMenuHandlers",
    r"Software\Classes\\Folder\\shellex\\ContextMenuHandlers",
]
# 直接检查四个关键片段是否都出现
# 用「归一化」而不是「精确转义」来比对：
# repr() 显示成 \\ 只是 Python 的呈现方式，文件里的实际字节是单个反斜杠。
# 反复数转义层数容易出错（本轮已经错过两次），所以把两侧的反斜杠归一成 "/" 再比。
_flat = cs.replace("\\", "/")
frags = [
    ("Software/Classes/CLSID/", "CLSID 主键"),
    ("Software/Classes/*/shellex/ContextMenuHandlers/", "* (所有文件)"),
    ("Software/Classes/Directory/shellex/ContextMenuHandlers/", "Directory"),
    ("Software/Classes/Folder/shellex/ContextMenuHandlers/", "Folder"),
]
missing = [label for frag, label in frags if frag not in _flat]
if missing:
    print("  !!   缺少路径: %s" % ", ".join(missing))
    problems.append("残留检查缺少路径: " + ", ".join(missing))
else:
    print("  OK   四条 shellex 残留路径齐全")

# ---------------------------------------------------------------- 结论
print("")
print("=" * 74)
if problems:
    print("发现 %d 项问题：" % len(problems))
    for p in problems:
        print("  - " + p)
    print("")
    print("FAIL")
    sys.exit(1)

print("C# 与 C++ 的 CLSID / Handler 名完全一致")
print("PASS")
sys.exit(0)
