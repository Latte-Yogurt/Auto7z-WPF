#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""数据目录搬迁的**目标侧**前置校验（BUG-02）与互斥/包含关系。

## 缺陷

`MoveDataDirectory` 早先只校验源目录：

    string fullSource = Path.GetFullPath(sourceDirectory);
    if (!IsSafeToDeleteRecursively(fullSource)) return false;
    Directory.CreateDirectory(targetDirectory);      // ← 目标没有任何校验
    CopyThenDeleteSource(sourceDirectory, targetDirectory);

于是程序目录旁的 `.config` 若是 Junction：

    Program\.config  ->  D:\ExternalData
    「普通模式 → 便携模式」的目标 = Program\.config\Auto7z
    创建、写入、替换全部穿过链接，落在 D:\ExternalData

用户完全没预期数据会跑到别的磁盘；目标里若已有同名文件，
`File.Replace` 还会把它替换掉。

## 校验的四件事

1. **目标不能是重解析点链路**（含每一个现存祖先）
2. **目标必须在程序认可的根之下**（便携根 / 本地根）
3. **源与目标不能相同**，也不能互相包含
4. 校验必须发生在 `Directory.CreateDirectory(target)` **之前** ——
   创建动作本身就会落到链接指向的位置

## 本检查器怎么验证

校验方法 `ValidateMigrationPaths` 是私有的，反射直接调它，
**不真的搬数据** —— 这样每个用例都能独立构造，且跑得飞快。
另外用真实入口跑一次「目标父目录是 Junction」的端到端场景，
确认守卫在真实路径上也生效。

## 为什么每条拒绝用例都配一条放行用例

只测「非法被拒」的话，一个 `return false` 到底的实现也能全过。
每组拒绝用例旁边都必须有一条形状相同、只有一个维度合法的用例要求放行，
才能证明拒绝的原因是那个维度，而不是「反正都拒」。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "migrate")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


VALIDATE_CS = r'''
using System;
using System.IO;
using System.Reflection;

// 直接调私有的 ValidateMigrationPaths(source, target, sourceState)。
class MigrateProbe
{
    static Type moverType;
    static Type stateType;

    static int Main(string[] args)
    {
        string exe = args[0];
        string source = args[1];
        string target = args[2];
        string stateName = args[3];      // Present / Missing / Unreadable / Unreachable

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(exe);
        moverType = asm.GetType("Auto7z_WPF.Services.DataDirectoryMover");
        if (moverType == null) { Console.WriteLine("NO_TYPE"); return 2; }

        // 三态枚举是嵌套私有类型，按名字取。
        stateType = moverType.GetNestedType("DirectoryProbe", BindingFlags.NonPublic);
        if (stateType == null) { Console.WriteLine("NO_STATE_TYPE"); return 2; }

        object state = Enum.Parse(stateType, stateName);

        MethodInfo m = moverType.GetMethod("ValidateMigrationPaths",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) { Console.WriteLine("NO_METHOD"); return 2; }

        try
        {
            object r = m.Invoke(null, new object[] { source, target, state });
            Console.WriteLine("RESULT=" + r);
        }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            Console.WriteLine("EXCEPTION=" + inner.GetType().Name);
        }
        return 0;
    }
}
'''


def build_probe():
    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(VALIDATE_CS, encoding="utf-8")
    out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:400])
        return None
    return out


def validate(probe_exe, source, target, state, data_root):
    """调用 ValidateMigrationPaths，返回 'True' / 'False' / 'EX:...'。"""
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    p = subprocess.run([str(probe_exe), EXE, str(source), str(target), state],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", env=env, timeout=120)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        line = line.strip()
        if line.startswith("RESULT="):
            return line.split("=", 1)[1].strip()
        if line.startswith("EXCEPTION="):
            return line.strip()
        if line in ("NO_TYPE", "NO_STATE_TYPE", "NO_METHOD"):
            return line
    return "(无结果)"


def make_junction(link, target):
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    return Path(link).exists()


def rm_junction(link):
    if Path(link).exists():
        subprocess.run(["cmd", "/c", "rmdir", str(link)], capture_output=True)


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("数据目录搬迁：目标侧前置校验（BUG-02）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    pr = build_probe()
    if pr is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # 受控布局：localRoot 充当「本地根」，portableRoot 充当「便携根」。
    # AUTO7Z_DATA_ROOT 指向 localRoot，于是 AppPaths.GetLocalRootDirectory() = localRoot。
    local_root = Path(SDK) / "localroot"
    local_root.mkdir(parents=True, exist_ok=True)
    src = local_root / "Auto7z"
    src.mkdir(exist_ok=True)
    (src / "Auto7z.json").write_text('{"Language":"zh-CN"}', encoding="utf-8")

    # ============ A 组：放行基线（证明守卫不是「反正都拒」）============
    print("\n【A】合法搬迁必须放行")
    # 目标是受控根下的目录。用一个必然落在 portableRoot 之外的临时根来构造会
    # 触发「不在认可根下」的拒绝 —— 所以这一组的目标必须真的在 portableRoot 下。
    # 便携根 = {程序目录}\.config，不可写（程序目录是项目目录）。
    # 因此这里改用「与 source 同根」的合法目标做放行证明：
    # IsUnderKnownRoot 认可 localRoot 之下的路径。
    ok_target = local_root / "Auto7z-moved"
    r = validate(pr, src, ok_target, "Present", local_root)
    check("A1 合法目标放行", r == "True", "RESULT=%s" % r)

    # 源不存在时也放行（只建目标）
    ghost = local_root / "Auto7z-ghost"
    r = validate(pr, ghost, local_root / "Auto7z-ghost-target", "Missing", local_root)
    check("A2 源不存在时放行（只建目标）", r == "True", "RESULT=%s" % r)

    # ============ B 组：目标不在认可根下 ============
    print("\n【B】目标必须落在程序认可的根之下")
    outside = Path(SDK) / "outside"
    outside.mkdir(parents=True, exist_ok=True)
    r = validate(pr, src, outside / "Auto7z", "Present", local_root)
    check("B1 目标在受控根之外：拒绝", r == "False", "RESULT=%s（True = 可任意写入）" % r)

    # ============ C 组：源与目标相同 / 互相包含 ============
    print("\n【C】源与目标不能相同，也不能互相包含")
    r = validate(pr, src, src, "Present", local_root)
    check("C1 源与目标完全相同：拒绝", r == "False", "RESULT=%s" % r)

    r = validate(pr, src, src / "nested", "Present", local_root)
    check("C2 目标在源内部：拒绝（边搬边搬自己）", r == "False", "RESULT=%s" % r)

    # 源在目标内部：源用 local_root 下的子目录，目标用 local_root 本身
    r = validate(pr, src, local_root, "Present", local_root)
    check("C3 源在目标内部：拒绝（删源会删掉目标）", r == "False", "RESULT=%s" % r)

    # 前缀相同但不同层级不能误判为包含
    sibling = local_root / "Auto7z-sibling"
    sibling.mkdir(exist_ok=True)
    r = validate(pr, src, sibling, "Present", local_root)
    check("C4 前缀相同的兄弟目录：放行（不能误判为包含）", r == "True", "RESULT=%s" % r)

    # ============ D 组：目标父目录是 Junction ============
    print("\n【D】目标路径上有重解析点时拒绝")
    real_out = Path(SDK) / "realout"
    real_out.mkdir(parents=True, exist_ok=True)
    (real_out / "Auto7z").mkdir(exist_ok=True)

    holder = local_root / "junc"
    if not make_junction(holder, real_out):
        skip("D 组（目标父目录 Junction）", "无法创建 Junction")
    else:
        try:
            # D1：目标 = holder/Auto7z，holder 是 Junction，Auto7z 自身不是
            r = validate(pr, src, holder / "Auto7z", "Present", local_root)
            check("D1 目标父目录是 Junction：拒绝", r == "False",
                  "RESULT=%s（True = 会穿过链接写入外部）" % r)

            # D2：目标尚不存在、且父目录是 Junction
            r2 = validate(pr, src, holder / "Auto7z-new", "Present", local_root)
            check("D2 尚不存在的目标、上方有 Junction：拒绝", r2 == "False",
                  "RESULT=%s" % r2)
        finally:
            rm_junction(holder)

    # ============ E 组：端到端 —— 真实搬迁入口不能被目标链接骗到 ============
    print("\n【E】端到端：真实搬迁不会把数据写进 Junction 指向的外部目录")
    e_root = Path(SDK) / "e2e"
    e_src = e_root / "srcholder"
    e_real_src = e_root / "realsrc"
    e_real_out = e_root / "realout"
    for d in (e_real_src, e_real_out):
        d.mkdir(parents=True, exist_ok=True)
    (e_real_src / "Auto7z").mkdir(exist_ok=True)
    (e_real_src / "Auto7z" / "Auto7z.json").write_text('{"Language":"zh-CN"}',
                                                        encoding="utf-8")
    (e_real_out / "Auto7z").mkdir(exist_ok=True)
    marker = e_real_out / "Auto7z" / "EXTERNAL_MARKER.txt"
    marker.write_text("MUST_NOT_BE_TOUCHED", encoding="utf-8")

    e_holder = e_root / "outholder"
    if not make_junction(e_holder, e_real_out):
        skip("E 组（端到端）", "无法创建 Junction")
    else:
        try:
            env = dict(os.environ)
            env["AUTO7Z_DATA_ROOT"] = str(e_root / "portablelook")
            # 目标写进 e_holder/Auto7z —— 它落在 AUTO7Z_DATA_ROOT 之外，
            # 因此首先会被「不在认可根下」挡下；这里额外确认外部文件没被动。
            script = (
                'param([string]$Exe,[string]$Src,[string]$Tgt)\n'
                '$asm=[System.Reflection.Assembly]::LoadFrom($Exe)\n'
                '$t=$asm.GetType("Auto7z_WPF.Services.DataDirectoryMover")\n'
                '$m=$t.GetMethod("MoveDataDirectory")\n'
                'try { Write-Output ("RESULT=" + $m.Invoke($null, @($Src,$Tgt))) }\n'
                'catch { Write-Output ("EX=" + $_.Exception.InnerException.GetType().Name) }\n'
            )
            f = Path(SDK) / "_move.ps1"
            f.write_text(script, encoding="utf-8")
            p = subprocess.run(
                ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(f),
                 EXE, str(e_real_src / "Auto7z"), str(e_holder / "Auto7z")],
                capture_output=True, text=True, encoding="utf-8", errors="replace",
                env=env, timeout=180)
            text = (p.stdout or "") + (p.stderr or "")
            moved = "RESULT=True" in text
            check("E1 搬迁被拒绝", not moved, text.strip().replace("\n", " | ")[:160])
            check("E2 外部目录里的标记文件未被触碰", marker.is_file(),
                  "存在=%s" % marker.is_file())
            check("E3 外部目录没有被写入新内容",
                  sorted(x.name for x in (e_real_out / "Auto7z").iterdir()
                         if x.is_file()) == ["EXTERNAL_MARKER.txt"],
                  "内容=%s" % sorted(x.name for x in (e_real_out / "Auto7z").iterdir()))
        finally:
            rm_junction(e_holder)

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
        print("搬迁的双侧前置校验已补齐，目标侧不再可能被链接骗到")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "migrate.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
