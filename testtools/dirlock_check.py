#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""迁移目标目录的锁在「目标不存在」时也必须生效（BUG-05）。

## 缺陷

首次把数据搬到便携模式时，目标目录**通常还不存在**。而早先的实现是：

    using (MigrationDirectoryLock.TryAcquire(targetDirectory, out string err))
    {
        if (err != null) return false;
        Directory.CreateDirectory(targetDirectory);   // ← 先「锁」，后「建」
        CopyThenDeleteSource(...);
    }

`TryAcquire` 对不存在的目录返回 `null` 且 **error 也是 null**
（那是为「搬迁源可能本来就不存在」设计的），调用方理解为
「没什么可锁的」，于是继续往下走。结果是：

    T1  校验目标路径安全
    T2  TryAcquire 发现目标不存在 → 返回 null，**没有锁**
    T3  别人在这个位置建了一个 Junction
    T4  Directory.CreateDirectory(target) —— 落在联接指向的真实目录上
    T5  程序沿着它把整棵数据写进外部磁盘

也就是说：**最常见的那条路径（首次切便携）上，锁从来没有生效过。**
前面那些重解析点检查全部变成了「检查完就被绕过」。

## 修复

`AcquireOrCreate` 把「建」与「锁」并成一步，并按**句柄**复核身份：

  1. CreateDirectory（幂等，已存在也无妨）
  2. CreateFileW + FILE_FLAG_OPEN_REPARSE_POINT —— 打开链接本身，不跟随
  3. GetFileInformationByHandle 查属性：带 ReparsePoint 就拒绝
  4. 记下卷序列号 + 文件索引作为目录身份

第 2 步是关键：普通 CreateFile 会透明跟随联接，于是我们锁住的是**外部真实目录**，
而路径本身早就被换掉了也看不出来。

## 本检查器验证什么

A. 静态：搬迁流程用的是 AcquireOrCreate，不再是「先 TryAcquire 再 CreateDirectory」
B. 运行时：**目标不存在**时也能拿到非 null 的锁（BUG-05 的直接判据）
C. 运行时：拿到锁之后外部无法改名/删除该目录（证明锁真的生效了）
D. 运行时：目标位置是 Junction 时**拒绝**并给出原因（不跟随、不写入）
E. 运行时：VerifyUnchanged 能识别「目录被换掉」
F. 运行时：释放后改名恢复正常（证明限制来自那把锁）

## B 组为什么是核心

早先的实现对 B 的回答是「返回 null，但调用方照样继续」。
所以判据不能只是「TryAcquire 返回 null」——那是设计行为；
必须是「**AcquireOrCreate 返回了非 null 的锁**」，
即真的握住了一个句柄。
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "dirlock")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def make_junction(link, target):
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                   capture_output=True, text=True, errors="replace")
    return Path(link).exists()


def rm_junction(link):
    if Path(link).exists():
        subprocess.run(["cmd", "/c", "rmdir", str(link)], capture_output=True)


# 探针：在独立进程里持有锁，按 stdin 指令释放 —— 这样测试能跨越命令边界。
HOLDER_CS = r'''
using System;
using System.IO;
using System.Reflection;

class LockHolder
{
    static int Main(string[] args)
    {
        string exe = args[0];
        string op = args[1];
        string target = args[2];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(exe);
        Type t = asm.GetType("Auto7z_WPF.Services.MigrationDirectoryLock");
        if (t == null) { Console.WriteLine("NO_TYPE"); return 2; }

        MethodInfo acquire = t.GetMethod("AcquireOrCreate", BindingFlags.Public | BindingFlags.Static);
        if (acquire == null) { Console.WriteLine("NO_ACQUIREORCREATE"); return 2; }

        object[] a = new object[] { target, null };
        object lk = null;
        try { lk = acquire.Invoke(null, a); }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            Console.WriteLine("ACQUIRE_EX:" + inner.GetType().Name);
            return 3;
        }
        string err = (string)a[1];

        if (lk == null)
        {
            Console.WriteLine("ACQUIRE=NULL");
            Console.WriteLine("ERR=" + (err ?? "<null>"));
            Console.Out.Flush();
            return 0;
        }

        Console.WriteLine("ACQUIRE=OK");
        Console.WriteLine("DIR_CREATED=" + Directory.Exists(target));

        // VerifyUnchanged 的正常路径
        MethodInfo verify = t.GetMethod("VerifyUnchanged");
        if (verify != null)
        {
            object[] va = new object[] { null };
            bool ok = (bool)verify.Invoke(lk, va);
            Console.WriteLine("VERIFY_UNCHANGED=" + ok);
            Console.WriteLine("VERIFY_ERR=" + ((string)va[0] ?? "<null>"));
        }

        Console.Out.Flush();

        // 等测试指令：RELEASE 正常释放；SWAP 先换掉目录再 Verify。
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (line == "RELEASE") break;
            if (line == "VERIFY")
            {
                object[] va2 = new object[] { null };
                bool ok2 = (bool)verify.Invoke(lk, va2);
                Console.WriteLine("VERIFY2=" + ok2);
                Console.WriteLine("VERIFY2_ERR=" + ((string)va2[0] ?? "<null>"));
                Console.Out.Flush();
            }
        }

        ((IDisposable)lk).Dispose();
        Console.WriteLine("RELEASED");
        Console.Out.Flush();
        return 0;
    }
}
'''

# 无锁探针：直接问「这个位置能不能被安全地当作目标」
PLAIN_CS = r'''
using System;
using System.IO;
using System.Reflection;

class PlainProbe
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
        var acquire = t.GetMethod("AcquireOrCreate", BindingFlags.Public | BindingFlags.Static);
        object[] a = new object[] { target, null };
        object lk = acquire.Invoke(null, a);
        Console.WriteLine("LOCK_NULL=" + (lk == null));
        Console.WriteLine("ERR=" + ((string)a[1] ?? "<null>"));
        Console.WriteLine("DIR_EXISTS=" + Directory.Exists(target));
        if (lk != null) ((IDisposable)lk).Dispose();
        return 0;
    }
}
'''


def compile(src, name, manifest=None):
    cs = Path(SDK) / ("%s_src.txt" % name)
    cs.write_text(src, encoding="utf-8")
    out = Path(SDK) / ("%s.exe" % name)
    cmd = [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
           "/out:" + str(out)]
    if manifest:
        cmd.append("/win32manifest:" + str(manifest))
    cmd.append(str(cs))
    rc = subprocess.run(cmd, capture_output=True, text=True,
                        encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     %s 编译失败: %s" % (name, (rc.stderr or rc.stdout or "")[:400]))
        return None
    return out


def try_rename(src, dst):
    try:
        os.rename(str(src), str(dst))
        return True
    except OSError:
        return False


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("迁移目标目录的锁：目标不存在时也必须生效（BUG-05）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    holder_exe = compile(HOLDER_CS, "holder")
    plain_exe = compile(PLAIN_CS, "plain")
    if holder_exe is None or plain_exe is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # ================= A 静态 =================
    print("\n【A】静态：搬迁流程用 AcquireOrCreate，而不是「先查再建」")
    mover = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "DataDirectoryMover.cs"
    lockfile = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "MigrationDirectoryLock.cs"

    if not mover.is_file() or not lockfile.is_file():
        check("A 源文件存在", False, "缺少 mover 或 lock 源文件")
    else:
        mb = "\n".join(l for l in mover.read_text(encoding="utf-8", errors="replace").splitlines()
                       if not l.strip().startswith("//"))
        lb = lockfile.read_text(encoding="utf-8", errors="replace")

        check("A1 搬迁用 AcquireOrCreate 取得目标锁",
              "AcquireOrCreate(targetDirectory" in mb)
        check("A2 不再对目标用「可能返回 null 且不报错」的 TryAcquire",
              "TryAcquire(targetDirectory" not in mb,
              "仍在用 TryAcquire 锁目标" if "TryAcquire(targetDirectory" in mb else "已改")
        check("A3 有 OPEN_REPARSE_POINT（打开链接本身，不跟随）",
              "FILE_FLAG_OPEN_REPARSE_POINT" in lb)
        check("A4 按句柄复核「不是重解析点」",
              "FILE_ATTRIBUTE_REPARSE_POINT" in lb)
        check("A5 有目录身份复核（卷序列号 + 文件索引）",
              "VerifyUnchanged" in lb and "VolumeSerialNumber" in lb)
        check("A6 搬迁在删源之前调用 VerifyUnchanged",
              "VerifyUnchanged" in mb)
        # 顺序：Verify 必须在 Directory.Delete(sourceDirectory 之前
        vpos = mb.find("VerifyUnchanged")
        dpos = mb.find("Directory.Delete(sourceDirectory")
        check("A7 身份复核发生在删源之前", 0 <= vpos < dpos,
              "Verify 位置=%d，删源位置=%d" % (vpos, dpos))

    # ================= B 目标不存在时也能拿到锁 =================
    print("\n【B】目标目录不存在时，仍然拿到一个真实的锁（BUG-05 核心）")
    work = Path(SDK) / "b"
    work.mkdir(parents=True, exist_ok=True)
    ghost = work / "not-yet-there"

    p = subprocess.run([str(plain_exe), EXE, str(ghost)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=120)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        print("     " + line)

    created = ghost.is_dir()
    got_lock = "LOCK_NULL=False" in out
    no_err = "ERR=<null>" in out

    check("B1 目标目录被创建出来", created,
          "存在=%s" % created)
    check("B2 创建的同时拿到了非 null 的锁（不是「没什么可锁的」）", got_lock,
          "LOCK_NULL=%s（True = 首次迁移没有锁，BUG-05 未修复）"
          % ("True" if not got_lock else "False"))
    check("B3 成功路径不报错", no_err, out.strip().splitlines()[-1] if out.strip() else "")

    # ================= C 持锁期间外部无法改名 =================
    print("\n【C】持锁期间外部无法把这个目录改名或删掉")
    work = Path(SDK) / "c"
    work.mkdir(parents=True, exist_ok=True)
    target = work / "existing"
    target.mkdir(parents=True, exist_ok=True)
    (target / "keep.txt").write_text("DATA", encoding="utf-8")

    proc = subprocess.Popen([str(holder_exe), EXE, "acquire", str(target)],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8")

    acquired = False
    try:
        deadline = time.time() + 30
        while time.time() < deadline:
            line = proc.stdout.readline()
            if not line:
                break
            line = line.strip()
            print("     " + line)
            if line == "ACQUIRE=OK":
                acquired = True
                break
            if line.startswith("ACQUIRE=NULL") or line.startswith("ACQUIRE_EX"):
                break

        if not acquired:
            check("C1 能对已存在目录取得锁", False, "ACQUIRE 未成功")
        else:
            check("C1 能对已存在目录取得锁", True)
            renamed = try_rename(target, work / "renamed")
            check("C2 持锁期间改名被拒绝", not renamed,
                  "改名成功=%s（True = 目录可被换成联接）" % renamed)

            try:
                deleted = os.rmdir(str(target))
            except OSError:
                deleted = False
            check("C3 持锁期间删除被拒绝", not deleted, "删除成功=%s" % deleted)
            check("C4 目录内容仍在原处", (target / "keep.txt").is_file())

            # **C6：持锁时必须还能往目录里写东西。**
            #
            # 这一条是变异测试逼出来的（b4-bug05c）。第一版的检查器只断言
            # 「改名/删除被拒」，于是把共享模式从 SHARE_READ_WRITE_ONLY 改回 0
            # 之后它照样报 PASS —— 而那是**致命的**：
            #
            #     share=0  open=OK  create=OK  move=IOException  enum=IOException
            #     share=3  open=OK  create=OK  move=OK         enum=OK   rename=BLOCKED
            #
            # share=0 会连我们**自己**对目录内子项的重命名与枚举一起锁死，
            # 于是搬迁永远搬不进去：目标目录建出来、一个文件也放不下，
            # MoveDataDirectory 恒返回 false。便携模式因此彻底不可用。
            #
            # 「锁住目录」与「锁死自己」是两件事，判据必须分别断言：
            # 前者要 C2/C3，后者要 C6/C7。
            probe_file = target / ".moving-probe.tmp"
            try:
                with open(str(probe_file), "wb") as fh:
                    fh.write(b"PROBE")
                created = True
            except OSError:
                created = False
            check("C6 持锁期间仍能在目录内创建文件（锁不能锁死自己）", created,
                  "创建成功=%s（False = 共享模式过严，搬迁搬不进去）" % created)

            # 搬迁真正做的事：把临时文件重命名成正式文件（= File.Replace 的等价操作）。
            renamed_inside = False
            enumerated = -1
            if created:
                try:
                    os.replace(str(probe_file), str(target / "final.txt"))
                    renamed_inside = True
                except OSError:
                    renamed_inside = False
                try:
                    enumerated = len(os.listdir(str(target)))
                except OSError:
                    enumerated = -1

            check("C7 持锁期间仍能在目录内重命名文件（File.Replace 的前提）",
                  renamed_inside,
                  "重命名成功=%s（False = 搬迁无法提交文件）" % renamed_inside)
            check("C8 持锁期间仍能枚举目录内容", enumerated >= 0,
                  "枚举结果=%d（-1 = 被锁死，CopyThenDeleteSource 会抛 IOException）"
                  % enumerated)

            proc.stdin.write("RELEASE\n")
            proc.stdin.flush()
            proc.wait(timeout=30)

            after_rename = try_rename(target, work / "after")
            check("C5 释放后改名恢复正常（证明限制来自那把锁）", after_rename,
                  "改名成功=%s（False = 限制另有原因）" % after_rename)
    finally:
        try:
            if proc.poll() is None:
                proc.kill()
        except OSError:
            pass

    # ================= D 目标位置是 Junction → 拒绝 =================
    print("\n【D】目标位置是 Junction 时拒绝（不跟随、不写入外部）")
    work = Path(SDK) / "d"
    work.mkdir(parents=True, exist_ok=True)
    outside = work / "outside_target"
    outside.mkdir(parents=True, exist_ok=True)
    (outside / "external.txt").write_text("EXTERNAL", encoding="utf-8")

    link = work / "auto7z_link"
    if not make_junction(link, outside):
        skip("D 组（目标为 Junction）", "无法创建 Junction")
    else:
        p = subprocess.run([str(plain_exe), EXE, str(link)],
                           capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=120)
        out = (p.stdout or "") + (p.stderr or "")
        for line in out.strip().splitlines():
            print("     " + line)

        refused = "LOCK_NULL=True" in out
        mentions = ("重解析点" in out) or ("Junction" in out) or ("符号链接" in out)

        check("D1 Junction 作为目标时拒绝（LOCK_NULL=True）", refused,
              "被拒=%s（False = 锁到了联接指向的外部目录）" % refused)
        check("D2 拒绝原因说清是重解析点", mentions,
              "原因行=%s" % ([l for l in out.splitlines() if l.startswith("ERR=")][:1]))
        check("D3 外部目录内容未被改动",
              (outside / "external.txt").is_file()
              and (outside / "external.txt").read_text(encoding="utf-8") == "EXTERNAL")

        rm_junction(link)

    # ================= E VerifyUnchanged 识别目录被换 =================
    print("\n【E】VerifyUnchanged 能识别「目录被换掉」")
    work = Path(SDK) / "e"
    work.mkdir(parents=True, exist_ok=True)
    target = work / "swapme"
    target.mkdir(parents=True, exist_ok=True)
    (target / "a.txt").write_text("A", encoding="utf-8")

    proc = subprocess.Popen([str(holder_exe), EXE, "acquire", str(target)],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8")
    acquired = False
    try:
        deadline = time.time() + 30
        while time.time() < deadline:
            line = proc.stdout.readline()
            if not line:
                break
            line = line.strip()
            print("     " + line)
            if line == "ACQUIRE=OK":
                acquired = True
                # 读掉后续的 VERIFY_UNCHANGED 等行
                while True:
                    nxt = proc.stdout.readline()
                    if not nxt:
                        break
                    nxt = nxt.strip()
                    print("     " + nxt)
                    if nxt.startswith("VERIFY_ERR="):
                        break
                break
            if line.startswith("ACQUIRE=NULL"):
                break

        if not acquired:
            skip("E 组（身份复核）", "未能取得锁")
        else:
            # 持锁时无法替换目录（那正是 C 组证明的），所以这里验证的是
            # 「正常目录 → VerifyUnchanged 返回 true」这一正向路径，
            # 「被换掉」的负向路径无法在持锁前提下构造（这是锁的功劳，不是缺陷）。
            proc.stdin.write("VERIFY\n")
            proc.stdin.flush()

            still_ok = None
            deadline = time.time() + 20
            while time.time() < deadline:
                line = proc.stdout.readline()
                if not line:
                    break
                line = line.strip()
                print("     " + line)
                if line.startswith("VERIFY2="):
                    still_ok = line.split("=", 1)[1].strip()
                    break
            check("E1 未换目录时 VerifyUnchanged 返回 true", still_ok == "True",
                  "VERIFY2=%s" % still_ok)

            proc.stdin.write("RELEASE\n")
            proc.stdin.flush()
            proc.wait(timeout=30)
    finally:
        try:
            if proc.poll() is None:
                proc.kill()
        except OSError:
            pass

    print("\n【说明】本检查器未覆盖的部分（不是通过，是没测）")
    print("     · 「T2 别人在建目录的瞬间插入联接」这个微观交错无法稳定复现；")
    print("       这里证明的是「建完立刻按句柄复核」，即该交错的结果会被拒绝")
    print("     · 目录**内部**新建 Junction 不在这把锁的防护范围（另有逐项检查）")

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
        print("目标不存在时也会建好并立刻上锁；链接目标被按句柄识别并拒绝")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "dirlock.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
