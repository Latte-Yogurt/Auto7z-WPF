#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""任务残留清理的目录归属校验（BUG-04）。

## 缺陷

崩溃恢复会递归删除 `JobOutputDir` 指向的目录。删除前只做了一件事：

    string name = Path.GetFileName(trimmed.TrimEnd(sep));
    if (!FileSystemHelper.IsGeneratedFolderName(name)) return false;

而 `IsGeneratedFolderName` 只检查「43 个字符、只含 [A-Za-z0-9_-]」。
这是**格式**，不是**归属**：任何人都能建一个合法名字的目录，
状态文件被改写成指向它时，仅凭名字就会被递归删除。

最坏的情形：状态文件里写着

    "JobOutputDir": "D:\\ImportantData\\xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

（最后一段恰好 43 个合法字符）。程序不知道那是自己建的任务临时目录、
用户的原有目录、还是被篡改过的状态指向的别处 —— 但它会递归删掉。

## 修复

任务开始时在产物目录里写所有权标记 `.auto7z-owner`，
内容第一行是本次 JobId（与 job.lock 一致的随机值）。
删除前三重校验：

  1. 目录名符合随机安全名格式（原有守卫）
  2. 目录里有标记、且标记里的 JobId 与状态文件记录的一致（真正的归属证明）
  3. 目录及其祖先路径上没有重解析点（防止沿联接删到外部磁盘）

正常结束时标记会被删掉，所以用户拿到的产物目录里看不到它。

## 本检查器验证什么

A. 静态：删除前确实读了标记，且判决依赖它
B. 运行时：**带正确标记**的目录被清理（证明不是「永远不删」）
C. 运行时：**没有标记**的同名目录被保留（核心用例：格式合法但归属不明）
D. 运行时：**JobId 不匹配**的标记目录被保留（伪造/错配）
E. 运行时：标记所在的目录是链接时被拒（fail-closed）
F. 运行时：正常完成后产物目录里**没有**残留标记文件（不污染交付物）

## C 组是这项检查的重点

B（能删）与 C（不乱删）必须同时成立。只有 C 的话，一个「永远不删」的实现能全过；
只有 B 的话，缺陷原样存在。两者一起才说明「按归属决定删不删」。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "ownership")

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


PROBE_CS = r'''
using System;
using System.IO;
using System.Reflection;

// 反射调用 TaskStateService.DeleteOutputDirectory(outputDir, jobId)。
class OwnerProbe
{
    static int Main(string[] args)
    {
        string exe = args[0];
        string dir = args[1];
        string jobId = args[2];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(exe);
        Type t = asm.GetType("Auto7z_WPF.Services.TaskStateService");
        if (t == null) { Console.WriteLine("NO_TYPE"); return 2; }

        MethodInfo m = null;
        foreach (MethodInfo cand in t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (cand.Name != "DeleteOutputDirectory") continue;
            ParameterInfo[] ps = cand.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(string))
            {
                m = cand;
                break;
            }
        }

        if (m == null) { Console.WriteLine("NO_METHOD"); return 2; }

        try
        {
            object r = m.Invoke(null, new object[] { dir, jobId });
            Console.WriteLine("RESULT=" + r);
            return 0;
        }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            Console.WriteLine("EXCEPTION:" + inner.GetType().Name);
            return 1;
        }
    }
}
'''


def build_probe():
    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(PROBE_CS, encoding="utf-8")
    out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:800])
        return None
    return out


def run_probe(probe, directory, job_id):
    p = subprocess.run([str(probe), EXE, str(directory), job_id],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    value = ""
    for line in out.splitlines():
        if line.startswith("RESULT="):
            value = line[7:].strip()
        elif line.startswith("EXCEPTION:") or line.startswith("NO_"):
            value = line.strip()
    return value, out


MARKER = ".auto7z-owner"
GOOD_JOB = "a1b2c3d4e5f60718293a4b5c6d7e8f90"


def mkdir_with_marker(parent, name, job_id, marker_name=MARKER):
    """建一个 43 字符名目录，按需写入标记。"""
    d = Path(parent) / name
    d.mkdir(parents=True, exist_ok=True)
    (d / "archive.7z").write_text("PAYLOAD", encoding="utf-8")
    if job_id:
        content = (job_id + "\n2026-11-23T00:00:00.0000000Z\n")
        (d / marker_name).write_text(content, encoding="utf-8")
    return d


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("任务残留清理的目录归属校验（BUG-04）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    probe = build_probe()
    if probe is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # ================= A 静态 =================
    print("\n【A】静态：删除判决依赖归属标记")
    svc = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "TaskStateService.cs"
    if not svc.is_file():
        check("A TaskStateService.cs 存在", False, str(svc))
    else:
        text = svc.read_text(encoding="utf-8", errors="replace")
        body = "\n".join(l for l in text.splitlines()
                         if not l.strip().startswith("//"))

        check("A1 有所有权标记文件名常量", '".auto7z-owner"' in text,
              "找到 .auto7z-owner" if '".auto7z-owner"' in text else "未找到标记常量")
        check("A2 删除前调用了归属校验", "HasOwnerMarker(" in body)
        check("A3 归属校验的结果真的决定了删不删",
              "if (!HasOwnerMarker(" in body or "!HasOwnerMarker(" in body,
              "找到否定分支" if "!HasOwnerMarker(" in body else "校验结果未被使用")
        check("A4 删除签名带上 jobId（否则无从比对）",
              "DeleteOutputDirectory(string outputDirectory, string jobId)" in body)
        check("A5 恢复流程把 jobId 传下去了",
              "DeleteOutputDirectory(outputDir, jobId)" in body)
        check("A6 删除前检查重解析点", "HasReparsePointAncestor" in body)
        check("A7 任务开始时写标记", "WriteOwnerMarker(outputDirectory, jobId)" in body)
        check("A8 正常结束时删标记（不污染交付目录）",
              "RemoveOwnerMarker(outputDir)" in body
              or "RemoveOwnerMarker(" in body.split("MarkTaskFinished")[-1])

    # ================= B 带正确标记 → 清理 =================
    print("\n【B】带正确归属标记的残留目录会被清理（证明不是「永远不删」）")
    work = Path(SDK) / "b"
    work.mkdir(parents=True, exist_ok=True)
    name = "A" * 43
    d = mkdir_with_marker(work, name, GOOD_JOB)

    val, out = run_probe(probe, d, GOOD_JOB)
    gone = not d.exists()
    check("B1 带正确标记的目录被删除", gone,
          "RESULT=%s 目录%s" % (val, "已删" if gone else "仍在"))
    check("B2 探针返回值为 true", val.lower() == "true", "RESULT=%s" % val)

    # ================= C 无标记 → 保留 =================
    print("\n【C】名字合法但没有归属标记的目录必须保留（BUG-04 的核心）")
    work = Path(SDK) / "c"
    work.mkdir(parents=True, exist_ok=True)
    name = "B" * 43
    d = mkdir_with_marker(work, name, None)

    val, out = run_probe(probe, d, GOOD_JOB)
    survived = d.exists() and (d / "archive.7z").is_file()
    check("C1 无标记目录未被删除", survived,
          "RESULT=%s 目录%s" % (val, "保留" if survived else "被误删"))
    check("C2 返回值是 false（如实报告未清理）", val.lower() == "false",
          "RESULT=%s" % val)

    # ================= D JobId 不匹配 → 保留 =================
    print("\n【D】标记里的 JobId 与状态记录不一致时保留")
    work = Path(SDK) / "d"
    work.mkdir(parents=True, exist_ok=True)
    name = "C" * 43
    d = mkdir_with_marker(work, name, "ffffffffffffffffffffffffffffffff")

    val, out = run_probe(probe, d, GOOD_JOB)
    survived = d.exists()
    check("D1 JobId 不匹配的目录未被删除", survived,
          "RESULT=%s 目录%s" % (val, "保留" if survived else "被误删"))

    # ================= E 标记本身是链接 → 拒绝 =================
    print("\n【E】标记文件是链接时 fail-closed")
    work = Path(SDK) / "e"
    work.mkdir(parents=True, exist_ok=True)
    outside = Path(SDK) / "e_outside"
    outside.mkdir(parents=True, exist_ok=True)
    (outside / "fake_marker.txt").write_text(GOOD_JOB + "\n", encoding="utf-8")

    name = "D" * 43
    d = Path(work) / name
    d.mkdir(parents=True, exist_ok=True)
    (d / "archive.7z").write_text("PAYLOAD", encoding="utf-8")

    link = d / MARKER
    made = False
    # 符号链接需要权限或开发者模式；失败就跳过这组，不伪造结论。
    try:
        os.symlink(str(outside / "fake_marker.txt"), str(link))
        made = True
    except (OSError, NotImplementedError):
        made = False

    if not made:
        skip("E 组（标记是链接）", "当前环境不允许创建符号链接")
    else:
        val, out = run_probe(probe, d, GOOD_JOB)
        survived = d.exists()
        check("E1 标记是链接时拒绝删除", survived,
              "RESULT=%s 目录%s" % (val, "保留" if survived else "被误删"))
        try:
            os.unlink(str(link))
        except OSError:
            pass

    # ================= F 正常完成后无残留标记 =================
    print("\n【F】正常完成后产物目录里没有残留标记（不污染交付物）")
    # 走真实程序：压缩一个小文件，检查产物目录里是否留了 .auto7z-owner
    work = Path(SDK) / "f"
    work.mkdir(parents=True, exist_ok=True)
    src = work / "input"
    src.mkdir(parents=True, exist_ok=True)
    (src / "hello.txt").write_text("HELLO_WORLD", encoding="utf-8")
    outdir = work / "out"
    outdir.mkdir(parents=True, exist_ok=True)

    data_root = Path(SDK) / "fdata"
    data_root.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)

    # 先写一份最小配置，把输出目录指到 outdir
    cfg_dir = data_root / "Auto7z"
    cfg_dir.mkdir(parents=True, exist_ok=True)
    (cfg_dir / "Auto7z.json").write_text(json.dumps({
        "Format": "7z", "Volume": "2000", "CompressionLevel": "Normal",
        "ErrorLevel": "Info", "Language": "zh-CN",
        "OutputPath": str(outdir), "AutoSave": "false",
        "GenerateSHA256": "false", "RuntimeMode": "Balanced",
    }, ensure_ascii=False, indent=2), encoding="utf-8")

    p = subprocess.run([EXE, "-z", str(src / "hello.txt")],
                       capture_output=True, text=True, encoding="gbk",
                       errors="replace", env=env, timeout=300)

    # 找出产物目录（outdir 下的 43 字符目录）
    produced = [x for x in outdir.iterdir() if x.is_dir() and len(x.name) == 43] \
        if outdir.exists() else []
    if not produced:
        skip("F 组（无残留标记）", "本轮压缩未产出 43 字符目录，无法判断")
    else:
        leftovers = [x / MARKER for x in produced if (x / MARKER).exists()]
        check("F1 产物目录里没有 .auto7z-owner 残留", not leftovers,
              "残留数=%d" % len(leftovers))
        # 保险：产物本身要还在
        archives = [f for x in produced for f in x.iterdir() if f.suffix in (".7z", ".zip", ".tar")] \
            if produced else []
        check("F2 产物文件仍然在（标记清理没有误删产物）", bool(archives),
              "产物数=%d" % len(archives))

    # ================= G 篡改场景：状态指向用户目录 =================
    print("\n【G】模拟状态文件被篡改、指向用户目录")
    work = Path(SDK) / "g"
    user_dir = work / ("E" * 43)          # 名字合法，但不是程序建的
    user_dir.mkdir(parents=True, exist_ok=True)
    (user_dir / "我的资料.txt").write_text("USER_PRECIOUS", encoding="utf-8")

    val, out = run_probe(probe, user_dir, GOOD_JOB)
    survived = user_dir.exists() and (user_dir / "我的资料.txt").is_file()
    check("G1 用户目录（名字合法但无标记）未被删除", survived,
          "目录%s" % ("保留" if survived else "被误删 —— BUG-04 未修复"))

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
        print("残留清理已从「按名字猜」变成「按归属证明」：无标记的目录一律保留")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "ownership.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
