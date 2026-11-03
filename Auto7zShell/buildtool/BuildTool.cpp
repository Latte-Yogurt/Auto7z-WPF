
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>

#include <cstdio>
#include <cstring>
#include <cwctype>
#include <string>
#include <vector>

// 除 wmain 外全部收进匿名命名空间，避免和 CRT 或系统头里的符号撞名
namespace
{
    // 三个目录全从 exe 自身位置推出来，工具在哪儿被调用都不影响结果
    struct BuildContext
    {
        std::wstring projectDir;
        std::wstring srcDir;
        std::wstring outDir;
        std::wstring vcvars;
        bool clean = false;
    };

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

    // 控制台支持宽字符就直接写 Unicode，被重定向到文件或管道时 WriteConsoleW 会失败，再退回按代码页转换的字节流
    void WriteLineTo(HANDLE handle, const std::wstring &text)
    {
        std::wstring line = text + L"\n";

        DWORD written = 0;
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE &&
            WriteConsoleW(handle, line.c_str(), static_cast<DWORD>(line.size()), &written, nullptr))
        {
            return;
        }

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

    std::wstring JoinPath(const std::wstring &left, const wchar_t *right)
    {
        std::wstring result = left;
        if (!result.empty() && result.back() != L'\\' && result.back() != L'/')
            result += L'\\';
        result += right;
        return result;
    }

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
                // 产物可能带只读属性，不先摘掉 DeleteFileW 会失败，--clean 就删不干净
                SetFileAttributesW(item.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileW(item.c_str());
            }
        } while (FindNextFileW(find, &findData));

        FindClose(find);
    }

    // vcvars64.bat 设的环境变量只活在自己的 cmd 会话里，所以编译、资源、链接三步必须拼进同一次调用，分开执行后两步就没有编译环境
    int RunCommand(const std::wstring &commandLine, const std::wstring &workingDir)
    {
        std::wstring full = L"cmd.exe /c \"" + commandLine + L"\"";

        STARTUPINFOW startup = {};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process = {};

        std::vector<wchar_t> mutableCommand(full.begin(), full.end());
        mutableCommand.push_back(L'\0');

        // CREATE_NO_WINDOW 是为了不在每次构建时闪一个黑窗口
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
        // 配 /c 时 cmd 会把最后一条命令的退出码原样带出来，链接失败自然就是非零
        return static_cast<int>(exitCode);
    }

    // 用户显式指定就不再猜，只核对文件在不在
    std::wstring FindVcvars(const std::wstring &userGiven)
    {
        if (!userGiven.empty())
        {
            // 指定了却不存在的路径直接报错，不悄悄退回自动探测，否则编出来的东西和预期不符还查不到原因
            if (!FileExists(userGiven))
            {
                PrintErrorLine(L"指定的 vcvars64.bat 不存在: " + userGiven);
                return L"";
            }
            return userGiven;
        }

        // 只按安装器的默认位置猜，装在自定义目录的用户请用 --vcvars 指定
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
            // 候选按顺序取第一个存在的，不比对版本号，装了多套时以列表靠前的为准
            if (FileExists(candidate))
                return candidate;
        }
        return L"";
    }

    // 找不到工具链时顺手开下载页，报错文字写得再细，多数人也不会自己去翻下载地址
    void OpenDownloadPage()
    {
        ShellExecuteW(nullptr, L"open",
                      L"https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022",
                      nullptr, nullptr, SW_SHOWNORMAL);
    }

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

// 走 wmain 而不是 main，路径参数本来就是宽字符，省掉一次从 ANSI 代码页的转换
int wmain(int argc, wchar_t **argv)
{
    BuildContext context;

    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    // 以 exe 所在目录为基准，不依赖调用方的当前目录
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

    // exe 可能被单独拷进 build 子目录里运行，找不到 src 就往上退一层再试
    if (!DirectoryExists(context.srcDir))
        context.projectDir = DirectoryOf(context.projectDir);
    context.srcDir = JoinPath(context.projectDir, L"src");
    context.outDir = JoinPath(context.projectDir, L"build");

    if (!DirectoryExists(context.srcDir))
    {
        PrintErrorLine(L"找不到源码目录: " + context.srcDir);
        return 1;
    }

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

    // /utf-8 让 cl 按 UTF-8 读源码，中文字符串才不会被代码页截断；/MT 静态链接 CRT，DLL 拷到别的机器上不用再带运行库
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
    // 工作目录设在 src，cl 生成的 .obj 就落在源码旁边，和手工编译的结果一致
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
