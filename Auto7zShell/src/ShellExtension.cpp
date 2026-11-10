#include "ShellProtocol.h"

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <ole2.h>
#include <olectl.h>
#include <strsafe.h>
#include <process.h>
#include <new>
#include <string>
#include <vector>

// 全部实现收在匿名命名空间里，对外只留 Dll* 导出符号，免得和主程序或别的扩展重名。
namespace {

// 这个 CLSID 同时写进注册表与主程序的注册流程，改了必须重新注册，否则资源管理器仍按旧号找组件。
const CLSID kClassId = {
    0x6C3F8A21,
    0x4B7D,
    0x4E5A,
    {0x9A, 0x2C, 0x3D, 0x71, 0x0E, 0x5B, 0x88, 0x41}};

const wchar_t kClassName[] = L"Auto7z Shell Extension";

// 谓词名是脚本与 GetCommandString 认的稳定标识，不随界面语言变化。
const wchar_t kVerbCompress[] = L"Auto7z.Compress";
const wchar_t kVerbExtract[] = L"Auto7z.Extract";

// 字符串资源按语言分段：101 段简体、201 段繁体、301 段英文，运行时按配置挑一组。
const int kStringCompressZhCN = 101;
const int kStringExtractZhCN = 102;
const int kStringCompressZhTW = 201;
const int kStringExtractZhTW = 202;
const int kStringCompressEnUS = 301;
const int kStringExtractEnUS = 302;

const int kStringCompressDefault = kStringCompressEnUS;
const int kStringExtractDefault = kStringExtractEnUS;

const UINT kCmdCompress = 0;
const UINT kCmdExtract = 1;

// 子进程迟迟不来读共享内存时的兜底上限，超时即无条件释放，免得句柄跟着资源管理器常驻。
const DWORD kHandoffTimeoutMs = 30000;

// 主程序与图形入口都按同目录查找，外壳扩展只负责把路径拼成绝对路径。
const wchar_t kMainExeName[] = L"Auto7z.exe";

const wchar_t kGuiExeName[] = L"Auto7zGUI.exe";

const wchar_t kHandlerKeyName[] = L"Auto7z";

HMODULE g_module = nullptr;

// 这三个计数由多个线程并发修改，读写一律走 Interlocked，避免读到撕裂的中间值。
LONG g_refCount = 0;

LONG g_lockCount = 0;

// 待交接的子进程数计入 DllCanUnloadNow 的判定，否则资源管理器会在子进程还没取数据时卸载组件。
LONG g_pendingHandoffs = 0;

// 对象引用计数在构造函数与析构函数里配对增减，DllCanUnloadNow 靠它判断是否已无活动对象。
inline void AddObjectRef() { InterlockedIncrement(&g_refCount); }
inline void ReleaseObjectRef() { InterlockedDecrement(&g_refCount); }

inline LONG ReadLong(volatile LONG *value)
{
    return InterlockedCompareExchange(value, 0, 0);
}


// 注册表只认「{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}」这种带花括号的写法，C++ 的 CLSID 常量不能直接写进去。
void FormatClassId(wchar_t *buffer, size_t bufferChars)
{
    _snwprintf_s(buffer, bufferChars, _TRUNCATE,
                 L"{%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
                 kClassId.Data1, kClassId.Data2, kClassId.Data3,
                 kClassId.Data4[0], kClassId.Data4[1], kClassId.Data4[2], kClassId.Data4[3],
                 kClassId.Data4[4], kClassId.Data4[5], kClassId.Data4[6], kClassId.Data4[7]);
}

const HKEY kClassesRoot = HKEY_CLASSES_ROOT;

const HKEY kMachineClasses = HKEY_LOCAL_MACHINE;

// MainExePath 挂在 CLSID 之下而不是类名之下：它是本组件的私有登记项，卸载时随 CLSID 一并删除。
std::wstring GetMainExeValueKey()
{
    wchar_t clsid[64];
    FormatClassId(clsid, 64);
    return std::wstring(L"CLSID\\") + clsid + L"\\Auto7z";
}

// 从组件自身的加载位置推目录，组件被拷到哪里都按同目录找主程序。
bool GetModuleDirectory(wchar_t *buffer, size_t bufferChars)
{
    DWORD length = GetModuleFileNameW(g_module, buffer, static_cast<DWORD>(bufferChars));
    if (length == 0 || length >= bufferChars) return false;

    wchar_t *separator = wcsrchr(buffer, L'\\');
    if (separator == nullptr) return false;
    *separator = L'\0';
    return true;
}

// 优先读注册时登记的主程序路径，这样主程序被改名或挪动后仍能用注册表里的真值。
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

    if (size == 0 || size > sizeof(buffer)) return false;
    buffer[(size / sizeof(wchar_t)) - 1] = L'\0';
    if (buffer[0] == L'\0') return false;

    path.assign(buffer);
    return true;
}

// 语言写在 HKCU 而不是 HKLM：普通用户自己就有写权限，切换语言不必提权。
const wchar_t kLanguageValueKey[] = L"Software\\Auto7z";

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


std::wstring ReadLanguageFromConfig(const std::wstring &path);

// 回退顺序固定：先读 HKCU 的值，再读便携目录与本机目录的配置文件，最后才用英文。
std::wstring ReadConfiguredLanguage()
{
    std::wstring fromRegistry = ReadLanguageFromRegistry();
    if (!fromRegistry.empty()) return fromRegistry;

    std::vector<std::wstring> candidates;

    wchar_t localAppData[MAX_PATH] = {};
    if (SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData) == S_OK)
    {
        std::wstring localConfig = std::wstring(localAppData) + L"\\Auto7z\\Auto7z.json";
        candidates.push_back(localConfig);
    }

    wchar_t directory[MAX_PATH] = {};
    if (GetModuleDirectory(directory, MAX_PATH))
    {
        std::wstring portableConfig = std::wstring(directory) + L"\\.config\\Auto7z\\Auto7z.json";
        candidates.push_back(portableConfig);
    }

    for (const std::wstring &path : candidates)
    {
        std::wstring language = ReadLanguageFromConfig(path);
        if (!language.empty()) return language;
    }

    return L"";
}

// 这里只做一次极简扫描：找 "Language" 键的字符串值，不引入完整 JSON 解析器，组件体积与启动开销都更小。
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

    int length = MultiByteToWideChar(CP_UTF8, 0, buffer.data(), static_cast<int>(read), nullptr, 0);
    if (length <= 0) return L"";

    std::wstring text(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, buffer.data(), static_cast<int>(read), &text[0], length);

    const std::wstring key = L"\"Language\"";
    size_t position = text.find(key);
    if (position == std::wstring::npos) return L"";

    size_t colon = text.find(L':', position + key.size());
    if (colon == std::wstring::npos) return L"";

    size_t valueStart = colon + 1;
    while (valueStart < text.size() && (text[valueStart] == L' ' || text[valueStart] == L'\t' ||
                                        text[valueStart] == L'\r' || text[valueStart] == L'\n'))
    {
        valueStart++;
    }

    if (valueStart >= text.size() || text[valueStart] != L'"') return L"";

    valueStart++;
    std::wstring value;
    for (size_t i = valueStart; i < text.size(); i++)
    {
        wchar_t ch = text[i];

        if (ch == L'"') return value;

        if (ch == L'\\')
        {
            if (i + 1 >= text.size()) return L"";
            wchar_t escape = text[++i];
            switch (escape)
            {
                case L'"': value.push_back(L'"'); break;
                case L'\\': value.push_back(L'\\'); break;
                case L'/': value.push_back(L'/'); break;
                default: return L"";
            }
            continue;
        }

        if (ch == L'\r' || ch == L'\n') return L"";

        value.push_back(ch);
    }

    return L"";
}

// 注册表里的路径要先用 GetFileAttributes 验活，登记项可能是从旧机器搬过来的死路径。
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

bool BuildGuiExePath(const wchar_t *consoleExe, wchar_t *buffer, size_t bufferChars)
{
    const wchar_t *slash = wcsrchr(consoleExe, L'\\');
    if (slash == nullptr) return false;

    size_t directoryChars = static_cast<size_t>(slash - consoleExe) + 1;
    if (directoryChars + (sizeof(kGuiExeName) / sizeof(wchar_t)) > bufferChars) return false;

    wmemcpy(buffer, consoleExe, directoryChars);
    buffer[directoryChars] = L'\0';
    return wcscat_s(buffer, bufferChars, kGuiExeName) == 0;
}


// 菜单图标取 16 像素：资源管理器的小图标槽位就是这个尺寸，大图会被拉伸成毛边。
const int kMenuIconSize = 16;

// 图标只抽取一次并缓存：每次弹出右键菜单都调 ExtractIconEx 会明显拖慢菜单出现。
HICON LoadMainIconOnce()
{
    wchar_t exePath[MAX_PATH] = {};
    if (!GetMainExePath(exePath, MAX_PATH)) return nullptr;

    HICON iconSmall = nullptr;
    if (ExtractIconExW(exePath, 0, nullptr, &iconSmall, 1) > 0 && iconSmall != nullptr)
        return iconSmall;

    HICON iconLarge = nullptr;
    if (ExtractIconExW(exePath, 0, &iconLarge, nullptr, 1) > 0 && iconLarge != nullptr)
        return iconLarge;

    return nullptr;
}

HICON LoadMainIcon()
{
    static const HICON cached = LoadMainIconOnce();
    return cached;
}

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

            RECT rect = {0, 0, size, size};
            FillRect(memory, &rect, (HBRUSH)GetStockObject(WHITE_BRUSH));

            SetStretchBltMode(memory, HALFTONE);
            SetBrushOrgEx(memory, 0, 0, nullptr);
            DrawIconEx(memory, 0, 0, icon, size, size, 0, nullptr, DI_NORMAL);

            SelectObject(memory, oldBitmap);
        }
        DeleteDC(memory);
    }

    ReleaseDC(nullptr, screen);
    return bitmap;
}

HBITMAP LoadMainMenuBitmap()
{
    // 位图转换结果静态缓存，DllCanUnloadNow 不把这张图算进引用，它随模块一起释放。
    static const HBITMAP cached = IconToBitmap(LoadMainIcon(), kMenuIconSize);
    return cached;
}


// 只对真实存在的文件与文件夹出菜单：回收站、设备节点与盘符根目录都不该出现压缩 / 解压项。
bool IsRealFileOrFolder(const wchar_t *path)
{
    if (path == nullptr || path[0] == 0) return false;

    if (StrStrIW(path, L"\\$Recycle.Bin") != nullptr) return false;

    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) return false;

    if (attributes & FILE_ATTRIBUTE_DEVICE) return false;

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

            if (length == pathLength && _wcsnicmp(volumeRoot, path, length) == 0)
                return false;
        }
        return true;
    }

    return true;
}

// 先取 CF_HDROP，取不到再退回 CFSTR_SHELLIDLIST：前者覆盖绝大多数选区，后者兜住虚拟命名空间里的条目。
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

    FORMATETC idListFormat = {static_cast<CLIPFORMAT>(reinterpret_cast<ULONG_PTR>(CFSTR_SHELLIDLIST)),
                               nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    if (SUCCEEDED(dataObject->GetData(&idListFormat, &medium)))
    {
        const CIDA *cida = static_cast<const CIDA *>(GlobalLock(medium.hGlobal));
        if (cida != nullptr)
        {
            if (cida->cidl > 0 && cida->cidl < 0x10000)
            {
                const BYTE *base = reinterpret_cast<const BYTE *>(cida);
                PCIDLIST_ABSOLUTE parent = reinterpret_cast<PCIDLIST_ABSOLUTE>(
                    const_cast<BYTE *>(base + cida->aoffset[0]));

                for (UINT i = 0; i < cida->cidl; i++)
                {
                    PCUIDLIST_RELATIVE child = reinterpret_cast<PCUIDLIST_RELATIVE>(
                        const_cast<BYTE *>(base + cida->aoffset[i + 1]));

                    PIDLIST_ABSOLUTE full = ILCombine(parent, child);
                    if (full == nullptr) continue;

                    wchar_t path[MAX_PATH];
                    if (SHGetPathFromIDListW(full, path)) paths.push_back(path);
                    ILFree(full);
                }
            }

            GlobalUnlock(medium.hGlobal);
        }
        ReleaseStgMedium(&medium);
    }

    return !paths.empty();
}


// 映射名与事件名必须全进程唯一，用 PID 加自增序号拼出来，同一进程连点两次也不会撞名。
void MakeRandomName(wchar_t *buffer, size_t bufferChars, const wchar_t *prefix)
{
    static LONG counter = 0;
    LONG serial = InterlockedIncrement(&counter);

    _snwprintf_s(buffer, bufferChars, _TRUNCATE, L"%s%08X%08X",
                 prefix, GetCurrentProcessId(), serial);
}

// 写进共享内存的是一串以 \0 分隔的 UTF-16 路径，首字符是协议标记，主程序据此判断参数来源。
bool CreatePathMap(const std::vector<std::wstring> &paths,
                   std::wstring &mapName, std::wstring &eventName, size_t &totalBytes,
                   HANDLE &mappingHandle, HANDLE &eventHandle)
{
    size_t totalChars = 1;
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

    mappingHandle = mapping;
    eventHandle = event;
    return true;
}

// 交接线程只负责在超时内等子进程把数据读走，等不到也要释放，不能让句柄跟着资源管理器一起变老。
struct ChildHandoff
{
    HANDLE eventHandle;
    HANDLE mappingHandle;
};

unsigned __stdcall WaitForChildHandoff(void *parameter)
{
    ChildHandoff *handoff = static_cast<ChildHandoff *>(parameter);

    WaitForSingleObject(handoff->eventHandle, kHandoffTimeoutMs);
    CloseHandle(handoff->eventHandle);
    CloseHandle(handoff->mappingHandle);

    delete handoff;
    InterlockedDecrement(&g_pendingHandoffs);
    return 0;
}

// 建交接线程失败时把已建好的内核对象立刻释放，否则这次失败的右键会永久泄漏一份映射。
bool StartChildHandoff(HANDLE eventHandle, HANDLE mappingHandle)
{
    ChildHandoff *handoff = new (std::nothrow) ChildHandoff();
    if (handoff == nullptr) return false;

    handoff->eventHandle = eventHandle;
    handoff->mappingHandle = mappingHandle;

    InterlockedIncrement(&g_pendingHandoffs);

    uintptr_t thread = _beginthreadex(nullptr, 0, WaitForChildHandoff, handoff, 0, nullptr);
    if (thread == 0)
    {
        InterlockedDecrement(&g_pendingHandoffs);
        delete handoff;
        return false;
    }

    CloseHandle(reinterpret_cast<HANDLE>(thread));
    return true;
}

// 有 Auto7zGUI.exe 就走图形入口，它不创建控制台；只有控制台版本时退回 Auto7z.exe。
bool LaunchAuto7z(const std::vector<std::wstring> &paths, const wchar_t *modeSwitch)
{
    if (paths.empty()) return false;

    wchar_t exePath[MAX_PATH];
    if (!GetMainExePath(exePath, MAX_PATH)) return false;

    wchar_t guiPath[MAX_PATH];
    if (BuildGuiExePath(exePath, guiPath, MAX_PATH) &&
        GetFileAttributesW(guiPath) != INVALID_FILE_ATTRIBUTES)
    {
        wcscpy_s(exePath, MAX_PATH, guiPath);
    }

    std::wstring mapName;
    std::wstring eventName;
    size_t totalBytes = 0;
    HANDLE mappingHandle = nullptr;
    HANDLE eventHandle = nullptr;
    if (!CreatePathMap(paths, mapName, eventName, totalBytes, mappingHandle, eventHandle))
        return false;

    wchar_t sizeText[32];
    _snwprintf_s(sizeText, 32, _TRUNCATE, L"%llu", static_cast<unsigned long long>(totalBytes));

    std::wstring commandLine;
    commandLine += L'"';
    commandLine += exePath;
    commandLine += L'"';
    commandLine += L' ';
    commandLine += SHELL_MAP_PREFIX;
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

    if (!StartChildHandoff(eventHandle, mappingHandle))
    {
        CloseHandle(eventHandle);
        CloseHandle(mappingHandle);
        return FALSE;
    }

    return TRUE;
}


// 一个对象同时实现 IContextMenu 与 IShellExtInit，选区在 Initialize 一次拿全，之后不再反复查资源管理器。
class CShellContextMenu : public IContextMenu, public IShellExtInit
{
public:
    CShellContextMenu() : m_refCount(1)
    {
        AddObjectRef();
    }

    virtual ~CShellContextMenu()
    {
        ReleaseObjectRef();
    }

    // 多重继承下两个接口在对象里的偏移不同，QueryInterface 必须按接口分别取地址，否则资源管理器会调错函数并静默禁用扩展。
    STDMETHODIMP QueryInterface(REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        if (IsEqualIID(interfaceId, IID_IUnknown))
        {
            *object = static_cast<IContextMenu *>(this);
            AddRef();
            return S_OK;
        }
        if (IsEqualIID(interfaceId, IID_IShellExtInit))
        {
            *object = static_cast<IShellExtInit *>(this);
            AddRef();
            return S_OK;
        }
        if (IsEqualIID(interfaceId, IID_IContextMenu))
        {
            *object = static_cast<IContextMenu *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&m_refCount));
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        LONG remaining = InterlockedDecrement(&m_refCount);
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    // Initialize 只记录选区，不做任何耗时操作：它跑在资源管理器的界面线程上。
    STDMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, LPDATAOBJECT dataObject, HKEY hkeyProgID) override
    {
        (void)pidlFolder;
        (void)hkeyProgID;

        m_paths.clear();
        if (dataObject != nullptr)
            ReadSelectedPaths(dataObject, m_paths);

        return S_OK;
    }

    // 返回值是「加进去的菜单项个数」而不是 S_OK，返回 0 等于告诉资源管理器这里没有菜单项。
    STDMETHODIMP QueryContextMenu(HMENU menu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT flags) override
    {
        if (menu == nullptr) return E_INVALIDARG;

        if (idCmdFirst > idCmdLast) return E_INVALIDARG;

        std::vector<std::wstring> usable;
        for (const std::wstring &path : m_paths)
        {
            if (IsRealFileOrFolder(path.c_str())) usable.push_back(path);
        }
        if (usable.empty()) return static_cast<HRESULT>(0);

        if ((flags & CMF_DEFAULTONLY) != 0) return static_cast<HRESULT>(0);

        m_paths.swap(usable);

        wchar_t compress[64] = {};
        wchar_t extract[64] = {};
        int compressId = kStringCompressDefault;
        int extractId = kStringExtractDefault;

        std::wstring language = ReadConfiguredLanguage();
        if (_wcsicmp(language.c_str(), L"zh-CN") == 0)
        {
            compressId = kStringCompressZhCN;
            extractId = kStringExtractZhCN;
        }
        else if (_wcsicmp(language.c_str(), L"zh-TW") == 0)
        {
            compressId = kStringCompressZhTW;
            extractId = kStringExtractZhTW;
        }

        LoadStringW(g_module, compressId, compress, 64);
        LoadStringW(g_module, extractId, extract, 64);

        UINT highest = 0;

        MENUITEMINFOW item = {};
        item.cbSize = sizeof(item);
        item.fMask = MIIM_STRING | MIIM_ID;
        item.hSubMenu = nullptr;

        HBITMAP bitmap = LoadMainMenuBitmap();
        if (bitmap != nullptr)
        {
            item.fMask |= MIIM_BITMAP;
            item.hbmpItem = bitmap;
        }

        item.wID = idCmdFirst + kCmdCompress;
        item.dwTypeData = compress;
        if (InsertMenuItemW(menu, indexMenu, TRUE, &item)) highest = kCmdCompress + 1;

        item.wID = idCmdFirst + kCmdExtract;
        item.dwTypeData = extract;
        if (InsertMenuItemW(menu, indexMenu + 1, TRUE, &item)) highest = kCmdExtract + 1;

        return static_cast<HRESULT>(highest);
    }

    // 压缩与解压只是模式开关不同，两者的启动与传参路径完全共用。
    STDMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO info) override
    {
        if (info == nullptr) return E_INVALIDARG;
        if (m_paths.empty()) return E_FAIL;

        UINT slot = kCmdCompress;
        if (FindCommandSlot(info, &slot) != S_OK) return E_FAIL;

        const wchar_t *modeSwitch = (slot == kCmdExtract) ? L"-uz" : L"-z";
        if (!LaunchAuto7z(m_paths, modeSwitch))
        {
            return E_FAIL;
        }
        return S_OK;
    }

    // 谓词名要能被脚本与资源管理器的「验证」查询命中，因此按 A / W 两种形态各答一次。
    STDMETHODIMP GetCommandString(UINT_PTR id, UINT type, UINT *reserved, LPSTR buffer, UINT bufferChars) override
    {
        (void)reserved;

        if (type & GCS_UNICODE)
        {
            if ((type & 0xF) != GCS_VERBW && (type & 0xF) != GCS_VALIDATEW) return E_NOTIMPL;

            const wchar_t *verb = (id & 0x3) == kCmdExtract ? kVerbExtract : kVerbCompress;
            if ((type & 0xF) == GCS_VALIDATEW) return S_OK;

            return wcscpy_s(reinterpret_cast<wchar_t *>(buffer), bufferChars, verb) == 0
                ? S_OK
                : E_FAIL;
        }

        if ((type & 0xF) == GCS_VALIDATEA) return S_OK;
        if ((type & 0xF) != GCS_VERBA) return E_NOTIMPL;

        {
            const wchar_t *verb = (id & 0x3) == kCmdExtract ? kVerbExtract : kVerbCompress;
            int written = WideCharToMultiByte(CP_ACP, 0, verb, -1, buffer,
                                              static_cast<int>(bufferChars), nullptr, nullptr);
            return (written > 0) ? S_OK : E_FAIL;
        }
    }

private:
    // 调用方可能传字符串谓词，也可能传菜单项下标，两种都要认；下标越界即视为无效命令。
    HRESULT FindCommandSlot(LPCMINVOKECOMMANDINFO info, UINT *slot)
    {
        LPCMINVOKECOMMANDINFOEX infoEx = nullptr;
        if (info->cbSize == sizeof(CMINVOKECOMMANDINFOEX) &&
            (info->fMask & CMIC_MASK_UNICODE) != 0)
        {
            infoEx = reinterpret_cast<LPCMINVOKECOMMANDINFOEX>(info);

            // lpVerbW 是 NULL 时不能落到下标分支：LOWORD(NULL) 等于 0，会被当成「压缩」，
            // 于是点「解压」也压缩。调用方置了 Unicode 位却只填了 ANSI 的 lpVerb 时就是这种形态，
            // 这里改按 lpVerb 判，两者都拿不到有效槽位就失败关闭。
            if (infoEx->lpVerbW == nullptr) return FindAnsiCommandSlot(info->lpVerb, slot);

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

            UINT index = LOWORD(reinterpret_cast<ULONG_PTR>(infoEx->lpVerbW));
            if (index > kCmdExtract) return E_FAIL;
            *slot = index;
            return S_OK;
        }

        return FindAnsiCommandSlot(info->lpVerb, slot);
    }

    // 只认 MAKEINTRESOURCE 形态的谓词；字符串谓词不带 Unicode 位时同样算认不出来。
    HRESULT FindAnsiCommandSlot(LPCSTR verb, UINT *slot)
    {
        if (!IS_INTRESOURCE(verb)) return E_FAIL;

        UINT value = LOWORD(reinterpret_cast<ULONG_PTR>(verb));
        if (value > kCmdExtract) return E_FAIL;

        *slot = value;
        return S_OK;
    }

    std::vector<std::wstring> m_paths;
    LONG m_refCount;
};

// 类工厂负责按 CLSID 造出菜单对象，聚合请求一律拒绝，本组件不支持聚合。
class CShellClassFactory : public IClassFactory
{
public:
    CShellClassFactory() : m_refCount(1)
    {
        AddObjectRef();
    }

    virtual ~CShellClassFactory()
    {
        ReleaseObjectRef();
    }

    STDMETHODIMP QueryInterface(REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        if (IsEqualIID(interfaceId, IID_IUnknown) || IsEqualIID(interfaceId, IID_IClassFactory))
        {
            *object = static_cast<IClassFactory *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&m_refCount));
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        LONG remaining = InterlockedDecrement(&m_refCount);
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    STDMETHODIMP CreateInstance(IUnknown *outer, REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        if (outer != nullptr) return CLASS_E_NOAGGREGATION;

        CShellContextMenu *instance = new (std::nothrow) CShellContextMenu();
        if (instance == nullptr) return E_OUTOFMEMORY;

        HRESULT result = instance->QueryInterface(interfaceId, object);
        instance->Release();
        return result;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_lockCount);
        else InterlockedDecrement(&g_lockCount);
        return S_OK;
    }

private:
    LONG m_refCount;
};


// 注册表写入集中在这一处，成功与否用返回值表达，调用方据此决定是否上报注册失败。
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

// 递归删除要自下而上：先清空子键再删自己，否则 RegDeleteKey 会因为还有子键而失败。
void DeleteKeyTree(HKEY root, const wchar_t *path)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, path, 0, KEY_WRITE | KEY_READ, &key) != ERROR_SUCCESS) return;

    wchar_t subName[256];
    DWORD subNameChars = 256;
    while (RegEnumKeyExW(key, 0, subName, &subNameChars, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS)
    {
        DeleteKeyTree(root, (std::wstring(path) + L"\\" + subName).c_str());
        subNameChars = 256;
    }

    RegCloseKey(key);
    RegDeleteKeyW(root, path);
}

// 只删值不删键：值可能被别的组件共用，键本身留给后续注册继续使用。
void DeleteRegValue(HKEY root, const wchar_t *subKeyPath, const wchar_t *valueName)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, subKeyPath, 0, KEY_SET_VALUE, &key) != ERROR_SUCCESS) return;

    RegDeleteValueW(key, valueName);
    RegCloseKey(key);
}

// 四个挂载点是产品的注册契约，一处都不能少：文件、文件夹、Folder 与目录背景。
bool RegisterHandlers(bool set)
{
    static const wchar_t *kTypes[] = {L"*", L"Directory", L"Folder", L"Directory\\Background"};

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    bool anyWritten = false;
    for (const wchar_t *type : kTypes)
    {
        std::wstring path = std::wstring(L"Software\\Classes\\")
            + type + L"\\shellex\\ContextMenuHandlers\\" + kHandlerKeyName;
        if (set)
        {
            if (WriteStringValue(kMachineClasses, path.c_str(), nullptr, clsid))
            {
                anyWritten = true;
            }
            else
            {
                OutputDebugStringW(L"Auto7z: 菜单挂载点写入失败\r\n");
            }
        }
        else
        {
            DeleteKeyTree(kMachineClasses, path.c_str());
        }
    }

    return set ? anyWritten : true;
}

}


// DllMain 只存模块句柄并关掉线程通知，不做任何可能死锁的活。
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

// 类对象按 CLSID 精确匹配，不认识的 CLSID 一律报 CLASS_E_CLASSNOTAVAILABLE。
extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID classId, REFIID interfaceId, void **object)
{
    if (object == nullptr) return E_POINTER;
    *object = nullptr;

    if (!IsEqualIID(classId, kClassId)) return CLASS_E_CLASSNOTAVAILABLE;

    CShellClassFactory *factory = new (std::nothrow) CShellClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;

    HRESULT result = factory->QueryInterface(interfaceId, object);

    factory->Release();
    return result;
}

// 对象引用、锁计数与待交接句柄都归零才允许卸载，缺一项都会让下次右键找不到已加载的组件。
extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    bool idle = ReadLong(&g_refCount) == 0
             && ReadLong(&g_lockCount) == 0
             && ReadLong(&g_pendingHandoffs) == 0;

    return idle ? S_OK : S_FALSE;
}

// 注册要写 HKCR 的 CLSID、HKLM 的 Approved 列表与四个挂载点；任何一步失败都整次按失败上报。
extern "C" HRESULT STDAPICALLTYPE DllRegisterServer()
{
    wchar_t dllPath[MAX_PATH];
    DWORD dllPathChars = GetModuleFileNameW(g_module, dllPath, MAX_PATH);
    if (dllPathChars == 0 || dllPathChars >= MAX_PATH) return SELFREG_E_CLASS;

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    std::wstring classPath = std::wstring(L"CLSID\\") + clsid;
    if (!WriteStringValue(kClassesRoot, classPath.c_str(), nullptr, kClassName))
        return SELFREG_E_CLASS;

    std::wstring inprocPath = classPath + L"\\InprocServer32";
    if (!WriteStringValue(kClassesRoot, inprocPath.c_str(), nullptr, dllPath))
        return SELFREG_E_CLASS;
    WriteStringValue(kClassesRoot, inprocPath.c_str(), L"ThreadingModel", L"Apartment");

    if (!WriteStringValue(HKEY_LOCAL_MACHINE,
                          L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
                          clsid, kClassName))
    {
        return SELFREG_E_CLASS;
    }

    if (!RegisterHandlers(true)) return SELFREG_E_CLASS;

    wchar_t exePath[MAX_PATH] = {};
    if (GetMainExePath(exePath, MAX_PATH))
    {
        WriteStringValue(kClassesRoot, GetMainExeValueKey().c_str(), L"MainExePath", exePath);
    }

    return S_OK;
}

// 注销的顺序与注册相反：先摘挂载点再删 CLSID，避免中途失败留下指向不存在组件的菜单。
extern "C" HRESULT STDAPICALLTYPE DllUnregisterServer()
{
    RegisterHandlers(false);

    wchar_t clsid[64];
    FormatClassId(clsid, 64);

    std::wstring classPath = std::wstring(L"CLSID\\") + clsid;
    DeleteKeyTree(kClassesRoot, (classPath + L"\\InprocServer32").c_str());
    DeleteKeyTree(kClassesRoot, classPath.c_str());

    DeleteRegValue(HKEY_LOCAL_MACHINE,
                   L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
                   clsid);
    return S_OK;
}

// 主程序把自身路径写进登记项，外壳扩展启动子进程时优先用它，便携拷贝也能跟着走。
extern "C" HRESULT STDAPICALLTYPE DllSetMainExePath(LPCWSTR path)
{
    if (path == nullptr || *path == L'\0') return E_INVALIDARG;

    if (!WriteStringValue(kClassesRoot, GetMainExeValueKey().c_str(), L"MainExePath", path))
        return SELFREG_E_CLASS;
    return S_OK;
}

// 语言值写在 HKCU，组件下次建菜单时按它挑字符串资源。
extern "C" HRESULT STDAPICALLTYPE DllSetLanguage(LPCWSTR language)
{
    if (language == nullptr || *language == L'\0') return E_INVALIDARG;

    if (!WriteStringValue(HKEY_CURRENT_USER, kLanguageValueKey, L"Language", language))
        return SELFREG_E_CLASS;

    return S_OK;
}

// 清掉登记项与它所在的键：留着空键会让后续查询读到空路径。
extern "C" HRESULT STDAPICALLTYPE DllRemoveMainExePath()
{
    std::wstring valuePath = GetMainExeValueKey();
    DeleteRegValue(kClassesRoot, valuePath.c_str(), L"MainExePath");
    RegDeleteKeyW(kClassesRoot, valuePath.c_str());
    return S_OK;
}
