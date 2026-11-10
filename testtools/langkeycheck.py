#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""资源键完整性检查：代码取用的键必须存在于三语词典里。

为什么需要它：`find_dead2.py` 只做三语词典**互相**差集，从不与代码实际取用的
键集合比对。三份词典同时缺同一个键时，它会报「一致」，而代码里取不到该键、
静默回落到硬编码文案——这个缺陷已经真实发生过两次。

用法：
    python langkeycheck.py [源码目录]
退出码：0 = 全部存在，1 = 有缺失
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_PROJECT = HERE.parent / "Auto7z_WPF" / "Auto7z_WPF"

# 取用语言资源键的全部入口
ACCESSORS = (
    "GetResourceText", "GetErrorText", "GetFormattedText",
    "TryFindResource", "FindResource",
)


def load_language_keys(project):
    lang_dir = project / "Resources" / "Languages"
    if not lang_dir.is_dir():
        return {}
    return {
        f.stem: set(re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8-sig", errors="replace")))
        for f in sorted(lang_dir.glob("*.xaml"))
    }


def load_used_keys(project):
    used = {}
    pattern = re.compile(r'\b(?:' + "|".join(ACCESSORS) + r')\s*\(\s*"([^"]+)"')
    for cs in project.rglob("*.cs"):
        if any(part in ("obj", "objgui", "bin") for part in cs.parts):
            continue
        for lineno, line in enumerate(
                cs.read_text(encoding="utf-8-sig", errors="replace").splitlines(), 1):
            for match in pattern.finditer(line):
                used.setdefault(match.group(1), []).append("%s:%d" % (cs.name, lineno))
    return used


def main():
    project = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_PROJECT
    lang = load_language_keys(project)
    used = load_used_keys(project)

    print("=" * 70)
    print("资源键完整性检查")
    print("源码目录:", project)
    print("=" * 70)

    if not lang:
        print("!! 找不到 Resources/Languages/*.xaml")
        return 1

    problems = []

    print("\n--- 三语词典两两一致 ---")
    for name, keys in lang.items():
        print("  %-6s %d 键" % (name, len(keys)))
    names = sorted(lang)
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            a, b = lang[names[i]], lang[names[j]]
            if a != b:
                problems.append("%s 与 %s 键集合不一致" % (names[i], names[j]))
                print("  !! %s 独有: %s" % (names[i], sorted(a - b)))
                print("  !! %s 独有: %s" % (names[j], sorted(b - a)))

    print("\n--- 代码取用键 ⊆ 词典键 ---")
    all_keys = set().union(*lang.values())
    missing = sorted(set(used) - all_keys)
    if missing:
        for key in missing:
            problems.append("缺失键 " + key)
            print("  !! 缺失 %s  取用点: %s" % (key, ", ".join(used[key][:3])))
    else:
        print("  OK：代码取用 %d 个键，全部存在于词典" % len(used))

    print("\nTOTAL PROBLEMS: %d" % len(problems))
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
