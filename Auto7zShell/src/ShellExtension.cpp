
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

// 全部实现收在匿名命名空间里，对外只留 Dll* 导出符号，免得和主程序或别的扩展重名
namespace {

// 这个号同时写进注册表和主程序的注册流程，改了必须重新注册，否则资源管理器仍按旧号找组件
const CLSID kClassId = {
    0x6C3F8A21,
    0x4B7D,
    0x4E5A,
    {0x9A, 0x2C, 0x3D, 0x71, 0x0E, 0x5B, 0x88, 0x41}};

const wchar_t kClassName[] = L"Auto7z Shell Extension";

// 谓词名是脚本和 GetCommandString 认的稳定标识，不跟着界面语言变
const wchar_t kVerbCompress[] = L"Auto7z.Compress";
const wchar_t kVerbExtract[] = L"Auto7z.Extract";

// 语言段按百位分开，201 留给繁体、301 留给英文，运行时按配置挑一组
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

// 子进程迟迟不来读共享内存时的兜底上限，超过就无条件释放，免得句柄跟着资源管理器常驻
const DWORD kHandoffTimeoutMs = 30000;

const wchar_t kMainExeName[] = L"Auto7z.exe";

const wchar_t kGuiExeName[] = L"Auto7zGUI.exe";

const wchar_t kHandlerKeyName[] = L"Auto7z";

// 自身模块句柄要留着，LoadStringW 取菜单文字和注册时取 DLL 路径都得靠它
HMODULE g_module = nullptr;

LONG g_refCount = 0;

LONG g_lockCount = 0;

// 还在等子进程取数据的后台线程数，DllCanUnloadNow 必须把它算进去
LONG g_pendingHandoffs = 0;

inline void AddObjectRef() { InterlockedIncrement(&g_refCount); }
inline void ReleaseObjectRef() { InterlockedDecrement(&g_refCount); }

// 这几个计数由多个线程并发改，取值也走 Interlocked，避免读到撕裂的中间值
inline LONG ReadLong(volatile LONG *value)
{
    return InterlockedCompareExchange(value, 0, 0);
}


// 注册表键名只认花括号大写形式的 GUID，这里现拼，省得再维护一份字符串常量
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

// 主程序路径挂在 CLSID 底下，反注册时能跟着 CLSID 那棵树一起清干净
std::wstring GetMainExeValueKey()
{
    wchar_t clsid[64];
    FormatClassId(clsid, 64);
    return std::wstring(L"CLSID\\") + clsid + L"\\Auto7z";
}

bool GetModuleDirectory(wchar_t *buffer, size_t bufferChars)
{
    DWORD length = GetModuleFileNameW(g_module, buffer, static_cast<DWORD>(bufferChars));
    if (length == 0 || length >= bufferChars) return false;

    wchar_t *separator = wcsrchr(buffer, L'\\');
    if (separator == nullptr) return false;
    *separator = L'\0';
    return true;
}

// 主程序可能装在别处，先信注册时记下的路径，找不到再退回 DLL 所在目录
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

    // 调用成功了不代表内容可信，长度先卡一道再按字符下标截断
    if (size == 0 || size > sizeof(buffer)) return false;
    buffer[(size / sizeof(wchar_t)) - 1] = L'\0';
    if (buffer[0] == L'\0') return false;

    path.assign(buffer);
    return true;
}

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

// 三处来源依次试：注册表、用户配置目录、便携配置目录，谁先给出非空值就定谁
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

// 配置是标准 JSON，这里只取顶层 "Language" 键的值。
// 这条路只在没有注册表值时才会走到，所以宁可多容错也不报错：
// 解析失败一律返回空串，由调用方回落下一处来源。
std::wstring ReadLanguageFromConfig(const std::wstring &path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return L"";

    DWORD size = GetFileSize(file, nullptr);
    // 正常配置只有几百字节，超过 1MB 多半是被别的文件顶替了，不往下读
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

    // 配置固定是 UTF-8（无 BOM），直接按 UTF-8 解
    int length = MultiByteToWideChar(CP_UTF8, 0, buffer.data(), static_cast<int>(read), nullptr, 0);
    if (length <= 0) return L"";

    std::wstring text(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, buffer.data(), static_cast<int>(read), &text[0], length);

    // 找 "Language" 键：要求左边是引号、右边紧跟冒号，避免命中值里的同名子串
    const std::wstring key = L"\"Language\"";
    size_t position = text.find(key);
    if (position == std::wstring::npos) return L"";

    size_t colon = text.find(L':', position + key.size());
    if (colon == std::wstring::npos) return L"";

    // 冒号之后跳过空白，值必须用双引号包裹
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

        // 只处理语言代码会用到的转义；出现别的转义说明文件异常，整个放弃
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

        // 语言代码里不该出现换行；出现说明引号没闭合，按异常处理
        if (ch == L'\r' || ch == L'\n') return L"";

        value.push_back(ch);
    }

    return L"";
}

// 注册表里的路径可能指向已卸载的位置，必须真探到文件才采信
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

// 同目录下有 GUI 版就换用它，进度和提问才有窗口承接
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


// 菜单项位图按小图标尺寸画，系统不会代为缩放
const int kMenuIconSize = 16;

// 先取小图标，没有再退而取大图标，都拿不到就干脆不带图标
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

// 图标只抽一次并常驻，菜单每弹一次都会走到这里，重复抽取等于在资源管理器进程里按次泄漏 GDI 句柄
HICON LoadMainIcon()
{
    static const HICON cached = LoadMainIconOnce();
    return cached;
}

// 菜单项只认位图不认图标，这里把图标重画到白底位图上
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

            // 不做 HALFTONE 的话，大图标缩到 16×16 会糊成一团色块
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

// 位图同样只做一次，每弹一次菜单重新画一张，等于按次漏 GDI 句柄
HBITMAP LoadMainMenuBitmap()
{
    static const HBITMAP cached = IconToBitmap(LoadMainIcon(), kMenuIconSize);
    return cached;
}


// 资源管理器给的路径里混着盘符根、回收站和虚拟命名空间，这些位置压缩解压都没有意义
bool IsRealFileOrFolder(const wchar_t *path)
{
    if (path == nullptr || path[0] == 0) return false;

// 回收站条目看着有完整路径，实际取不到可读内容，只能按路径里这一段挡掉
if (StrStrIW(path, L"\\$Recycle.Bin") != nullptr) return false;

    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) return false;

    // 设备对象也带目录属性，不先挡掉会被当成文件夹放行
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

// 先走 CF_HDROP，虚拟项拿不到文件路径时才退到 IDList，两种格式覆盖的范围并不重合
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
            // CIDA 的内容由别的进程填写，先粗查数量再按偏移取指针，坏数据不至于把本进程读崩
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


// 名字里带进程号和自增序号，多个资源管理器进程同时弹菜单也不会撞名
void MakeRandomName(wchar_t *buffer, size_t bufferChars, const wchar_t *prefix)
{
    static LONG counter = 0;
    LONG serial = InterlockedIncrement(&counter);

    _snwprintf_s(buffer, bufferChars, _TRUNCATE, L"%s%08X%08X",
                 prefix, GetCurrentProcessId(), serial);
}

// 共享内存里按「一个标记 + 每项以 L'\0' 收尾」平铺，主程序顺序读到底即可，不必再解析长度
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

struct ChildHandoff
{
    HANDLE eventHandle;
    HANDLE mappingHandle;
};

// 这段等待绝不能放在资源管理器的界面线程上，主程序启动慢时曾在这里卡满 30 秒，右键菜单跟着冻住
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

// 命名共享内存最后一个句柄关掉就销毁，所以句柄要留到子进程映射进去并读完为止
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

// 命令行形如 "Auto7z.exe" #映射名:字节数:事件名 -z，主程序按这个格式反解
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


// 一个对象同时实现两个接口，资源管理器先用 IShellExtInit 灌入选中项，再回头调 IContextMenu 出菜单
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

    // IContextMenu 与 IShellExtInit 在对象内的偏移不同，必须按请求的接口分别返回对应子对象的地址，一律给同一个地址会让资源管理器调错函数并静默禁掉扩展
    STDMETHODIMP QueryInterface(REFIID interfaceId, void **object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        // GUID 没有重载 operator==，直接比比的是地址，判接口一律走 IsEqualIID
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

    // 计数归零就 delete this，对象自己决定生死，调用方只负责把引用还回来
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG remaining = InterlockedDecrement(&m_refCount);
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    // 取不到路径也返回 S_OK，这里报失败会让资源管理器认定整个扩展有问题，连别的文件夹的菜单项一起摘掉
    STDMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, LPDATAOBJECT dataObject, HKEY hkeyProgID) override
    {
        (void)pidlFolder;
        (void)hkeyProgID;

        m_paths.clear();
        if (dataObject != nullptr)
            ReadSelectedPaths(dataObject, m_paths);

        return S_OK;
    }

    // 返回值是插进去的菜单项个数而不是 S_OK，返回 0 等于告诉资源管理器这儿没有菜单项
    STDMETHODIMP QueryContextMenu(HMENU menu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT flags) override
    {
        if (menu == nullptr) return E_INVALIDARG;

        if (idCmdFirst > idCmdLast) return E_INVALIDARG;

        // 过滤结果先另存一份，等确认要出菜单了再换进 m_paths
        std::vector<std::wstring> usable;
        for (const std::wstring &path : m_paths)
        {
            if (IsRealFileOrFolder(path.c_str())) usable.push_back(path);
        }
        // 一个能用的都没有就当没这个扩展，回 0 而不是错误码
        if (usable.empty()) return static_cast<HRESULT>(0);

        // 只判 CMF_DEFAULTONLY，它表示调用方只要默认项，此时不该往菜单里塞东西，其余标志与本扩展无关
        if ((flags & CMF_DEFAULTONLY) != 0) return static_cast<HRESULT>(0);

        // 换入必须放在上面两个提前返回之后，提前换会把 m_paths 里原来的选中项冲掉，后面 InvokeCommand 拿到的是另一份列表
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

        // 记的是已插入的项数，用最大命令偏移加一表示，和 QueryContextMenu 要求的返回值口径一致
        UINT highest = 0;

        MENUITEMINFOW item = {};
        item.cbSize = sizeof(item);
        item.fMask = MIIM_STRING | MIIM_ID;
        item.hSubMenu = nullptr;

        // MIIM_BITMAP 只记指针不复制位图，插入后这份位图必须一直活着，实际由函数内的静态缓存持有
        HBITMAP bitmap = LoadMainMenuBitmap();
        if (bitmap != nullptr)
        {
            item.fMask |= MIIM_BITMAP;
            item.hbmpItem = bitmap;
        }

        item.wID = idCmdFirst + kCmdCompress;
        item.dwTypeData = compress;
        // 按位置插入而不是追加，菜单项才会落在资源管理器的默认项之后
        if (InsertMenuItemW(menu, indexMenu, TRUE, &item)) highest = kCmdCompress + 1;

        item.wID = idCmdFirst + kCmdExtract;
        item.dwTypeData = extract;
        // 第二项要排在第一项后面，所以下标加一，否则会插到前一项上面去
        if (InsertMenuItemW(menu, indexMenu + 1, TRUE, &item)) highest = kCmdExtract + 1;

        return static_cast<HRESULT>(highest);
    }

    // lpVerb 可能是序号也可能是谓词名，统一交给 FindCommandSlot 归一后再分派
    STDMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO info) override
    {
        if (info == nullptr) return E_INVALIDARG;
        if (m_paths.empty()) return E_FAIL;

        UINT slot = kCmdCompress;
        if (FindCommandSlot(info, &slot) != S_OK) return E_FAIL;

        // 路径已经在共享内存里，命令行只留模式和映射名，不必操心命令行长度上限
        const wchar_t *modeSwitch = (slot == kCmdExtract) ? L"-uz" : L"-z";
        if (!LaunchAuto7z(m_paths, modeSwitch))
        {
            return E_FAIL;
        }
        return S_OK;
    }

    // 宽窄两支必须分开走，调用方按带不带 GCS_UNICODE 决定缓冲区是 wchar_t 还是 char
    STDMETHODIMP GetCommandString(UINT_PTR id, UINT type, UINT *reserved, LPSTR buffer, UINT bufferChars) override
    {
        (void)reserved;

        // GCS_UNICODE 只表明缓冲区宽度，到底要谓词还是只要校验，还得看低四位
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
            // 窄版按系统 ANSI 代码页转换，谓词目前是纯 ASCII，换页也不会丢字符
            int written = WideCharToMultiByte(CP_ACP, 0, verb, -1, buffer,
                                              static_cast<int>(bufferChars), nullptr, nullptr);
            return (written > 0) ? S_OK : E_FAIL;
        }
    }

private:
    // 资源管理器有时传序号有时传谓词，还有不认 lpVerbW 的旧调用方，三种都得兜住
    HRESULT FindCommandSlot(LPCMINVOKECOMMANDINFO info, UINT *slot)
    {
        LPCMINVOKECOMMANDINFOEX infoEx = nullptr;
        // 先比结构体大小再读扩展字段，调用方给的可能是老的 CMINVOKECOMMANDINFO
        if (info->cbSize == sizeof(CMINVOKECOMMANDINFOEX) &&
            (info->fMask & CMIC_MASK_UNICODE) != 0)
        {
            infoEx = reinterpret_cast<LPCMINVOKECOMMANDINFOEX>(info);
            // 指针落在资源段之外才是真字符串，否则低位存的是菜单项序号
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

        if (!IS_INTRESOURCE(info->lpVerb)) return E_FAIL;

        UINT value = LOWORD(reinterpret_cast<ULONG_PTR>(info->lpVerb));
        if (value > kCmdExtract) return E_FAIL;

        *slot = value;
        return S_OK;
    }

    std::vector<std::wstring> m_paths;
    LONG m_refCount;
};

// 类厂自己不干活，只负责把 CShellContextMenu 造出来交给资源管理器
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

        // 用 nothrow 分配，内存不足时拿回空指针，由这里换成 E_OUTOFMEMORY
        CShellContextMenu *instance = new (std::nothrow) CShellContextMenu();
        if (instance == nullptr) return E_OUTOFMEMORY;

        HRESULT result = instance->QueryInterface(interfaceId, object);
        // QueryInterface 成功时会再加一次引用，这里把构造时那次还掉，否则对象永远退不掉
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

// RegDeleteKeyW 不删非空键，得先把子键递归清空
void DeleteKeyTree(HKEY root, const wchar_t *path)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, path, 0, KEY_WRITE | KEY_READ, &key) != ERROR_SUCCESS) return;

    wchar_t subName[256];
    DWORD subNameChars = 256;
    // 每次都取第 0 个子键，删掉一个后面的会前移，用递增下标反而会漏删
    while (RegEnumKeyExW(key, 0, subName, &subNameChars, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS)
    {
        DeleteKeyTree(root, (std::wstring(path) + L"\\" + subName).c_str());
        subNameChars = 256;
    }

    RegCloseKey(key);
    RegDeleteKeyW(root, path);
}

void DeleteRegValue(HKEY root, const wchar_t *subKeyPath, const wchar_t *valueName)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, subKeyPath, 0, KEY_SET_VALUE, &key) != ERROR_SUCCESS) return;

    RegDeleteValueW(key, valueName);
    RegCloseKey(key);
}

// 四个挂载点分别覆盖文件、文件夹和文件夹空白处，带 Background 的那个才是空白处右键的来源
bool RegisterHandlers(bool set)
{
    // 反斜杠在这里要写两个，漏写一个会变成 DirectoryBackground，空白处菜单从此再不出现，而且不报任何错
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

    // 卸载时不追究有没有真删到东西，残留的键不值得让整个反注册失败
    return set ? anyWritten : true;
}

}


// 这里只记模块句柄，线程附着通知用不上，顺手关掉省去每次建线程的加锁
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

// 资源管理器只按注册表里的 CLSID 来要类厂，号对不上就回 CLASS_E_CLASSNOTAVAILABLE
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

// 后台交接线程没跑完也算忙，提前卸载会把它们脚下的代码抽走
extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    bool idle = ReadLong(&g_refCount) == 0
             && ReadLong(&g_lockCount) == 0
             && ReadLong(&g_pendingHandoffs) == 0;

    return idle ? S_OK : S_FALSE;
}

// 注册要做三件事：登记 CLSID、进 Approved 白名单、挂到各类型的右键菜单下
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
    // 必须声明 Apartment，资源管理器在单线程套间里调用，写成 Both 反而要多绕一层封送
    WriteStringValue(kClassesRoot, inprocPath.c_str(), L"ThreadingModel", L"Apartment");

    // 不进 Approved 名单的话，扩展会被直接跳过，而且不给任何提示
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

// 按注册的相反顺序拆：先摘菜单挂载点，再删 CLSID 树和白名单条目
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

// 主程序挪了位置由它自己调这里更新，不必把整个 DLL 重新注册一遍
extern "C" HRESULT STDAPICALLTYPE DllSetMainExePath(LPCWSTR path)
{
    if (path == nullptr || *path == L'\0') return E_INVALIDARG;

    if (!WriteStringValue(kClassesRoot, GetMainExeValueKey().c_str(), L"MainExePath", path))
        return SELFREG_E_CLASS;
    return S_OK;
}

// 语言存在 HKCU 下，同一台机器上不同用户可以各用各的界面语言
extern "C" HRESULT STDAPICALLTYPE DllSetLanguage(LPCWSTR language)
{
    if (language == nullptr || *language == L'\0') return E_INVALIDARG;

    if (!WriteStringValue(HKEY_CURRENT_USER, kLanguageValueKey, L"Language", language))
        return SELFREG_E_CLASS;

    return S_OK;
}

// 先删值再删键，空键留着会被主程序当成「已注册但路径为空」
extern "C" HRESULT STDAPICALLTYPE DllRemoveMainExePath()
{
    std::wstring valuePath = GetMainExeValueKey();
    DeleteRegValue(kClassesRoot, valuePath.c_str(), L"MainExePath");
    RegDeleteKeyW(kClassesRoot, valuePath.c_str());
    return S_OK;
}
