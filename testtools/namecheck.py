#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""§4.1 命名风格一致性检查。

规则（按 C# 惯例 + 本工程实测多数派，§4.1「以现有文件的主流风格为准」）：
  private 实例字段（非 static、非 const）    -> _camelCase
  private static 字段（含 static readonly） -> _camelCase
      实测多数派 21:0（改名前是 14:10，已统一）
  private const                              -> PascalCase
  属性（public / private，含 => 表达式体）   -> PascalCase
  方法                                         -> PascalCase
  局部变量                                    -> camelCase

注：private static string X => ... 是属性（表达式体），不是字段，归 PascalCase。
"""
import re
import sys
from pathlib import Path

# 源码目录：可由命令行第一个参数指定（变异测试要指向副本）
if len(sys.argv) > 1:
    SRC = Path(sys.argv[1])
else:
    SRC = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF")

# 只匹配真正的字段声明行（末尾是 = 或 ;，排除 == ）
FIELD = re.compile(
    r"(?m)^[ \t]*private\s+(?:(?:readonly|static|const)\s+)*"
    r"[\w\.<>\[\],\?]+\s+(\w+)\s*(?:=|;)(?!=)")
# 属性：X Y {  或  X Y =>
PROP = re.compile(r"(?m)^[ \t]*(?:public|private|internal|protected)\s+"
                  r"(?:(?:static|virtual|override|new)\s+)*"
                  r"[\w\.<>\[\],\?]+\s+(\w+)\s*(?:\{|=>)")
METHOD = re.compile(r"(?m)^[ \t]*(?:public|private|internal|protected)\s+"
                    r"(?:(?:static|virtual|override|new|async)\s+)*"
                    r"[\w\.<>\[\],\?]+\s+(\w+)\s*\(")
LOCAL = re.compile(r"(?m)^[ \t]+(?:var|int|string|bool|double|long|byte\[\]|char|uint)\s+(\w+)\s*=")
DECL = re.compile(r"(?m)^[ \t]*private\s+(?:struct|class|enum|interface|delegate)\b")

KEYWORDS = {"if", "for", "while", "switch", "catch", "foreach", "using",
            "lock", "return", "get", "set", "new", "typeof", "nameof"}

bad = []
tot = {"field": 0, "const": 0, "prop": 0, "local": 0, "method": 0}

for f in sorted(SRC.rglob("*.cs")):
    if f.name.endswith(".Designer.cs"):
        continue
    t = f.read_text(encoding="utf-8", errors="replace")

    decl_starts = {t.rfind("\n", 0, m.start()) + 1 for m in DECL.finditer(t)}

    for m in FIELD.finditer(t):
        ls = t.rfind("\n", 0, m.start()) + 1
        if ls in decl_starts:
            continue
        n = m.group(1)
        # 取整行来判定：表达式体属性（X => ...）不是字段
        le = t.find("\n", m.end())
        if le < 0:
            le = len(t)
        line = t[ls:le]
        if "=>" in line:
            continue
        # const -> PascalCase
        if re.search(r"\bconst\b", line):
            tot["const"] += 1
            if not n[:1].isupper():
                bad.append((f.name, "常量", n, "应 PascalCase"))
            continue
        # static 字段（含 static readonly）-> _camelCase（工程多数派）
        if re.search(r"\bstatic\b", line):
            tot["static"] = tot.get("static", 0) + 1
            if not n.startswith("_") or not n[1:2].islower():
                bad.append((f.name, "静态字段", n, "应 _camelCase"))
            continue
        # 实例字段 -> _camelCase
        tot["field"] += 1
        if not n.startswith("_") or not n[1:2].islower():
            bad.append((f.name, "私有实例字段", n, "应 _camelCase"))

    for m in PROP.finditer(t):
        n = m.group(1)
        if n in KEYWORDS:
            continue
        tot["prop"] += 1
        if not n[:1].isupper():
            bad.append((f.name, "属性", n, "应 PascalCase"))

    for m in METHOD.finditer(t):
        n = m.group(1)
        if n in KEYWORDS:
            continue
        tot["method"] += 1
        if not n[:1].isupper():
            bad.append((f.name, "方法", n, "应 PascalCase"))

    for m in LOCAL.finditer(t):
        n = m.group(1)
        tot["local"] += 1
        if not n[:1].islower():
            bad.append((f.name, "局部变量", n, "应 camelCase"))

print("统计: 私有实例字段 %d，静态字段 %d，常量 %d，属性 %d，方法 %d，局部变量 %d"
      % (tot["field"], tot.get("static", 0), tot["const"], tot["prop"], tot["method"], tot["local"]))
if bad:
    print("不一致 %d 处:" % len(bad))
    for b in bad:
        print("  %-26s %-10s %-26s %s" % b)
    sys.exit(1)
print("全部一致 ✓")
sys.exit(0)
