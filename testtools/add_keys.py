#!/usr/bin/env python3
# 给三语词典补充按钮文本与密码错误提示的键
import io
import os
import re

LANG_DIR = r"C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF\Resources\Languages"

ADD = {
    "zh-CN": [
        ('YesButton', '是'),
        ('NoButton', '否'),
        ('OkButton', '确定'),
        ('PasswordWrongAttempt', '密码错误，第 {0} 次尝试，重新询问密码'),
    ],
    "en-US": [
        ('YesButton', 'Yes'),
        ('NoButton', 'No'),
        ('OkButton', 'OK'),
        ('PasswordWrongAttempt', 'Wrong password, attempt {0}. Please try again.'),
    ],
    "zh-TW": [
        ('YesButton', '是'),
        ('NoButton', '否'),
        ('OkButton', '確定'),
        ('PasswordWrongAttempt', '密碼錯誤，第 {0} 次嘗試，重新詢問密碼'),
    ],
}

for loc, items in ADD.items():
    path = os.path.join(LANG_DIR, loc + ".xaml")
    text = io.open(path, encoding="utf-8").read()
    added = []
    for key, value in items:
        if 'x:Key="%s"' % key in text:
            continue
        line = '    <sys:String x:Key="%s">%s</sys:String>' % (key, value)
        # 插到 ConfirmButton 那一行后面
        m = re.search(r'( *<sys:String x:Key="ConfirmButton">[^\n]*\n)', text)
        if m:
            text = text[:m.end(1)] + line + "\n" + text[m.end(1):]
        else:
            text = text.replace("</ResourceDictionary>", line + "\n</ResourceDictionary>")
        added.append(key)
    io.open(path, "w", encoding="utf-8").write(text)
    print("%s: 新增 %s" % (loc, ", ".join(added) if added else "无"))
