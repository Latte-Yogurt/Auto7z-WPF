#!/usr/bin/env python3
# 删掉三语词典里已经废弃的键
import io
import os
import re

LANG_DIR = r"C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF\Resources\Languages"
REMOVE = ["DeleteTEMPCheckBox", "Tip_DeleteTEMP"]

for loc in ("zh-CN", "en-US", "zh-TW"):
    path = os.path.join(LANG_DIR, loc + ".xaml")
    lines = io.open(path, encoding="utf-8").read().splitlines(True)
    out = []
    removed = []
    for line in lines:
        m = re.search(r'x:Key="([^"]+)"', line)
        if m and m.group(1) in REMOVE:
            removed.append(m.group(1))
            continue
        out.append(line)
    io.open(path, "w", encoding="utf-8").write("".join(out))
    print("%s: 移除 %s" % (loc, ", ".join(removed) if removed else "无"))
