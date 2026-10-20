// 构建 Auto7zShell 外壳扩展。
//
// 做成 exe 而不是 bat, 是为了不让人随手改构建步骤。
// 需要 Visual Studio 的 x64 工具链, 靠 vcvars64.bat 把环境变量准备好。
//
// 用法:
//   buildtool.exe            构建
//   buildtool.exe --clean    先删掉 build 目录再构建
//   buildtool.exe --vcvars <路径>   指定 vcvars64.bat 的位置
//   buildtool.exe --help     显示说明

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>

#include <cstdio>
#include <cstring>
#include <cwctype>
#include <string>
#include <vector>

namespace
{
    struct BuildContext
    {
        std::wstring projectDir;   // 组件根目录
        std::wstring srcDir;       // 源码目录
        std::wstring outDir;       // 产物目录
        std::wstring vcvars;       // vcvars64.bat 的位置
        bool clean = false;        // 构建前是否清空产物目录
    };

    // 往控制台写一行。
    //
    // 用 WriteConsoleW 而不是 wprintf: 控制台代码页是 GBK(936),
    // wprintf 会把中文按 UTF-8 写过去, 显示出来全是问号。
    // 按控制台的代码页把宽字符转成多字节。
    //
    // 输出被重定向到文件或管道时 WriteConsoleW 用不了, 只能自己转。
    // 直接 fwrite 宽字符会写出乱码, 因为文件里存的是多字节。
    std::string ToConsoleBytes(const std::wstring &text)
    {
        UINT codePage = GetConsoleOutputCP();
        if (codePage == 0)
            codePage = CP_ACP;

        int size = WideCharToMultiByte(codePage, 0, text.c_str(), static_cast<int>(text.size()),
                                       nullptr, 0, nullptr, nullptr);
        if (size <= 0)
            return std::string();

        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(codePage, 0, text.c_str(), static_cast<int>(text.size()),
                            &result[0], size, nullptr, nullptr);
        return result;
    }

    void WriteLineTo(HANDLE handle, const std::wstring &text)
    {
        std::wstring line = text + L"\n";

        DWORD written = 0;
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE &&
            WriteConsoleW(handle, line.c_str(), static_cast<DWORD>(line.size()), &written, nullptr))
        {
            return;
        }

        // 输出被重定向, 按代码页转成多字节再写
        std::string bytes = ToConsoleBytes(line);
        FILE *stream = (handle == GetStdHandle(STD_ERROR_HANDLE)) ? stderr : stdout;
        fwrite(bytes.data(), 1, bytes.size(), stream);
        fflush(stream);
    }

    void PrintLine(const std::wstring &text = L"")
    {
        WriteLineTo(GetStdHandle(STD_OUTPUT_HANDLE), text);
    }

    void PrintErrorLine(const std::wstring &text)
    {
        WriteLineTo(GetStdHandle(STD_ERROR_HANDLE), text);
    }

    // 拼接路径
    std::wstring JoinPath(const std::wstring &left, const wchar_t *right)
    {
        std::wstring result = left;
        if (!result.empty() && result.back() != L'\\' && result.back() != L'/')
            result += L'\\';
        result += right;
        return result;
    }

    // 路径所在的目录, 末尾不带分隔符
    std::wstring DirectoryOf(const std::wstring &path)
    {
        size_t pos = path.find_last_of(L"\\/:");
        if (pos == std::wstring::npos)
            return L".";
        if (pos == 0)
            return path.substr(0, 1);
        return path.substr(0, pos);
    }

    bool FileExists(const std::wstring &path)
    {
        DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES &&
               (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    bool DirectoryExists(const std::wstring &path)
    {
        DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES &&
               (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    // 建目录, 已存在就当成功
    bool MakeDirectory(const std::wstring &path)
    {
        if (DirectoryExists(path))
            return true;

        std::wstring parent = DirectoryOf(path);
        if (!parent.empty() && parent != path && !DirectoryExists(parent))
        {
            if (!MakeDirectory(parent))
                return false;
        }
        return CreateDirectoryW(path.c_str(), nullptr) != 0 ||
               GetLastError() == ERROR_ALREADY_EXISTS;
    }

    // 删一个目录下的所有东西, 目录本身保留
    void RemoveDirectoryContent(const std::wstring &path)
    {
        std::wstring pattern = JoinPath(path, L"*");
        WIN32_FIND_DATAW findData = {};
        HANDLE find = FindFirstFileW(pattern.c_str(), &findData);
        if (find == INVALID_HANDLE_VALUE)
            return;

        do
        {
            if (wcscmp(findData.cFileName, L".") == 0 || wcscmp(findData.cFileName, L"..") == 0)
                continue;

            std::wstring item = JoinPath(path, findData.cFileName);
            if (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                RemoveDirectoryContent(item);
                RemoveDirectoryW(item.c_str());
            }
            else
            {
                SetFileAttributesW(item.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileW(item.c_str());
            }
        } while (FindNextFileW(find, &findData));

        FindClose(find);
    }

    // 跑一条命令, 返回退出码
    int RunCommand(const std::wstring &commandLine, const std::wstring &workingDir)
    {
        std::wstring full = L"cmd.exe /c \"" + commandLine + L"\"";

        STARTUPINFOW startup = {};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process = {};

        std::vector<wchar_t> mutableCommand(full.begin(), full.end());
        mutableCommand.push_back(L'\0');

        if (!CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE,
                            CREATE_NO_WINDOW, nullptr,
                            workingDir.empty() ? nullptr : workingDir.c_str(),
                            &startup, &process))
        {
            return -1;
        }

        WaitForSingleObject(process.hProcess, INFINITE);
        DWORD exitCode = 0;
        GetExitCodeProcess(process.hProcess, &exitCode);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return static_cast<int>(exitCode);
    }

    // 找 vcvars64.bat, 按常见的安装位置挨个试
    std::wstring FindVcvars(const std::wstring &userGiven)
    {
        if (!userGiven.empty())
        {
            // 用户指定了路径, 文件不在就直接说清楚
            if (!FileExists(userGiven))
            {
                PrintErrorLine(L"指定的 vcvars64.bat 不存在: " + userGiven);
                return L"";
            }
            return userGiven;
        }

        static const wchar_t *kCandidates[] = {
            L"C:\\Program Files (x86)\\Microsoft Visual Studio\\18\\BuildTools\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files\\Microsoft Visual Studio\\18\\BuildTools\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\Community\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\Professional\\VC\\Auxiliary\\Build\\vcvars64.bat",
            L"C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\Enterprise\\VC\\Auxiliary\\Build\\vcvars64.bat",
        };

        for (const wchar_t *candidate : kCandidates)
        {
            if (FileExists(candidate))
                return candidate;
        }
        return L"";
    }

    // 用默认浏览器打开下载页
    void OpenDownloadPage()
    {
        try
        {
            ShellExecuteW(nullptr, L"open",
                          L"https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022",
                          nullptr, nullptr, SW_SHOWNORMAL);
        }
        catch (...)
        {
            // 打不开就算了, 链接已经打印出来了
        }
    }

    // 找不到工具链时, 给出下载步骤并直接拉起浏览器。
    void PrintMissingToolchain()
    {
        PrintErrorLine(L"需要 Visual Studio Build Tools 的 x64 工具链才能编译。");
        PrintErrorLine(L"下载地址:");
        PrintErrorLine(L"  https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022");
        PrintErrorLine(L"装的时候勾选「使用 C++ 的桌面开发」, 右边的工作负载选「MSVC 生成工具」,");
        PrintErrorLine(L"组件里保留「适用于 Windows 的 C++ MSVC 生成工具」与「Windows 11 SDK」。");
        PrintErrorLine(L"装完重开命令行再跑一次就行。");
        PrintErrorLine(L"已经装在别的位置的话, 用 --vcvars 指定 vcvars64.bat。");
        PrintErrorLine(L"正在打开下载页...");
        OpenDownloadPage();
    }

    void PrintUsage()
    {
        PrintLine(L"Auto7zShell 构建工具");
        PrintLine();
        PrintLine(L"  buildtool.exe                  构建外壳扩展");
        PrintLine(L"  buildtool.exe --clean          先清空产物目录再构建");
        PrintLine(L"  buildtool.exe --vcvars <路径>   指定 vcvars64.bat 的位置");
        PrintLine(L"  buildtool.exe --help           显示本说明");
        PrintLine();
        PrintLine(L"产物在 build\\Auto7zShell.dll。");
        PrintLine(L"构建完要把它拷到主程序旁边, 再跑一次 Auto7z.exe -r 注册。");
    }
}

int wmain(int argc, wchar_t **argv)
{
    BuildContext context;

    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    context.projectDir = DirectoryOf(exePath);
    context.srcDir = JoinPath(context.projectDir, L"src");
    context.outDir = JoinPath(context.projectDir, L"build");

    for (int i = 1; i < argc; i++)
    {
        if (wcscmp(argv[i], L"--clean") == 0)
        {
            context.clean = true;
        }
        else if (wcscmp(argv[i], L"--vcvars") == 0 && i + 1 < argc)
        {
            context.vcvars = argv[++i];
        }
        else if (wcscmp(argv[i], L"--help") == 0 || wcscmp(argv[i], L"-h") == 0)
        {
            PrintUsage();
            return 0;
        }
        else
        {
            PrintLine(std::wstring(L"不认识的参数: ") + argv[i]);
            PrintUsage();
            return 1;
        }
    }

    // 工具放在 buildtool 子目录里, 组件根目录在上一层
    if (!DirectoryExists(context.srcDir))
        context.projectDir = DirectoryOf(context.projectDir);
    context.srcDir = JoinPath(context.projectDir, L"src");
    context.outDir = JoinPath(context.projectDir, L"build");

    if (!DirectoryExists(context.srcDir))
    {
        PrintErrorLine(L"找不到源码目录: " + context.srcDir);
        return 1;
    }

    // 工具链准备: 借 vcvars64.bat 把 MSVC 的环境变量设置好。
    std::wstring vcvars = FindVcvars(context.vcvars);
    if (vcvars.empty())
    {
        PrintErrorLine(L"找不到 MSVC 的 x64 工具链");
        PrintMissingToolchain();
        return 1;
    }

    if (context.clean && DirectoryExists(context.outDir))
    {
        PrintLine(L"清空产物目录...");
        RemoveDirectoryContent(context.outDir);
    }

    if (!MakeDirectory(context.outDir))
    {
        PrintErrorLine(L"建不了产物目录: " + context.outDir);
        return 1;
    }

    std::wstring source = JoinPath(context.srcDir, L"ShellExtension.cpp");
    std::wstring resource = JoinPath(context.srcDir, L"ShellResource.rc");
    std::wstring defFile = JoinPath(context.srcDir, L"ShellExtension.def");
    std::wstring object = JoinPath(context.srcDir, L"ShellExtension.obj");
    std::wstring resFile = JoinPath(context.outDir, L"ShellResource.res");
    std::wstring dll = JoinPath(context.outDir, L"Auto7zShell.dll");

    // 三步放在同一个 cmd 会话里做。
    //
    // vcvars64.bat 靠修改当前进程的环境变量生效, 它是个批处理,
    // 在子进程里跑改不到我们这边, 搬回来又容易出岔子。
    // 让同一个 cmd 先跑 vcvars 再跑 cl 与 link, 环境变量自然是对的。
    // vcvars64.bat 与编译放在同一个 cmd 会话里做。
    // vcvars 靠修改当前进程的环境变量生效, 它是个批处理,
    // 在子进程里跑改不到我们这边, 搬回来又容易出岔子。
    std::wstring script =
        L"call \"" + vcvars + L"\" >nul && "
        L"cl /nologo /c /utf-8 /EHsc /O2 /MT /W3 /DUNICODE /D_UNICODE"
        L" /I \"" + context.srcDir + L"\" \"" + source + L"\" && "
        L"rc /nologo /fo \"" + resFile + L"\" \"" + resource + L"\" && "
        L"link /nologo /DLL /OUT:\"" + dll + L"\""
        L" \"" + object + L"\" \"" + resFile + L"\""
        L" /DEF:\"" + defFile + L"\""
        L" ole32.lib shell32.lib advapi32.lib shlwapi.lib user32.lib gdi32.lib";

    PrintLine(L"编译并链接...");
    int result = RunCommand(script, context.srcDir);
    if (result != 0)
    {
        PrintErrorLine(L"构建失败");
        return 1;
    }

    PrintLine();
    PrintLine(L"构建完成: " + dll);
    return 0;
}
