// ShellExtension.cpp
// Auto7z 的资源管理器右键菜单扩展。
//
// 选中一个或多个文件/文件夹后, 右键菜单里会出现
// "用 Auto7z 压缩" 与 "用 Auto7z 解压" 两项,
// 文字跟着主程序的界面语言走。
// 点击后把选中的所有路径一次性交给 Auto7z.exe 处理。
//
// 挂在 shellex\ContextMenuHandlers 下, 资源管理器会把整个选区
// 一次性交过来, 由本组件收集路径后启动单个 Auto7z 进程。
// 换成 shell\command 那种注册方式, 资源管理器会对每个文件各起一个进程。

#include "ShellProtocol.h"

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <ole2.h>
#include <olectl.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

namespace {

// 本组件的 CLSID, 注册表里用它关联 dll 路径
const CLSID kClassId = {
    0x6C3F8A21,
    0x4B7D,
    0x4E5A,
    {0x9A, 0x2C, 0x3D, 0x71, 0x0E, 0x5B, 0x88, 0x41}};

const wchar_t kClassName[] = L"Auto7z Shell Extension";

// 菜单项的动词。GetCommandString 返回它, 资源管理器点击时会把动词传回来。
const wchar_t kVerbCompress[] = L"Auto7z.Compress";
const wchar_t kVerbExtract[] = L"Auto7z.Extract";

// 菜单项文字的资源 id。三种语言各一套, 100 的倍数区分。
const int kStringCompressZhCN = 101;
const int kStringExtractZhCN = 102;
const int kStringCompressZhTW = 201;
const int kStringExtractZhTW = 202;
const int kStringCompressEnUS = 301;
const int kStringExtractEnUS = 302;

// 没取到语言时用哪一套
const int kStringCompressDefault = kStringCompressEnUS;
const int kStringExtractDefault = kStringExtractEnUS;

// 菜单项在命令表里的序号
const UINT kCmdCompress = 0;
const UINT kCmdExtract = 1;

// 主程序文件名, 与本 dll 放在同一个目录
const wchar_t kMainExeName[] = L"Auto7z.exe";

// 注册表里菜单项的键名
const wchar_t kHandlerKeyName[] = L"Auto7z";

HMODULE g_module = nullptr;
LONG g_refCount = 0;

inline void AddRefCount() { InterlockedIncrement(&g_refCount); }
inline void ReleaseCount() { InterlockedDecrement(&g_refCount); }

// ---------- 路径与注册表键 ----------

// 把 CLSID 写成 {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} 形式
void FormatClassId(wchar_t *buffer, size_t bufferChars)
{
    _snwprintf_s(buffer, bufferChars, _TRUNCATE,
                 L"{%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
                 kClassId.Data1, kClassId.Data2, kClassId.Data3,
                 kClassId.Data4[0], kClassId.Data4[1], kClassId.Data4[2], kClassId.Data4[3],
                 kClassId.Data4[4], kClassId.Data4[5], kClassId.Data4[6], kClassId.Data4[7]);
}

// 正式注册写在 HKEY_CLASSES_ROOT 下, 也就是 HKLM\Software\Classes。
// COM 查 CLSID 只认这个位置, 写 HKEY_CURRENT_USER\Software\Classes 是没用的。
// 所以注册要管理员权限, 主程序会用 UAC 提权跑一次。
// 用户级那份只作为记录, Explorer 不会用。
const HKEY kClassesRoot = HKEY_CLASSES_ROOT;

// 菜单挂载点要写 HKEY_LOCAL_MACHINE\Software\Classes 这一份。
// 往 HKEY_CLASSES_ROOT 的 "*" 底下写会被系统拒掉(错误 5):
// 那个键只认 HKLM 那一份, 合并视图不给写。
const HKEY kMachineClasses = HKEY_LOCAL_MACHINE;

// 组件在注册表里的主程序路径键
std::wstring GetMainExeValueKey()
{
    wchar_t clsid[64];
    FormatClassId(clsid, 64);
    return std::wstring(L"CLSID\\") + clsid + L"\\Auto7z";
}

// 取 dll 所在目录
bool GetModuleDirectory(wchar_t *buffer, size_t bufferChars)
{
    DWORD length = GetModuleFileNameW(g_module, buffer, static_cast<DWORD>(bufferChars));
    if (length == 0 || length >= bufferChars) return false;

    wchar_t *separator = wcsrchr(buffer, L'\\');
    if (separator == nullptr) return false;
    *separator = L'\0';
    return true;
}

// 从注册表里读主程序路径
//
// 主程序每次启动都会把自己的位置写进来,
// 这样即使 exe 被挪走, 右键菜单仍然能找到它。
bool ReadRegisteredExePath(std::wstring &path)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(kClassesRoot, GetMainExeValueKey().c_str(), 0, KEY_READ, &key) != ERROR_SUCCESS)
        return false;

    wchar_t buffer[MAX_PATH] = {};
    DWORD type = 0;
    DWORD size = sizeof(buffer);
    LONG result = RegQueryValueExW(key, L"MainExePath", nullptr, &type,
                                   reinterpret_cast<BYTE *>(buffer), &size);
    RegCloseKey(key);

    if (result != ERROR_SUCCESS || type != REG_SZ) return false;
    if (buffer[0] == L'\0') return false;

    path.assign(buffer);
    return true;
}

// 主程序把语言写在这个键下
const wchar_t kLanguageValueKey[] = L"Software\\Auto7z";

// 读 HKCU 里主程序写的语言
std::wstring ReadLanguageFromRegistry()
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kLanguageValueKey, 0, KEY_READ, &key) != ERROR_SUCCESS)
        return L"";

    wchar_t buffer[32] = {};
    DWORD type = 0;
    DWORD size = sizeof(buffer);
    LONG result = RegQueryValueExW(key, L"Language", nullptr, &type,
                                   reinterpret_cast<BYTE *>(buffer), &size);
    RegCloseKey(key);

    if (result != ERROR_SUCCESS || type != REG_SZ) return L"";
    return std::wstring(buffer);
}

// ---------- 语言 ----------

// 从一个 xml 文件里取 Language 元素的值
std::wstring ReadLanguageFromConfig(const std::wstring &path);

// 从主程序的配置文件里读当前用的语言
//
// 语言存在 Auto7z.xml 的 Language 键里。配置的位置有两种:
//   普通模式 %LOCALAPPDATA%\Auto7z\Auto7z.xml
//   便携模式 程序目录\.config\Auto7z\Auto7z.xml
// 两种都试一下, 读到就用。
//
// 不走注册表: 那是管理员才能写的, 主程序平时没那个权限。
std::wstring ReadConfiguredLanguage()
{
    // 主程序把语言写在 HKCU 里, 那是它最准的来源
    std::wstring fromRegistry = ReadLanguageFromRegistry();
    if (!fromRegistry.empty()) return fromRegistry;

    // 没有的话再从配置文件里找
    std::vector<std::wstring> candidates;

    wchar_t localAppData[MAX_PATH] = {};
    if (SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData) == S_OK)
    {
        std::wstring localConfig = std::wstring(localAppData) + L"\\Auto7z\\Auto7z.xml";
        candidates.push_back(localConfig);
    }

    wchar_t directory[MAX_PATH] = {};
    if (GetModuleDirectory(directory, MAX_PATH))
    {
        std::wstring portableConfig = std::wstring(directory) + L"\\.config\\Auto7z\\Auto7z.xml";
        candidates.push_back(portableConfig);
    }

    for (const std::wstring &path : candidates)
    {
        std::wstring language = ReadLanguageFromConfig(path);
        if (!language.empty()) return language;
    }

    return L"";
}

// 只按文本找 "<Language>" 与 "</Language>", 不引入 xml 解析库。
std::wstring ReadLanguageFromConfig(const std::wstring &path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return L"";

    DWORD size = GetFileSize(file, nullptr);
    if (size == INVALID_FILE_SIZE || size == 0 || size > 1024 * 1024)
    {
        CloseHandle(file);
        return L"";
    }

    std::vector<char> buffer(static_cast<size_t>(size) + 1, 0);
    DWORD read = 0;
    if (!ReadFile(file, buffer.data(), size, &read, nullptr))
    {
        CloseHandle(file);
        return L"";
    }
    CloseHandle(file);
    buffer[read] = 0;

    // 先按 utf-8 找一遍, 找不到再按 utf-16 找
    std::string text(buffer.data(), read);
    size_t begin = text.find("<Language>");
    if (begin != std::string::npos)
    {
        size_t end = text.find("</Language>", begin);
        if (end != std::string::npos)
        {
            std::string value = text.substr(begin + 10, end - begin - 10);
            int count = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
            if (count > 1)
            {
                std::wstring wide(static_cast<size_t>(count), L'\0');
                MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, &wide[0], count);
                wide.resize(static_cast<size_t>(count) - 1);
                if (!wide.empty()) return wide;
            }
        }
    }

    // utf-16 的配置文件
    const wchar_t *openTag = L"<Language>";
    const wchar_t *closeTag = L"</Language>";
    std::wstring content;
    if (read >= 2 && (static_cast<unsigned char>(buffer[0]) == 0xFF) &&
        (static_cast<unsigned char>(buffer[1]) == 0xFE))
    {
        content.assign(reinterpret_cast<const wchar_t *>(buffer.data() + 2), (read - 2) / 2);
    }
    else if (read >= 2 && (static_cast<unsigned char>(buffer[0]) == 0xFE) &&
             (static_cast<unsigned char>(buffer[1]) == 0xFF))
    {
        int count = static_cast<int>((read - 2) / 2);
        content.assign(static_cast<size_t>(count), L'\0');
        for (int i = 0; i < count; i++)
        {
            content[i] = static_cast<wchar_t>(
                (static_cast<unsigned char>(buffer[2 + i * 2]) << 8) |
                static_cast<unsigned char>(buffer[3 + i * 2]));
        }
    }

    if (!content.empty())
    {
        size_t begin = content.find(openTag);
        if (begin != std::wstring::npos)
        {
            size_t end = content.find(closeTag, begin);
            if (end != std::wstring::npos)
            {
                std::wstring value = content.substr(begin + wcslen(openTag), end - begin - wcslen(openTag));
                // 去掉首尾空白
                size_t first = value.find_first_not_of(L" \t\r\n");
                size_t last = value.find_last_not_of(L" \t\r\n");
                if (first != std::wstring::npos && last != std::wstring::npos)
                    return value.substr(first, last - first + 1);
            }
        }
    }

    return L"";
}

// 取主程序 Auto7z.exe 的完整路径
//
// 优先用注册表里登记的位置, 没有就用 dll 同目录下的。
bool GetMainExePath(wchar_t *buffer, size_t bufferChars)
{
    std::wstring registered;
    if (ReadRegisteredExePath(registered))
    {
        if (GetFileAttributesW(registered.c_str()) != INVALID_FILE_ATTRIBUTES)
            return wcscpy_s(buffer, bufferChars, registered.c_str()) == 0;
    }

    wchar_t directory[MAX_PATH];
    if (!GetModuleDirectory(directory, MAX_PATH)) return false;

    wchar_t path[MAX_PATH];
    if (!PathCombineW(path, directory, kMainExeName)) return false;
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return false;

    return wcscpy_s(buffer, bufferChars, path) == 0;
}

// ---------- 图标 ----------

// 从主程序 exe 里取图标。
//
// 主程序的位置记在注册表 CLSID\{...}\Auto7z\MainExePath 下,
// 从那儿取图标, 免得跟主程序对不上。
// 取不到就返回空, 菜单项还是没有图标, 但不影响用。
HICON LoadMainIcon()
{
    // 已经取过就直接复用
    static HICON cached = nullptr;
    static bool tried = false;
    if (tried) return cached;
    tried = true;

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    wchar_t valuePath[128];
    _snwprintf_s(valuePath, 128, _TRUNCATE, L"CLSID\\%s\\Auto7z", clsid);

    HKEY key = nullptr;
    if (RegOpenKeyExW(kClassesRoot, valuePath, 0, KEY_READ, &key) != ERROR_SUCCESS)
        return nullptr;

    wchar_t exePath[MAX_PATH] = {};
    DWORD size = sizeof(exePath);
    DWORD type = 0;
    LONG result = RegQueryValueExW(key, L"MainExePath", nullptr, &type,
                                   reinterpret_cast<LPBYTE>(exePath), &size);
    RegCloseKey(key);
    if (result != ERROR_SUCCESS || type != REG_SZ || exePath[0] == 0)
        return nullptr;

    // 菜单就用 16 像素那个小图标。
    // 大图标在这个 exe 上取不到(句柄是 0), 小图标才拿得着。
    HICON smallIcon = nullptr;
    if (ExtractIconExW(exePath, 0, &smallIcon, nullptr, 1) > 0 && smallIcon != nullptr)
    {
        cached = smallIcon;
        return cached;
    }

    return nullptr;
}

// 把图标画成位图, 菜单项要的是位图不是图标
HBITMAP IconToBitmap(HICON icon, int size)
{
    if (icon == nullptr) return nullptr;

    HDC screen = GetDC(nullptr);
    if (screen == nullptr) return nullptr;

    HDC memory = CreateCompatibleDC(screen);
    HBITMAP bitmap = nullptr;
    HGDIOBJ oldBitmap = nullptr;

    if (memory != nullptr)
    {
        bitmap = CreateCompatibleBitmap(screen, size, size);
        if (bitmap != nullptr)
        {
            oldBitmap = SelectObject(memory, bitmap);

            // 菜单项图标是叠在底色上的, 先铺个白底再画
            RECT rect = {0, 0, size, size};
            FillRect(memory, &rect, (HBRUSH)GetStockObject(WHITE_BRUSH));

            DrawIconEx(memory, 0, 0, icon, size, size, 0, nullptr, DI_NORMAL);

            SelectObject(memory, oldBitmap);
        }
        DeleteDC(memory);
    }

    ReleaseDC(nullptr, screen);
    return bitmap;
}

// ---------- 选区与路径 ----------

// 判断路径是不是磁盘上真实存在的文件或目录。
//
// 回收站里的项目路径长这样: C:\$Recycle.Bin\S-1-5-21-...\$RXXXX.ext
// 它们是回收站自己的内部文件, 压缩没有意义。
// 桌面背景、此电脑这些地方拿到的则是虚拟路径, 磁盘上并不存在。
// 这两种都要挡掉, 不然在那些地方右键也会冒出菜单项。
bool IsRealFileOrFolder(const wchar_t *path)
{
    if (path == nullptr || path[0] == 0) return false;

    // 回收站的内部目录直接排除
    if (wcsstr(path, L"\\$Recycle.Bin\\") != nullptr) return false;

    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) return false;

    // 设备、管道这类不是普通文件
    if (attributes & FILE_ATTRIBUTE_DEVICE) return false;

    // 盘符根目录(C:\ 、 D:\)也要排除。
    // 它在磁盘上确实存在, 但压缩整个盘没有意义,
    // 在"此电脑"里右键磁盘时不该冒出菜单项。
    if (attributes & FILE_ATTRIBUTE_DIRECTORY)
    {
        wchar_t volumeRoot[MAX_PATH];
        if (GetVolumePathNameW(path, volumeRoot, MAX_PATH) != 0)
        {
            size_t length = wcslen(volumeRoot);
            while (length > 0 && (volumeRoot[length - 1] == L'\\' || volumeRoot[length - 1] == L'/'))
                volumeRoot[--length] = 0;

            size_t pathLength = wcslen(path);
            while (pathLength > 0 && (path[pathLength - 1] == L'\\' || path[pathLength - 1] == L'/'))
                pathLength--;

            if (length == pathLength && wcsncmp(volumeRoot, path, length) == 0)
                return false;
        }
        return true;
    }

    return true;
}

// 从 IDataObject 里取出被选中的全部路径
//
// 优先用 CF_HDROP, 那是资源管理器在选区是普通文件/文件夹时给的标准格式;
// 取不到再退回 CFSTR_SHELLIDLIST。
bool ReadSelectedPaths(IDataObject *dataObject, std::vector<std::wstring> &paths)
{
    if (dataObject == nullptr) return false;

    FORMATETC format = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM medium = {};
    if (SUCCEEDED(dataObject->GetData(&format, &medium)))
    {
        HDROP drop = static_cast<HDROP>(GlobalLock(medium.hGlobal));
        if (drop != nullptr)
        {
            UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
            for (UINT i = 0; i < count; i++)
            {
                UINT length = DragQueryFileW(drop, i, nullptr, 0);
                if (length == 0) continue;

                std::wstring buffer(length + 1, L'\0');
                if (DragQueryFileW(drop, i, &buffer[0], length + 1) == 0) continue;

                buffer.resize(wcslen(buffer.c_str()));
                paths.push_back(buffer);
            }
            GlobalUnlock(medium.hGlobal);
        }
        ReleaseStgMedium(&medium);
        if (!paths.empty()) return true;
    }

    // CF_HDROP 拿不到时, 尝试 shell idlist
    FORMATETC idListFormat = {static_cast<CLIPFORMAT>(reinterpret_cast<ULONG_PTR>(CFSTR_SHELLIDLIST)),
                               nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    if (SUCCEEDED(dataObject->GetData(&idListFormat, &medium)))
    {
        PIDLIST_ABSOLUTE idList = static_cast<PIDLIST_ABSOLUTE>(GlobalLock(medium.hGlobal));
        if (idList != nullptr)
        {
            // idlist 的头两项是固定的, 真正的文件项从第三项开始。
            // 逐项按 ITEMIDLIST 的长度往后走, 不用 ILGetNext,
            // 那个函数在不同 SDK 里的返回类型不一样。
            const BYTE *base = reinterpret_cast<const BYTE *>(idList);
            UINT offset = sizeof(ITEMIDLIST) + 2;  // 头两项: 桌面 + 根目录
            UINT totalSize = ILGetSize(idList);

            while (offset + sizeof(ITEMIDLIST) <= totalSize)
            {
                const ITEMIDLIST *item = reinterpret_cast<const ITEMIDLIST *>(base + offset);
                if (item->mkid.cb == 0) break;

                wchar_t path[MAX_PATH];
                if (SHGetPathFromIDListW(
                        reinterpret_cast<PCIDLIST_ABSOLUTE>(const_cast<BYTE *>(base + offset)),
                        path))
                {
                    paths.push_back(path);
                }

                offset += item->mkid.cb;
            }

            GlobalUnlock(medium.hGlobal);
        }
        ReleaseStgMedium(&medium);
    }

    return !paths.empty();
}

// ---------- 共享内存 ----------

// 造一个随机名字, 避免共享内存重名
void MakeRandomName(wchar_t *buffer, size_t bufferChars, const wchar_t *prefix)
{
    static LONG counter = 0;
    LONG serial = InterlockedIncrement(&counter);

    wchar_t guid[64] = {};
    if (FAILED(StringFromGUID2(IID_NULL, guid, 0))) wcscpy_s(guid, 64, L"00000000");

    _snwprintf_s(buffer, bufferChars, _TRUNCATE, L"%s%s%08X%08X",
                 prefix, guid, GetCurrentProcessId(), serial);
}

// 把路径列表写进共享内存, 并给出映射名与事件名
//
// 映射句柄不能在写完就关掉: 命名映射对象靠句柄存活,
// 最后一个句柄一关, 对象就没了, 子进程再去打开会失败。
// 所以这里把句柄交给调用方一直握着, 等子进程读完了再关。
bool CreatePathMap(const std::vector<std::wstring> &paths,
                   std::wstring &mapName, std::wstring &eventName, size_t &totalBytes,
                   HANDLE &mappingHandle, HANDLE &eventHandle)
{
    size_t totalChars = 1;  // 开头的标记字符
    for (const std::wstring &path : paths)
        totalChars += path.size() + 1;

    totalBytes = totalChars * sizeof(wchar_t);

    wchar_t rawMapName[128];
    wchar_t rawEventName[128];
    MakeRandomName(rawMapName, 128, SHELL_MAP_NAME_PREFIX);
    MakeRandomName(rawEventName, 128, SHELL_EVENT_NAME_PREFIX);

    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                        static_cast<DWORD>(totalBytes >> 32),
                                        static_cast<DWORD>(totalBytes & 0xFFFFFFFF),
                                        rawMapName);
    if (mapping == nullptr) return false;

    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, rawEventName);
    if (event == nullptr)
    {
        CloseHandle(mapping);
        return false;
    }

    void *view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, totalBytes);
    if (view == nullptr)
    {
        CloseHandle(event);
        CloseHandle(mapping);
        return false;
    }

    wchar_t *cursor = static_cast<wchar_t *>(view);
    *cursor++ = SHELL_MAP_MARKER;
    for (const std::wstring &path : paths)
    {
        size_t chars = path.size() + 1;
        memcpy(cursor, path.c_str(), chars * sizeof(wchar_t));
        cursor += chars;
    }
    UnmapViewOfFile(view);

    mapName.assign(rawMapName);
    eventName.assign(rawEventName);

    // 句柄交给调用方保管, 子进程读完之前不能关
    mappingHandle = mapping;
    eventHandle = event;
    return true;
}

// 启动 Auto7z, 把路径通过共享内存传过去
bool LaunchAuto7z(const std::vector<std::wstring> &paths, const wchar_t *modeSwitch)
{
    if (paths.empty()) return false;

    wchar_t exePath[MAX_PATH];
    if (!GetMainExePath(exePath, MAX_PATH)) return false;

    std::wstring mapName;
    std::wstring eventName;
    size_t totalBytes = 0;
    HANDLE mappingHandle = nullptr;
    HANDLE eventHandle = nullptr;
    if (!CreatePathMap(paths, mapName, eventName, totalBytes, mappingHandle, eventHandle))
        return false;

    // 命令行: #映射名:字节数:事件名
    wchar_t sizeText[32];
    _snwprintf_s(sizeText, 32, _TRUNCATE, L"%llu", static_cast<unsigned long long>(totalBytes));

    std::wstring commandLine;
    commandLine += L'"';
    commandLine += exePath;
    commandLine += L'"';
    commandLine += L" #";
    commandLine += mapName;
    commandLine += L':';
    commandLine += sizeText;
    commandLine += L':';
    commandLine += eventName;
    if (modeSwitch != nullptr && *modeSwitch != L'\0')
    {
        commandLine += L' ';
        commandLine += modeSwitch;
    }

    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_SHOWNORMAL;

    PROCESS_INFORMATION process = {};
    BOOL created = CreateProcessW(exePath, &mutableCommand[0], nullptr, nullptr, FALSE,
                                  0, nullptr, nullptr, &startup, &process);
    if (!created)
    {
        CloseHandle(eventHandle);
        CloseHandle(mappingHandle);
        return FALSE;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);

    // 等子进程把路径取走。它读完会发信号, 收到后映射才能释放。
    // 最多等 30 秒, 超时就直接放掉, 免得一直占着。
    if (WaitForSingleObject(eventHandle, 30000) == WAIT_TIMEOUT)
    {
        // 子进程没来取, 映射留着也没用
    }

    CloseHandle(eventHandle);
    CloseHandle(mappingHandle);
    return TRUE;
}

// ---------- COM 对象 ----------

// 右键菜单处理对象
class CShellContextMenu : public IContextMenu, public IShellExtInit
{
public:
    CShellContextMenu()
    {
        AddRefCount();
    }

    virtual ~CShellContextMenu()
    {
        ReleaseCount();
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        // 各接口在对象里的位置不一样, 要分别转成对应那个接口的指针。
        // 都返回 IContextMenu 的地址的话, 资源管理器拿 IShellExtInit 去用就会调错函数。
        if (IsEqualIID(interfaceId, IID_IUnknown))
        {
            *object = static_cast<IContextMenu *>(this);
            AddRefCount();
            return S_OK;
        }
        if (IsEqualIID(interfaceId, IID_IShellExtInit))
        {
            *object = static_cast<IShellExtInit *>(this);
            AddRefCount();
            return S_OK;
        }
        if (IsEqualIID(interfaceId, IID_IContextMenu))
        {
            *object = static_cast<IContextMenu *>(this);
            AddRefCount();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        AddRefCount();
        return static_cast<ULONG>(g_refCount);
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        ReleaseCount();
        return static_cast<ULONG>(g_refCount);
    }

    // IShellExtInit
    STDMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, LPDATAOBJECT dataObject, HKEY hkeyProgID) override
    {
        (void)pidlFolder;
        (void)hkeyProgID;

        m_paths.clear();
        if (dataObject != nullptr)
            ReadSelectedPaths(dataObject, m_paths);

        // 一直返回 S_OK。资源管理器拿到失败会静默禁用整个扩展,
        // 右键菜单就不出现了。路径取不到就当没有, 到 InvokeCommand 再说。
        return S_OK;
    }

    // IContextMenu
    STDMETHODIMP QueryContextMenu(HMENU menu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT flags) override
    {
        if (menu == nullptr) return E_INVALIDARG;

        // 返回值要说明加了几个菜单项, 资源管理器按这个数决定菜单项是否有效。
        // 返回 0 就等于告诉它没有菜单项。
        if (idCmdFirst > idCmdLast) return E_INVALIDARG;

        // 路径必须是磁盘上真实存在的文件或目录。
        // 回收站、桌面背景、此电脑这些地方拿到的路径都不是真文件, 要挡掉。
        std::vector<std::wstring> usable;
        for (const std::wstring &path : m_paths)
        {
            if (IsRealFileOrFolder(path.c_str())) usable.push_back(path);
        }
        if (usable.empty()) return static_cast<HRESULT>(0);
        m_paths.swap(usable);

        // 这些标志表示这次调用不是来取完整菜单的, 加了也不会被显示。
        if ((flags & 0x000F) != CMF_NORMAL
            && (flags & CMF_VERBSONLY) == 0
            && (flags & CMF_EXPLORE) == 0)
            return static_cast<HRESULT>(0);

        wchar_t compress[64] = {};
        wchar_t extract[64] = {};
        int compressId = kStringCompressDefault;
        int extractId = kStringExtractDefault;

        std::wstring language = ReadConfiguredLanguage();
        if (language == L"zh-CN")
        {
            compressId = kStringCompressZhCN;
            extractId = kStringExtractZhCN;
        }
        else if (language == L"zh-TW")
        {
            compressId = kStringCompressZhTW;
            extractId = kStringExtractZhTW;
        }

        LoadStringW(g_module, compressId, compress, 64);
        LoadStringW(g_module, extractId, extract, 64);

        UINT used = 0;

        MENUITEMINFOW item = {};
        item.cbSize = sizeof(item);
        item.fMask = MIIM_STRING | MIIM_ID;
        item.hSubMenu = nullptr;
        // 用主程序的图标
        HICON icon = LoadMainIcon();
        if (icon != nullptr)
        {
            item.fMask |= MIIM_BITMAP;
            item.hbmpItem = IconToBitmap(icon, 16);
        }

        // 两项都要用同一个位置插入, 但第二项要往下挪一格,
        // 否则它会插到第一项前面去, 顺序就反了。
        item.wID = idCmdFirst + kCmdCompress;
        item.dwTypeData = compress;
        if (InsertMenuItemW(menu, indexMenu, TRUE, &item)) used++;

        item.wID = idCmdFirst + kCmdExtract;
        item.dwTypeData = extract;
        if (InsertMenuItemW(menu, indexMenu + 1, TRUE, &item)) used++;

        // 位图不能在这里删。MIIM_BITMAP 不会复制它, 菜单项一直引用着那个句柄,
        // 删掉之后菜单里就变成空白图标了。留着让系统回收。

        // 返回加进去的菜单项个数
        return static_cast<HRESULT>(used);
    }

    STDMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO info) override
    {
        if (info == nullptr) return E_INVALIDARG;
        if (m_paths.empty()) return E_FAIL;

        UINT slot = kCmdCompress;
        if (FindCommandSlot(info, &slot) != S_OK) return E_FAIL;

        const wchar_t *modeSwitch = (slot == kCmdExtract) ? L"-U" : L"-Z";
        LaunchAuto7z(m_paths, modeSwitch);
        return S_OK;
    }

    STDMETHODIMP GetCommandString(UINT_PTR id, UINT type, UINT *reserved, LPSTR buffer, UINT bufferChars) override
    {
        (void)reserved;

        if (type == GCS_VERB)
        {
            const wchar_t *verb = (id & 0x3) == kCmdExtract ? kVerbExtract : kVerbCompress;
            int written = WideCharToMultiByte(CP_ACP, 0, verb, -1, buffer,
                                              static_cast<int>(bufferChars), nullptr, nullptr);
            return (written > 0) ? S_OK : E_FAIL;
        }
        return E_NOTIMPL;
    }

private:
    // 从 InvokeCommand 的参数里找出用户点的是哪一项
    //
    // 资源管理器有两种传法:
    //   lpVerb 是整数     —— 直接取低位当命令序号
    //   lpVerb 是动词字符串 —— 拿 GetCommandString 里给的动词来比对
    HRESULT FindCommandSlot(LPCMINVOKECOMMANDINFO info, UINT *slot)
    {
        // 新版资源管理器会传 CMINVOKECOMMANDINFOEX, 里面是宽字符动词
        LPCMINVOKECOMMANDINFOEX infoEx = nullptr;
        if (info->cbSize == sizeof(CMINVOKECOMMANDINFOEX) &&
            (info->fMask & CMIC_MASK_UNICODE) != 0)
        {
            infoEx = reinterpret_cast<LPCMINVOKECOMMANDINFOEX>(info);
            if (!IS_INTRESOURCE(infoEx->lpVerbW))
            {
                if (wcscmp(infoEx->lpVerbW, kVerbExtract) == 0)
                {
                    *slot = kCmdExtract;
                    return S_OK;
                }
                if (wcscmp(infoEx->lpVerbW, kVerbCompress) == 0)
                {
                    *slot = kCmdCompress;
                    return S_OK;
                }
                return E_FAIL;
            }
        }

        // 整数形式: 低位就是命令序号
        if (!IS_INTRESOURCE(info->lpVerb)) return E_FAIL;

        UINT value = LOWORD(reinterpret_cast<ULONG_PTR>(info->lpVerb));
        if (value > kCmdExtract) return E_FAIL;

        *slot = value;
        return S_OK;
    }

    std::vector<std::wstring> m_paths;
};

// COM 类工厂
class CShellClassFactory : public IClassFactory
{
public:
    CShellClassFactory()
    {
        AddRefCount();
    }

    virtual ~CShellClassFactory()
    {
        ReleaseCount();
    }

    STDMETHODIMP QueryInterface(REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        if (IsEqualIID(interfaceId, IID_IUnknown) || IsEqualIID(interfaceId, IID_IClassFactory))
        {
            *object = static_cast<IClassFactory *>(this);
            AddRefCount();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        AddRefCount();
        return static_cast<ULONG>(g_refCount);
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        ReleaseCount();
        return static_cast<ULONG>(g_refCount);
    }

    STDMETHODIMP CreateInstance(IUnknown *outer, REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        if (outer != nullptr) return CLASS_E_NOAGGREGATION;

        CShellContextMenu *instance = new (std::nothrow) CShellContextMenu();
        if (instance == nullptr) return E_OUTOFMEMORY;

        HRESULT result = instance->QueryInterface(interfaceId, object);
        if (FAILED(result)) delete instance;
        return result;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        (void)lock;
        return S_OK;
    }
};

// ---------- 注册表写入 ----------

// 写一个注册表字符串值
bool WriteStringValue(HKEY root, const wchar_t *path, const wchar_t *name, const wchar_t *value)
{
    HKEY key = nullptr;
    LONG result = RegCreateKeyExW(root, path, 0, nullptr, REG_OPTION_NON_VOLATILE,
                                  KEY_WRITE, nullptr, &key, nullptr);
    if (result != ERROR_SUCCESS) return false;

    result = RegSetValueExW(key, name, 0, REG_SZ,
                            reinterpret_cast<const BYTE *>(value),
                            static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return result == ERROR_SUCCESS;
}

// 删一个注册表键(连同子键)
void DeleteKeyTree(HKEY root, const wchar_t *path)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, path, 0, KEY_WRITE | KEY_READ, &key) != ERROR_SUCCESS) return;

    wchar_t subName[256];
    DWORD subNameChars = 0;
    while (RegEnumKeyExW(key, 0, subName, &subNameChars, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS)
        DeleteKeyTree(root, (std::wstring(path) + L"\\" + subName).c_str());

    RegCloseKey(key);
    RegDeleteKeyW(root, path);
}

// 删掉某个子键下的一个值
void DeleteRegValue(HKEY root, const wchar_t *subKeyPath, const wchar_t *valueName)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, subKeyPath, 0, KEY_SET_VALUE, &key) != ERROR_SUCCESS) return;

    RegDeleteValueW(key, valueName);
    RegCloseKey(key);
}

// 把菜单项挂到各个文件类型上, 或者摘掉
void RegisterHandlers(bool set)
{
    // Directory 是目录项, Directory\Background 是在文件夹空白处右键
    static const wchar_t *kTypes[] = {L"*", L"Directory", L"Folder", L"Directory\Background"};

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    for (const wchar_t *type : kTypes)
    {
        std::wstring path = std::wstring(L"Software\\Classes\\")
            + type + L"\\shellex\\ContextMenuHandlers\\" + kHandlerKeyName;
        if (set)
        {
            // 有的挂载点所在键受保护, 写不进去就跳过, 不影响其他几个
            if (!WriteStringValue(kMachineClasses, path.c_str(), nullptr, clsid))
            {
                OutputDebugStringW(L"Auto7z: 菜单挂载点写入失败\r\n");
            }
        }
        else
        {
            DeleteKeyTree(kMachineClasses, path.c_str());
        }
    }
}

}  // namespace

// ---------- dll 导出 ----------

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

// DllGetClassObject / DllCanUnloadNow 在 combaseapi.h 里已有声明,
// 这里只给链接器看导出表, 不要再加 dllexport, 否则会重复定义。
extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID classId, REFIID interfaceId, void **object)
{
    if (object == nullptr) return E_POINTER;
    *object = nullptr;

    if (!IsEqualIID(classId, kClassId)) return CLASS_E_CLASSNOTAVAILABLE;

    CShellClassFactory *factory = new (std::nothrow) CShellClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;

    HRESULT result = factory->QueryInterface(interfaceId, object);
    if (FAILED(result)) delete factory;
    return result;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    return (g_refCount == 0) ? S_OK : S_FALSE;
}

extern "C" HRESULT STDAPICALLTYPE DllRegisterServer()
{
    wchar_t dllPath[MAX_PATH];
    if (GetModuleFileNameW(g_module, dllPath, MAX_PATH) == 0) return SELFREG_E_CLASS;

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    std::wstring classPath = std::wstring(L"CLSID\\") + clsid;
    if (!WriteStringValue(kClassesRoot, classPath.c_str(), nullptr, kClassName))
        return SELFREG_E_CLASS;

    std::wstring inprocPath = classPath + L"\\InprocServer32";
    if (!WriteStringValue(kClassesRoot, inprocPath.c_str(), nullptr, dllPath))
        return SELFREG_E_CLASS;
    WriteStringValue(kClassesRoot, inprocPath.c_str(), L"ThreadingModel", L"Apartment");

    // Win7 以后要在这里登记才不会被拦下。
    // 必须写 HKEY_LOCAL_MACHINE: 资源管理器只认这一份,
    // 写在 HKEY_CURRENT_USER 下会被静默拦掉, 菜单根本不显示。
    WriteStringValue(HKEY_LOCAL_MACHINE,
                     L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
                     clsid, kClassName);

    RegisterHandlers(true);

    // 顺手把主程序路径也写上。
    // dll 与主程序放在同一个目录, 这里能直接算出 exe 的位置。
    // 菜单图标要用这个路径, 少写一步就少一个出错的可能。
    wchar_t exePath[MAX_PATH] = {};
    if (GetMainExePath(exePath, MAX_PATH))
    {
        WriteStringValue(kClassesRoot, GetMainExeValueKey().c_str(), L"MainExePath", exePath);
    }

    return S_OK;
}

extern "C" HRESULT STDAPICALLTYPE DllUnregisterServer()
{
    RegisterHandlers(false);

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    std::wstring classPath = std::wstring(L"CLSID\\") + clsid;
    DeleteKeyTree(kClassesRoot, (classPath + L"\\InprocServer32").c_str());
    DeleteKeyTree(kClassesRoot, classPath.c_str());

    DeleteRegValue(HKEY_CURRENT_USER,
                   L"Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
                   clsid);
    return S_OK;
}

// 让主程序调用: 把主程序路径写进注册表,
// 这样即使 dll 被移动过也能找到 exe。
extern "C" HRESULT STDAPICALLTYPE DllSetMainExePath(LPCWSTR path)
{
    if (path == nullptr || *path == L'\0') return E_INVALIDARG;

    if (!WriteStringValue(kClassesRoot, GetMainExeValueKey().c_str(), L"MainExePath", path))
        return SELFREG_E_CLASS;
    return S_OK;
}

// 记下主程序当前用的语言。
//
// 组件建菜单时按这个语言挑文字。
// 写在 HKEY_CURRENT_USER 下, 用户自己就能写, 不需要管理员权限,
// 跟程序装在哪儿也没有关系, 便携模式切换不影响。
extern "C" HRESULT STDAPICALLTYPE DllSetLanguage(LPCWSTR language)
{
    if (language == nullptr || *language == L'\0') return E_INVALIDARG;

    if (!WriteStringValue(HKEY_CURRENT_USER, kLanguageValueKey, L"Language", language))
        return SELFREG_E_CLASS;

    return S_OK;
}

// 注销主程序路径
extern "C" HRESULT STDAPICALLTYPE DllRemoveMainExePath()
{
    std::wstring valuePath = GetMainExeValueKey();
    DeleteRegValue(kClassesRoot, valuePath.c_str(), L"MainExePath");
    RegDeleteKeyW(kClassesRoot, valuePath.c_str());
    return S_OK;
}
