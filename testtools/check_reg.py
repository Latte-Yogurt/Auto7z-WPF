#!/usr/bin/env python3
"""检查外壳扩展的注册状态"""
import winreg

CLSID = r"{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"
HKLM = winreg.HKEY_LOCAL_MACHINE
HKCR = winreg.HKEY_CLASSES_ROOT


def show(hive, path, names, label):
    """按给定的值名逐个查。"""
    try:
        with winreg.OpenKey(hive, path) as k:
            parts = []
            for name in names:
                try:
                    value, _ = winreg.QueryValueEx(k, name)
                    parts.append("%s=%s" % (name or "(默认)", value))
                except FileNotFoundError:
                    parts.append("%s=(无)" % (name or "(默认)"))
            print("  %-50s %s" % (label, "; ".join(parts)))
    except FileNotFoundError:
        print("  %-50s (键不存在)" % label)
    except OSError as e:
        print("  %-50s 错误: %s" % (label, e))


DEFAULT = [""]
DEFAULT_AND_MODEL = ["", "ThreadingModel"]
DEFAULT_AND_PATH = ["", "MainExePath"]

print("=== 1) 菜单挂载点 ===")
for sub in ["*", "Directory", "Folder"]:
    show(HKLM, "Software\\Classes\\" + sub + r"\shellex\ContextMenuHandlers\Auto7z", DEFAULT, "HKLM [%s]" % sub)
for sub in ["*", "Directory", "Folder"]:
    show(HKCR, sub + r"\shellex\ContextMenuHandlers\Auto7z", DEFAULT, "HKCR [%s]" % sub)

print("")
print("=== 2) COM 类注册 ===")
show(HKLM, r"Software\Classes\CLSID\%s" % CLSID, DEFAULT, "HKLM CLSID")
show(HKLM, r"Software\Classes\CLSID\%s\InprocServer32" % CLSID, DEFAULT_AND_MODEL, "HKLM InprocServer32")
show(HKLM, r"Software\Classes\CLSID\%s\Auto7z" % CLSID, DEFAULT_AND_PATH, "HKLM Auto7z")
show(HKCR, r"CLSID\%s\InprocServer32" % CLSID, DEFAULT_AND_MODEL, "HKCR InprocServer32")

print("")
print("=== 3) Approved 列表 ===")
approved = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"
show(HKLM, approved, [CLSID], "HKLM")
show(winreg.HKEY_CURRENT_USER, approved, [CLSID], "HKCU")
