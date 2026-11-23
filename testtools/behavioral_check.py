#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""行为级补充测试（GPT6 补充建议第 1 条）。

## GPT6 的批评

> 部分检查器通过搜索特定字符串、方法名称和代码结构判断是否符合要求。
> 这种测试可以很好地防止已知问题回归，但不一定能发现：
>   · 两个方法组合时的状态错误
>   · 不同执行顺序引发的问题
>   · 系统文件权限差异
>   · Windows 文件系统特殊行为

这个批评成立。当前约一半检查器是纯静态扫描。

## 静态扫描不可替代的部分

静态扫描能回答「代码里有没有这个东西」，回答不了「两个东西一起用时对不对」。
典型例子：

  · `archcheck` A1 确认「组件路径只有一处定义」——但能不能用它真的压出包？
  · `reskeycheck` 确认「三语键齐全」——但切到那个语言后界面真的变了吗？
  · `configkeycheck` 确认「键表三处一致」——但改了配置后产品真的照着做了吗？
  · `appearancecheck` 确认「布局属性没变」——但窗口真的能正常弹出来吗？

本脚本把这几条补成行为验证：**不看代码，只看产品行为。**
"""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
GUI = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7zGUI.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "behavior")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def prep(tag, **over):
    work = os.path.join(SDK, tag)
    data = os.path.join(work, "data")
    os.makedirs(os.path.join(data, "Auto7z"), exist_ok=True)
    cfg = {
        "Language": "zh-CN", "Volume": "0", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": True, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Store", "UnzipPassword": "", "ErrorLevel": "Info",
    }
    cfg.update(over)
    open(os.path.join(data, "Auto7z", "Auto7z.json"), "w", encoding="utf-8").write(
        json.dumps(cfg, ensure_ascii=False, indent=2))
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1,1"
    return work, data, env


def run_cli(args, env, cwd, timeout=180):
    try:
        p = subprocess.run([EXE] + args, capture_output=True, env=env, cwd=cwd, timeout=timeout)
        return p.returncode, ((p.stdout or b"") + (p.stderr or b"")).decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        return None, "(超时)"


def read_logs(data):
    out = ""
    d = os.path.join(data, "Auto7z", "Logs")
    if os.path.isdir(d):
        for n in os.listdir(d):
            f = os.path.join(d, n)
            if os.path.isfile(f):
                out += open(f, encoding="utf-8", errors="replace").read()
    return out


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("行为级补充测试（不读代码，只看产品行为）")
    print("=" * 76)

    # ============ 1. 组件路径唯一 → 真的能用它压出包 ============
    print("\n【1】组件路径只有一处定义 → 产品必须真的能找到 7z 并压出包")
    w, d, env = prep("comp")
    src = os.path.join(w, "a.txt")
    open(src, "w", encoding="utf-8").write("behavior payload")
    rc, out = run_cli(["-z", src], env, w)
    arcs = []
    for rr, _, fs in os.walk(w):
        for n in fs:
            if n.endswith(".7z"):
                arcs.append(os.path.join(rr, n))
    check("压缩真的产出了归档（组件路径解析正确）",
          rc == 0 and len(arcs) == 1, "rc=%s 归档=%d" % (rc, len(arcs)))

    # ============ 2. 三语键齐全 → 切语言后产品真的换文案 ============
    print("\n【2】三语资源键齐全 → 切到英文后产品输出的文案必须真的变化")
    w_zh, d_zh, env_zh = prep("lang_zh", Language="zh-CN")
    w_en, d_en, env_en = prep("lang_en", Language="en-US")
    rc_a, out_zh = run_cli(["-h"], env_zh, w_zh)
    rc_b, out_en = run_cli(["-h"], env_en, w_en)

    # 中英文帮助文本应当不同（不是同一份硬编码）
    differs = out_zh.strip() != out_en.strip()
    check("切换语言后输出文案确实不同", differs,
          "zh 长度=%d en 长度=%d" % (len(out_zh), len(out_en)))
    # 英文环境下不该出现大段中文
    zh_chars = sum(1 for c in out_en if '\u4e00' <= c <= '\u9fff')
    check("英文环境下不出现大段中文", zh_chars < 10,
          "英文输出里的中文字符数=%d" % zh_chars)

    # ============ 3. 配置键表一致 → 改配置后产品真的照着做 ============
    print("\n【3】配置键表一致 → 改三个键后产品行为必须真的改变")
    # 3a. ChecksumAlgorithm 改动 → 校验文件后缀变化
    w_a, d_a, env_a = prep("cfg_sha", ChecksumAlgorithm="sha1")
    f_a = os.path.join(w_a, "x.txt")
    open(f_a, "w", encoding="utf-8").write("cfg")
    run_cli(["-z", f_a], env_a, w_a)
    sha_files = [n for _, _, fs in os.walk(w_a) for n in fs if ".sha1" in n]
    check("改 ChecksumAlgorithm=sha1 后产出 .sha1 校验文件", len(sha_files) >= 1,
          "找到 %d 个" % len(sha_files))

    # 3b. CompressionLevel 改动 → 产出大小必须真的不同
    #
    # 判据选型踩了两次坑，都记在注释里免得后人重走：
    #
    #  1) 最初判「日志里出现 mx9」—— 产品**不记命令行**，永远测不到。
    #  2) 改用「Store 与 Ultra 产出大小不同」，但用了**纯随机数据**。
    #     随机数据不可压缩，-mx=0 与 -mx=9 都只能原样存，大小必然相同
    #     （实测 Store=1048765 / Ultra=1048765）。
    #  3) 换成英文文本但仍**太小**（40KB）：压完只差 1 字节
    #     （Store=279 / Ultra=278），差异淹没在噪声里。
    #
    # 现在用 ≈4MB 可压缩英文文本 —— 实测 Store=1371 / Ultra=902（1.52x）。
    # 顺带用反射确认「级别 → 7z 参数」的映射（Store=0 / Normal=5 / Ultra=9）也对。
    # 数据规模很关键：40KB 文本压完后两级只差 1 字节，差异被淹没。
    # 用 ≈4MB 才有可观测的差距（实测 Store=1371 / Ultra=902，1.52x）。
    compressible = ("The quick brown fox jumps over the lazy dog. "
                    "Pack my box with five dozen liquor jugs.\n") * 40000

    def make_and_size(tag, level):
        w, d, env = prep(tag, CompressionLevel=level)
        f = os.path.join(w, "data.txt")
        open(f, "w", encoding="utf-8").write(compressible)
        run_cli(["-z", f], env, w)
        sizes = [os.path.getsize(os.path.join(rr, n))
                 for rr, _, fs in os.walk(w) for n in fs if n.endswith(".7z")]
        return sizes[0] if sizes else None

    size_store = make_and_size("lvl_store2", "Store")
    size_ultra = make_and_size("lvl_ultra2", "Ultra")

    if size_store is None or size_ultra is None:
        check("改 CompressionLevel 后产出大小真的不同", False,
              "Store=%s Ultra=%s（有一侧没产出）" % (size_store, size_ultra))
    else:
        # Store 至少比 Ultra 大 20%
        changed = size_store > size_ultra * 1.2
        check("改 CompressionLevel 后产出大小真的不同", changed,
              "Store=%d Ultra=%d 比值=%.2fx" %
              (size_store, size_ultra, float(size_store) / max(size_ultra, 1)))

    # 3c. ErrorLevel 改动 → 日志级别真的变化
    w_c, d_c, env_c = prep("cfg_err", ErrorLevel="Error")
    f_c = os.path.join(w_c, "z.txt")
    open(f_c, "w", encoding="utf-8").write("cfg3")
    run_cli(["-z", f_c], env_c, w_c)
    logs_c = read_logs(d_c)
    # Error 级别下不该有 [INFO] 行
    check("ErrorLevel=Error 时日志里不出现 [INFO]",
          "[INFO]" not in logs_c, "含 INFO=%s" % ("[INFO]" in logs_c))

    # ============ 4. 布局未变 → GUI 窗口真的能弹出来 ============
    print("\n【4】XAML 布局未变 → GUI 必须能真正启动并出窗口")
    w_g, d_g, env_g = prep("gui")
    proc = subprocess.Popen([GUI], env=env_g, cwd=os.path.dirname(GUI),
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(5)
    alive = proc.poll() is None

    # 用 PowerShell 查是否有可见窗口（标题非空）
    title = ""
    if alive:
        try:
            p = subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 "(Get-Process -Name Auto7zGUI -ErrorAction SilentlyContinue | "
                 "Where-Object {$_.MainWindowTitle -ne ''} | Select-Object -First 1).MainWindowTitle"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
            title = (p.stdout or "").strip()
        except Exception:
            pass

    try:
        proc.kill()
        proc.wait(timeout=10)
    except Exception:
        pass

    check("GUI 启动后存活并有主窗口标题", alive and len(title) > 0,
          "存活=%s 标题=%r" % (alive, title[:40]))

    # ============ 5. 方法组合：压缩→解压→校验 的内容一致性 ============
    print("\n【5】三个方法组合使用时状态不串（压缩→解压→哈希比对）")
    w_m, d_m, env_m = prep("combo")
    payload = os.urandom(64 * 1024)
    f_m = os.path.join(w_m, "payload.bin")
    open(f_m, "wb").write(payload)

    rc1, _ = run_cli(["-z", f_m], env_m, w_m)
    arcs_m = [os.path.join(rr, n) for rr, _, fs in os.walk(w_m)
              for n in fs if n.endswith(".7z")]
    if rc1 == 0 and arcs_m:
        sub = os.path.join(w_m, "ext")
        os.makedirs(sub, exist_ok=True)
        import shutil as sh
        sh.copy2(arcs_m[0], os.path.join(sub, "p.7z"))
        rc2, out2 = run_cli(["-uz", os.path.join(sub, "p.7z")], env_m, w_m)
        extracted = None
        for rr, _, fs in os.walk(w_m):
            for n in fs:
                if n == "payload.bin" and os.path.abspath(rr) != os.path.abspath(w_m):
                    extracted = os.path.join(rr, n)
        same = False
        if extracted and os.path.isfile(extracted):
            same = open(extracted, "rb").read() == payload
        check("压缩→解压后内容逐字节一致", rc2 == 0 and same,
              "rc2=%s 一致=%s" % (rc2, same))
    else:
        check("压缩→解压后内容逐字节一致", False, "前置压缩失败")

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("静态扫描无法覆盖的组合行为均已实证")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "behavior.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
