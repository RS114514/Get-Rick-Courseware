package main

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

var (
	modKernel32 = syscall.NewLazyDLL("kernel32.dll")
	modUser32   = syscall.NewLazyDLL("user32.dll")
	modShell32  = syscall.NewLazyDLL("shell32.dll")

	procCreateMutexW            = modKernel32.NewProc("CreateMutexW")
	procGetLastError            = modKernel32.NewProc("GetLastError")
	procCloseHandle             = modKernel32.NewProc("CloseHandle")
	procGetLogicalDriveStringsW = modKernel32.NewProc("GetLogicalDriveStringsW")
	procGetDriveTypeW           = modKernel32.NewProc("GetDriveTypeW")
	procGetVolumeInformationW   = modKernel32.NewProc("GetVolumeInformationW")
	procGetModuleHandleW        = modKernel32.NewProc("GetModuleHandleW")

	procRegisterClassExW    = modUser32.NewProc("RegisterClassExW")
	procCreateWindowExW     = modUser32.NewProc("CreateWindowExW")
	procDefWindowProcW      = modUser32.NewProc("DefWindowProcW")
	procGetMessageW         = modUser32.NewProc("GetMessageW")
	procTranslateMessage    = modUser32.NewProc("TranslateMessage")
	procDispatchMessageW    = modUser32.NewProc("DispatchMessageW")
	procPostQuitMessage     = modUser32.NewProc("PostQuitMessage")
	procPostMessageW        = modUser32.NewProc("PostMessageW")
	procMessageBoxW         = modUser32.NewProc("MessageBoxW")
	procCreatePopupMenu     = modUser32.NewProc("CreatePopupMenu")
	procAppendMenuW         = modUser32.NewProc("AppendMenuW")
	procTrackPopupMenu      = modUser32.NewProc("TrackPopupMenu")
	procDestroyMenu         = modUser32.NewProc("DestroyMenu")
	procGetCursorPos        = modUser32.NewProc("GetCursorPos")
	procSetForegroundWindow = modUser32.NewProc("SetForegroundWindow")
	procLoadIconW           = modUser32.NewProc("LoadIconW")
	procLoadCursorW         = modUser32.NewProc("LoadCursorW")

	procShell_NotifyIconW = modShell32.NewProc("Shell_NotifyIconW")
	procShellExecuteW     = modShell32.NewProc("ShellExecuteW")
)

const (
	ERROR_ALREADY_EXISTS = 183
	DRIVE_REMOVABLE      = 2

	WM_CREATE    = 0x0001
	WM_DESTROY   = 0x0002
	WM_COMMAND   = 0x0111
	WM_USER      = 0x0400
	WM_TRAYICON  = WM_USER + 1
	WM_RBUTTONUP = 0x0205
	WM_LDBLCLK   = 0x0203

	NIM_ADD    = 0x00000000
	NIM_MODIFY = 0x00000001
	NIM_DELETE = 0x00000002

	NIF_MESSAGE = 0x00000001
	NIF_ICON    = 0x00000002
	NIF_TIP     = 0x00000004
	NIF_INFO    = 0x00000010

	NIIF_INFO = 0x00000001

	MF_STRING    = 0x00000000
	MF_SEPARATOR = 0x00000800

	IDM_SCAN_NOW = 1001
	IDM_OPEN     = 1002
	IDM_ABOUT    = 1003
	IDM_EXIT     = 1004
)

type NOTIFYICONDATAW struct {
	CbSize           uint32
	HWnd             uintptr
	UID              uint32
	UFlags           uint32
	UCallbackMessage uint32
	HIcon            uintptr
	SzTip            [128]uint16
	DwState          uint32
	DwStateMask      uint32
	SzInfo           [256]uint16
	UTimeoutOrVer    uint32
	SzInfoTitle      [64]uint16
	DwInfoFlags      uint32
	GuidItem         [16]byte
	HBalloonIcon     uintptr
}

type WNDCLASSEXW struct {
	CbSize        uint32
	Style         uint32
	LpfnWndProc   uintptr
	CbClsExtra    int32
	CbWndExtra    int32
	HInstance     uintptr
	HIcon         uintptr
	HCursor       uintptr
	HbrBackground uintptr
	LpszMenuName  *uint16
	LpszClassName *uint16
	HIconSm       uintptr
}

type POINT struct {
	X, Y int32
}

type MSG struct {
	HWnd    uintptr
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	Pt      POINT
}

var (
	gHWnd      uintptr
	gNid       NOTIFYICONDATAW
	gSelfDrive string
	gBackupDir string
	gIniPath   string

	processedLock sync.Mutex
	processedDevs = make(map[string]bool)
)

func utf16Ptr(s string) *uint16 {
	p, _ := syscall.UTF16PtrFromString(s)
	return p
}

func copyUTF16(dst []uint16, src string) {
	chars, _ := syscall.UTF16FromString(src)
	copy(dst, chars)
}

func showBalloon(title, text string) {
	gNid.UFlags = NIF_INFO
	gNid.DwInfoFlags = NIIF_INFO
	copyUTF16(gNid.SzInfoTitle[:], title)
	copyUTF16(gNid.SzInfo[:], text)
	procShell_NotifyIconW.Call(NIM_MODIFY, uintptr(unsafe.Pointer(&gNid)))
}

func openBackupFolder() {
	if gBackupDir == "" {
		return
	}
	os.MkdirAll(gBackupDir, 0755)
	pDir := utf16Ptr(gBackupDir)
	pExplore := utf16Ptr("explore")
	procShellExecuteW.Call(0, uintptr(unsafe.Pointer(pExplore)), uintptr(unsafe.Pointer(pDir)), 0, 0, 1)
}

func loadConfig() {
	exe, err := os.Executable()
	var baseDir string
	if err == nil {
		baseDir = filepath.Dir(exe)
		if len(exe) >= 2 && exe[1] == ':' {
			gSelfDrive = strings.ToUpper(exe[0:1])
		}
	} else {
		baseDir, _ = os.Getwd()
	}

	gIniPath = filepath.Join(baseDir, "RickConfig.ini")
	gBackupDir = filepath.Join(baseDir, "课件备份")

	// 简单读取 ini 文件
	content, err := os.ReadFile(gIniPath)
	if err == nil {
		lines := strings.Split(string(content), "\n")
		for _, line := range lines {
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, "BackupPath=") {
				val := strings.TrimPrefix(line, "BackupPath=")
				val = strings.TrimSpace(val)
				if val != "" {
					gBackupDir = val
				}
			}
		}
	} else {
		// 写入默认配置
		iniContent := fmt.Sprintf("[Settings]\r\nBackupPath=%s\r\n", gBackupDir)
		os.WriteFile(gIniPath, []byte(iniContent), 0644)
	}

	os.MkdirAll(gBackupDir, 0755)
}

func getVolumeLabel(rootPath string) string {
	var volNameBuf [260]uint16
	var fsNameBuf [260]uint16
	pRoot := utf16Ptr(rootPath)

	r, _, _ := procGetVolumeInformationW.Call(
		uintptr(unsafe.Pointer(pRoot)),
		uintptr(unsafe.Pointer(&volNameBuf[0])),
		uintptr(len(volNameBuf)),
		0, 0, 0,
		uintptr(unsafe.Pointer(&fsNameBuf[0])),
		uintptr(len(fsNameBuf)),
	)
	if r != 0 {
		label := syscall.UTF16ToString(volNameBuf[:])
		if label != "" {
			return label
		}
	}
	return "未命名U盘"
}

func shouldSkip(name string) bool {
	if name == "" || name == "." || name == ".." {
		return true
	}
	if strings.HasPrefix(name, ".") {
		return true
	}
	lower := strings.ToLower(name)
	if lower == "system volume information" || lower == "$recycle.bin" {
		return true
	}
	return false
}

func copyFolder(src, dst string) error {
	os.MkdirAll(dst, 0755)
	entries, err := os.ReadDir(src)
	if err != nil {
		return err
	}

	for _, entry := range entries {
		name := entry.Name()
		if shouldSkip(name) {
			continue
		}

		srcPath := filepath.Join(src, name)
		dstPath := filepath.Join(dst, name)

		if entry.IsDir() {
			copyFolder(srcPath, dstPath)
		} else {
			copyFile(srcPath, dstPath)
		}
	}
	return nil
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()

	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	defer out.Close()

	_, err = io.Copy(out, in)
	return err
}

func backupDrive(driveRoot string) {
	// 等待驱动器装载就绪
	time.Sleep(1500 * time.Millisecond)

	label := getVolumeLabel(driveRoot)
	driveLetter := strings.ToUpper(driveRoot[0:1])

	timestamp := time.Now().Format("20060102_150405")
	folderName := fmt.Sprintf("%s_%s_%s", timestamp, driveLetter, label)
	targetDir := filepath.Join(gBackupDir, folderName)

	showBalloon("获取Rick课件", fmt.Sprintf("发现 U 盘 [%s: %s]，正在静默归档课件...", driveLetter, label))

	copyFolder(driveRoot, targetDir)

	showBalloon("获取Rick课件", fmt.Sprintf("U 盘 [%s: %s] 课件归档已完成！", driveLetter, label))
}

func scanRemovableDrives() {
	var buf [512]uint16
	r, _, _ := procGetLogicalDriveStringsW.Call(uintptr(len(buf)-1), uintptr(unsafe.Pointer(&buf[0])))
	if r == 0 {
		return
	}

	raw := syscall.UTF16ToString(buf[:r])
	drives := strings.Split(raw, "\x00")

	for _, d := range drives {
		if d == "" {
			continue
		}
		pD := utf16Ptr(d)
		dt, _, _ := procGetDriveTypeW.Call(uintptr(unsafe.Pointer(pD)))
		if dt == DRIVE_REMOVABLE {
			driveLetter := strings.ToUpper(d[0:1])
			// 排除程序自身所在盘符，防止直接在 U 盘中运行时死循环自我复制
			if gSelfDrive != "" && driveLetter == gSelfDrive {
				continue
			}

			processedLock.Lock()
			if !processedDevs[driveLetter] {
				processedDevs[driveLetter] = true
				processedLock.Unlock()
				go backupDrive(d)
			} else {
				processedLock.Unlock()
			}
		}
	}
}

// 后台定时监控协程（补充硬件消息通知）
func monitorLoop() {
	for {
		scanRemovableDrives()
		time.Sleep(2 * time.Second)
	}
}

func wndProc(hWnd uintptr, uMsg uint32, wParam, lParam uintptr) uintptr {
	switch uMsg {
	case WM_CREATE:
		gNid.CbSize = uint32(unsafe.Sizeof(gNid))
		gNid.HWnd = hWnd
		gNid.UID = 1
		gNid.UFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO
		gNid.UCallbackMessage = WM_TRAYICON

		hIcon, _, _ := procLoadIconW.Call(0, 32512) // IDI_APPLICATION
		gNid.HIcon = hIcon

		copyUTF16(gNid.SzTip[:], "获取Rick课件 (Go极速版) - 监控中")
		copyUTF16(gNid.SzInfoTitle[:], "获取Rick课件")
		copyUTF16(gNid.SzInfo[:], "程序已在后台静默运行，正在实时监控U盘...")
		gNid.DwInfoFlags = NIIF_INFO

		procShell_NotifyIconW.Call(NIM_ADD, uintptr(unsafe.Pointer(&gNid)))
		return 0

	case WM_TRAYICON:
		if lParam == WM_RBUTTONUP {
			hMenu, _, _ := procCreatePopupMenu.Call()
			pScan, _ := syscall.UTF16PtrFromString("立即扫描备份当前U盘")
			pOpen, _ := syscall.UTF16PtrFromString("打开课件保存目录")
			pAbout, _ := syscall.UTF16PtrFromString("关于 获取Rick课件")
			pExit, _ := syscall.UTF16PtrFromString("退出程序")

			procAppendMenuW.Call(hMenu, MF_STRING, IDM_SCAN_NOW, uintptr(unsafe.Pointer(pScan)))
			procAppendMenuW.Call(hMenu, MF_STRING, IDM_OPEN, uintptr(unsafe.Pointer(pOpen)))
			procAppendMenuW.Call(hMenu, MF_SEPARATOR, 0, 0)
			procAppendMenuW.Call(hMenu, MF_STRING, IDM_ABOUT, uintptr(unsafe.Pointer(pAbout)))
			procAppendMenuW.Call(hMenu, MF_SEPARATOR, 0, 0)
			procAppendMenuW.Call(hMenu, MF_STRING, IDM_EXIT, uintptr(unsafe.Pointer(pExit)))

			var pt POINT
			procGetCursorPos.Call(uintptr(unsafe.Pointer(&pt)))
			procSetForegroundWindow.Call(hWnd)
			procTrackPopupMenu.Call(hMenu, 0x0002|0x0020, uintptr(pt.X), uintptr(pt.Y), 0, hWnd, 0)
			procDestroyMenu.Call(hMenu)
		} else if lParam == WM_LDBLCLK {
			openBackupFolder()
		}
		return 0

	case WM_COMMAND:
		switch loword(wParam) {
		case IDM_SCAN_NOW:
			processedLock.Lock()
			processedDevs = make(map[string]bool)
			processedLock.Unlock()
			go scanRemovableDrives()
		case IDM_OPEN:
			openBackupFolder()
		case IDM_ABOUT:
			pTitle, _ := syscall.UTF16PtrFromString("关于 获取Rick课件")
			pText, _ := syscall.UTF16PtrFromString("获取Rick课件 (Go 语言原生静态版)\n\n" +
				"版本: v3.0.0-go\n" +
				"架构: x86 (通用兼容 Win7 / Win8 / Win10 / Win11)\n" +
				"特性: 0依赖、自动冷热扫描、自身U盘防回环、静默托盘常驻")
			procMessageBoxW.Call(hWnd, uintptr(unsafe.Pointer(pText)), uintptr(unsafe.Pointer(pTitle)), 0x00000040)
		case IDM_EXIT:
			procPostMessageW.Call(hWnd, WM_DESTROY, 0, 0)
		}
		return 0

	case WM_DESTROY:
		procShell_NotifyIconW.Call(NIM_DELETE, uintptr(unsafe.Pointer(&gNid)))
		procPostQuitMessage.Call(0)
		return 0
	}

	r, _, _ := procDefWindowProcW.Call(hWnd, uintptr(uMsg), wParam, lParam)
	return r
}

func loword(w uintptr) uint16 {
	return uint16(w & 0xffff)
}

func main() {
	// 1. 单实例互斥体检测
	pMutexName, _ := syscall.UTF16PtrFromString("RickCourseware_Go_SingleInstance")
	hMutex, _, _ := procCreateMutexW.Call(0, 1, uintptr(unsafe.Pointer(pMutexName)))
	lastErr, _, _ := procGetLastError.Call()
	if lastErr == ERROR_ALREADY_EXISTS {
		pTitle, _ := syscall.UTF16PtrFromString("获取Rick课件")
		pMsg, _ := syscall.UTF16PtrFromString("程序已在后台运行中！\n请查看任务栏右下角系统托盘图标（可能在折叠箭头 ^ 内部）。")
		procMessageBoxW.Call(0, uintptr(unsafe.Pointer(pMsg)), uintptr(unsafe.Pointer(pTitle)), 0x00000040)
		return
	}
	defer procCloseHandle.Call(hMutex)

	// 2. 加载配置
	loadConfig()

	// 3. 启动后台监控协程
	go monitorLoop()

	// 4. 创建隐藏窗口与托盘
	hInst, _, _ := procGetModuleHandleW.Call(0)
	className, _ := syscall.UTF16PtrFromString("RickCourseware_GoClass")
	appName, _ := syscall.UTF16PtrFromString("获取Rick课件")

	var wc WNDCLASSEXW
	wc.CbSize = uint32(unsafe.Sizeof(wc))
	wc.LpfnWndProc = syscall.NewCallback(wndProc)
	wc.HInstance = hInst
	wc.LpszClassName = className
	cursor, _, _ := procLoadCursorW.Call(0, 32512)
	wc.HCursor = cursor

	procRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc)))

	hWnd, _, _ := procCreateWindowExW.Call(
		0,
		uintptr(unsafe.Pointer(className)),
		uintptr(unsafe.Pointer(appName)),
		0,
		0, 0, 0, 0,
		0, 0, hInst, 0,
	)
	gHWnd = hWnd

	if gHWnd == 0 {
		return
	}

	// 5. 消息循环
	var msg MSG
	for {
		r, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&msg)))
		procDispatchMessageW.Call(uintptr(unsafe.Pointer(&msg)))
	}
}
