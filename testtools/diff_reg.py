#!/usr/bin/env python3
"""把 7-Zip 和 Auto7z 的注册表结构完整列出来对比"""
import winreg

Z7 = r"{23170F69-20BB-278A-1000-000100020000}"
A7 = r"{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"
HKLM = winreg.HKEY_LOCAL_MACHINE


def dump(hive, path, label, depth=0):
    """递归打印键与值"""
    try:
        with winreg.OpenKey(hive, path) as k:
            n_values, n_subkeys, _ = winreg.QueryInfoKey(k)
            for i in range(n_values):
                name, value, vtype = winreg.EnumValue(k, i)
                print("  %s%s = %s" % ("  " * depth, name or "(默认)", value))
            for i in range(n_subkeys):
                sub = winreg.EnumKey(k, i)
                print("  %s[%s]" % ("  " * depth, sub))
                dump(hive, path + "\\" + sub, label, depth + 1)
    except FileNotFoundError:
        print("  %s(不存在) %s" % ("  " * depth, path))
    except OSError as e:
        print("  %s(错误 %s) %s" % ("  " * depth, e, path))


print("========== 7-Zip 的 CLSID ==========")
dump(HKLM, r"Software\Classes\CLSID\%s" % Z7, "7-Zip")

print("")
print("========== Auto7z 的 CLSID ==========")
dump(HKLM, r"Software\Classes\CLSID\%s" % A7, "Auto7z")

print("")
print("========== 7-Zip 的菜单挂载点 ==========")
for sub in ["*", "Directory", "Folder"]:
    p = r"Software\Classes\%s\shellex\ContextMenuHandlers" % sub
    try:
        with winreg.OpenKey(HKLM, p) as k:
            n_values, n_subkeys, _ = winreg.QueryInfoKey(k)
            names = [winreg.EnumKey(k, i) for i in range(n_subkeys)]
            print("  [%s] 子键: %s" % (sub, ", ".join(names)))
    except FileNotFoundError:
        print("  [%s] (不存在)" % sub)

print("")
print("========== Auto7z 的菜单挂载点 ==========")
for sub in ["*", "Directory", "Folder"]:
    p = r"Software\Classes\%s\shellex\ContextMenuHandlers" % sub
    try:
        with winreg.OpenKey(HKLM, p) as k:
            n_values, n_subkeys, _ = winreg.QueryInfoKey(k)
            names = [winreg.EnumKey(k, i) for i in range(n_subkeys)]
            print("  [%s] 子键: %s" % (sub, ", ".join(names)))
    except FileNotFoundError:
        print("  [%s] (不存在)" % sub)
