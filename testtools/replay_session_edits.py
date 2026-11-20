#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""从 DSH 会话日志重放 edit/write 调用，重建某个文件的最终内容。

背景
----
一次误操作（PowerShell 按 ANSI 读写 UTF-8 源文件）把 `Auto7zViewModel.cs`
烧成了乱码，磁盘上没有可用备份。会话日志里完整记录了本次会话对该文件的
每一次 edit / write（含 old_string 与 new_string），因此可以按时间顺序
把补丁重放到一份干净基底上，得到与损坏前一模一样的文本。

**只读会话日志**：本脚本对 `.dsh\sessions` 只做读取，从不写入。

用法
----
    python replay_session_edits.py <session.jsonl> <目标文件路径> \
        --base <干净基底文件> --out <输出文件>

基底取用户提供的 2.4.0 备份；补丁按日志顺序应用，任一补丁找不到
old_string 就立即报错（说明基底不对，不能糊过去）。
"""
import argparse
import json
import sys
from pathlib import Path


def load_ops(log_path, target_suffix):
    """按顺序取出所有命中目标文件的 edit / write 调用。"""
    ops = []
    with open(log_path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("type") != "tool/call":
                continue
            data = rec.get("data") or {}
            name = data.get("name")
            if name not in ("edit", "write"):
                continue
            raw = data.get("arguments") or ""
            if target_suffix not in raw:
                continue
            try:
                args = json.loads(raw)
            except json.JSONDecodeError:
                continue
            ops.append({
                "seq": rec.get("seq"),
                "name": name,
                "path": args.get("file_path") or "",
                "old": args.get("old_string"),
                "new": args.get("new_string"),
                "content": args.get("content"),
                "replace_all": bool(args.get("replace_all")),
            })
    return ops


def apply_ops(base_text, ops, verbose=True):
    text = base_text
    applied = 0
    for i, op in enumerate(ops):
        name = op["name"]
        try:
            if name == "write":
                # 整文件替换：仅在基底与补丁都对不上时才用，这里如实照做。
                if op.get("content") is None:
                    continue
                text = op["content"]
                applied += 1
                if verbose:
                    print("  [%02d] write  整文件替换 -> %d 字符" % (i, len(text)))
                continue

            old = op.get("old")
            new = op.get("new")
            if old is None or new is None:
                continue

            if op["replace_all"]:
                if old not in text:
                    print("  [%02d] !! replace_all 找不到 old_string，跳过" % i)
                    return None, applied
                text = text.replace(old, new)
                applied += 1
                if verbose:
                    print("  [%02d] edit   replace_all 应用（%d 处）" % (i, text.count(new)))
                continue

            count = text.count(old)
            if count == 0:
                print("  [%02d] !! 找不到 old_string，基底与补丁不匹配" % i)
                print("       old head: %r" % (old[:120],))
                return None, applied
            if count > 1:
                print("  [%02d] !! old_string 出现 %d 次（不唯一），拒绝盲改" % (i, count))
                return None, applied

            text = text.replace(old, new, 1)
            applied += 1
            if verbose:
                print("  [%02d] edit   应用 (+%d/-%d 字符)" % (i, len(new), len(old)))
        except Exception as e:  # noqa: BLE001
            print("  [%02d] !! 异常 %s: %s" % (i, type(e).__name__, e))
            return None, applied
    return text, applied


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session")
    ap.add_argument("target")
    ap.add_argument("--base", required=True, help="干净基底文件（2.4.0 备份）")
    ap.add_argument("--out", required=True)
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args()

    target = a.target.replace("/", "\\")
    suffix = Path(target).name

    base_text = Path(a.base).read_text(encoding="utf-8-sig")
    print("基底: %s (%d 字符)" % (a.base, len(base_text)))

    ops = load_ops(a.session, suffix)
    print("日志里命中 %s 的补丁: %d 个" % (suffix, len(ops)))
    if not ops:
        print("没有可重放的补丁")
        return 1

    text, applied = apply_ops(base_text, ops, verbose=not a.quiet)
    if text is None:
        print("重放失败：基底与补丁链不匹配（应用了 %d/%d）" % (applied, len(ops)))
        return 1

    Path(a.out).write_text(text, encoding="utf-8", newline="")
    print("重放完成: 应用 %d/%d，输出 %d 字符 -> %s" % (applied, len(ops), len(text), a.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
