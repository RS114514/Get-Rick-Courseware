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
        private Thread monitorThread;
        private bool isRunning;
        private HashSet<string> processedDevices = new HashSet<string>();
        private readonly object processedDevicesLock = new object();

        // macOS 磁盘查询缓存，防止频繁执行 diskutil 导致系统卡顿
        private static readonly Dictionary<string, MacDriveCacheEntry> _macDriveCache = new Dictionary<string, MacDriveCacheEntry>();
        private static readonly object _macCacheLock = new object();

        private class MacDriveCacheEntry
        {
            public bool IsUsb { get; set; }
            public string UniqueId { get; set; }
            public string VolumeName { get; set; }
        }

        // 内部委托，允许在单元测试中 Mock 获取磁盘列表和处理逻辑
        internal Func<HashSet<string>> GetDrivesFunc { get; set; }
        internal Action<string> ProcessUSBAction { get; set; }

        public USBMonitor(string path, Action<string> log)
        {
            backupPath = path;
            logCallback = log;
            GetDrivesFunc = GetRemovableDrives;
            ProcessUSBAction = ProcessUSB;
        }

        public void Start()
        {
            isRunning = true;
            monitorThread = new Thread(MonitorLoop);
            monitorThread.IsBackground = true;
            monitorThread.Start();
            logCallback?.Invoke("🔍 监控已启动，等待U盘插入...");
        }

        public void Stop()
        {
            isRunning = false;
            if (monitorThread != null && monitorThread.IsAlive)
            {
                monitorThread.Join(2000);
            }
            logCallback?.Invoke("⏹ 监控已停止");
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
                string targetFolder = Path.Combine(backupPath, folderName);
                
                try
                {
                    Directory.CreateDirectory(targetFolder);
                    logCallback?.Invoke($"📁 创建文件夹: {folderName}");
                    logCallback?.Invoke($"💾 U盘名称: {usbName}");
                    logCallback?.Invoke($"🎯 目标路径: {targetFolder}");
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"❌ 创建文件夹失败: {ex.Message}");
                    return;
                }
                
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
                    
                    logCallback?.Invoke($"✅ 课件获取完成！共复制 {copiedFiles} 个文件");
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"❌ 复制过程出错: {ex.Message}");
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
