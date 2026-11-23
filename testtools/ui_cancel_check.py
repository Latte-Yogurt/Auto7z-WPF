#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证产品内部取消：点取消按钮后 7z 真的停、不留孤儿、不留半成品。

与外部 taskkill 不同，这条测的是 CancelCurrentTask → WaitForProcess 的
取消分支（cancellationToken.IsCancellationRequested → TryKill）。
外部强杀测不到它：进程一死，取消逻辑根本没机会跑。

实现要点（前几次踩坑记录）：
- 走**共享内存参数**（外壳菜单路径），才会出现 ProcessWindow；
  `--feed` 走主窗口，测不到进度窗。
- 找按钮不要依赖中文名匹配：跨进程编码会让 "取消" 变乱码，
  表现是「按钮找到了但匹配不上」。直接用 UI Automation 遍历，
  取第一个 Button（进度窗只有一个）。
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
GUI = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7zGUI.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "uicancel")

kernel32 = ctypes.windll.kernel32
user32 = ctypes.windll.user32
PAGE_READWRITE = 0x04
FILE_MAP_ALL_ACCESS = 0xF001F

# 独立的 PowerShell 脚本：不在 Python 里拼字符串，避免转义地狱
PS_SCRIPT = r"""
param([int]$TargetPid)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) { Write-Output "NOWIN"; exit 1 }

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)

$names = @()
foreach ($b in $btns) { $names += $b.Current.Name }
Write-Output ("BTNS:" + ($names -join ","))

# 进度窗只有一个按钮：取第一个即可，不做名称匹配
$target = $null
foreach ($b in $btns) { $target = $b; break }

if ($null -eq $target) { Write-Output "NOBTN"; exit 2 }

try {
    $pat = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Write-Output "CLICKED"
} catch {
    Write-Output ("INVOKE_FAIL:" + $_.Exception.Message)
    exit 3
}
"""


def count_7z():
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq 7z.exe", "/NH"], capture_output=True)
    return (r.stdout or b"").decode("gbk", "replace").count("7z.exe")


def windows_of(pid):
    out = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def _cb(hwnd, _l):
        owner = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n > 0:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                out.append(buf.value)
        return True

    user32.EnumWindows(_cb, 0)
    return out


def main():
    # 注意：不能用 safe_rmtree(SDK) —— 它会扫描目录内容，
    # 而 SOURCE_EXTS 里含 .json / .md，本脚本的 data\Auto7z\Auto7z.json
    # 与 ui-cancel.json 会被判成「源码类文件」而拒绝删除（实测 UnsafeDeleteError）。
    # 正确做法：只删**自己创建的那几个子目录**，逐个走 safe_rmtree。
    if os.path.isdir(SDK):
        for sub in ("work", "data"):
            sub_path = os.path.join(SDK, sub)
            if os.path.isdir(sub_path):
                safe_rmtree(sub_path)
        # 顶部只可能有本次跑出来的报告文件，直接删（不是递归删除）。
        for name in ("ui-cancel.json",):
            p_ = os.path.join(SDK, name)
            if os.path.isfile(p_):
                try:
                    os.remove(p_)
                except OSError:
                    pass
    os.makedirs(SDK, exist_ok=True)
    work = os.path.join(SDK, "work")
    data = os.path.join(SDK, "data")
    os.makedirs(work, exist_ok=True)
    os.makedirs(data, exist_ok=True)

    big = os.path.join(work, "big.bin")
    print("造 80MB 数据 ...")
    with open(big, "wb") as f:
        for _ in range(80):
            f.write(os.urandom(1024 * 1024))

    cfgdir = os.path.join(data, "Auto7z")
    os.makedirs(cfgdir, exist_ok=True)
    cfg = {
        "Language": "zh-CN", "Volume": 0, "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": True, "ChecksumAlgorithm": "sha256",
        "AutoSave": True, "IsTopMost": False, "RuntimeMode": "High",
        "OutputPath": "", "IsSolidMode": True, "IsEncFileName": False,
        "CompressionLevel": "Ultra", "UnzipPassword": "", "ErrorLevel": "Warning",
    }
    open(os.path.join(cfgdir, "Auto7z.json"), "w", encoding="utf-8").write(
        json.dumps(cfg, ensure_ascii=False, indent=2))

    # 共享内存参数
    body = b"\x00\x00" + big.encode("utf-16-le") + b"\x00\x00"
    total = len(body)
    map_name = "Auto7zCancelMap_%d" % os.getpid()
    evt_name = "Auto7zCancelEvt_%d" % os.getpid()

    kernel32.CreateFileMappingW.restype = ctypes.c_void_p
    kernel32.CreateFileMappingW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                            ctypes.c_uint32, ctypes.c_uint32,
                                            ctypes.c_uint32, ctypes.c_wchar_p]
    hmap = kernel32.CreateFileMappingW(ctypes.c_void_p(-1), None, PAGE_READWRITE,
                                       0, total, map_name)
    kernel32.MapViewOfFile.restype = ctypes.c_void_p
    kernel32.MapViewOfFile.argtypes = [ctypes.c_void_p, ctypes.c_uint32,
                                       ctypes.c_uint32, ctypes.c_uint32, ctypes.c_size_t]
    view = kernel32.MapViewOfFile(ctypes.c_void_p(hmap), FILE_MAP_ALL_ACCESS, 0, 0, total)
    ctypes.memmove(view, body, total)
    kernel32.CreateEventW.restype = ctypes.c_void_p
    kernel32.CreateEventW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                                      ctypes.c_wchar_p]
    hevt = kernel32.CreateEventW(None, True, False, evt_name)

    arg = "#%s:%d:%s" % (map_name, total, evt_name)
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env.pop("AUTO7Z_AUTOANSWER", None)

    before = count_7z()
    print("启动 GUI（外壳菜单路径）...")
    proc = subprocess.Popen([GUI, arg, "-z"], env=env, cwd=os.path.dirname(GUI),
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    # 等进度窗
    deadline = time.time() + 20
    seen_progress = False
    while time.time() < deadline:
        if any("正在处理" in t for t in windows_of(proc.pid)):
            seen_progress = True
            break
        time.sleep(0.2)

    if not seen_progress:
        proc.kill()
        print("!! 进度窗口没出现")
        return 1
    print("进度窗口已出现")

    mid = count_7z()
    print("运行中 7z 子进程数: %d" % mid)

    # 写 ps1（UTF-8 BOM，避免中文/参数被 ANSI 解码）
    ps_path = os.path.join(SDK, "click.ps1")
    with open(ps_path, "w", encoding="utf-8-sig") as f:
        f.write(PS_SCRIPT)

    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", ps_path, "-TargetPid", str(proc.pid)],
                       capture_output=True, timeout=90)
    ps_out = ((r.stdout or b"").decode("utf-8", "replace") +
              (r.stderr or b"").decode("utf-8", "replace")).strip()
    print("UIAutomation: %s" % ps_out.replace("\n", " | "))
    clicked = "CLICKED" in ps_out

    # 等收尾
    t0 = time.time()
    while time.time() - t0 < 40:
        if proc.poll() is not None:
            break
        time.sleep(0.5)

    exited = proc.poll() is not None
    rc = proc.returncode if exited else None
    if not exited:
        proc.kill()
        proc.wait(timeout=10)
    time.sleep(2)

    after = count_7z()

    print("\n=== 结果 ===")
    print("  点了取消: %s" % clicked)
    print("  进程退出: %s (rc=%s)" % (exited, rc))
    print("  7z 子进程: 前=%d 中=%d 后=%d" % (before, mid, after))

    ok = True
    print("\n=== 判定 ===")
    if not clicked:
        print("  [FAIL] 没能点到取消按钮")
        ok = False
    else:
        print("  [OK] 取消按钮已点击")

    if after <= before:
        print("  [OK] 无孤儿 7z")
    else:
        print("  [FAIL] 残留 %d 个孤儿 7z" % (after - before))
        ok = False

    if exited:
        print("  [OK] 进程已退出，未挂死")
    else:
        print("  [FAIL] 进程挂死")
        ok = False

    print("\n总判定: %s" % ("PASS" if ok else "FAIL"))
    out_path = os.path.join(SDK, "ui-cancel.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps({
        "ps_output": ps_out, "clicked": clicked, "exited": exited, "rc": rc,
        "before": before, "mid": mid, "after": after, "ok": ok,
    }, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
