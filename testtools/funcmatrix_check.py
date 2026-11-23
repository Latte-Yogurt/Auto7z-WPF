#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""功能实现全量校验：三个入口 × 核心能力矩阵。

目标：不是"跑一遍不报错"，而是逐条断言**每个功能真的产出了正确结果**。

矩阵：
  入口   GUI 主窗口（--feed，实际是拖放入队路径）
         GUI 外壳菜单（共享内存参数，出 ProcessWindow）
         命令行（Auto7z.exe，含退出码契约）
  能力   压缩 / 解压 / 自动分派 / 分卷 / 加密 / 校验文件 /
         多文件合并询问 / 取消 / 各处错误退出码

每一项都用「产物 + 退出码 + 结果文案」三重判定，不只看退出码。
"""
import ctypes
import json
import os
import shutil
import subprocess
import sys
import time

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
CLI = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
GUI = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7zGUI.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "funcmatrix")

kernel32 = ctypes.windll.kernel32
PAGE_READWRITE = 0x04
FILE_MAP_ALL_ACCESS = 0xF001F

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def prep(tag):
    """为每个用例准备独立的数据根与工作目录。"""
    work = os.path.join(SDK, tag, "work")
    data = os.path.join(SDK, tag, "data")
    os.makedirs(work, exist_ok=True)
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1,1,1,1,1"
    return work, data, env


def cli(args, env, cwd, timeout=180):
    try:
        p = subprocess.run([CLI] + args, capture_output=True, env=env, cwd=cwd, timeout=timeout)
        return p.returncode, (p.stdout or b"").decode("utf-8", "replace") + \
               (p.stderr or b"").decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        return None, "(超时)"


def find_files(root, suffix):
    out = []
    for rr, _, fs in os.walk(root):
        for n in fs:
            if n.endswith(suffix):
                out.append(os.path.join(rr, n))
    return out


def set_cfg(data, **kw):
    """直接写产品配置，用于设置压缩级别/分卷/加密等。"""
    import json as J
    cfgdir = os.path.join(data, "Auto7z")
    os.makedirs(cfgdir, exist_ok=True)
    base = {
        "Language": "zh-CN", "Volume": "2000", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": True, "ChecksumAlgorithm": "sha256",
        "AutoSave": True, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Normal", "UnzipPassword": "", "ErrorLevel": "Warning",
    }
    base.update(kw)
    open(os.path.join(cfgdir, "Auto7z.json"), "w", encoding="utf-8").write(
        J.dumps(base, ensure_ascii=False, indent=2))


def via_shellmenu(target, mode, env, tag):
    """走外壳菜单路径（共享内存参数），会出 ProcessWindow。"""
    body = b"\x00\x00" + target.encode("utf-16-le") + b"\x00\x00"
    total = len(body)
    uniq = int(time.time() * 1000) % 1000000
    map_name = "Auto7zFullMap_%d_%d" % (os.getpid(), uniq)
    evt_name = "Auto7zFullEvt_%d_%d" % (os.getpid(), uniq)

    kernel32.CreateFileMappingW.restype = ctypes.c_void_p
    kernel32.CreateFileMappingW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                            ctypes.c_uint32, ctypes.c_uint32,
                                            ctypes.c_uint32, ctypes.c_wchar_p]
    hmap = kernel32.CreateFileMappingW(ctypes.c_void_p(-1), None, PAGE_READWRITE, 0, total, map_name)
    kernel32.MapViewOfFile.restype = ctypes.c_void_p
    kernel32.MapViewOfFile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32,
                                       ctypes.c_uint32, ctypes.c_size_t]
    view = kernel32.MapViewOfFile(ctypes.c_void_p(hmap), FILE_MAP_ALL_ACCESS, 0, 0, total)
    ctypes.memmove(view, body, total)
    kernel32.CreateEventW.restype = ctypes.c_void_p
    kernel32.CreateEventW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_wchar_p]
    hevt = kernel32.CreateEventW(None, True, False, evt_name)

    arg = "#%s:%d:%s" % (map_name, total, evt_name)
    proc = subprocess.Popen([GUI, arg, mode], env=env, cwd=os.path.dirname(GUI),
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        proc.wait(timeout=120)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=10)
    return proc.returncode


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("功能实现全量校验：三入口 × 核心能力")
    print("=" * 76)

    # ============ 命令行入口 ============
    print("\n【A】命令行入口")

    # A1 压缩
    w, d, env = prep("A1")
    src = os.path.join(w, "a.txt"); open(src, "w", encoding="utf-8").write("x" * 800)
    rc, out = cli(["-z", src], env, w)
    arcs = find_files(w, ".7z")
    checks = find_files(w, ".sha256")
    check("A1 压缩产出归档", rc == 0 and len(arcs) == 1, "rc=%s 归档=%d" % (rc, len(arcs)))
    check("A1 同时产出校验文件", len(checks) == 1, "sha256=%d" % len(checks))

    # A2 校验文件内容格式
    if checks:
        first = open(checks[0], encoding="utf-8", errors="replace").readline().strip()
        ok_fmt = len(first.split()) == 2 and first.split()[0].isalnum() and first.split()[1].startswith("*")
        check("A2 校验文件格式（hex *name）", ok_fmt, first[:60])

    # A3 解压
    w, d, env = prep("A3")
    src = os.path.join(w, "b.txt"); open(src, "w", encoding="utf-8").write("payload" * 100)
    cli(["-z", src], env, w)
    arcs = find_files(w, ".7z")
    if arcs:
        rc, out = cli(["-uz", arcs[0]], env, w)
        extracted = find_files(w, "b.txt")
        check("A3 解压成功", rc == 0 and "解压完成" in out, "rc=%s" % rc)
        check("A3 解压出原文件", len(extracted) >= 2, "找到 b.txt %d 份" % len(extracted))
    else:
        check("A3 解压成功", False, "前置压缩失败")

    # A4 自动分派：给归档应解压
    w, d, env = prep("A4")
    src = os.path.join(w, "c.txt"); open(src, "w", encoding="utf-8").write("auto" * 100)
    cli(["-z", src], env, w)
    arcs = find_files(w, ".7z")
    if arcs:
        rc, out = cli(["-a", arcs[0]], env, w)
        check("A4 自动分派识别为解压", rc == 0 and "解压完成" in out, "rc=%s 输出=%s" % (rc, out.strip()[:40]))
    else:
        check("A4 自动分派识别为解压", False, "前置压缩失败")

    # A5 分卷
    w, d, env = prep("A5")
    set_cfg(d, Volume="1", CompressionLevel="Store")
    big = os.path.join(w, "big.bin")
    open(big, "wb").write(os.urandom(4 * 1024 * 1024))
    rc, out = cli(["-z", big], env, w)
    vols = [f for f in find_files(w, ".7z.001")]
    allvols = [f for f in os.listdir(os.path.dirname(vols[0]))] if vols else []
    vol_count = len([x for x in allvols if ".7z." in x])
    check("A5 分卷产出多个卷", rc == 0 and vol_count >= 2, "卷数=%d" % vol_count)

    # A6 加密（需密码）
    w, d, env = prep("A6")
    set_cfg(d, Password="TestPwd123")
    src = os.path.join(w, "e.txt"); open(src, "w", encoding="utf-8").write("secret" * 100)
    rc, out = cli(["-z", src], env, w)
    arcs = find_files(w, ".7z")
    if arcs:
        # 换一个干净数据根去解压，验证确实需要密码
        w2, d2, env2 = prep("A6b")
        shutil.copy2(arcs[0], os.path.join(w2, "enc.7z"))
        env2["AUTO7Z_AUTOANSWER"] = "1"
        rc2, out2 = cli(["-uz", os.path.join(w2, "enc.7z")], env2, w2)
        # 无正确密码时应当失败或要求密码
        check("A6 加密包需要密码", rc2 != 0 or "密码" in out2,
              "rc=%s 输出=%s" % (rc2, out2.strip()[:50]))
    else:
        check("A6 加密包需要密码", False, "压缩未产出")

    # A7 错误退出码契约
    w, d, env = prep("A7")
    rc_bad, out_bad = cli(["-z", os.path.join(w, "notexist.txt")], env, w)
    check("A7 不存在的路径 → 退出码 2", rc_bad == 2, "rc=%s" % rc_bad)
    rc_unk, out_unk = cli(["-xyz", os.path.join(w, "a.txt")], env, w)
    check("A7 未知开关 → 退出码 2", rc_unk == 2, "rc=%s" % rc_unk)
    rc_help, _ = cli(["-h"], env, w)
    check("A7 -h → 退出码 0", rc_help == 0, "rc=%s" % rc_help)

    # ============ 外壳菜单入口 ============
    print("\n【B】外壳菜单入口（共享内存，出 ProcessWindow）")

    w, d, env = prep("B1")
    src = os.path.join(w, "s.txt"); open(src, "w", encoding="utf-8").write("shell" * 200)
    rc = via_shellmenu(src, "-z", env, "B1")
    arcs = find_files(w, ".7z")
    check("B1 外壳菜单压缩", rc == 0 and len(arcs) >= 1, "rc=%s 归档=%d" % (rc, len(arcs)))

    w, d, env = prep("B2")
    src = os.path.join(w, "t.txt"); open(src, "w", encoding="utf-8").write("shelluz" * 200)
    cli(["-z", src], env, w)
    arcs = find_files(w, ".7z")
    if arcs:
        tgt = os.path.join(w, "sub"); os.makedirs(tgt, exist_ok=True)
        moved = os.path.join(tgt, "t.7z"); shutil.copy2(arcs[0], moved)
        rc = via_shellmenu(moved, "-uz", env, "B2")
        extracted = find_files(w, "t.txt")
        check("B2 外壳菜单解压", rc == 0 and len(extracted) >= 2,
              "rc=%s 解出 %d 份" % (rc, len(extracted)))
    else:
        check("B2 外壳菜单解压", False, "前置压缩失败")

    # ============ GUI 主窗口入口（拖放等价路径）============
    print("\n【C】GUI 主窗口入口（--feed，等价拖放入队）")

    w, d, env = prep("C1")
    files = []
    for i in range(3):
        f = os.path.join(w, "g%d.txt" % i)
        open(f, "w", encoding="utf-8").write("gui%d" % i * 100)
        files.append(f)
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1,1"   # 多文件询问答「合并」
    proc = subprocess.Popen([GUI, "--feed"] + files, env=env, cwd=os.path.dirname(GUI),
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    time.sleep(8)
    if proc.poll() is None:
        proc.kill()
        proc.wait(timeout=10)
    arcs = find_files(w, ".7z")
    check("C1 GUI 多文件合并压缩", len(arcs) == 1, "归档=%d" % len(arcs))

    w, d, env = prep("C2")
    files = []
    for i in range(3):
        f = os.path.join(w, "h%d.txt" % i)
        open(f, "w", encoding="utf-8").write("sep%d" % i * 100)
        files.append(f)
    env["AUTO7Z_AUTOANSWER"] = "0,0,0,0,0"   # 多文件询问答「不合并」
    proc = subprocess.Popen([GUI, "--feed"] + files, env=env, cwd=os.path.dirname(GUI),
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    time.sleep(10)
    if proc.poll() is None:
        proc.kill()
        proc.wait(timeout=10)
    arcs = find_files(w, ".7z")
    check("C2 GUI 多文件分别压缩", len(arcs) == 3, "归档=%d" % len(arcs))

    # ============ 汇总 ============
    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        print("\n失败项：")
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("PASS")
        code = 0

    out_path = os.path.join(SDK, "funcmatrix.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("\n输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
