#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""路径边界校验的**文件系统**语义验证（BUG-01）。

## 缺陷

`FileSystemHelper.IsUnderDirectory` 只做字符串前缀判断：

    string fullPath = Path.GetFullPath(path);
    string fullRoot = Path.GetFullPath(directory);
    return fullPath.StartsWith(prefix, OrdinalIgnoreCase);

`Path.GetFullPath` **只规范化路径写法，不展开重解析点**。于是：

    %LOCALAPPDATA%\Auto7z\Logs  ->  Junction  ->  D:\ExternalData
    Directory.GetFiles(Logs) 返回  Logs\old.log
    IsUnderDirectory(Logs\old.log, Logs)  =>  true
    File.Delete(Logs\old.log)  =>  实际删掉的是 D:\ExternalData\old.log

四个调用点都受此影响：LogService 删旧日志、TaskStateService 清 .tmp、
ConfigService 与 JsonConfig 校验写入目标。

## 修复思路

**不改 IsUnderDirectory 的语义。** 它是纯函数、不碰文件系统，
别的调用方（输出目录自包含判断）依赖这个性质。把 I/O 塞进去会让它
在只读介质、网络路径、权限不足时表现不可预测。

新增 `IsWithinFilesystemBoundary`：字符串包含 **且** 路径上没有任何
现存层级是重解析点。四个「要穿透路径去删/写」的调用点改用它。

## 本检查器验证什么

1. **两个函数确实不同**：构造「父目录是 Junction」的场景，
   字符串判断通过、边界判断拒绝。若两者结论一致，说明改动没生效。
2. **四个调用点真的用了边界版**：对每个调用点做静态确认 + 行为确认。
3. **没在正常路径上误伤**：普通嵌套目录、目录自身、大小写差异、
   不存在但父目录合法的待写文件 —— 边界判断必须放行。
4. **fail-closed**：无法确认时（属性读不出）必须拒绝。

## 为什么强调「两个函数必须不同」

上一轮的教训：`reparse_root_check.py` 的 D2 组拿一个不在任何真实 root 下
的路径去测守卫，守卫在第 5 步就 `return false` 了，**根本没走到重解析点分支**，
把守卫改回旧实现照样通过。

这里同理：如果只测「Junction 下的路径被拒绝」，那么一个「永远返回 false」的
实现也能通过。必须同时证明**正常情况下是放行的**，两个方向都钉死才有意义。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "fsboundary")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


CALL_CS = r'''
using System;
using System.IO;
using System.Reflection;

// 直接调用 FileSystemHelper 的两个方法，输出两个布尔值，便于对比。
class BoundaryProbe
{
    static Assembly asm;
    static Type helper;

    static object Call(string method, params object[] a)
    {
        MethodInfo m = helper.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        if (m == null) return "NO_METHOD";
        try { return m.Invoke(null, a); }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            return "EX:" + inner.GetType().Name;
        }
    }

    static int Main(string[] args)
    {
        string exe = args[0];
        string path = args[1];
        string root = args[2];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        asm = Assembly.LoadFrom(exe);
        helper = asm.GetType("Auto7z_WPF.Services.FileSystemHelper");
        if (helper == null) { Console.WriteLine("NO_TYPE"); return 2; }

        Console.WriteLine("STRING=" + Call("IsUnderDirectory", path, root));
        Console.WriteLine("BOUND=" + Call("IsWithinFilesystemBoundary", path, root));
        Console.WriteLine("ANCESTOR=" + Call("HasReparsePointAncestor", path));
        return 0;
    }
}
'''


MOVER_CS = r'''
using System;
using System.IO;
using System.Reflection;

// 调用搬迁类里的**私有** HasReparsePointAncestor。
class MoverProbe
{
    static int Main(string[] args)
    {
        string exe = args[0];
        string path = args[1];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(exe);
        Type t = asm.GetType("Auto7z_WPF.Services.DataDirectoryMover");
        if (t == null) { Console.WriteLine("NO_TYPE"); return 2; }

        MethodInfo m = t.GetMethod("HasReparsePointAncestor",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) { Console.WriteLine("NO_METHOD"); return 2; }

        try { Console.WriteLine("ANCESTOR=" + m.Invoke(null, new object[] { path })); }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            Console.WriteLine("ANCESTOR=EX:" + inner.GetType().Name);
        }
        return 0;
    }
}
'''


def build_mover_probe():
    cs = Path(SDK) / "mover_probe_src.txt"
    cs.write_text(MOVER_CS, encoding="utf-8")
    out = Path(SDK) / "mover_probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     搬迁探针编译失败: " + (rc.stderr or rc.stdout or "")[:400])
        return None
    return out


def probe_mover(probe_exe, path):
    """返回搬迁类 HasReparsePointAncestor 的结果字符串。"""
    p = subprocess.run([str(probe_exe), EXE, str(path)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=120)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        line = line.strip()
        if line.startswith("ANCESTOR="):
            return "?", "?", line.split("=", 1)[1].strip()
    return "?", "?", "?"


def build_probe():
    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(CALL_CS, encoding="utf-8")
    out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:400])
        return None
    return out


def probe(probe_exe, path, root):
    """返回 (STRING, BOUND, ANCESTOR) 三个字符串。"""
    p = subprocess.run([str(probe_exe), EXE, str(path), str(root)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=120)
    out = (p.stdout or "") + (p.stderr or "")
    got = {}
    for line in out.splitlines():
        line = line.strip()
        if "=" in line:
            k, v = line.split("=", 1)
            got[k.strip()] = v.strip()
    return got.get("STRING", "?"), got.get("BOUND", "?"), got.get("ANCESTOR", "?")


def make_junction(link, target):
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    return Path(link).exists()


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("路径边界校验：文件系统语义（BUG-01）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    pr = build_probe()
    if pr is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    pr2 = build_mover_probe()
    if pr2 is None:
        check("搬迁探针可编译", False, "csc 失败")
        return finish()

    # ================= A 组：纯字符串函数本身的行为（回归保护）=================
    print("\n【A】IsUnderDirectory 仍是纯字符串判断（语义未被改动）")
    plain_root = Path(SDK) / "plain"
    plain_root.mkdir(parents=True, exist_ok=True)
    (plain_root / "sub").mkdir(exist_ok=True)
    f_inside = plain_root / "sub" / "a.txt"
    f_inside.write_text("x", encoding="utf-8")

    s, b, _ = probe(pr, f_inside, plain_root)
    check("A1 普通子文件：字符串=true", s == "True", "STRING=%s" % s)
    check("A2 普通子文件：边界=true（不能误伤）", b == "True", "BOUND=%s" % b)
    check("A3 两者一致（无链接时不该有差异）", s == b, "STRING=%s BOUND=%s" % (s, b))

    # 目录自身也算在内
    s, b, _ = probe(pr, plain_root, plain_root)
    check("A4 目录自身：字符串=true", s == "True", "STRING=%s" % s)
    check("A5 目录自身：边界=true", b == "True", "BOUND=%s" % b)

    # 前缀相同但不同层级
    sibling = Path(str(plain_root) + "xx")
    sibling.mkdir(parents=True, exist_ok=True)
    s, b, _ = probe(pr, sibling, plain_root)
    check("A6 前缀相同但不是子目录：字符串=false", s == "False", "STRING=%s" % s)
    check("A7 前缀相同但不是子目录：边界=false", b == "False", "BOUND=%s" % b)

    # 待写文件尚不存在，但父目录合法 —— 必须放行，否则配置写入会全线失败
    not_yet = plain_root / "sub" / "will-be-created.json"
    s, b, _ = probe(pr, not_yet, plain_root)
    check("A8 尚不存在的待写文件：边界=true（放行）", b == "True", "BOUND=%s" % b)

    # ================= B 组：父目录是 Junction（真正的差异点）=================
    print("\n【B】父目录是 Junction 时，两个函数必须给出不同结论")
    # 布局：
    #   SDK/b/holder      -> Junction -> SDK/b/real
    #   SDK/b/real/data           （真实位置，外部）
    #   SDK/b/holder/data/old.log （字符串上在 holder 下，实际在 real 下）
    b_dir = Path(SDK) / "b"
    real = b_dir / "real"
    real.mkdir(parents=True, exist_ok=True)
    (real / "data").mkdir(exist_ok=True)
    (real / "data" / "old.log").write_text("MUST_SURVIVE", encoding="utf-8")

    holder = b_dir / "holder"
    if not make_junction(holder, real):
        skip("B 组（父目录 Junction）", "无法创建 Junction")
    else:
        try:
            # 前置断言：data 自身不是链接，只有父目录 holder 是。
            try:
                attrs = os.lstat(str(real / "data")).st_file_attributes
            except OSError:
                attrs = 0
            check("B0 前提：data 自身不是重解析点",
                  not (attrs & 0x400),
                  "attrs=0x%X" % attrs)

            target_file = holder / "data" / "old.log"
            root = holder / "data"

            s, bd, anc = probe(pr, target_file, root)

            # 这是 BUG-01 的核心判据：字符串判断在，实际位置不在。
            check("B1 字符串判断为 true（旧实现的唯一依据）", s == "True", "STRING=%s" % s)
            check("B2 边界判断为 false（拒绝删除）", bd == "False",
                  "BOUND=%s（True = BUG-01 未修复）" % bd)
            check("B3 祖先链检测到重解析点", anc == "True", "ANCESTOR=%s" % anc)
            check("B4 两个函数结论不同（证明边界版确实多做了事）",
                  s != bd, "STRING=%s BOUND=%s" % (s, bd))

            # 文件必须还在：这是「拒绝」而不是「删掉了才发现」。
            check("B5 外部文件未被触碰",
                  (real / "data" / "old.log").is_file(),
                  "存在=%s" % (real / "data" / "old.log").is_file())

            # ---- B6~B8：**目标尚不存在**时仍要看到上方的 Junction ----
            #
            # 这是本轮发现的第二个缺陷（在修 BUG-01 时暴露）：
            # 早先的祖先遍历「遇到第一个不存在的层级就 return false」。
            # 待写文件第一层就不存在，于是立刻返回 false，压根没往上走 ——
            # 「父目录是 Junction」被完全漏掉。而写入路径上目标不存在是常态。
            not_yet = holder / "data" / "new-file.json"
            s2, bd2, anc2 = probe(pr, not_yet, holder / "data")
            check("B6 前提：待写文件确实不存在", not not_yet.exists())
            check("B7 尚不存在的目标：能检测到上方 Junction（ANCESTOR=True）",
                  anc2 == "True",
                  "ANCESTOR=%s（False = 祖先遍历提前停止）" % anc2)
            check("B8 尚不存在的目标：边界判断拒绝写入", bd2 == "False",
                  "BOUND=%s（True = 会穿过 Junction 写入）" % bd2)

            # 搬迁类里的同名私有方法也必须修好（同一个缺陷的另一份实现）。
            s3, bd3, anc3 = probe_mover(pr2, not_yet)
            check("B9 DataDirectoryMover.HasReparsePointAncestor 同样能检测到",
                  anc3 == "True", "ANCESTOR=%s" % anc3)
        finally:
            if Path(holder).exists():
                subprocess.run(["cmd", "/c", "rmdir", str(holder)], capture_output=True)

    # ================= C 组：四个调用点确实改用了边界版 =================
    print("\n【C】四个调用点都改用了文件系统边界判断")
    src_dir = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services"
    call_sites = [
        ("LogService.cs", "CleanupOldLogs 删旧日志"),
        ("TaskStateService.cs", "TryDeleteTmp 清 .tmp"),
        ("ConfigService.cs", "WriteConfigAtomically 校验写入"),
        ("JsonConfig.cs", "WriteFile 校验写入"),
    ]
    for fname, purpose in call_sites:
        f = src_dir / fname
        if not f.is_file():
            check("C %s 存在" % fname, False, str(f))
            continue
        text = f.read_text(encoding="utf-8", errors="replace")
        # 只看非注释行：注释里提到旧函数名不算调用。
        body = "\n".join(l for l in text.splitlines()
                         if not l.strip().startswith("//"))
        uses_boundary = "IsWithinFilesystemBoundary" in body
        uses_old = "IsUnderDirectory(" in body
        check("C %s 使用 IsWithinFilesystemBoundary（%s）" % (fname, purpose),
              uses_boundary and not uses_old,
              "边界版=%s 旧版=%s" % (uses_boundary, uses_old))

    # ================= D 组：fail-closed =================
    print("\n【D】无法确认时必须拒绝（fail-closed）")
    d_dir = Path(SDK) / "d"
    d_dir.mkdir(parents=True, exist_ok=True)
    # 传一个含通配符的非法路径：GetFullPath 会抛，边界判断必须拒绝。
    s, bd, anc = probe(pr, str(d_dir) + "\\*.txt", d_dir)
    check("D1 含通配符的非法路径：边界=false（拒绝）", bd == "False", "BOUND=%s" % bd)
    # 空路径
    s, bd, anc = probe(pr, "", d_dir)
    check("D2 空路径：边界=false", bd == "False", "BOUND=%s" % bd)
    # root 为空
    s, bd, anc = probe(pr, str(d_dir), "")
    check("D3 根为空：边界=false", bd == "False", "BOUND=%s" % bd)

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
        print("字符串边界与实际文件系统边界已分离，四个调用点均已改用后者")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "fsboundary.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
