#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <dbt.h>

#define WM_TRAYICON (WM_USER + 1)
#define IDM_SCAN_NOW 1001
#define IDM_BROWSE   1002
#define IDM_OPEN     1003
#define IDM_ABOUT    1004
#define IDM_EXIT     1005

static const char* g_szAppName = "获取Rick课件 (原生C语言版)";
static const char* g_szClassName = "RickCourseware_CClass";
static const char* g_szMutexName = "RickCourseware_SingleInstance_Mutex";
static const char* g_szAppTitle = "获取Rick课件";

static HINSTANCE g_hInstance = NULL;
static HWND g_hWnd = NULL;
static HICON g_hIcon = NULL;
static HANDLE g_hMutex = NULL;
static NOTIFYICONDATAA g_nid;

static char g_selfDrive = '\0';
static char g_iniPath[MAX_PATH] = {0};
static char g_backupPath[MAX_PATH] = {0};

// 自定义内存清零与大写转换（无需任何 C 运行时）
void* memset(void* dest, int c, size_t count) {
    char* p = (char*)dest;
    while (count--) *p++ = (char)c;
    return dest;
}

void* memcpy(void* dest, const void* src, size_t count) {
    char* d = (char*)dest;
    const char* s = (const char*)src;
    while (count--) *d++ = *s++;
    return dest;
}

static inline char ToUpperChar(char c) {
    if (c >= 'a' && c <= 'z') return (char)(c - 'a' + 'A');
    return c;
}

// 路径拼接辅助函数
static void PathCombine(char* dest, const char* part1, const char* part2) {
    if (!dest) return;
    dest[0] = '\0';
    if (!part1) part1 = "";
    if (!part2) part2 = "";

    lstrcpyA(dest, part1);
    int len1 = lstrlenA(dest);
    if (len1 > 0 && dest[len1 - 1] != '\\' && dest[len1 - 1] != '/') {
        dest[len1] = '\\';
        dest[len1 + 1] = '\0';
    }
    lstrcatA(dest, part2);
}

// 过滤系统保留文件夹与系统隐藏文件
static int ShouldSkipItem(const char* name) {
    if (!name || name[0] == '\0') return 1;
    if (lstrcmpiA(name, ".") == 0 || lstrcmpiA(name, "..") == 0) return 1;
    if (name[0] == '.') return 1; // 过滤以 . 开头的文件 (如 .DS_Store, ._*)
    if (lstrcmpiA(name, "System Volume Information") == 0) return 1;
    if (lstrcmpiA(name, "$RECYCLE.BIN") == 0) return 1;
    if (lstrcmpiA(name, "$Recycle.Bin") == 0) return 1;
    return 0;
}

// 气泡通知
void ShowBalloon(const char* title, const char* msg, DWORD flags) {
    g_nid.uFlags = NIF_INFO;
    g_nid.dwInfoFlags = flags;
    lstrcpyA(g_nid.szInfoTitle, title);
    lstrcpyA(g_nid.szInfo, msg);
    Shell_NotifyIconA(NIM_MODIFY, &g_nid);
}

// 保存配置
void SaveSettings(void) {
    if (g_iniPath[0] != '\0' && g_backupPath[0] != '\0') {
        WritePrivateProfileStringA("Settings", "BackupPath", g_backupPath, g_iniPath);
    }
}

// 读取配置
void LoadSettings(void) {
    char currentDir[MAX_PATH] = {0};
    GetCurrentDirectoryA(MAX_PATH, currentDir);
    PathCombine(g_iniPath, currentDir, "RickConfig.ini");

    GetPrivateProfileStringA("Settings", "BackupPath", "", g_backupPath, sizeof(g_backupPath), g_iniPath);
    if (g_backupPath[0] == '\0') {
        PathCombine(g_backupPath, currentDir, "课件备份");
        SaveSettings();
    }
    CreateDirectoryA(g_backupPath, NULL);
}

// 打开备份目录
void OpenBackupDir(void) {
    if (g_backupPath[0] == '\0') {
        MessageBoxA(g_hWnd, "请先设置课件保存文件夹！", g_szAppTitle, MB_OK | MB_ICONWARNING);
        return;
    }
    CreateDirectoryA(g_backupPath, NULL);
    ShellExecuteA(NULL, "explore", g_backupPath, NULL, NULL, SW_SHOWNORMAL);
}

// 选择保存文件夹
void BrowseBackupDir(HWND hWnd) {
    BROWSEINFOA bi;
    memset(&bi, 0, sizeof(bi));
    bi.hwndOwner = hWnd;
    bi.lpszTitle = "选择课件保存文件夹";
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;

    LPITEMIDLIST pidl = SHBrowseForFolderA(&bi);
    if (pidl) {
        char selected[MAX_PATH] = {0};
        if (SHGetPathFromIDListA(pidl, selected)) {
            lstrcpyA(g_backupPath, selected);
            CreateDirectoryA(g_backupPath, NULL);
            SaveSettings();
            ShowBalloon("设置已保存", "课件保存路径已成功更新！", NIIF_INFO);
        }
        CoTaskMemFree(pidl);
    }
}

// 递归文件与文件夹拷贝
void CopyFolderRecursive(const char* srcDir, const char* dstDir) {
    CreateDirectoryA(dstDir, NULL);

    char searchMask[MAX_PATH];
    PathCombine(searchMask, srcDir, "*.*");

    WIN32_FIND_DATAA fd;
    HANDLE hFind = FindFirstFileA(searchMask, &fd);
    if (hFind == INVALID_HANDLE_VALUE) return;

    do {
        if (ShouldSkipItem(fd.cFileName)) {
            continue;
        }

        char srcItem[MAX_PATH];
        char dstItem[MAX_PATH];
        PathCombine(srcItem, srcDir, fd.cFileName);
        PathCombine(dstItem, dstDir, fd.cFileName);

        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            CopyFolderRecursive(srcItem, dstItem);
        } else {
            CopyFileA(srcItem, dstItem, FALSE);
        }
    } while (FindNextFileA(hFind, &fd));

    FindClose(hFind);
}

// 后台拷贝工作线程
DWORD WINAPI WorkerBackupThread(LPVOID lpParam) {
    char* pDriveRoot = (char*)lpParam;
    if (!pDriveRoot) return 0;

    char driveRoot[16];
    lstrcpyA(driveRoot, pDriveRoot);
    LocalFree(pDriveRoot); // 释放参数内存

    // 防抖延迟 1500 毫秒，等待系统完全装载驱动器
    Sleep(1500);

    if (g_backupPath[0] == '\0') {
        Sleep(1000);
        if (g_backupPath[0] == '\0') return 0;
    }

    char volName[128] = {0};
    char fsName[64] = {0};
    if (!GetVolumeInformationA(driveRoot, volName, sizeof(volName), NULL, NULL, NULL, fsName, sizeof(fsName)) || volName[0] == '\0') {
        lstrcpyA(volName, "未命名U盘");
    }

    SYSTEMTIME st;
    GetLocalTime(&st);

    char timeBuf[32];
    wsprintfA(timeBuf, "%04d%02d%02d_%02d%02d%02d",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    char folderName[260];
    wsprintfA(folderName, "%s_%c_%s", timeBuf, driveRoot[0], volName);

    char targetDir[MAX_PATH];
    PathCombine(targetDir, g_backupPath, folderName);

    CreateDirectoryA(targetDir, NULL);

    char balloonMsg[256];
    wsprintfA(balloonMsg, "检测到 U 盘 [%c: %s]，正在静默归档...", driveRoot[0], volName);
    ShowBalloon(g_szAppTitle, balloonMsg, NIIF_INFO);

    // 递归拷贝
    CopyFolderRecursive(driveRoot, targetDir);

    wsprintfA(balloonMsg, "U 盘 [%c: %s] 课件归档已完成！", driveRoot[0], volName);
    ShowBalloon(g_szAppTitle, balloonMsg, NIIF_INFO);

    return 0;
}

// 扫描所有已插入的 U 盘（冷启动 / 手动扫描）
void ScanExistingRemovableDrives(void) {
    char drives[512];
    memset(drives, 0, sizeof(drives));
    DWORD len = GetLogicalDriveStringsA(sizeof(drives) - 1, drives);
    if (len == 0) return;

    char* p = drives;
    while (*p) {
        if (GetDriveTypeA(p) == DRIVE_REMOVABLE) {
            // 排除程序自身所在盘符（防止在 U 盘中运行时自我死循环拷贝）
            if (g_selfDrive == '\0' || ToUpperChar(*p) != ToUpperChar(g_selfDrive)) {
                char* pCopy = (char*)LocalAlloc(LPTR, 16);
                if (pCopy) {
                    lstrcpyA(pCopy, p);
                    CreateThread(NULL, 0, WorkerBackupThread, pCopy, 0, NULL);
                }
            }
        }
        p += lstrlenA(p) + 1;
    }
}

// 窗口过程
LRESULT CALLBACK WndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    switch (uMsg) {
        case WM_CREATE: {
            memset(&g_nid, 0, sizeof(g_nid));
            g_nid.cbSize = sizeof(NOTIFYICONDATAA);
            g_nid.hWnd = hWnd;
            g_nid.uID = 1;
            g_nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO;
            g_nid.uCallbackMessage = WM_TRAYICON;
            g_nid.hIcon = g_hIcon;
            lstrcpyA(g_nid.szTip, "获取Rick课件 - 监控中");
            lstrcpyA(g_nid.szInfoTitle, g_szAppTitle);
            lstrcpyA(g_nid.szInfo, "程序已在后台静默运行，正在监听U盘...");
            g_nid.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIconA(NIM_ADD, &g_nid);
            return 0;
        }

        case WM_TRAYICON: {
            if (lParam == WM_RBUTTONUP) {
                HMENU hMenu = CreatePopupMenu();
                AppendMenuA(hMenu, MF_STRING, IDM_SCAN_NOW, "立即扫描备份当前U盘");
                AppendMenuA(hMenu, MF_STRING, IDM_BROWSE, "设置课件保存文件夹...");
                AppendMenuA(hMenu, MF_STRING, IDM_OPEN, "打开课件保存目录");
                AppendMenuA(hMenu, MF_SEPARATOR, 0, NULL);
                AppendMenuA(hMenu, MF_STRING, IDM_ABOUT, "关于 获取Rick课件");
                AppendMenuA(hMenu, MF_SEPARATOR, 0, NULL);
                AppendMenuA(hMenu, MF_STRING, IDM_EXIT, "退出程序");

                POINT pt;
                GetCursorPos(&pt);
                SetForegroundWindow(hWnd);
                TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.x, pt.y, 0, hWnd, NULL);
                DestroyMenu(hMenu);
            } else if (lParam == WM_LBUTTONDBLCLK) {
                OpenBackupDir();
            }
            return 0;
        }

        case WM_COMMAND: {
            WORD cmdId = LOWORD(wParam);
            switch (cmdId) {
                case IDM_SCAN_NOW:
                    ScanExistingRemovableDrives();
                    break;
                case IDM_BROWSE:
                    BrowseBackupDir(hWnd);
                    break;
                case IDM_OPEN:
                    OpenBackupDir();
                    break;
                case IDM_ABOUT:
                    MessageBoxA(hWnd,
                                "获取Rick课件 (原生 C 语言静态纯净版)\n\n"
                                "版本: v3.0.0-c\n"
                                "架构: 32位 Win32 原生 (兼容 Win7 / Win8 / Win10 / Win11)\n"
                                "特性: 0依赖、自动冷热扫描、自身U盘防回环、静默托盘常驻",
                                g_szAppTitle,
                                MB_OK | MB_ICONINFORMATION);
                    break;
                case IDM_EXIT:
                    PostMessageA(hWnd, WM_DESTROY, 0, 0);
                    break;
            }
            return 0;
        }

        case WM_DEVICECHANGE: {
            if (wParam == DBT_DEVICEARRIVAL) {
                PDEV_BROADCAST_HDR pHdr = (PDEV_BROADCAST_HDR)lParam;
                if (pHdr && pHdr->dbch_devicetype == DBT_DEVTYP_VOLUME) {
                    PDEV_BROADCAST_VOLUME pVol = (PDEV_BROADCAST_VOLUME)lParam;
                    DWORD unitmask = pVol->dbcv_unitmask;
                    for (int i = 0; i < 26; i++) {
                        if (unitmask & (1 << i)) {
                            char driveRoot[8];
                            wsprintfA(driveRoot, "%c:\\", 'A' + i);
                            if (GetDriveTypeA(driveRoot) == DRIVE_REMOVABLE) {
                                if (g_selfDrive == '\0' || ToUpperChar(driveRoot[0]) != ToUpperChar(g_selfDrive)) {
                                    char* pCopy = (char*)LocalAlloc(LPTR, 16);
                                    if (pCopy) {
                                        lstrcpyA(pCopy, driveRoot);
                                        CreateThread(NULL, 0, WorkerBackupThread, pCopy, 0, NULL);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return DefWindowProcA(hWnd, uMsg, wParam, lParam);
        }

        case WM_DESTROY: {
            Shell_NotifyIconA(NIM_DELETE, &g_nid);
            PostQuitMessage(0);
            return 0;
        }

        default:
            return DefWindowProcA(hWnd, uMsg, wParam, lParam);
    }
}

// 主入口
int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow) {
    (void)hPrevInstance;
    (void)lpCmdLine;
    (void)nCmdShow;

    g_hInstance = hInstance;

    // 1. 获取自身所在可执行文件盘符（避免自身防回环拷贝）
    char exePath[MAX_PATH];
    memset(exePath, 0, sizeof(exePath));
    if (GetModuleFileNameA(NULL, exePath, sizeof(exePath)) > 0) {
        if (exePath[1] == ':') {
            g_selfDrive = exePath[0];
        }
    }

    // 2. 单实例检测互斥体
    g_hMutex = CreateMutexA(NULL, TRUE, g_szMutexName);
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        MessageBoxA(NULL,
                    "程序已在后台运行中！\n请查看任务栏右下角系统托盘图标（可能在折叠箭头 ^ 内部）。",
                    g_szAppTitle,
                    MB_OK | MB_ICONINFORMATION);
        ExitProcess(0);
    }

    // 3. 加载配置文件与默认路径
    LoadSettings();

    // 4. 加载应用程序图标
    g_hIcon = LoadIconA(hInstance, MAKEINTRESOURCEA(1));
    if (!g_hIcon) {
        g_hIcon = LoadIconA(NULL, IDI_APPLICATION);
    }

    // 5. 注册隐藏宿主窗口类
    WNDCLASSEXA wc;
    memset(&wc, 0, sizeof(wc));
    wc.cbSize = sizeof(WNDCLASSEXA);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.hIcon = g_hIcon;
    wc.hCursor = LoadCursorA(NULL, IDC_ARROW);
    wc.lpszClassName = g_szClassName;
    wc.hIconSm = g_hIcon;
    RegisterClassExA(&wc);

    // 6. 创建隐藏宿主窗口
    g_hWnd = CreateWindowExA(0, g_szClassName, g_szAppName, 0, 0, 0, 0, 0, NULL, NULL, hInstance, NULL);
    if (!g_hWnd) {
        if (g_hMutex) CloseHandle(g_hMutex);
        ExitProcess(1);
    }

    // 7. 启动时立即执行冷启动扫描（已连接的 U 盘立即归档）
    ScanExistingRemovableDrives();

    // 8. 消息循环
    MSG msg;
    while (GetMessageA(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageA(&msg);
    }

    if (g_hMutex) {
        CloseHandle(g_hMutex);
    }
    ExitProcess((UINT)msg.wParam);
    return 0;
}

// 当使用 -nostdlib 时的入口点
void mainEntry(void) {
    HINSTANCE hInst = GetModuleHandleA(NULL);
    WinMain(hInst, NULL, NULL, SW_SHOWNORMAL);
}
