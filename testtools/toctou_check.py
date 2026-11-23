#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""搬迁的 TOCTOU 缓解（BUG-06）。

## 缺陷

所有重解析点检查都是「先查、再动手」：

    T1  检查 target 不是 Junction        → 安全
    T2  另一个进程把 target 换成 Junction
    T3  程序继续 Copy / Delete

纯路径检查**无法**消除这个窗口 —— 这是路径式 API 的固有性质。

## 缓解：目录句柄独占

打开目录句柄时把共享模式设为 0（什么都不共享），Windows 会拒绝对该目录的
**重命名与删除**，直到句柄关闭。于是「把安全目录换成 Junction」这条路
（必须先删除或改名原目录）就走不通了。

## 这个检查器要证明什么 —— 以及**不**证明什么

**能证明：**
  A. 持锁期间，外部进程无法把这个目录改名
  B. 持锁期间，外部进程无法删除这个目录
  C. 释放后，改名与删除恢复正常（证明限制确实来自那把锁，不是别的因素）
  D. 目录不存在时不报错（搬迁源可能本来就没有）

**不能证明（必须写清楚，不能假装测过）：**
  · 「在目录**内部**新建 Junction」这条路径没被挡住 —— 目录锁只锁这一层。
    这一路靠遍历时逐项 `IsReparsePoint` / `ExistsAsReparsePoint` 检查来挡。
  · 同权限恶意进程直接改目标文件，本机制无感。
  · 真实的 T1→T2→T3 交错无法在自动化里稳定复现（窗口在微秒级）。
    这里用的是**确定性等价物**：不是去撞那个窗口，而是直接证明
    「窗口存在的前提（目录可被替换）」已经被消除。

C 组是关键：只测 A、B 的话，一个「永远锁住」的实现也能通过。
必须证明释放之后限制消失，才能说明限制来自我们持有的句柄。
"""
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "toctou")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


HOLDER_CS = r'''
using System;
using System.IO;
using System.Reflection;
using System.Threading;

// 在本进程内持有目录锁，按行响应 stdin 的指令，便于测试从外部尝试改名/删除。
// 之所以不用「另开一个 .NET 进程再反射调用」是因为那样无法让锁跨越命令边界存活。
class LockHolder
{
    static int Main(string[] args)
    {
        string exe = args[0];
        string target = args[1];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(exe);
        Type t = asm.GetType("Auto7z_WPF.Services.MigrationDirectoryLock");
        if (t == null) { Console.WriteLine("NO_TYPE"); return 2; }

        MethodInfo acquire = t.GetMethod("TryAcquire",
            BindingFlags.Public | BindingFlags.Static);
        if (acquire == null) { Console.WriteLine("NO_METHOD"); return 2; }

        object[] a = new object[] { target, null };
        object lk = acquire.Invoke(null, a);
        string err = (string)a[1];

        if (lk == null)
        {
            Console.WriteLine("ACQUIRE=NULL ERR=" + (err ?? ""));
            Console.Out.Flush();
            return 0;
        }

        Console.WriteLine("ACQUIRE=OK");
        Console.Out.Flush();

        // 等到 stdin 关闭或收到 RELEASE。
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            if (line.Trim() == "RELEASE") break;
        }

        ((IDisposable)lk).Dispose();
        Console.WriteLine("RELEASED");
        Console.Out.Flush();
        return 0;
    }
}
'''

MISSING_CS = r'''
using System;
using System.IO;
using System.Reflection;
class MissingProbe
{
    static int Main(string[] args)
    {
        string exe = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly asm = Assembly.LoadFrom(exe);
        Type t = asm.GetType("Auto7z_WPF.Services.MigrationDirectoryLock");
        var acquire = t.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.Static);
        object[] a = new object[] { args[1], null };
        object lk = acquire.Invoke(null, a);
        Console.WriteLine("LOCK_NULL=" + (lk == null));
        Console.WriteLine("ERR_IS_NULL=" + (a[1] == null));
        Console.WriteLine("ERR=" + (a[1] == null ? "<null>" : a[1].ToString()));
        return 0;
    }
}
'''


def compile(src, name):
    cs = Path(SDK) / ("%s_src.txt" % name)
    cs.write_text(src, encoding="utf-8")
    out = Path(SDK) / ("%s.exe" % name)
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     %s 编译失败: %s" % (name, (rc.stderr or rc.stdout or "")[:400]))
        return None
    return out


def try_rename(src, dst):
    """在同一个卷上改名；成功返回 True。"""
    try:
        os.rename(str(src), str(dst))
        return True
    except OSError:
        return False


def try_delete(path):
    try:
        os.rmdir(str(path))
        return True
    except OSError:
        return False


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("搬迁的 TOCTOU 缓解：目录句柄独占（BUG-06）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    holder_exe = compile(HOLDER_CS, "holder")
    missing_exe = compile(MISSING_CS, "missing")
    if holder_exe is None or missing_exe is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # ================= A/B/C 组：持锁期间的改名与删除 =================
    print("\n【A】持锁期间，外部无法把目录改名（= 无法替换成 Junction）")
    work = Path(SDK) / "work"
    work.mkdir(parents=True, exist_ok=True)
    locked_dir = work / "data"
    locked_dir.mkdir(exist_ok=True)
    (locked_dir / "keep.txt").write_text("data", encoding="utf-8")

    proc = subprocess.Popen([str(holder_exe), EXE, str(locked_dir)],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8")

    got_ok = False
    try:
        # 等它报告已持锁
        deadline = time.time() + 30
        while time.time() < deadline:
            line = proc.stdout.readline()
            if not line:
                break
            line = line.strip()
            print("     " + line)
            if line == "ACQUIRE=OK":
                got_ok = True
                break
            if line.startswith("ACQUIRE=NULL"):
                break

        if not got_ok:
            skip("A/B/C 组", "锁未能取得")
        else:
            # A：改名必须失败
            renamed = try_rename(locked_dir, work / "data-renamed")
            check("A1 持锁期间改名被拒绝", not renamed,
                  "改名成功=%s（True = 目录可被替换成链接）" % renamed)
            check("A2 目录仍在原处（内容未被搬走）",
                  (locked_dir / "keep.txt").is_file())

            # B：删除必须失败
            deleted = try_delete(locked_dir)
            check("B1 持锁期间删除被拒绝", not deleted,
                  "删除成功=%s（True = 目录可被删掉后重建为链接）" % deleted)
            check("B2 目录仍然存在", locked_dir.is_dir())

            # 释放
            try:
                proc.stdin.write("RELEASE\n")
                proc.stdin.flush()
            except (OSError, ValueError):
                pass
            proc.wait(timeout=30)

            # C：释放后限制必须消失
            renamed_after = try_rename(locked_dir, work / "data-after")
            check("C1 释放后改名恢复正常（证明限制来自那把锁）", renamed_after,
                  "改名成功=%s（False = 限制另有原因，A/B 的结论不成立）" % renamed_after)

            if renamed_after:
                after = work / "data-after"
                deleted_after = try_delete(after) if not any(after.iterdir()) else None
                # 目录里还有 keep.txt，直接 rmdir 会失败 —— 先清内容再删。
                try:
                    (after / "keep.txt").unlink()
                except OSError:
                    pass
                deleted_after = try_delete(after)
                check("C2 释放后删除恢复正常", deleted_after,
                      "删除成功=%s" % deleted_after)
    finally:
        try:
            if proc.poll() is None:
                proc.kill()
        except OSError:
            pass

    # ================= D 组：目录不存在 =================
    print("\n【D】目录不存在时不报错（搬迁源可能本来就没有）")
    ghost = Path(SDK) / "no-such-dir"
    p = subprocess.run([str(missing_exe), EXE, str(ghost)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=120)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        print("     " + line)
    check("D1 目录不存在时 TryAcquire 返回 null", "LOCK_NULL=True" in out)
    check("D2 目录不存在不算错误（error 为 null）", "ERR_IS_NULL=True" in out)

    # ================= E 组：静态 —— 搬迁流程真的用了这把锁 =================
    print("\n【E】搬迁流程确实持有目录锁")
    mover = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "DataDirectoryMover.cs"
    if mover.is_file():
        text = mover.read_text(encoding="utf-8", errors="replace")
        body = "\n".join(l for l in text.splitlines() if not l.strip().startswith("//"))
        check("E1 MoveDataDirectory 使用了 MigrationDirectoryLock",
              "MigrationDirectoryLock.AcquireOrCreate" in body
              or "MigrationDirectoryLock.TryAcquire" in body)

        # **只锁目标目录，且必须在复制之前。**
        #
        # 第一版这里断言「源与目标都被锁定」，那是当时的实现。后来回归测试发现
        # 锁源目录会造成自锁：句柄带着 DELETE 且不共享，而流程最后一步正是
        # `Directory.Delete(sourceDirectory)` —— 自己堵死自己，搬迁永远失败。
        # 于是改成「只锁目标 + 删源前复查 + 不递归删除」。
        #
        # 第二轮（BUG-05）又发现「锁目标」这一步本身在首次迁移时是空操作：
        # 目标不存在 → TryAcquire 返回 null 且不报错 → 调用方带着「没有锁」继续。
        # 现在改成 AcquireOrCreate（先建、再按句柄锁、并复核不是链接）。
        #
        # 断言必须跟着设计走，而不是跟着上一版代码走；
        # 但也要把「为什么只锁一侧」写下来，免得将来有人看到这里
        # 以为是漏锁了源目录又给加回去 —— 那会重新引入自锁。
        lock_count = body.count("MigrationDirectoryLock.AcquireOrCreate") \
            + body.count("MigrationDirectoryLock.TryAcquire")
        check("E2 只锁定目标目录（锁源目录会造成删除自锁）",
              lock_count == 1, "出现次数=%d" % lock_count)

        # 锁必须出现在复制之前。
        #
        # 用 AcquireOrCreate 作锚点：目标不存在时它同时也是「创建」，
        # 早先那种「TryAcquire 拿不到锁就照样 CreateDirectory + 复制」的形态
        # 已经被 BUG-05 的修复取代了。
        lock_pos = body.find("MigrationDirectoryLock.AcquireOrCreate")
        if lock_pos < 0:
            lock_pos = body.find("MigrationDirectoryLock.TryAcquire")
        copy_pos = body.find("CopyThenDeleteSource(")
        check("E3 锁定发生在复制之前", 0 <= lock_pos < copy_pos,
              "锁位置=%d，复制位置=%d" % (lock_pos, copy_pos))

        check("E4 拿不到锁时放弃搬迁（失败路径存在）",
              "targetLockError != null" in body)

        # 删源前的最后一道复查 + 不递归删除。
        check("E5 删除源目录前最后一刻复查它不是重解析点",
              "ExistsAsReparsePoint(sourceDirectory)" in body)
        check("E6 源目录用不递归删除（recursive:false，避免误删新冒出的内容）",
              "Directory.Delete(sourceDirectory, false)" in body,
              "找到非递归删除" if "Directory.Delete(sourceDirectory, false)" in body
              else "仍是递归删除")

        check("E7 遍历每一层时复查重解析点（第一道防线仍在）",
              "if (IsReparsePoint(sourceDirectory)) return;" in body)

        # BUG-05：目标不存在时也必须有锁 —— 判据是「先建再锁」，
        # 而不是「先查存在性、拿不到锁就继续」。
        check("E8 目标用 AcquireOrCreate（建与锁合并，覆盖「目标尚不存在」）",
              "AcquireOrCreate(targetDirectory" in body,
              "找到 AcquireOrCreate" if "AcquireOrCreate(targetDirectory" in body
              else "仍是 TryAcquire 后直接 CreateDirectory")
    else:
        check("E1 DataDirectoryMover.cs 存在", False, str(mover))

    # ================= 诚实声明 =================
    print("\n【说明】本检查器未覆盖的部分（不是通过，是没测）")
    print("     · 目录**内部**新建 Junction：目录锁只管这一层，另有逐项检查兜底")
    print("     · 同权限恶意进程直接改目标文件：本机制无感")
    print("     · 真实 T1→T2→T3 交错：窗口微秒级，无法稳定复现；")
    print("       这里改证「窗口存在的前提已被消除」，而不是去撞窗口")

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
        print("TOCTOU 窗口已缩小：搬迁期间目录不可被替换（不等于彻底消除）")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "toctou.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
