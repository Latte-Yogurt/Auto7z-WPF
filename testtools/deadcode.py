#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""死代码审查（只报告，不改代码）。

审查维度
--------
一、完全零引用成员      —— 字段/属性/方法/常量/枚举成员，除声明外无人提及
二、只写不读的成员      —— 有赋值但一次都没被读过（零引用判据抓不到这一类：
                            `AppState.CurrentStatus` 有 9 处赋值，引用数看着很"健康"）
三、无人引用的词典键    —— 三语词典里的键，代码与 XAML 都不取用
四、C++ 零引用函数      —— 外壳扩展与构建工具
五、死枚举类型          —— 枚举的所有成员都无人引用时，整个类型都是多余的
六、测试工具调用情况    —— 编了却没有任何脚本调用

判定口径
--------
"引用"= 标识符作为独立单词出现在**任一**被扫文件里，包含注释与字符串字面量。

为什么把字符串也算进来：本项目大量按名字取成员——反射探针
（`sha256probe` 调 `CalculateSHA256`、`guardprobe` 调守卫方法）、
WPF 的 `DynamicResource` / `{Binding X}`、安装脚本的命令行开关。
把这些算成"未引用"会得到一份满屏假阳性的报告，比不报更糟。

代价是漏报：真正的死代码只要名字在别处被提过一次就查不出来。
所以本工具的输出是**候选清单**，每条都必须人工复核后才能删。

反向的坑也踩过：本文件的注释里写过 "AppState.BasePath 就是这样躲过审查的"，
结果那句话本身成了 BasePath 的一次"引用"，让它继续隐身。
所以本脚本把自己排除在语料之外（SKIP_NAMES）。

用法:
    python deadcode.py            # 扫当前工程
    python deadcode.py --verbose  # 连引用数一起打印
退出码: 恒为 0（只报告，不判失败）
"""
import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"
SHELL = WORKSPACE / "Auto7zShell"

# 接口实现与框架约定：这些成员必须存在，但没有直接调用点。
INTERFACE_MEMBERS = {
    "Execute", "CanExecute", "RaiseCanExecuteChanged",       # ICommand
    "Dispose", "GetHashCode", "Equals", "ToString",           # 框架约定
    "OnPropertyChanged", "SetProperty",                       # 基类受保护成员
    "QueryInterface", "AddRef", "Release",                    # COM
    "CreateInstance", "LockServer",                           # IClassFactory
    "Initialize", "QueryContextMenu", "InvokeCommand", "GetCommandString",
    "DllMain", "DllGetClassObject", "DllCanUnloadNow",
    "DllRegisterServer", "DllUnregisterServer", "DllSetMainExePath",
    "DllRemoveMainExePath", "DllSetLanguage",
    "Main", "wmain",
    # WPF 由 XAML 触发的回调
    "InitializeComponent", "OnKeyDown", "OnExit", "OnStartup",
}

# P/Invoke 结构体字段：布局必须与 Win32 逐字段对齐，删掉会改变后续字段的偏移。
# 判据：所在结构体带 StructLayout 特性时，其字段一律保留。
STRUCT_LAYOUT_HINT = re.compile(r"StructLayout")

# 「只写不读」里有意为之的例外。
# acc：微基准里的累加器 —— 反射调用的返回值必须被用掉，否则 JIT 会把整个调用优化掉，
# 基准就测了个空。它天生"只写不读"，不是死代码。
ACCUMULATOR_BY_DESIGN = {"acc"}

ROOTS = [
    WORKSPACE / "README.md",
    WORKSPACE / "Auto7z_ReleaseFix.md",
    PROJECT,
    SHELL / "src",
    SHELL / "buildtool",
    WORKSPACE / "testtools",
]

SCAN_SUFFIXES = {".cs", ".cpp", ".h", ".xaml", ".py", ".ps1", ".md", ".rc", ".bat", ".csproj", ".json"}
SKIP_PARTS = {"bin", "obj", "objgui", "__pycache__", ".vs"}

# 审查脚本自己提到某个名字，不该算那个名字"被引用"
SKIP_NAMES = {"deadcode.py", "deadcode2.py"}


def gather_files():
    files = []
    for root in ROOTS:
        if root.is_file():
            if root.name not in SKIP_NAMES:
                files.append(root)
            continue
        if not root.exists():
            continue
        for p in root.rglob("*"):
            if not p.is_file() or p.name in SKIP_NAMES:
                continue
            if p.suffix.lower() not in SCAN_SUFFIXES:
                continue
            if any(part in SKIP_PARTS for part in p.parts):
                continue
            files.append(p)
    return sorted(set(files))


def load_corpus(files):
    corpus = {}
    for p in files:
        try:
            corpus[p] = p.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            try:
                corpus[p] = p.read_text(encoding="gbk", errors="replace")
            except OSError:
                continue
    return corpus


def word_pattern(name):
    return re.compile(r"(?<![\w$])" + re.escape(name) + r"(?![\w$])")


def count_refs(name, corpus):
    pattern = word_pattern(name)
    return sum(len(pattern.findall(text)) for text in corpus.values())


def count_reads(name, corpus, decl_path, decl_line):
    """统计"读"的次数：排除声明行，也排除 `X = ...` / `X++` 这类纯赋值。

    这是本工具的核心判据。零引用判据对"只写不读"完全失效 ——
    `AppState.CurrentStatus` 被赋值 9 次，引用数高达 10，看着很健康，
    但从来没有一处读取过它，整个状态机（含 volatile/Interlocked）都是空转。
    """
    pattern = word_pattern(name)
    reads = 0
    for path, text in corpus.items():
        for m in pattern.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            if path == decl_path and line == decl_line:
                continue      # 声明本身
            tail = text[m.end():m.end() + 3]
            if re.match(r"\s*(=[^=]|\+=|-=|\*=|/=|\+\+|--)", tail):
                continue      # 纯赋值不算读
            reads += 1
    return reads


# ---------------------------------------------------------------- C# 声明提取

ENUM_BLOCK = re.compile(r"(?ms)enum\s+(\w+)\s*\{(.*?)\}")
ENUM_MEMBER = re.compile(r"(?m)^\s*(\w+)\s*(?:=\s*[^,\n]+)?,?\s*$")

CS_MODIFIERS = re.compile(
    r"\b(?:public|private|protected|internal|static|readonly|volatile|async|"
    r"const|override|virtual|sealed|partial|new|extern|unsafe|abstract)\b")

CS_DECL_LINE = re.compile(
    r"^(?P<type>[\w<>\[\],\.\?]+)\s+(?P<name>\w+)\s*(?P<tail>\(|\{|=>|=|;)")

CS_NOT_DECL = re.compile(r"^(?:return|throw|if|else|for|foreach|while|switch|case|using|lock|"
                         r"try|catch|finally|do|break|continue|yield|await|new|var|this|base)\b")


def _extract_decls_from_line(line):
    stripped = line.strip()
    if not stripped or stripped.startswith(("//", "/*", "*", "#", "[")):
        return None

    bare = re.sub(r"\s+", " ", CS_MODIFIERS.sub(" ", stripped)).strip()
    if CS_NOT_DECL.match(bare):
        return None

    m = CS_DECL_LINE.match(bare)
    if not m:
        return None

    tail = m.group("tail")
    kind = {"(": "method", "{": "property", "=>": "property", "=": "field", ";": "field"}.get(tail)
    if kind is None:
        return None

    name = m.group("name")
    if name in ("get", "set", "return", "value"):
        return None
    return name, kind


def _looks_like_bare_decl(line):
    """形如 `Type Name` 且没有后续符号：多行声明的第一行。"""
    stripped = line.strip()
    if not stripped or stripped.startswith(("//", "/*", "*", "#", "[")):
        return None
    bare = re.sub(r"\s+", " ", CS_MODIFIERS.sub(" ", stripped)).strip()
    if CS_NOT_DECL.match(bare):
        return None
    if re.fullmatch(r"[\w<>\[\],\.\?]+\s+\w+", bare):
        return bare.split()[-1]
    return None


def extract_cs_decls(corpus):
    """{name: (kind, path, line)}；同名取第一次出现。"""
    decls = {}
    struct_ranges = {}      # path -> [(start_line, end_line)]，StructLayout 结构体的行范围

    for path, text in corpus.items():
        if path.suffix != ".cs":
            continue

        lines = text.splitlines()

        # 先标出 P/Invoke 结构体的行范围：其字段一律不算死代码
        for m in re.finditer(r"(?m)^\s*\[StructLayout[^\]]*\]\s*$", text):
            start = text.count("\n", 0, m.start()) + 1
            depth = 0
            end = start
            for i in range(start - 1, len(lines)):
                depth += lines[i].count("{") - lines[i].count("}")
                if depth > 0 and i > start - 1:
                    end = i + 1
                if depth > 0 and lines[i].strip() == "}" and depth == 1:
                    end = i + 1
                    break
            struct_ranges.setdefault(path, []).append((start, end))

        for index, line in enumerate(lines):
            if not line[:1].isspace():
                continue

            got = _extract_decls_from_line(line)

            # 多行声明：名字在这一行，`{` 在下一行非空行。
            # `public static OperationStatus CurrentStatus` 换行后才是 `{`，
            # 只看单行会整条漏掉。
            if got is None:
                name = _looks_like_bare_decl(line)
                if name:
                    for nxt in lines[index + 1:]:
                        if not nxt.strip():
                            continue
                        if nxt.strip().startswith(("{", "=>")):
                            got = (name, "property")
                        break

            if not got:
                continue
            name, kind = got
            if name in decls:
                continue
            if kind == "field":
                line_no = index + 1
                if any(a <= line_no <= b for a, b in struct_ranges.get(path, [])):
                    continue      # P/Invoke 布局字段，保留
            decls[name] = (kind, path, index + 1)

        for block in ENUM_BLOCK.finditer(text):
            body = block.group(2)
            body_start = block.start(2)
            for m in ENUM_MEMBER.finditer(body):
                member = m.group(1)
                if not member or member in ("}", "{") or member in decls:
                    continue
                line = text.count("\n", 0, body_start + m.start()) + 1
                decls[member] = ("enum", path, line)

    return decls


def extract_enums(corpus):
    """{enum_name: (path, line, [members])}"""
    enums = {}
    for path, text in corpus.items():
        if path.suffix != ".cs":
            continue
        for m in re.finditer(r"(?m)^\s*(?:public|private|protected|internal)?\s*"
                             r"(?:static\s+)?enum\s+(\w+)\s*\{([^}]*)\}", text):
            name = m.group(1)
            if name in enums:
                continue
            members = [mm.group(1) for mm in ENUM_MEMBER.finditer(m.group(2))
                       if mm.group(1) not in ("}", "{")]
            enums[name] = (path, text.count("\n", 0, m.start()) + 1, members)
    return enums


# ---------------------------------------------------------------- C++ 声明提取

CPP_DECL_PATTERNS = [
    ("func", re.compile(r"(?m)^[ \t]*(?:static\s+)?(?:inline\s+)?"
                        r"(?:void|bool|int|HRESULT|LONG|HICON|HBITMAP|std::wstring|unsigned|UINT|DWORD)\s+"
                        r"(\w+)\s*\(")),
    ("export", re.compile(r'extern\s+"C"\s+HRESULT\s+STDAPICALLTYPE\s+(\w+)\s*\(')),
    ("export", re.compile(r'extern\s+"C"\s+BOOL\s+WINAPI\s+(\w+)\s*\(')),
]

CPP_FRAMEWORK = INTERFACE_MEMBERS | {"WinMain"}


def extract_cpp_decls(corpus):
    decls = {}
    for path, text in corpus.items():
        if path.suffix not in (".cpp", ".h"):
            continue
        for kind, pattern in CPP_DECL_PATTERNS:
            for m in pattern.finditer(text):
                name = m.group(1)
                if name in decls or name in CPP_FRAMEWORK:
                    continue
                decls[name] = (kind, path, text.count("\n", 0, m.start()) + 1)
    return decls


# ---------------------------------------------------------------- 词典键

LANG_KEY = re.compile(r'x:Key="([^"]+)"')


def collect_lang_keys(corpus):
    keys = {}
    for path, text in corpus.items():
        if path.suffix != ".xaml" or "Languages" not in str(path):
            continue
        for m in LANG_KEY.finditer(text):
            keys.setdefault(m.group(1), []).append(path)
    return keys


def dynamic_resource_keys(corpus):
    used = set()
    pattern = re.compile(r"\{(?:Dynamic|Static)Resource\s+([\w\.]+)\s*\}")
    for path, text in corpus.items():
        if path.suffix != ".xaml":
            continue
        for m in pattern.finditer(text):
            used.add(m.group(1))
    return used


# ---------------------------------------------------------------- 测试工具

def tool_usage(corpus):
    cs_dir = WORKSPACE / "testtools" / "cs"
    if not cs_dir.exists():
        return []
    tools = sorted(d.name for d in cs_dir.iterdir()
                   if d.is_dir() and (d / (d.name + ".cs")).exists())

    scripts = {p: t for p, t in corpus.items()
               if p.suffix in (".py", ".ps1") and "testtools" in str(p)}

    report = []
    for tool in tools:
        mentioned = [p.name for p, t in scripts.items()
                     if re.search(r"(?<![\w])" + re.escape(tool) + r"(?![\w])", t)]
        report.append((tool, mentioned))
    return report


# ---------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    corpus = load_corpus(gather_files())
    print("=" * 74)
    print("死代码审查")
    print("=" * 74)
    print("扫描文件 %d 个（.cs/.cpp/.xaml/.py/.ps1/.md，跳过 bin/obj 与本脚本自身）" % len(corpus))

    findings = 0

    def emit(kind, name, rel, line, detail=""):
        nonlocal findings
        findings += 1
        loc = "%s:%d" % (rel, line) if line else rel
        print("  !! %-30s %-11s %-46s %s" % (name, kind, loc, detail))

    # ---- 一、完全零引用 ----
    print("")
    print("-" * 74)
    print("一、完全零引用成员")
    print("-" * 74)
    decls = extract_cs_decls(corpus)
    enums = extract_enums(corpus)
    enum_members = {m for _, _, ms in enums.values() for m in ms}

    zero = []
    for name, (kind, path, line) in sorted(decls.items()):
        if name in INTERFACE_MEMBERS:
            continue
        # 枚举类型交给第五节判、枚举成员这里判
        if name in enums:
            continue
        refs = count_refs(name, corpus)
        if refs <= 1:
            zero.append((name, kind, path, line, refs))
    if zero:
        for name, kind, path, line, refs in zero:
            emit(kind, name, path.relative_to(WORKSPACE), line,
                 "refs=%d" % refs if args.verbose else "")
    else:
        print("  (无)")

    # ---- 二、只写不读 ----
    print("")
    print("-" * 74)
    print("二、只写不读的成员（有赋值、从未被读取）")
    print("-" * 74)
    writeonly = []

    # 已删除、无需再审的键（Type 声明与 enum 成员走别的判据）
    for name, (kind, path, line) in sorted(decls.items()):
        if kind not in ("field", "property"):
            continue
        if name in INTERFACE_MEMBERS or name in ACCUMULATOR_BY_DESIGN:
            continue
        refs = count_refs(name, corpus)
        if refs <= 1:
            continue          # 已在第一节报过
        reads = count_reads(name, corpus, path, line)
        if reads == 0:
            writeonly.append((name, kind, path, line, refs))

    # C++ 里没有属性概念，这里只看 C#
    if writeonly:
        for name, kind, path, line, refs in writeonly:
            emit(kind, name, path.relative_to(WORKSPACE), line,
                 "writes=%d reads=0" % refs)
    else:
        print("  (无)")

    # ---- 三、词典键 ----
    print("")
    print("-" * 74)
    print("三、无人引用的词典键")
    print("-" * 74)
    lang_keys = collect_lang_keys(corpus)
    used_keys = dynamic_resource_keys(corpus)
    dead_keys = []
    for key in sorted(lang_keys):
        if key in used_keys:
            continue
        if count_refs(key, corpus) > len(lang_keys[key]):
            continue
        dead_keys.append(key)
    if dead_keys:
        for key in dead_keys:
            emit("lang-key", key, "Resources/Languages", 0, "%d 种语言" % len(lang_keys[key]))
    else:
        print("  (无)")

    # ---- 四、C++ ----
    print("")
    print("-" * 74)
    print("四、C++ 零引用函数")
    print("-" * 74)
    cpp_decls = extract_cpp_decls(corpus)
    cpp_zero = [(n, k, p, l, count_refs(n, corpus))
                for n, (k, p, l) in sorted(cpp_decls.items()) if count_refs(n, corpus) <= 1]
    if cpp_zero:
        for name, kind, path, line, refs in cpp_zero:
            emit(kind, name, path.relative_to(WORKSPACE), line, "refs=%d" % refs)
    else:
        print("  (无)")

    # ---- 五、死枚举 ----
    print("")
    print("-" * 74)
    print("五、死枚举类型（所有成员都无人引用）")
    print("-" * 74)
    dead_enums = []
    for name, (path, line, members) in sorted(enums.items()):
        if not members:
            continue
        # 类型名本身被引用过就说明还在用
        if count_refs(name, corpus) > 1:
            continue
        used_members = [m for m in members if count_refs(m, corpus) > 1]
        if not used_members:
            dead_enums.append((name, path, line, members))
    if dead_enums:
        for name, path, line, members in dead_enums:
            emit("enum", name, path.relative_to(WORKSPACE), line,
                 "成员 %s 全无人用" % ",".join(members))
    else:
        print("  (无)")

    # ---- 六、测试工具 ----
    print("")
    print("-" * 74)
    print("六、测试工具调用情况")
    print("-" * 74)
    for tool, mentioned in tool_usage(corpus):
        if mentioned:
            print("  %-14s 被调用: %s" % (tool, ", ".join(sorted(mentioned)[:3])))
        else:
            emit("tool", tool, "testtools/cs", 0, "没有任何脚本调用")

    print("")
    print("=" * 74)
    print("候选合计: %d 条（每条都要人工复核后才可删）" % findings)
    print("=" * 74)
    return 0


if __name__ == "__main__":
    sys.exit(main())
