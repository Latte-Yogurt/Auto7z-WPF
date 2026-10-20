#!/usr/bin/env python3
# 给三语词典补充便携模式相关的键
import io
import os
import re

LANG_DIR = r"C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF\Resources\Languages"

ADD = {
    "zh-CN": [
        ("PortableCheckBox", "便携模式"),
        ("Tip_Portable", "便携模式：把配置、组件与日志都放在程序目录的 .config\\Auto7z 下，整个程序可以随身携带。取消勾选则移回 %LOCALAPPDATA%\\Auto7z。"),
        ("Err_PortableMoveFailed", "切换{0}失败：数据目录没能移动"),
        ("Log_PortableSwitched", "已切换到{0}，数据目录：{1}"),
    ],
    "en-US": [
        ("PortableCheckBox", "Portable"),
        ("Tip_Portable", "Portable mode: keeps the config, components and logs in .config\\Auto7z next to the program, so it can be carried around. Unchecking moves them back to %LOCALAPPDATA%\\Auto7z."),
        ("Err_PortableMoveFailed", "Failed to switch to {0}: the data folder could not be moved"),
        ("Log_PortableSwitched", "Switched to {0}, data folder: {1}"),
    ],
    "zh-TW": [
        ("PortableCheckBox", "便攜模式"),
        ("Tip_Portable", "便攜模式：把設定、元件與日誌都放在程式目錄的 .config\\Auto7z 下，整個程式可以隨身攜帶。取消勾選則移回 %LOCALAPPDATA%\\Auto7z。"),
        ("Err_PortableMoveFailed", "切換{0}失敗：資料目錄沒能移動"),
        ("Log_PortableSwitched", "已切換到{0}，資料目錄：{1}"),
    ],
}

for loc, items in ADD.items():
    path = os.path.join(LANG_DIR, loc + ".xaml")
    text = io.open(path, encoding="utf-8").read()
    added = []
    for key, value in items:
        if 'x:Key="%s"' % key in text:
            continue
        value = value.replace("&", "&amp;").replace("<", "&lt;")
        line = '    <sys:String x:Key="%s">%s</sys:String>' % (key, value)
        m = re.search(r'( *<sys:String x:Key="AutoSaveCheckBox">[^\n]*\n)', text)
        if m:
            text = text[:m.end(1)] + line + "\n" + text[m.end(1):]
        else:
            text = text.replace("</ResourceDictionary>", line + "\n</ResourceDictionary>")
        added.append(key)
    io.open(path, "w", encoding="utf-8").write(text)
    print("%s: %s" % (loc, ", ".join(added) if added else "无"))
