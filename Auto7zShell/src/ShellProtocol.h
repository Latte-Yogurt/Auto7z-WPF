
// 只放协议常量，主程序和外壳扩展两侧共用，改一处两边同时生效
#pragma once

// 命令行里用 # 打头标记「后面跟的是映射名」，主程序靠它把协议参数和普通参数分开
#define SHELL_MAP_PREFIX L'#'

// 共享内存第一个 wchar_t 是这枚标记，为 0 表示其后按 UTF-16 平铺路径，留出以后换编码的余地
#define SHELL_MAP_MARKER 0

// 名字拼成 Auto7zShellMap<PID><序号>，序号随进程内自增，同一进程连点两次也不会同名
#define SHELL_MAP_NAME_PREFIX L"Auto7zShellMap"
#define SHELL_EVENT_NAME_PREFIX L"Auto7zShellEvent"
