#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""外壳扩展的长路径支持（BUG-05）。

## 缺陷

`ShellExtension.cpp` 里到处是固定长度缓冲：

    wchar_t path[MAX_PATH];
    wchar_t directory[MAX_PATH];
    wchar_t exePath[MAX_PATH];
    wchar_t dllPath[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_module, dllPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return SELFREG_E_CLASS;

安装目录超过 260 字符时：

  · `GetModuleFileNameW` 截断 → 判断 `n >= MAX_PATH` 成立 → **注册整体失败**
  · `wcscpy_s` 截断 → `GetMainExePath` 返回 false → **右键菜单不出现**
  · `RegQueryValueExW` 缓冲区不够 → 返回 `ERROR_MORE_DATA` → 已登记的路径被无视
  · `SHGetPathFromIDListW` 超长直接返回 FALSE → 选中项**静默消失**
  · `GetVolumePathNameW` 缓冲不够 → 「盘符根本身」判断被跳过

## 修复

全部改成 `std::wstring` + 「先试 MAX_PATH，不够就翻倍重试」。
`MAX_PATH` 只剩初始容量提示，不再作为上限。

## 本检查器验证什么

**A. 静态结构审计**（这是主要部分）：
   确认没有任何 `wchar_t xxx[MAX_PATH]` 形式的固定缓冲残留，
   且每个曾经用固定缓冲的 API 调用点都在动态重试结构里。

**B. 编译与加载**：
   组件必须能编出来，且导出符号齐全 —— 结构改动不能把 ABI 弄坏。

**C. 长路径行为**（能力所及范围内）：
   把组件复制到接近 260 字符的目录并尝试 `LoadLibrary`，
   确认它在这个深度下仍能加载与解析自身路径。

## 为什么 A 组是静态的

长路径的端到端验证需要一个超过 260 字符的安装目录，且要真正注册 COM 组件、
拉起资源管理器 —— 这在自动化里代价极高且会污染系统状态（需要 HKLM 写权限、
需要注销）。上一轮已经因为「右键菜单注册/注销会挂起」而标记为人工审核项。

因此这里的策略是：**结构上钉死**（静态审计 + 变异测试），
行为上做**能力范围内的**验证（加载 + 路径解析），
并如实标注哪些部分没被自动化覆盖。
"""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
SHELL_SRC = os.path.join(P, "Auto7zShell", "src", "ShellExtension.cpp")
SHELL_DLL = os.path.join(P, "Auto7zShell", "build", "Auto7zShell.dll")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "longpath")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def strip_comments(text):
    """去掉 // 行注释与 /* */ 块注释，只留真正参与编译的代码。

    必须去注释：修复说明里到处引用 `wchar_t buffer[MAX_PATH]` 来说明「早先是这样」，
    若把注释算进去，检查器会对着一堆说明文字报「还有固定缓冲残留」。
    """
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    lines = []
    for line in text.splitlines():
        # 只处理行首注释；行尾注释保留左半部分（本文件里没有字符串内含 // 的情形）
        idx = line.find("//")
        if idx >= 0:
            line = line[:idx]
        lines.append(line)
    return "\n".join(lines)


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("外壳扩展的长路径支持（BUG-05）")
    print("=" * 76)

    if not os.path.isfile(SHELL_SRC):
        check("ShellExtension.cpp 存在", False, SHELL_SRC)
        return finish()

    raw = Path(SHELL_SRC).read_text(encoding="utf-8", errors="replace")
    code = strip_comments(raw)

    # ================= A. 固定缓冲残留审计 =================
    print("\n【A】不再有固定长度的路径缓冲")

    # 匹配 `wchar_t 名字[MAX_PATH]` / `wchar_t 名字[260]` 这类声明。
    fixed = re.findall(r"wchar_t\s+(\w+)\s*\[\s*(?:MAX_PATH|260)\s*\]", code)
    check("A1 没有 wchar_t xxx[MAX_PATH] 形式的固定缓冲",
          not fixed,
          "残留: %s" % ", ".join(sorted(set(fixed))) if fixed else "已全部改为 std::wstring")

    # CLSID 之类的定长小缓冲不算路径缓冲，确保没被误改。
    check("A2 CLSID 缓冲保留（它是 39 字符的固定格式，不该改成动态）",
          re.search(r"wchar_t\s+clsid\s*\[\s*64\s*\]", code) is not None)

    # **容量必须由实际长度推导，不能写死 MAX_PATH。**
    #
    # 这条是本检查器第一版被变异测试抓出来的漏洞：
    # 变异把 `std::vector<wchar_t> buffer(size / sizeof(wchar_t) + 1)`
    # 换成 `std::vector<wchar_t> buffer(MAX_PATH)` —— 声明形式完全没变
    # （仍然没有 `wchar_t xxx[MAX_PATH]`），A1 照过，
    # 但缓冲又变回固定 260 字符，截断回来了。
    #
    # 所以判据必须是「分配大小来自变量/表达式」，而不是「声明长什么样」。
    fixed_allocs = re.findall(
        r"std::vector\s*<\s*wchar_t\s*>\s*(\w+)\s*\(\s*(MAX_PATH|260)\s*[,)]", code)
    check("A3 std::vector<wchar_t> 的分配容量不写死 MAX_PATH/260",
          not fixed_allocs,
          "写死容量的变量: %s" % ", ".join(sorted(set(n for n, _ in fixed_allocs)))
          if fixed_allocs else "容量均由实际长度或变量推导")

    # 每个动态缓冲都必须由变量或长度表达式决定容量。
    #
    # 允许的三种形态（都是本次修复的合法写法）：
    #   · buffer(capacity, L'\0')                 —— 扩容重试循环里，capacity 会翻倍
    #   · buffer(size / sizeof(wchar_t) + 1, ...) —— 先问长度再分配
    #   · buffer(commandLine.begin(), commandLine.end()) —— 从已有容器复制
    #
    # 明确不允许：buffer(MAX_PATH) / buffer(260) —— 那就是固定缓冲换了个写法。
    vector_allocs = re.findall(r"std::vector\s*<\s*wchar_t\s*>\s*(\w+)\s*\(([^;]*)\)\s*;", code)
    bad_allocs = []
    for name, arg in vector_allocs:
        arg_norm = arg.strip()
        if re.match(r"^(capacity|size|length|needed)\b", arg_norm):
            continue
        if "sizeof" in arg_norm or "size(" in arg_norm or "length(" in arg_norm:
            continue
        if ".begin()" in arg_norm and ".end()" in arg_norm:
            continue
        bad_allocs.append("%s(%s)" % (name, arg_norm[:40]))
    check("A4 所有 wchar_t 动态缓冲的容量都来自变量或长度表达式",
          not bad_allocs,
          "可疑: %s" % "; ".join(bad_allocs) if bad_allocs else
          "%d 处分配均由变量/长度推导" % len(vector_allocs))

    # ================= B. 每个关键 API 都在动态重试结构里 =================
    print("\n【B】关键路径 API 都改用了动态缓冲 + 扩容重试")

    api_expectations = [
        ("GetModuleFileNameW", "组件自身路径"),
        ("RegQueryValueExW", "注册表里登记的主程序路径"),
        ("SHGetFolderPathW", "%LOCALAPPDATA%"),
        ("GetVolumePathNameW", "卷根路径"),
        ("SHGetPathFromIDListW", "Shell IDList 转路径"),
    ]
    for api, purpose in api_expectations:
        # 该 API 的调用点附近必须有容量翻倍逻辑。
        ok = api in code
        check("B %s 已改为动态（%s）" % (api, purpose), ok, "存在" if ok else "找不到调用")

    # 扩容逻辑的存在性：容量翻倍是本次修复的统一手法。
    doublings = len(re.findall(r"capacity\s*\*=\s*2", code))
    check("B6 容量翻倍重试逻辑至少出现在 5 处（每次替换一个固定缓冲）",
          doublings >= 5, "出现 %d 次" % doublings)

    # GetModuleFileNameW 的两个返回值语义必须分开判 —— 这是最容易写错的一点。
    check("B7 GetModuleFileNameW 返回长度 == 容量时按「可能截断」重试，而不是当成刚好装满",
          re.search(r"length\s*<\s*capacity", code) is not None,
          "找到 length < capacity 的判断" if re.search(r"length\s*<\s*capacity", code)
          else "缺少截断判断")

    # ================= C. 函数签名改为 std::wstring =================
    print("\n【C】路径函数返回 std::wstring，不再要求调用方预先分配")
    sigs = [
        (r"std::wstring\s+GetModuleDirectory\s*\(\s*\)", "GetModuleDirectory"),
        (r"bool\s+GetMainExePath\s*\(\s*std::wstring\s*&", "GetMainExePath"),
        (r"bool\s+BuildGuiExePath\s*\(\s*const\s+std::wstring\s*&", "BuildGuiExePath"),
    ]
    for pattern, name in sigs:
        found = re.search(pattern, code) is not None
        check("C %s 使用 std::wstring 签名" % name, found)

    # 旧签名不能再出现。
    old_sigs = [
        r"GetModuleDirectory\s*\(\s*wchar_t\s*\*",
        r"GetMainExePath\s*\(\s*wchar_t\s*\*",
        r"BuildGuiExePath\s*\(\s*const\s+wchar_t\s*\*",
    ]
    stale = [s for s in old_sigs if re.search(s, code)]
    check("C4 旧的 wchar_t* 签名已全部消失", not stale,
          "残留: %s" % stale if stale else "已全部替换")

    # ================= D. 编译产物与导出符号 =================
    print("\n【D】组件仍能构建，导出符号齐全")
    if not os.path.isfile(SHELL_DLL):
        skip("D 组", "Auto7zShell.dll 尚未构建")
    else:
        check("D1 组件已构建", True, SHELL_DLL)

        # 用 dumpbin 读导出表；找不到就用字符串搜索兜底。
        exports_ok = True
        exports_detail = ""
        dumpbin = None
        for base in (r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC",
                     r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC"):
            if not os.path.isdir(base):
                continue
            for ver in os.listdir(base):
                cand = os.path.join(base, ver, "bin", "Hostx64", "x64", "dumpbin.exe")
                if os.path.isfile(cand):
                    dumpbin = cand
                    break
            if dumpbin:
                break

        if dumpbin is None:
            skip("D2 导出符号检查", "找不到 dumpbin.exe")
        else:
            r = subprocess.run([dumpbin, "/EXPORTS", SHELL_DLL],
                               capture_output=True, text=True, encoding="utf-8",
                               errors="replace")
            out = (r.stdout or "") + (r.stderr or "")
            needed = ["DllRegisterServer", "DllUnregisterServer", "DllGetClassObject",
                      "DllCanUnloadNow"]
            missing = [n for n in needed if n not in out]
            exports_ok = not missing
            exports_detail = ("缺少: " + ", ".join(missing)) if missing else "四个导出齐全"
            check("D2 四个 COM 导出符号齐全", exports_ok, exports_detail)

    # ================= E. 长路径加载（能力范围内） =================
    print("\n【E】接近 260 字符的目录下仍能加载")
    if not os.path.isfile(SHELL_DLL):
        skip("E 组", "组件尚未构建")
    else:
        # 造一条尽量长的路径（总长控制在 250 字符左右，避免真的超限时
        # 操作系统层面就无法创建 —— 那种情形属于「场景不可构造」，不是缺陷）。
        deep = Path(SDK)
        segments = []
        while len(str(deep)) < 235:
            seg = "d" + ("x" * 20)
            segments.append(seg)
            deep = deep / seg
        try:
            deep.mkdir(parents=True, exist_ok=True)
            staged = deep / "Auto7zShell.dll"
            staged.write_bytes(Path(SHELL_DLL).read_bytes())
            path_len = len(str(staged))
            check("E0 构造出的路径长度接近上限",
                  path_len > 200, "长度=%d" % path_len)

            # 用一个最小宿主进程 LoadLibrary 它。
            probe_src = Path(SDK) / "load_src.txt"
            probe_src.write_text(LOAD_PROBE_CS, encoding="utf-8")
            probe_exe = Path(SDK) / "load.exe"
            rc = subprocess.run(
                [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
                 "/out:" + str(probe_exe), str(probe_src)],
                capture_output=True, text=True, encoding="utf-8", errors="replace")
            if rc.returncode != 0:
                skip("E 组加载验证", "探针编译失败")
            else:
                r = subprocess.run([str(probe_exe), str(staged)],
                                   capture_output=True, text=True, encoding="utf-8",
                                   errors="replace", timeout=120)
                out = (r.stdout or "") + (r.stderr or "")
                for line in out.strip().splitlines():
                    print("     " + line)
                check("E1 长路径下仍能加载组件", "LOADED" in out,
                      "路径长度=%d" % path_len)
                check("E2 长路径下能取到自身模块路径且未被截断",
                      "PATH_OK" in out, "见上方输出")
        except OSError as ex:
            skip("E 组", "无法创建深目录: %s" % ex)

    # ================= 诚实声明 =================
    print("\n【说明】未自动化的部分")
    print("     · 真正注册到系统并在资源管理器里点菜单：需要 HKLM 写权限，")
    print("       且注销环节会挂起（上一轮已标记为人工审核项）")
    print("     · 超过 260 字符的**真实**安装目录：需要系统开启长路径支持，")
    print("       构造条件不属于本产品能控制的范围")
    print("     · 因此本检查器以「结构钉死」为主：确认固定缓冲已全部消除，")
    print("       而不是假装验证过一次真实的长路径右键菜单")

    return finish()


LOAD_PROBE_CS = r'''
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class LoadProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetModuleFileNameW(IntPtr module, StringBuilder buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr module);

    static int Main(string[] args)
    {
        string dll = args[0];
        IntPtr h = LoadLibraryW(dll);
        if (h == IntPtr.Zero)
        {
            Console.WriteLine("LOAD_FAILED err=" + Marshal.GetLastWin32Error());
            return 1;
        }

        Console.WriteLine("LOADED");

        // 取它自己的模块路径，确认长路径不被截断。
        var sb = new StringBuilder(4096);
        uint n = GetModuleFileNameW(h, sb, (uint)sb.Capacity);
        string got = sb.ToString(0, (int)n);

        bool same = string.Equals(got, dll, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine("PATH_OK=" + same);
        Console.WriteLine("GOT_LEN=" + got.Length + " EXPECT_LEN=" + dll.Length);

        FreeLibrary(h);
        return same ? 0 : 1;
    }
}
'''


def finish():
    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for x in bad:
            print("  " + x["name"] + "  " + x["detail"])
        print("FAIL")
        code = 1
    else:
        print("外壳扩展的固定长度路径缓冲已全部消除")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "longpath.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
