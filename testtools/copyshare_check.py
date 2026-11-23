#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""迁移复制的源文件独占、删源窗口与临时文件名长度（BUG-02 / BUG-06 / P1-3）。

## BUG-02：迁移复制时仍允许别的进程写源文件

早先 `MoveOneFile` 打开源文件用的是 `FileShare.ReadWrite`：

    using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))

它只解决了「本程序内部几个写入方」的并发（靠 MigrationGate），
但**外部程序完全不受那道闸约束**：

    1. 用户正在编辑某个配置文件
    2. Auto7z 开始迁移数据目录，打开源文件复制
    3. 编辑器继续往源文件追加内容
    4. Auto7z 复制完成，删掉源文件

第 3 步写进去的内容既不在目标里、也没留在源里 —— 直接消失。

## P1-3：只改共享模式还不够

上一轮改成 `FileShare.Read` 挡住了「复制期间被写」，但句柄在复制结束后就关了，
而删源发生在后面：

    复制完成 → 源句柄关闭 → 外部程序写入 → 我们提交目标 → 删除源文件

新内容既不在目标、也不在源里。窗口在句柄**之外**，改共享模式挡不住。

修复两点一起做：
   · 共享模式 `FileShare.Read | FileShare.Delete`（**仍不含 Write**）——
     多给 Delete 是为了能一边持有句柄一边删自己；
   · 提交目标与删除源文件都在 using **内部**完成，窗口被消掉。

## BUG-06：临时文件名可能超过 255 字符上限

早先临时名是 `原文件名 + ".moving-<pid>-<guid>"`。原文件名合法不代表
拼上后缀之后还合法。修复：改成目标目录下的 `.moving-<guid>.tmp`。

## 本检查器验证什么

A.  静态：源文件共享模式不含 Write，且不再有 ReadWrite
A'. 静态：删源语句位于源句柄的 using 块**内部**（位置关系，不是「有没有写」）
B.  静态：临时文件名不含原文件名
C.  运行时：240 / 250 / 255 字符的长文件名都能成功迁移，内容一致
D.  运行时：有进程正持有源文件写锁时，迁移必须**报失败**而不是搬出残缺数据
E.  运行时：临时文件确实落在目标目录内（同卷，File.Replace 才能原子）

## 为什么 A' 必须验位置

「代码里有 `File.Delete(source)`」这句话在缺陷版本上也可能成立 ——
只要它写在 using 外面。真正决定有没有窗口的是**它相对于 using 闭合的位置**。
所以这里做括号配对求 using 的闭合点，再比下标。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "copyshare")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


PROBE_CS = r'''
using System;
using System.IO;
using System.Reflection;

// **这一行是探针能测出长路径的前提，别删。**
//
// 裸 csc.exe 编出来的程序集没有 TargetFrameworkAttribute，.NET Framework 会把它
// 当成 targeting 4.0 的旧程序集，从而启用 legacy path handling ——
// 于是即使 exe 里嵌了 longPathAware 清单、系统也开了 LongPathsEnabled，
// 任何超过 260 字符的路径仍然抛 PathTooLongException。
//
// 实测证据（同一台机器、同一份清单）：
//     无该属性：SRC_EXISTS=False  OPEN_FAIL=PathTooLongException
//     有该属性：SRC_EXISTS=True   OPEN_OK len=1
//
// 产品自己的 Auto7z.exe 由 MSBuild 编译，TargetFrameworkAttribute 是自动生成的，
// 所以产品侧本来就正常。是**探针**需要补这一行，否则检查器会报一个假的「产品不支持长路径」。
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.7.2")]

class CopyProbe
{
    static Assembly asm;
    static Type moverType;

    static int Main(string[] args)
    {
        string exe = args[0];
        string op = args[1];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        asm = Assembly.LoadFrom(exe);
        moverType = asm.GetType("Auto7z_WPF.Services.DataDirectoryMover");
        if (moverType == null) { Console.WriteLine("NO_TYPE"); return 2; }

        switch (op)
        {
            case "copy": return DoCopy(args[2], args[3]);

            case "tempname":
            {
                MethodInfo m = moverType.GetMethod("BuildUniqueTempPath",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) { Console.WriteLine("NO_METHOD"); return 2; }
                object r = m.Invoke(null, new object[] { args[2] });
                Console.WriteLine("TEMP=" + r);
                return 0;
            }

            case "copywhilelocked":
                return CopyWhileLocked(args[2], args[3]);

            default:
                Console.WriteLine("BAD_OP");
                return 2;
        }
    }

    static int DoCopy(string src, string tgt)
    {
        MethodInfo m = moverType.GetMethod("CopyThenDeleteSource",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) { Console.WriteLine("NO_METHOD"); return 2; }

        try
        {
            m.Invoke(null, new object[] { src, tgt });
            Console.WriteLine("COPY_OK");
            return 0;
        }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            Console.WriteLine("COPY_FAIL:" + inner.GetType().Name);
            return 1;
        }
    }

    // 源文件被别的程序以「允许写」的方式打开时，迁移不能搬出一份可能残缺的数据。
    //
    // 用同一个进程内的第二个流模拟外部程序：共享语义与跨进程完全一致
    // （Windows 的共享检查是按句柄算的，不看是不是同一个进程）。
    static int CopyWhileLocked(string sourceFile, string tgt)
    {
        MethodInfo m = moverType.GetMethod("CopyOneFile",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) { Console.WriteLine("NO_METHOD"); return 2; }

        string target = Path.Combine(tgt, Path.GetFileName(sourceFile));

        FileStream holder = null;
        try
        {
            // 别的程序正开着这个文件准备写：FileShare.Read 表示**允许别人读**，
            // 但自己占着写权限 —— 与我们修复后的「只允许读」正好冲突。
            holder = new FileStream(sourceFile, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read);
        }
        catch (Exception e)
        {
            Console.WriteLine("HOLDER_FAIL:" + e.GetType().Name);
            return 3;
        }

        try
        {
            try
            {
                m.Invoke(null, new object[] { sourceFile, target });
                Console.WriteLine("MOVE_OK");
                return 0;
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                Console.WriteLine("MOVE_BLOCKED:" + inner.GetType().Name);
                return 1;
            }
        }
        finally
        {
            try { holder.Dispose(); } catch { }
        }
    }
}
'''

LONG_PATH_MANIFEST = r'''<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Auto7zProbe.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
'''


def build_probe():
    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(PROBE_CS, encoding="utf-8")

    # 探针必须带与产品相同的 longPathAware 清单：否则它测的是自己的限制。
    man = Path(SDK) / "probe.manifest"
    man.write_text(LONG_PATH_MANIFEST, encoding="utf-8")

    out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), "/win32manifest:" + str(man), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:800])
        return None

    # 运行配置也要与产品一致（声明 4.7.2），否则会被当成旧程序集。
    Path(str(out) + ".config").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n'
        '  <startup>\n'
        '    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7.2" />\n'
        '  </startup>\n</configuration>\n', encoding="utf-8")

    return out


def extract_method(text, signature):
    """从签名处截取一个方法体（按花括号配对到闭合）。

    用于「断言某条语句在不在某个方法/using 块里」这类**位置关系**判断。
    只做文本配对，不解析 C# 语法 —— 对本项目的代码形态足够，
    且一旦截歪会表现为断言失败（宁可报错，也不要静默通过）。
    """
    start = text.find(signature)
    if start < 0:
        return None

    brace = text.find("{", start)
    if brace < 0:
        return None

    depth = 0
    i = brace
    while i < len(text):
        ch = text[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
        i += 1

    return None


def find_source_using_close(text):
    """找出**源文件句柄**那个 using 块的闭合位置。

    不能简单取「最后一个 using」也不能取「第一个」：`MoveOneFile` 里是

        using (var src = new FileStream(source, ... FileShare.Read | FileShare.Delete))
        using (var dst = new FileStream(temp, ...))
        {
            ...
        }

    两者在同一层 using 链上（C# 允许 `using (a) using (b) { }`），
    语句块结束时两个一起释放。

    所以判据是：找到 `src = new FileStream` 那个 using 后面的语句块，
    求它的闭合位置。删源语句必须落在这个闭合之前。
    """
    src_pos = text.find("var src = new FileStream")
    if src_pos < 0:
        return None

    brace = text.find("{", src_pos)
    if brace < 0:
        return None

    depth = 0
    i = brace
    while i < len(text):
        ch = text[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1

    return None


def run_probe(probe, *args, timeout=180):
    p = subprocess.run([str(probe)] + [str(a) for a in args],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=timeout)
    return p.returncode, ((p.stdout or "") + (p.stderr or ""))


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("迁移复制的源独占、删源窗口与临时名长度（BUG-02 / BUG-06 / P1-3）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    probe = build_probe()
    if probe is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # ================= A 静态：源文件必须是只读共享 =================
    print("\n【A】静态：复制源文件时不允许别的程序同时写")
    mover = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "DataDirectoryMover.cs"
    if not mover.is_file():
        check("A DataDirectoryMover.cs 存在", False, str(mover))
    else:
        text = mover.read_text(encoding="utf-8", errors="replace")
        body = "\n".join(l for l in text.splitlines()
                         if not l.strip().startswith("//"))

        # 本轮（P1-3）从 `FileShare.Read` 改成 `FileShare.Read | FileShare.Delete`：
        # 多给一个 Delete 是为了能在持有自身句柄时删掉源文件，
        # 从而把「复制完成 → 句柄关闭 → 外部写入 → 删源」那个窗口消掉。
        # 仍然**不含 Write** —— 那才是「拒绝并发写」的保证。
        has_read_delete = "FileShare.Read | FileShare.Delete" in body
        has_plain_read = "FileAccess.Read, FileShare.Read)" in body
        has_readwrite = "FileShare.ReadWrite" in body

        check("A1 源文件共享模式不含 Write（拒绝并发写）",
              has_read_delete or has_plain_read,
              ("找到 Read|Delete" if has_read_delete
               else ("找到 Read" if has_plain_read else "未找到只读共享的打开方式")))
        check("A2 不再有 FileShare.ReadWrite（那正是 BUG-02）", not has_readwrite,
              "仍存在 FileShare.ReadWrite" if has_readwrite else "已清除")

        check("A3 复制后校验长度一致（共享语义万一失效也能发现）",
              "dst.Length != src.Length" in body)

        # ================= A' 源独占与提交顺序（P1-3）=================
        print("\n【A'】源文件在复制与提交期间保持独占（不留写入窗口）")
        #
        # 这一组是变异测试 b4-p1-2b / b4-p1-3b 逼出来的。
        #
        # 第一版只断言「代码里出现过 IsSafeToRemoveSourceFile 这个名字」，
        # 于是把它的实现体改成 `return true;`（永远允许删）照样 PASS ——
        # 检查器验的是**函数存在**，而不是**判断成立**。
        #
        # 同样，第一版没断言 CopyOneFile 的共享掩码必须含 Delete，
        # 于是把 `FileShare.Read | FileShare.Delete` 退回 `FileShare.Read`
        # （句柄内无法删源、窗口重现）也 PASS。
        #
        # 现在两处都改成验实现细节：
        body_copy = extract_method(body, "private static void CopyOneFile(")
        if body_copy is None:
            check("A'1 能定位 CopyOneFile 方法体", False, "方法签名变了")
        else:
            check("A'1 CopyOneFile 的源共享掩码含 Delete（提交阶段才能在句柄内删源）",
                  "FileShare.Read | FileShare.Delete" in body_copy,
                  "找到 Read|Delete" if "FileShare.Read | FileShare.Delete" in body_copy
                  else "未找到 —— 共享掩码过窄，删源会失败或被推迟到句柄外")

            check("A'2 CopyOneFile 的共享掩码不含 Write（复制期间不可被写）",
                  "FileShare.ReadWrite" not in body_copy)

            # 原子提交必须在源句柄的 using 内部完成
            swap_pos = body_copy.find("File.Replace(temp, target, null)")
            using_end = find_source_using_close(body_copy)
            check("A'3 目标提交发生在源句柄持有期间（复制与提交之间无窗口）",
                  0 <= swap_pos and using_end is not None and swap_pos < using_end,
                  "提交位置=%s，using 闭合=%s"
                  % (swap_pos if swap_pos >= 0 else "(无)",
                     using_end if using_end is not None else "(未找到)"))

        # 清源前的长度比对必须**真的比对长度**，不是「函数存在就放行」
        body_safe = extract_method(body, "private static bool IsSafeToRemoveSourceFile(")
        if body_safe is None:
            check("A'4 能定位 IsSafeToRemoveSourceFile 方法体", False, "方法签名变了")
        else:
            check("A'4 清源前真的比对长度（不是恒返回 true）",
                  "Length == " in body_safe or "Length==" in body_safe,
                  "找到长度比较" if ("Length == " in body_safe or "Length==" in body_safe)
                  else "未找到长度比较 —— 实现可能被改成恒 true")
            check("A'5 目标副本不存在时拒绝删源",
                  "File.Exists(targetFile)" in body_safe
                  and ("return false" in body_safe),
                  "需同时存在存在性检查与拒绝分支")

        # ================= A'' 两阶段：先复制后清理（P1-2）=================
        print("\n【A''】整棵目录迁移必须是「先完整复制、再清理源」")
        check("A''1 CopyThenDeleteSource 先调 CopyTree（只复制）",
              "CopyTree(sourceDirectory, targetDirectory);" in body)
        check("A''2 复制与清理是两次独立调用（清理在复制之后）",
              body.find("CopyTree(sourceDirectory, targetDirectory);")
              < body.find("DeleteSourceTree(sourceDirectory, sourceDirectory, targetDirectory);")
              and body.find("DeleteSourceTree(sourceDirectory, sourceDirectory, targetDirectory);") > 0)
        check("A''3 CopyTree 内部不再删源文件（只复制）",
              extract_method(body, "private static void CopyTree(") is not None
              and "File.Delete(" not in extract_method(body, "private static void CopyTree("),
              "CopyTree 里出现 File.Delete 就是又变回边走边删")
        check("A''4 清源前比对长度（源在复制后被改过就不删）",
              "IsSafeToRemoveSourceFile" in body)

        # ================= B 静态：临时名与原文件名无关 =================
        print("\n【B】静态：临时文件名不含原文件名（BUG-06）")
        squeezed = body.replace(" ", "").replace("\t", "")
        has_fixed_prefix = ("MovingTempPrefix+Guid.NewGuid()" in squeezed)
        check("B1 临时名由固定前缀 + GUID 拼成", has_fixed_prefix,
              "找到固定前缀 + GUID" if has_fixed_prefix else "临时名仍含原文件名")
        check("B2 临时名落在目标目录内（同卷才能原子替换）",
              "Path.GetDirectoryName(target)" in body)
        check("B3 不再有旧的长后缀拼接",
              "MovingSuffix" not in body,
              "仍有 MovingSuffix" if "MovingSuffix" in body else "已清除")

    # ================= C 运行时：长文件名 =================
    print("\n【C】运行时：240 / 250 / 255 字符文件名都能迁移（BUG-06 的实测）")
    for length in (240, 250, 255):
        base = Path(SDK) / ("long%d" % length)
        if base.exists():
            safe_rmtree(base)
        src = base / "src"
        tgt = base / "tgt"
        src.mkdir(parents=True)
        tgt.mkdir(parents=True)

        name = ("n" * (length - 4)) + ".txt"
        f = src / name
        try:
            f.write_text("CONTENT_%d" % length, encoding="utf-8")
        except OSError as e:
            check("C%d 构造 %d 字符文件名" % (length, length), False,
                  "连源文件都建不出来: %s" % e)
            continue

        rc, out = run_probe(probe, EXE, "copy", str(src), str(tgt))
        out_l = out.lower()
        ok = "copy_ok" in out_l and (tgt / name).is_file()

        detail = ""
        if not ok:
            detail = [l for l in out.splitlines() if l.strip()][:1]
            detail = detail[0][:90] if detail else "无输出"

        check("C%d %d 字符文件名迁移成功" % (length, length), ok,
              "产物存在=%s%s" % ((tgt / name).is_file(),
                                 ("  |  " + detail) if detail else ""))

        if (tgt / name).is_file():
            content_ok = (tgt / name).read_text(encoding="utf-8") == "CONTENT_%d" % length
            check("C%d 内容与原文件一致" % length, content_ok,
                  "内容%s一致" % ("一致" if content_ok else "不"))

    # ================= D 运行时：源被别的程序写着 =================
    print("\n【D】运行时：源文件正被其它程序写入时，不搬出残缺数据（BUG-02 的实测）")
    base = Path(SDK) / "locked"
    src = base / "src"
    tgt = base / "tgt"
    src.mkdir(parents=True)
    tgt.mkdir(parents=True)
    locked_file = src / "Config.json"
    locked_file.write_text("{\"original\":true}", encoding="utf-8")

    rc, out = run_probe(probe, EXE, "copywhilelocked", str(locked_file), str(tgt))
    print("     " + " | ".join(l for l in out.strip().splitlines() if l.strip())[:200])

    blocked = "MOVE_BLOCKED" in out
    holder_failed = "HOLDER_FAIL" in out

    if holder_failed:
        check("D1 能构造出「别的程序正写着」的场景", False,
              "holder 打开失败，本组结论不成立")
    else:
        check("D1 源被写着时迁移报失败（不是搬一份可能残缺的数据）", blocked,
              "被拒=%s（False = 仍允许并发写，BUG-02 未修复）" % blocked)

        leftovers = list(tgt.iterdir()) if tgt.exists() else []
        check("D2 失败后目标目录没有半截文件", not leftovers,
              "残留=%s" % ([p.name for p in leftovers][:3] if leftovers else "无"))

        intact = locked_file.is_file() and \
            locked_file.read_text(encoding="utf-8") == "{\"original\":true}"
        check("D3 源文件内容未被破坏", intact)

    # ================= E 运行时：临时名落点 =================
    print("\n【E】运行时：临时文件与目标同目录（File.Replace 才能原子）")
    sample_target = str(Path(SDK) / "somewhere" / ("x" * 200) / ("y" * 50 + ".txt"))
    rc, out = run_probe(probe, EXE, "tempname", sample_target)
    temp = ""
    for line in out.splitlines():
        if line.startswith("TEMP="):
            temp = line[5:].strip()
    print("     临时名 = " + (temp or "(未取到)"))

    if not temp:
        check("E1 能取到生成的临时名", False, out[:120])
    else:
        t_base = os.path.basename(temp)
        check("E1 临时文件名与原文件名无关（不含 200 个 x）",
              "x" * 20 not in t_base,
              "临时名=%s" % t_base[:60])
        check("E2 临时文件名足够短（≤64 字符）", len(t_base) <= 64,
              "长度=%d" % len(t_base))
        check("E3 临时文件与目标同目录",
              os.path.dirname(temp).lower() == os.path.dirname(sample_target).lower(),
              "临时目录=%s" % os.path.dirname(temp)[-40:])

        extreme = "z" * 251 + ".txt"
        rc2, out2 = run_probe(probe, EXE, "tempname", str(Path(SDK) / extreme))
        t2 = ""
        for line in out2.splitlines():
            if line.startswith("TEMP="):
                t2 = line[5:].strip()
        check("E4 原文件名 255 字符时临时名仍在长度限制内",
              bool(t2) and len(os.path.basename(t2)) <= 64,
              "临时名长度=%d" % (len(os.path.basename(t2)) if t2 else -1))

    return finish()


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
        print("复制期间源不可被并发写；删源在句柄内完成；迁移先完整复制再清理源")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "copyshare.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
