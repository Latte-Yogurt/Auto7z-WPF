// ShellProtocol.h
// 外壳扩展与主程序之间传递文件列表的约定。
//
// 命令行格式：
//   Auto7z.exe #<映射名>:<字节数>:<事件名> [-A|-Z|-U]
//
// 映射名与事件名由外壳扩展随机生成， 主程序按名字打开共享内存，
// 从里面读出以 \0 分隔的 UTF-16 路径列表。
// 用共享内存是为了绕开命令行的长度上限(32767 字符),
// 一次选中上百个长路径时命令行装不下。

#pragma once

// 命令行里共享内存引用的前缀
#define SHELL_MAP_PREFIX L'#'

// 共享内存里第一个 wchar_t 恒为 0, 表示后面按 UTF-16 读取
#define SHELL_MAP_MARKER 0

// 事件名与映射名的前缀
#define SHELL_MAP_NAME_PREFIX L"Auto7zShellMap"
#define SHELL_EVENT_NAME_PREFIX L"Auto7zShellEvent"
