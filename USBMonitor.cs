using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using System.Management;
#endif
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;

namespace USBAutoCopy
{
    public class USBMonitor
    {
        private string backupPath;
        private Action<string> logCallback;
        private Action<string, string> notifyCallback;
        private Thread monitorThread;
        private Thread syncThread;
        private bool isRunning;
        private HashSet<string> processedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object processedDevicesLock = new object();
        private static readonly HashSet<string> activeCopyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object activeCopyLock = new object();
        private static readonly object _syncLock = new object();

        // macOS 磁盘查询缓存，防止频繁执行 diskutil 导致系统卡顿
        private static readonly Dictionary<string, MacDriveCacheEntry> _macDriveCache = new Dictionary<string, MacDriveCacheEntry>();
        private static readonly object _macCacheLock = new object();

        private class MacDriveCacheEntry
        {
            public bool IsUsb { get; set; }
            public string UniqueId { get; set; }
            public string VolumeName { get; set; }
        }

        public const string OFFLINE_NOTIFICATION_MESSAGE = "云上春晖未连接，课件已暂存本地，等待网络恢复自动同步";

        // 内部委托，允许在单元测试中 Mock 获取磁盘列表、路径探测、UNC解析和通知
        internal Func<HashSet<string>> GetDrivesFunc { get; set; }
        internal Action<string> ProcessUSBAction { get; set; }
        internal static Func<string, string> MockResolveNetworkPathFunc { get; set; }
        internal static Func<string, bool> MockIsPathReachableFunc { get; set; }
        internal static Action<string, string> MockNotifyAction { get; set; }

        public string BackupPath
        {
            get => backupPath;
            set => backupPath = value;
        }

        public void UpdateBackupPath(string newPath)
        {
            backupPath = newPath;
            ThreadPool.QueueUserWorkItem(_ => ProbeTargetAtStartup());
        }

        public USBMonitor(string path, Action<string> log, Action<string, string> notify = null)
        {
            backupPath = path;
            logCallback = log;
            notifyCallback = notify;
            GetDrivesFunc = GetRemovableDrives;
            ProcessUSBAction = ProcessUSB;
        }

        public void Start()
        {
            isRunning = true;

            // 开机 / 启动监控时探测春晖 NAS 目标可达性
            ThreadPool.QueueUserWorkItem(_ => ProbeTargetAtStartup());

            monitorThread = new Thread(MonitorLoop);
            monitorThread.IsBackground = true;
            monitorThread.Start();

            syncThread = new Thread(SyncLoop);
            syncThread.IsBackground = true;
            syncThread.Start();

            logCallback?.Invoke("🔍 监控已启动，等待U盘插入...");
        }

        private void ProbeTargetAtStartup()
        {
            try
            {
                if (string.IsNullOrEmpty(backupPath)) return;

                string unc = ResolveNetworkPath(backupPath);
                bool reachable = IsPathReachable(backupPath);
                if (reachable)
                {
                    string info = (!string.Equals(unc, backupPath, StringComparison.OrdinalIgnoreCase))
                        ? $" (真实 UNC: {unc})" : "";
                    logCallback?.Invoke($"🌐 春晖 NAS 存储目标已就绪: {backupPath}{info}");

                    // 启动时若网络可用，立即同步先前离线暂存的历史课件
                    SyncLocalCacheToNas(backupPath);
                }
                else
                {
                    string info = (!string.Equals(unc, backupPath, StringComparison.OrdinalIgnoreCase))
                        ? $" (真实 UNC: {unc})" : "";
                    logCallback?.Invoke($"⚠ 春晖 NAS 存储目标当前离线或无法连通: {backupPath}{info}，系统已就绪本地暂存降级模式");
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"⚠ 启动探测春晖 NAS 状态异常: {ex.Message}");
            }
        }

        public void Stop()
        {
            isRunning = false;
            if (monitorThread != null && monitorThread.IsAlive)
            {
                monitorThread.Join(2000);
            }
            if (syncThread != null && syncThread.IsAlive)
            {
                syncThread.Join(2000);
            }
            logCallback?.Invoke("⏹ 监控已停止");
        }

        private void SyncLoop()
        {
            for (int i = 0; i < 6 && isRunning; i++)
            {
                Thread.Sleep(500);
            }

            while (isRunning)
            {
                try
                {
                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        SyncLocalCacheToNas(backupPath);
                    }
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"⚠ 自动同步异常: {ex.Message}");
                }

                // 后台定时每 30 秒探测春晖 NAS 连通状态并自动同步 (60 * 500ms = 30s)
                for (int i = 0; i < 60 && isRunning; i++)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private void MonitorLoop()
        {
            var previousDrives = GetDrivesFunc();

            while (isRunning)
            {
                try
                {
                    var currentDrives = GetDrivesFunc();

                    foreach (var drive in currentDrives)
                    {
                        if (!previousDrives.Contains(drive))
                        {
                            bool shouldProcess = false;
                            lock (processedDevicesLock)
                            {
                                if (!processedDevices.Contains(drive))
                                {
                                    shouldProcess = true;
                                }
                            }
                            if (shouldProcess)
                            {
                                ThreadPool.QueueUserWorkItem(_ => ProcessUSBAction(drive));
                            }
                        }
                    }

                    foreach (var drive in previousDrives)
                    {
                        if (!currentDrives.Contains(drive))
                        {
                            bool shouldRemove = false;
                            lock (processedDevicesLock)
                            {
                                if (processedDevices.Contains(drive))
                                {
                                    processedDevices.Remove(drive);
                                    shouldRemove = true;
                                }
                            }
                            if (shouldRemove)
                            {
                                logCallback?.Invoke($"💾 U盘已移除: {drive}");
                            }
                        }
                    }

                    previousDrives = currentDrives;
                    Thread.Sleep(2000);
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"⚠ 监控错误: {ex.Message}");
                    Thread.Sleep(5000);
                }
            }
        }

        private HashSet<string> GetRemovableDrives()
        {
            var drives = new HashSet<string>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        if (drive.DriveType == DriveType.Removable && drive.IsReady)
                        {
                            string drivePath = drive.Name.TrimEnd(new char[] { '\\' });
                            if (!string.IsNullOrEmpty(drivePath) && Directory.Exists(drivePath))
                            {
                                drives.Add(drivePath);
                            }
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (Directory.Exists("/Volumes"))
                    {
                        var currentDirs = Directory.GetDirectories("/Volumes");
                        var currentDirSet = new HashSet<string>(currentDirs);

                        // 清理已拔出 U 盘的缓存
                        lock (_macCacheLock)
                        {
                            var keysToRemove = new List<string>();
                            foreach (var key in _macDriveCache.Keys)
                            {
                                if (!currentDirSet.Contains(key))
                                {
                                    keysToRemove.Add(key);
                                }
                            }
                            foreach (var key in keysToRemove)
                            {
                                _macDriveCache.Remove(key);
                            }
                        }

                        // 查询并识别 USB 驱动器
                        foreach (var dir in currentDirs)
                        {
                            var entry = GetOrQueryMacDrive(dir);
                            if (entry.IsUsb)
                            {
                                drives.Add(dir);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"获取驱动器列表错误: {ex.Message}");
            }
            return drives;
        }

        private static MacDriveCacheEntry GetOrQueryMacDrive(string path)
        {
            lock (_macCacheLock)
            {
                if (_macDriveCache.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }

            try
            {
                string info = RunCommand("diskutil", $"info \"{path}\"");
                bool isExternal = false;
                bool isDiskImage = false;
                bool isRemovable = false;
                string uuid = string.Empty;

                if (!string.IsNullOrEmpty(info))
                {
                    using (var reader = new StringReader(info))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (line.StartsWith("Device Location:", StringComparison.OrdinalIgnoreCase))
                            {
                                isExternal = line.Contains("External", StringComparison.OrdinalIgnoreCase);
                            }
                            else if (line.StartsWith("Protocol:", StringComparison.OrdinalIgnoreCase))
                            {
                                isDiskImage = line.Contains("Disk Image", StringComparison.OrdinalIgnoreCase);
                            }
                            else if (line.StartsWith("Removable Media:", StringComparison.OrdinalIgnoreCase))
                            {
                                isRemovable = line.Contains("Removable", StringComparison.OrdinalIgnoreCase);
                            }
                            else if (line.StartsWith("Volume UUID:", StringComparison.OrdinalIgnoreCase))
                            {
                                uuid = line.Substring("Volume UUID:".Length).Trim();
                            }
                        }
                    }
                }

                bool isUsb = isExternal && !isDiskImage && isRemovable;
                string label = Path.GetFileName(path);
                string uniqueId = string.IsNullOrEmpty(uuid) ? label : $"{label}|{uuid}";

                var entry = new MacDriveCacheEntry
                {
                    IsUsb = isUsb,
                    UniqueId = uniqueId,
                    VolumeName = label
                };

                lock (_macCacheLock)
                {
                    _macDriveCache[path] = entry;
                }
                return entry;
            }
            catch
            {
                string label = Path.GetFileName(path);
                return new MacDriveCacheEntry
                {
                    IsUsb = false,
                    UniqueId = label,
                    VolumeName = label
                };
            }
        }

        public static bool IsMacUsbDrive(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return GetOrQueryMacDrive(path).IsUsb;
            }
            return false;
        }

        private string GetUSBName(string driveLetter)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string cleanPath = driveLetter.TrimEnd(new char[] { '\\', '/' });
                    if (cleanPath.Length > 0 && cleanPath[cleanPath.Length - 1] != ':')
                        cleanPath = cleanPath + ":";

                    DriveInfo drive = new DriveInfo(cleanPath);
                    if (!string.IsNullOrEmpty(drive.VolumeLabel))
                    {
                        return drive.VolumeLabel;
                    }
                    
                    try
                    {
                        string[] dirs = Directory.GetDirectories(cleanPath);
                        foreach (string dir in dirs)
                        {
                            string dirName = Path.GetFileName(dir);
                            if (!string.IsNullOrEmpty(dirName) && dirName.Length <= 20 && 
                                dirName != "System Volume Information" && dirName != "$RECYCLE.BIN")
                            {
                                return dirName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"获取U盘目录列表错误: {ex.Message}");
                    }
                    
                    return "未命名U盘";
                }
                else
                {
                    return GetOrQueryMacDrive(driveLetter).VolumeName;
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"获取U盘名称错误: {ex.Message}");
                return "未知U盘";
            }
        }

#if WINDOWS
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(
            string lpRootPathName,
            StringBuilder lpVolumeNameBuffer,
            uint nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer,
            uint nFileSystemNameSize);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WNetGetConnection(
            string lpLocalName,
            StringBuilder lpRemoteName,
            ref int lpnLength);
#endif

        public static string GetVolumeSerial(string driveLetter)
        {
#if WINDOWS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 优先使用 Win32 原生 API，极速且不依赖 WMI 服务
                try
                {
                    string drive = driveLetter.TrimEnd(new char[] { '\\', '/' });
                    if (!drive.EndsWith(":")) drive += ":";
                    string rootPath = drive + "\\";

                    if (GetVolumeInformationW(rootPath, null, 0, out uint serialNumber, out _, out _, null, 0))
                    {
                        if (serialNumber != 0)
                        {
                            return serialNumber.ToString("X8");
                        }
                    }
                }
                catch { }

                // 若 Win32 API 无法获取，降级尝试 WMI 查询
                try
                {
                    string drive = driveLetter.TrimEnd(new char[] { '\\', '/' });
                    if (!drive.EndsWith(":")) drive += ":";
                    using (var searcher = new ManagementObjectSearcher(
                        $"SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID='{drive}'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string serial = obj["VolumeSerialNumber"]?.ToString();
                            if (!string.IsNullOrEmpty(serial)) return serial;
                        }
                    }
                }
                catch { }
            }
#endif
            return "";
        }

        public static string GetDriveUniqueId(string driveLetter)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string clean = driveLetter.TrimEnd(new char[] { '\\', '/' });
                    string root = clean.EndsWith(":") ? clean + "\\" : clean + ":\\";
                    string label = "未命名U盘";
                    try
                    {
                        DriveInfo di = new DriveInfo(root);
                        if (di.IsReady && !string.IsNullOrEmpty(di.VolumeLabel))
                        {
                            label = di.VolumeLabel;
                        }
                    }
                    catch { }

                    string serial = GetVolumeSerial(driveLetter);
                    return string.IsNullOrEmpty(serial) ? label : $"{label}|{serial}";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return GetOrQueryMacDrive(driveLetter).UniqueId;
                }
                else
                {
                    return "未知U盘";
                }
            }
            catch { return "未知U盘"; }
        }

        public static string ResolveNetworkPath(string path)
        {
            if (MockResolveNetworkPathFunc != null)
            {
                return MockResolveNetworkPathFunc(path);
            }

            if (string.IsNullOrEmpty(path)) return path;

            if (path.StartsWith(@"\\") || path.StartsWith("//"))
            {
                return path;
            }

#if WINDOWS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    string root = Path.GetPathRoot(path);
                    if (!string.IsNullOrEmpty(root))
                    {
                        string drive = root.TrimEnd(new char[] { '\\', '/' }).ToUpperInvariant();
                        if (drive.Length == 2 && drive[1] == ':')
                        {
                            int capacity = 1024;
                            StringBuilder sb = new StringBuilder(capacity);
                            int res = WNetGetConnection(drive, sb, ref capacity);
                            if (res == 0 && sb.Length > 0)
                            {
                                string uncRoot = sb.ToString().TrimEnd(new char[] { '\\', '/' });
                                string sub = path.Substring(root.Length).TrimStart(new char[] { '\\', '/' });
                                return string.IsNullOrEmpty(sub) ? uncRoot : Path.Combine(uncRoot, sub);
                            }

                            // 离线时尝试从注册表读取持久映射记录 (HKCU\Network\<盘符>\RemotePath)
                            try
                            {
                                char letter = drive[0];
                                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\" + letter, false))
                                {
                                    string remote = key?.GetValue("RemotePath") as string;
                                    if (!string.IsNullOrEmpty(remote))
                                    {
                                        remote = remote.TrimEnd(new char[] { '\\', '/' });
                                        string sub = path.Substring(root.Length).TrimStart(new char[] { '\\', '/' });
                                        return string.IsNullOrEmpty(sub) ? remote : Path.Combine(remote, sub);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
#endif
            return path;
        }

        public static bool IsNetworkDrive(string path, out string uncPath)
        {
            uncPath = ResolveNetworkPath(path);
            if (!string.IsNullOrEmpty(uncPath) && (uncPath.StartsWith(@"\\") || uncPath.StartsWith("//")))
            {
                return true;
            }

            try
            {
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    DriveInfo di = new DriveInfo(root);
                    if (di.DriveType == DriveType.Network)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        public static bool IsPathReachable(string path)
        {
            if (MockIsPathReachableFunc != null)
            {
                return MockIsPathReachableFunc(path);
            }

            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (Directory.Exists(path)) return true;

                        string unc = ResolveNetworkPath(path);
                        if (!string.Equals(unc, path, StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(unc)) return true;

                            string uncRoot = Path.GetPathRoot(unc);
                            if (!string.IsNullOrEmpty(uncRoot) && Directory.Exists(uncRoot))
                            {
                                return true;
                            }
                        }

                        string root = Path.GetPathRoot(path);
                        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                        {
                            return true;
                        }
                    }
                    catch { }
                    return false;
                });

                if (task.Wait(2000))
                {
                    return task.Result;
                }
            }
            catch { }
            return false;
        }

        public static string GetLocalCacheDirectory()
        {
            // 1. 优先尝试本地运行目录下的「课件备份_暂存」
            try
            {
                string appBase = AppDomain.CurrentDomain.BaseDirectory;
                string localDir = Path.Combine(appBase, "课件备份_暂存");
                if (!Directory.Exists(localDir))
                {
                    Directory.CreateDirectory(localDir);
                }
                string testFile = Path.Combine(localDir, ".test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "1");
                File.Delete(testFile);
                return localDir;
            }
            catch { }

            // 2. 降级尝试 %LOCALAPPDATA%\GetRickCourseware\LocalCache
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string cacheDir = Path.Combine(localAppData, "GetRickCourseware", "LocalCache");
                    if (!Directory.Exists(cacheDir))
                    {
                        Directory.CreateDirectory(cacheDir);
                    }
                    return cacheDir;
                }
            }
            catch { }

            string tempCache = Path.Combine(Path.GetTempPath(), "RickLocalCache");
            if (!Directory.Exists(tempCache))
            {
                Directory.CreateDirectory(tempCache);
            }
            return tempCache;
        }

        public static List<string> GetAllPossibleCacheDirectories()
        {
            var list = new List<string>();
            try
            {
                string localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "课件备份_暂存");
                if (Directory.Exists(localDir) && !list.Contains(localDir)) list.Add(localDir);
            }
            catch { }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string cacheDir = Path.Combine(localAppData, "GetRickCourseware", "LocalCache");
                    if (Directory.Exists(cacheDir) && !list.Contains(cacheDir)) list.Add(cacheDir);
                }
            }
            catch { }

            try
            {
                string legacyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LocalCache");
                if (Directory.Exists(legacyDir) && !list.Contains(legacyDir)) list.Add(legacyDir);
            }
            catch { }

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "RickLocalCache");
                if (Directory.Exists(tempDir) && !list.Contains(tempDir)) list.Add(tempDir);
            }
            catch { }

            return list;
        }

        public static bool HasAnyCachedCourseware()
        {
            try
            {
                var cacheRoots = GetAllPossibleCacheDirectories();
                foreach (string root in cacheRoots)
                {
                    if (Directory.Exists(root))
                    {
                        var dirs = Directory.GetDirectories(root);
                        if (dirs != null && dirs.Length > 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public void SendNotification(string title, string message)
        {
            if (MockNotifyAction != null)
            {
                MockNotifyAction(title, message);
                return;
            }

            bool delivered = false;
            if (notifyCallback != null)
            {
                try
                {
                    notifyCallback(title, message);
                    delivered = true;
                }
                catch { }
            }

            if (!delivered && (Environment.OSVersion.Platform == PlatformID.Win32NT))
            {
                ShowWindowsToastNotification(title, message);
            }
        }

        public static void ShowWindowsToastNotification(string title, string message)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string safeTitle = (title ?? "获取Rick课件").Replace("'", "''").Replace("\"", "`\"");
                    string safeMsg = (message ?? "").Replace("'", "''").Replace("\"", "`\"");

                    // 使用 Windows 内置注册的 PowerShell AUMID，确保在 Windows 10/11 无需额外预装快捷方式即可稳定展示系统 Toast
                    string psScript =
                        "$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]; " +
                        "$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); " +
                        "$textNodes = $template.GetElementsByTagName('text'); " +
                        $"$textNodes.Item(0).AppendChild($template.CreateTextNode('{safeTitle}')) > $null; " +
                        $"$textNodes.Item(1).AppendChild($template.CreateTextNode('{safeMsg}')) > $null; " +
                        $"$toast = [Windows.UI.Notifications.ToastNotification]::new($template); " +
                        "$app = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe'; " +
                        "try { [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show($toast); } catch { try { [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('RS114514.GetRickCourseware').Show($toast); } catch { } }";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{psScript}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        p?.WaitForExit(4000);
                    }
                }
                catch { }
            });
        }

        public int SyncLocalCacheToNas(string destinationBase)
        {
            if (string.IsNullOrEmpty(destinationBase)) return 0;
            if (!HasAnyCachedCourseware()) return 0;
            if (!IsPathReachable(destinationBase)) return 0;

            lock (_syncLock)
            {
                var cacheRoots = GetAllPossibleCacheDirectories();
                if (cacheRoots.Count == 0) return 0;

                string resolvedDest = destinationBase;
                string unc = ResolveNetworkPath(resolvedDest);
                if (!string.IsNullOrEmpty(unc) && (Directory.Exists(unc) || IsPathReachable(unc)))
                {
                    resolvedDest = unc;
                }

                if (!Directory.Exists(resolvedDest))
                {
                    try
                    {
                        Directory.CreateDirectory(resolvedDest);
                    }
                    catch
                    {
                        if (!string.IsNullOrEmpty(unc) && !string.Equals(unc, resolvedDest, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                Directory.CreateDirectory(unc);
                                resolvedDest = unc;
                            }
                            catch
                            {
                                return 0;
                            }
                        }
                        else
                        {
                            return 0;
                        }
                    }
                }

                string fullResolvedDest = Path.GetFullPath(resolvedDest).TrimEnd(new char[] { '\\', '/' });

                int syncedFoldersCount = 0;
                foreach (string localCache in cacheRoots)
                {
                    if (!Directory.Exists(localCache)) continue;

                    string fullLocalCache = Path.GetFullPath(localCache).TrimEnd(new char[] { '\\', '/' });
                    // 如果目标目录与当前暂存目录为同一路径，跳过避免自身迁移引发异常
                    if (string.Equals(fullLocalCache, fullResolvedDest, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] cacheDirs;
                    try
                    {
                        cacheDirs = Directory.GetDirectories(localCache);
                    }
                    catch
                    {
                        continue;
                    }

                    if (cacheDirs == null || cacheDirs.Length == 0) continue;

                    foreach (string dir in cacheDirs)
                    {
                        string normalizedDir = Path.GetFullPath(dir).TrimEnd(new char[] { '\\', '/' });
                        lock (activeCopyLock)
                        {
                            if (activeCopyFolders.Contains(normalizedDir)) continue;
                        }

                        string folderName = Path.GetFileName(dir);
                        string destFolder = Path.Combine(resolvedDest, folderName);

                        try
                        {
                            MoveOrMergeDirectory(dir, destFolder);
                            syncedFoldersCount++;
                            logCallback?.Invoke($"☁️ 本地暂存课件已同步至春晖 NAS: {folderName}");
                        }
                        catch (Exception ex)
                        {
                            logCallback?.Invoke($"⚠ 同步暂存课件 {folderName} 失败: {ex.Message}");
                        }
                    }
                }

                if (syncedFoldersCount > 0)
                {
                    string msg = $"云上春晖已连接，本地暂存课件已成功同步至网络盘（共 {syncedFoldersCount} 个课件包）";
                    logCallback?.Invoke($"🎉 本地暂存课件同步完成，共迁移 {syncedFoldersCount} 个课件包至春晖 NAS");
                    SendNotification("获取Rick课件", msg);
                }

                return syncedFoldersCount;
            }
        }

        public static void MoveOrMergeDirectory(string sourceDir, string destDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(destDir)) return;
            if (!Directory.Exists(sourceDir)) return;

            string fullSource = Path.GetFullPath(sourceDir).TrimEnd(new char[] { '\\', '/' });
            string fullDest = Path.GetFullPath(destDir).TrimEnd(new char[] { '\\', '/' });

            // 避免源路径与目标路径完全相同时删除原文件造成数据丢失
            if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 避免目标路径是源路径的子目录导致无限递归
            if (fullDest.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(fullSource + "/", StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(fullSource + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destDir, fileName);

                string fullFile = Path.GetFullPath(file);
                string fullDestFile = Path.GetFullPath(destFile);
                if (string.Equals(fullFile, fullDestFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }
                    File.Move(file, destFile);
                }
                catch
                {
                    File.Copy(file, destFile, true);
                    File.Delete(file);
                }
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(dir);
                string destSubDir = Path.Combine(destDir, dirName);
                MoveOrMergeDirectory(dir, destSubDir);
            }

            try
            {
                if (Directory.Exists(sourceDir) && Directory.GetFileSystemEntries(sourceDir).Length == 0)
                {
                    Directory.Delete(sourceDir, true);
                }
            }
            catch { }
        }

        private static string RunCommand(string command, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        process.WaitForExit(3000);
                        return process.StandardOutput.ReadToEnd();
                    }
                }
            }
            catch {}
            return string.Empty;
        }

#nullable enable annotations
        public static string SanitizeFolderName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "未命名";

            char[] windowsInvalidChars = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            foreach (char c in windowsInvalidChars)
            {
                name = name.Replace(c, '_');
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                name = name.Replace(c, '_');
            }
            
            name = name.Trim();
            if (string.IsNullOrEmpty(name))
                return "未命名";
                
            if (name.Length > 30)
                name = name.Substring(0, 30).Trim();
                
            if (string.IsNullOrEmpty(name))
                return "未命名";
                
            return name;
        }
#nullable restore

        private void ProcessUSB(string driveLetter)
        {
            try
            {
                string cleanDrive = driveLetter;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string cleanDriveTemp = driveLetter.TrimEnd(new char[] { '\\', '/' });
                    if (cleanDriveTemp.Length > 0 && cleanDriveTemp[cleanDriveTemp.Length - 1] != ':')
                        cleanDriveTemp = cleanDriveTemp + ":";
                    cleanDrive = cleanDriveTemp + "\\";
                }

                logCallback?.Invoke($"🔌 检测到U盘插入: {cleanDrive}");
                
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(500);
                    if (Directory.Exists(cleanDrive))
                        break;
                }

                if (!Directory.Exists(cleanDrive))
                {
                    logCallback?.Invoke($"⚠ U盘未就绪: {cleanDrive}");
                    return;
                }

                string usbName = GetUSBName(cleanDrive);
                string driveLetterOnly = cleanDrive;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    driveLetterOnly = cleanDrive.TrimEnd(new char[] { '\\' });
                }
                string uniqueId = USBMonitor.GetDriveUniqueId(cleanDrive);

                var blocked = Properties.Settings.Default.GetBlockedList();
                if (blocked.Contains(uniqueId) || blocked.Contains(driveLetterOnly))
                {
                    logCallback?.Invoke($"🚫 U盘已屏蔽，跳过: {driveLetterOnly} ({usbName})");
                    lock (processedDevicesLock) { processedDevices.Add(driveLetter); }
                    return;
                }

                string sanitizedUSBName = SanitizeFolderName(usbName);
                
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string driveIdForFolder = driveLetterOnly;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    driveIdForFolder = Path.GetFileName(driveLetterOnly);
                }
                string folderName = $"{timestamp}_{driveIdForFolder}_{sanitizedUSBName}";
                
                folderName = SanitizeFolderName(folderName);

                string effectiveTargetBase;
                bool isLocalCache = false;

                if (IsPathReachable(backupPath))
                {
                    effectiveTargetBase = backupPath;
                    string unc = ResolveNetworkPath(effectiveTargetBase);
                    if (!string.IsNullOrEmpty(unc) && (Directory.Exists(unc) || IsPathReachable(unc)))
                    {
                        effectiveTargetBase = unc;
                    }
                }
                else
                {
                    isLocalCache = true;
                    effectiveTargetBase = GetLocalCacheDirectory();
                    logCallback?.Invoke($"⚡ 春晖 NAS 离线或无法连通，自动降级切换至本地暂存目录: {effectiveTargetBase}");
                    logCallback?.Invoke("📌 " + OFFLINE_NOTIFICATION_MESSAGE);
                    SendNotification("获取Rick课件", OFFLINE_NOTIFICATION_MESSAGE);
                }

                string targetFolder = Path.Combine(effectiveTargetBase, folderName);
                string normalizedTarget = Path.GetFullPath(targetFolder).TrimEnd(new char[] { '\\', '/' });

                lock (activeCopyLock)
                {
                    activeCopyFolders.Add(normalizedTarget);
                }

                try
                {
                    if (!isLocalCache)
                    {
                        try
                        {
                            Directory.CreateDirectory(targetFolder);
                        }
                        catch (Exception ex)
                        {
                            logCallback?.Invoke($"⚠ 创建目标目录失败 ({ex.Message})，自动降级切换至本地暂存目录");
                            isLocalCache = true;
                            effectiveTargetBase = GetLocalCacheDirectory();
                            lock (activeCopyLock)
                            {
                                activeCopyFolders.Remove(normalizedTarget);
                                targetFolder = Path.Combine(effectiveTargetBase, folderName);
                                normalizedTarget = Path.GetFullPath(targetFolder).TrimEnd(new char[] { '\\', '/' });
                                activeCopyFolders.Add(normalizedTarget);
                            }
                            Directory.CreateDirectory(targetFolder);
                            logCallback?.Invoke("📌 " + OFFLINE_NOTIFICATION_MESSAGE);
                            SendNotification("获取Rick课件", OFFLINE_NOTIFICATION_MESSAGE);
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(targetFolder);
                    }

                    logCallback?.Invoke($"📁 创建文件夹: {folderName}");
                    logCallback?.Invoke($"💾 U盘名称: {usbName}");
                    logCallback?.Invoke($"🎯 目标路径: {targetFolder}");

                    int totalFiles = 0;
                    int copiedFiles = 0;

                    try
                    {
                        totalFiles = CountFiles(cleanDrive);
                        logCallback?.Invoke($"📊 共发现 {totalFiles} 个文件，开始复制...");

                        CopyDirectory(cleanDrive, targetFolder, ref copiedFiles, totalFiles);

                        lock (processedDevicesLock)
                        {
                            processedDevices.Add(driveLetter);
                        }

                        if (isLocalCache)
                        {
                            logCallback?.Invoke($"✅ 本地暂存完成！共复制 {copiedFiles} 个文件，等待网络恢复自动同步");
                        }
                        else
                        {
                            logCallback?.Invoke($"✅ 课件获取完成！共复制 {copiedFiles} 个文件至春晖 NAS 目标目录");
                        }
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"❌ 复制过程出错: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"❌ 创建文件夹失败: {ex.Message}");
                    return;
                }
                finally
                {
                    lock (activeCopyLock)
                    {
                        activeCopyFolders.Remove(normalizedTarget);
                    }
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"❌ 处理U盘时出错: {ex.Message}");
            }
        }

        private int CountFiles(string path)
        {
            int count = 0;
            try
            {
                if (!Directory.Exists(path))
                    return 0;
                    
                string[] files = Directory.GetFiles(path);
                count += files.Length;
                
                string[] directories = Directory.GetDirectories(path);
                foreach (string dir in directories)
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName != "System Volume Information" && 
                        dirName != "$RECYCLE.BIN" &&
                        !dirName.StartsWith("."))
                    {
                        count += CountFiles(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"统计文件数错误: {ex.Message}");
            }
            return count;
        }

        private void CopyDirectory(string sourceDir, string destDir, ref int copiedCount, int totalFiles)
        {
            try
            {
                if (!Directory.Exists(sourceDir))
                    return;
                    
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                string[] files = Directory.GetFiles(sourceDir);
                foreach (string file in files)
                {
                    try
                    {
                        string fileName = Path.GetFileName(file);
                        if (string.IsNullOrEmpty(fileName))
                            continue;
                            
                        string destFile = Path.Combine(destDir, fileName);
                        
                        if (fileName.StartsWith(".") || fileName.StartsWith("._"))
                        {
                            continue;
                        }

                        if (IsFileLocked(file))
                        {
                            logCallback?.Invoke($"⚠ 文件被占用，跳过: {fileName}");
                            continue;
                        }
                        
                        File.Copy(file, destFile, true);
                        copiedCount++;
                        
                        if (totalFiles > 0 && copiedCount % 10 == 0)
                        {
                            int percent = (copiedCount * 100 / totalFiles);
                            logCallback?.Invoke($"📥 复制进度: {copiedCount}/{totalFiles} ({percent}%)");
                        }
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"⚠ 复制失败 {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                string[] directories = Directory.GetDirectories(sourceDir);
                foreach (string subDir in directories)
                {
                    string dirName = Path.GetFileName(subDir);
                    
                    if (dirName == "System Volume Information" || 
                        dirName == "$RECYCLE.BIN" || 
                        dirName.StartsWith("."))
                        continue;
                    
                    string destSubDir = Path.Combine(destDir, dirName);
                    CopyDirectory(subDir, destSubDir, ref copiedCount, totalFiles);
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"⚠ 目录复制错误: {ex.Message}");
            }
        }

        private bool IsFileLocked(string filePath)
        {
            try
            {
                using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
