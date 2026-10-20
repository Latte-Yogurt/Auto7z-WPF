#!/usr/bin/env python3
"""给主窗口的控件按视觉顺序加 TabIndex"""
import io
import re

XAML = r"C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF\MainWindow.xaml"

# 视觉顺序: 左侧面板(模式选择 -> 关于 -> 设置) -> 右侧设置区(按标签页)
#   常规页: 置顶 自动保存 便携 运行模式 输出路径 浏览
#   压缩页: 分卷 MD5 格式 固实 zstd 文件名加密 密码 压缩等级
#   解压页: 解压密码
TAB_INDEX = {
    # 左侧
    "RadioButton_Auto": 0,
    "RadioButton_Zip": 1,
    "RadioButton_UnZip": 2,
    "AboutButton": 3,
    "SettingButton": 4,
    # 常规页
    "CheckBox_TopMost": 10,
    "CheckBox_AutoSave": 11,
    "CheckBox_Portable": 12,
    "ComboBox_RuntimeMode": 13,
    "TextBox_FilePath": 14,
    "BrowseButton": 15,
    # 压缩页
    "TextBox_Volume": 20,
    "CheckBox_GenerateMD5": 21,
    "ComboBox_Format": 22,
    "CheckBox_SolidMode": 23,
    "CheckBox_Zstd": 24,
    "CheckBox_ENCFileName": 25,
    "PasswordBox_Zip": 26,
    "ComboBox_ZipLevel": 27,
    # 解压页
    "PasswordBox_UnZip": 30,
}

text = io.open(XAML, encoding="utf-8").read()

# 1) 三个模式单选按钮还没有 x:Name, 先加上
NAMED = {
    'Content="{DynamicResource AutoModeButton}"':
        'x:Name="RadioButton_Auto" Content="{DynamicResource AutoModeButton}"',
    'Content="{DynamicResource ZipModeButton}"':
        'x:Name="RadioButton_Zip" Content="{DynamicResource ZipModeButton}"',
    'Content="{DynamicResource UnZipModeButton}"':
        'x:Name="RadioButton_UnZip" Content="{DynamicResource UnZipModeButton}"',
}
for old, new in NAMED.items():
    if new not in text:
        assert old in text, "没找到: " + old
        text = text.replace(old, new, 1)

# 2) 浏览按钮也没有名字
old = '<Button Grid.Column="2" Content="{DynamicResource BrowseButton}" Width="Auto" Background="Transparent" Click="BrowseOutputFolder_Click"/>'
new = '<Button x:Name="BrowseButton" Grid.Column="2" Content="{DynamicResource BrowseButton}" Width="Auto" Background="Transparent" Click="BrowseOutputFolder_Click"/>'
if 'x:Name="BrowseButton"' not in text:
    assert old in text, "没找到浏览按钮"
    text = text.replace(old, new, 1)

# 3) 给每个控件加 TabIndex
added = []
for name, index in TAB_INDEX.items():
    # 找到该控件的起始标签
    m = re.search(r'<(?:CheckBox|ComboBox|TextBox|Button|RadioButton|PasswordBox)\s+[^>]*?x:Name="%s"' % re.escape(name),
                  text, re.S)
    if not m:
        print("  !! 没找到控件:", name)
        continue

    tag = m.group(0)
    if "TabIndex=" in tag:
        continue
    # 在 x:Name 后面插 TabIndex
    new_tag = tag.replace('x:Name="%s"' % name,
                          'x:Name="%s" TabIndex="%d"' % (name, index), 1)
    text = text[:m.start()] + new_tag + text[m.end():]
    added.append("%s=%d" % (name, index))

# 4) 启用 Tab 导航
text = text.replace('KeyboardNavigation.TabNavigation="None"',
                    'KeyboardNavigation.TabNavigation="Cycle"')

io.open(XAML, "w", encoding="utf-8").write(text)
print("已设置 TabIndex: " + ", ".join(added))
print("Tab 导航: " + ("已启用 Cycle" if 'TabNavigation="Cycle"' in text else "未启用"))
