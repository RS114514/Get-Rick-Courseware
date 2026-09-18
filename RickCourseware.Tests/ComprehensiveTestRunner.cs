using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using USBAutoCopy;
using USBAutoCopy.Properties;

namespace RickCourseware.Tests
{
    public static class ComprehensiveTestRunner
    {
        private static int passed = 0;
        private static int failed = 0;

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Assertion Failed: " + message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string message = "")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception($"Assertion Failed: Expected [{expected}], but got [{actual}]. {message}");
            }
        }

        private static void RunTest(string testName, Action testAction)
        {
            try
            {
                testAction();
                passed++;
                Console.WriteLine($"[PASS] {testName}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] {testName}: {ex.Message}");
            }
        }

        public static int Main()
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  Rick Courseware 综合自动化测试套件  ");
            Console.WriteLine("========================================");

            // 1. SanitizeFolderNameTests
            RunTest("SanitizeFolderName_NormalAndEdgeCases", () =>
            {
                AssertEqual("未命名", USBMonitor.SanitizeFolderName(null));
                AssertEqual("未命名", USBMonitor.SanitizeFolderName(""));
                AssertEqual("未命名", USBMonitor.SanitizeFolderName("   "));
                AssertEqual("正常名称", USBMonitor.SanitizeFolderName("正常名称"));
                AssertEqual("名称 带有 空格", USBMonitor.SanitizeFolderName("名称 带有 空格"));
                AssertEqual("带有非法字符_________", USBMonitor.SanitizeFolderName("带有非法字符\\/:*?\"<>|"));
                AssertEqual("超长文件名1234567890123456789012345", USBMonitor.SanitizeFolderName("超长文件名123456789012345678901234567890这部分应该被截断"));
                AssertEqual("超长文件名且截断后末尾是空格", USBMonitor.SanitizeFolderName("超长文件名且截断后末尾是空格                  "));
                AssertEqual("首尾带空格名称", USBMonitor.SanitizeFolderName("   首尾带空格名称   "));
                AssertEqual("a_b_c_d", USBMonitor.SanitizeFolderName("a:b\\c/d"));
            });

            // 2. SettingsTests
            RunTest("Settings_BackupPathAndAutoStart", () =>
            {
                string testPath = @"Z:\春晖课件\高一12班";
                Settings.Default.BackupPath = testPath;
                AssertEqual(testPath, Settings.Default.BackupPath);

                Settings.Default.AutoStart = true;
                AssertEqual(true, Settings.Default.AutoStart);
                Settings.Default.AutoStart = false;
                AssertEqual(false, Settings.Default.AutoStart);

                Settings.Default.BlockedDrives = "";
                AssertEqual(0, Settings.Default.GetBlockedList().Count);
                Settings.Default.AddBlocked("U_Disk_1");
                Settings.Default.AddBlocked("U_Disk_2");
                AssertEqual(2, Settings.Default.GetBlockedList().Count);
                Assert(Settings.Default.GetBlockedList().Contains("U_Disk_1"), "Contains U_Disk_1");
                Settings.Default.RemoveBlocked("U_Disk_1");
                AssertEqual(1, Settings.Default.GetBlockedList().Count);
                Assert(!Settings.Default.GetBlockedList().Contains("U_Disk_1"), "Removed U_Disk_1");
            });

            // 3. USBMonitorTests (startup drive ignore + insertion detection)
            RunTest("USBMonitor_IgnoreInitialDrivesAndDetectNew", () =>
            {
                var processedDrives = new List<string>();
                var logs = new List<string>();
                var drives = new HashSet<string> { "/dev/driveD" };

                var monitor = new USBMonitor("/tmp/backup_test", (msg) => logs.Add(msg));
                monitor.GetDrivesFunc = () =>
                {
                    lock (drives) { return new HashSet<string>(drives); }
                };
                monitor.ProcessUSBAction = (drive) =>
                {
                    lock (processedDrives) { processedDrives.Add(drive); }
                };

                monitor.Start();
                Thread.Sleep(500);

                lock (processedDrives)
                {
                    AssertEqual(0, processedDrives.Count, "Initial drive should be ignored");
                }

                lock (drives) { drives.Add("/dev/driveE"); }
                Thread.Sleep(2500);

                lock (processedDrives)
                {
                    AssertEqual(1, processedDrives.Count, "New drive should be processed");
                    Assert(processedDrives.Contains("/dev/driveE"), "Processed drive E");
                    Assert(!processedDrives.Contains("/dev/driveD"), "Did not process drive D");
                }

                monitor.Stop();
            });

            // 4. Test Local Cache Directory creation
            RunTest("USBMonitor_LocalCacheDirectoryValid", () =>
            {
                string cacheDir = USBMonitor.GetLocalCacheDirectory();
                Assert(!string.IsNullOrEmpty(cacheDir), "Cache directory should not be empty");
                Assert(Directory.Exists(cacheDir), "Cache directory should exist on disk");
            });

            // 5. Test Path Reachability & Offline Detection
            RunTest("USBMonitor_PathReachability", () =>
            {
                string validDir = Path.Combine(Path.GetTempPath(), "RickReachableTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(validDir);
                try
                {
                    Assert(USBMonitor.IsPathReachable(validDir), "Existing directory should be reachable");
                    
                    string validSubDir = Path.Combine(validDir, "Sub1", "Sub2");
                    Assert(USBMonitor.IsPathReachable(validSubDir), "Subdirectory under reachable root should be reachable");

                    string unreachablePath = @"Q:\NonExistentDrive\RickFolder";
                    Assert(!USBMonitor.IsPathReachable(unreachablePath), "Non-existent drive root should not be reachable");
                }
                finally
                {
                    if (Directory.Exists(validDir)) Directory.Delete(validDir, true);
                }
            });

            // 6. Test Local Cache & Offline USB Copy + Notification Text
            RunTest("USBMonitor_OfflineLocalCachingAndNotification", () =>
            {
                string offlineTarget = @"X:\UnreachableNas\Courseware";
                var logs = new List<string>();
                string notifiedTitle = null;
                string notifiedMessage = null;

                var monitor = new USBMonitor(offlineTarget, (msg) => logs.Add(msg), (title, msg) =>
                {
                    notifiedTitle = title;
                    notifiedMessage = msg;
                });

                // 创建模拟 U 盘目录和文件
                string fakeUsbDir = Path.Combine(Path.GetTempPath(), "FakeUSB_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fakeUsbDir);
                string testFile = Path.Combine(fakeUsbDir, "课件第一讲.pptx");
                File.WriteAllText(testFile, "课件演示数据");

                try
                {
                    // 调用私有/内部 ProcessUSBAction
                    monitor.ProcessUSBAction(fakeUsbDir);

                    // 验证通知内容完全一致
                    AssertEqual("获取Rick课件", notifiedTitle, "Notification title matches");
                    AssertEqual("云上春晖未连接，课件已暂存本地，等待网络恢复自动同步", notifiedMessage, "Notification message matches exactly");

                    // 验证课件确已暂存到 LocalCache
                    string cacheDir = USBMonitor.GetLocalCacheDirectory();
                    string[] subDirs = Directory.GetDirectories(cacheDir);
                    Assert(subDirs.Length > 0, "Local cache should contain cached courseware folder");

                    // 验证文件内容在缓存中完整
                    bool foundFile = false;
                    foreach (var sdir in subDirs)
                    {
                        string target = Path.Combine(sdir, "课件第一讲.pptx");
                        if (File.Exists(target))
                        {
                            foundFile = true;
                            AssertEqual("课件演示数据", File.ReadAllText(target), "Cached file content matches");
                            break;
                        }
                    }
                    Assert(foundFile, "Target file should exist inside local cache");
                }
                finally
                {
                    if (Directory.Exists(fakeUsbDir)) Directory.Delete(fakeUsbDir, true);
                }
            });

            // 7. Test Background Sync from Local Cache to NAS when reachable
            RunTest("USBMonitor_SyncLocalCacheToNas", () =>
            {
                string fakeNasDir = Path.Combine(Path.GetTempPath(), "FakeNas_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fakeNasDir);

                string notifiedTitle = null;
                string notifiedMessage = null;
                var logs = new List<string>();

                var monitor = new USBMonitor(fakeNasDir, (msg) => logs.Add(msg), (title, msg) =>
                {
                    notifiedTitle = title;
                    notifiedMessage = msg;
                });

                // 确保存储目录有缓存
                string cacheDir = USBMonitor.GetLocalCacheDirectory();
                string testBatchFolder = Path.Combine(cacheDir, "20260918_SyncTest_Folder");
                if (!Directory.Exists(testBatchFolder)) Directory.CreateDirectory(testBatchFolder);
                File.WriteAllText(Path.Combine(testBatchFolder, "同步测试课件.pdf"), "PDF内容12345");

                try
                {
                    int synced = monitor.SyncLocalCacheToNas(fakeNasDir);
                    Assert(synced > 0, "Should have synced at least 1 folder");

                    // 验证已迁移至 NAS 目录
                    string migratedFile = Path.Combine(fakeNasDir, "20260918_SyncTest_Folder", "同步测试课件.pdf");
                    Assert(File.Exists(migratedFile), "File should be migrated to fake NAS");
                    AssertEqual("PDF内容12345", File.ReadAllText(migratedFile), "Migrated file content intact");

                    // 验证本地暂存目录对应批次已被清理
                    Assert(!Directory.Exists(testBatchFolder), "Local cache folder should have been deleted after migration");

                    // 验证发出恢复同步成功的托盘通知
                    AssertEqual("获取Rick课件", notifiedTitle, "Sync notification title");
                    Assert(notifiedMessage != null && notifiedMessage.Contains("云上春晖已连接，本地暂存课件已成功同步至网络盘"), "Sync notification message");
                }
                finally
                {
                    if (Directory.Exists(fakeNasDir)) Directory.Delete(fakeNasDir, true);
                    if (Directory.Exists(testBatchFolder)) Directory.Delete(testBatchFolder, true);
                }
            });

            // 8. Test DpiScaler Scaling Math (100%, 200%, 300%)
            RunTest("DpiScaler_ProportionalScalingVerification", () =>
            {
                var baseFormSize = new Size(680, 630);
                var lblBounds = new Rectangle(20, 20, 120, 25);
                float lblFontSize = 10f;
                var txtBounds = new Rectangle(150, 18, 400, 25);
                float txtFontSize = 9f;

                // 测试 200% DPI (4K 屏幕常见比例)
                float scale200 = 2.0f;
                var formSize200 = DpiScaler.CalculateScaledSize(baseFormSize, scale200);
                var lblBounds200 = DpiScaler.CalculateScaledBounds(lblBounds, scale200);
                float lblFont200 = DpiScaler.CalculateScaledFontSize(lblFontSize, scale200);
                var txtBounds200 = DpiScaler.CalculateScaledBounds(txtBounds, scale200);
                float txtFont200 = DpiScaler.CalculateScaledFontSize(txtFontSize, scale200);

                AssertEqual(1360, formSize200.Width, "200% ClientSize Width");
                AssertEqual(1260, formSize200.Height, "200% ClientSize Height");
                AssertEqual(40, lblBounds200.Left, "200% Label Left");
                AssertEqual(40, lblBounds200.Top, "200% Label Top");
                AssertEqual(240, lblBounds200.Width, "200% Label Width");
                AssertEqual(50, lblBounds200.Height, "200% Label Height");
                AssertEqual(20f, lblFont200, "200% Label Font Size");

                AssertEqual(300, txtBounds200.Left, "200% TextBox Left");
                AssertEqual(36, txtBounds200.Top, "200% TextBox Top");
                AssertEqual(800, txtBounds200.Width, "200% TextBox Width");
                AssertEqual(50, txtBounds200.Height, "200% TextBox Height");
                AssertEqual(18f, txtFont200, "200% TextBox Font Size");

                // 测试 300% DPI (4K 教学一体机超高缩放比例)
                float scale300 = 3.0f;
                var formSize300 = DpiScaler.CalculateScaledSize(baseFormSize, scale300);
                var lblBounds300 = DpiScaler.CalculateScaledBounds(lblBounds, scale300);
                float lblFont300 = DpiScaler.CalculateScaledFontSize(lblFontSize, scale300);
                var txtBounds300 = DpiScaler.CalculateScaledBounds(txtBounds, scale300);
                float txtFont300 = DpiScaler.CalculateScaledFontSize(txtFontSize, scale300);

                AssertEqual(2040, formSize300.Width, "300% ClientSize Width");
                AssertEqual(1890, formSize300.Height, "300% ClientSize Height");
                AssertEqual(60, lblBounds300.Left, "300% Label Left");
                AssertEqual(60, lblBounds300.Top, "300% Label Top");
                AssertEqual(360, lblBounds300.Width, "300% Label Width");
                AssertEqual(75, lblBounds300.Height, "300% Label Height");
                AssertEqual(30f, lblFont300, "300% Label Font Size");

                AssertEqual(450, txtBounds300.Left, "300% TextBox Left");
                AssertEqual(54, txtBounds300.Top, "300% TextBox Top");
                AssertEqual(1200, txtBounds300.Width, "300% TextBox Width");
                AssertEqual(75, txtBounds300.Height, "300% TextBox Height");
                AssertEqual(27f, txtFont300, "300% TextBox Font Size");

                // 还原 100%
                float scale100 = 1.0f;
                var formSize100 = DpiScaler.CalculateScaledSize(baseFormSize, scale100);
                var lblBounds100 = DpiScaler.CalculateScaledBounds(lblBounds, scale100);
                float lblFont100 = DpiScaler.CalculateScaledFontSize(lblFontSize, scale100);

                AssertEqual(680, formSize100.Width, "100% ClientSize Width");
                AssertEqual(630, formSize100.Height, "100% ClientSize Height");
                AssertEqual(20, lblBounds100.Left, "100% Label Left");
                AssertEqual(20, lblBounds100.Top, "100% Label Top");
                AssertEqual(120, lblBounds100.Width, "100% Label Width");
                AssertEqual(25, lblBounds100.Height, "100% Label Height");
                AssertEqual(10f, lblFont100, "100% Label Font Size");
            });

            // 9. Test Preservation of Offline NAS Path in Settings
            RunTest("Settings_OfflineNasPathPreserved", () =>
            {
                string offlineNasPath = @"Z:\春晖云盘\高一12班课件";
                Settings.Default.BackupPath = offlineNasPath;
                Settings.Save();

                // 验证设置不依赖该路径当前是否存在
                Assert(!Directory.Exists(offlineNasPath), "Network drive is offline/non-existent");
                AssertEqual(offlineNasPath, Settings.Default.BackupPath, "Backup path must be preserved exactly");
            });

            // 10. Test DpiScaler FormFont and Dynamic Shrink (200% down to 100%)
            RunTest("DpiScaler_FormFontAndDynamicShrink", () =>
            {
                var baseFormSize = new Size(680, 630);
                float baseFontSize = 9f;

                // 放大至 200%
                var size200 = DpiScaler.CalculateScaledSize(baseFormSize, 2.0f);
                float font200 = DpiScaler.CalculateScaledFontSize(baseFontSize, 2.0f);
                AssertEqual(1360, size200.Width, "200% Width");
                AssertEqual(1260, size200.Height, "200% Height");
                AssertEqual(18f, font200, "200% Form Font Size");

                // 缩小回 100% (验证缩小算法)
                var size100 = DpiScaler.CalculateScaledSize(baseFormSize, 1.0f);
                float font100 = DpiScaler.CalculateScaledFontSize(baseFontSize, 1.0f);
                AssertEqual(680, size100.Width, "100% Width after downscale");
                AssertEqual(630, size100.Height, "100% Height after downscale");
                AssertEqual(9f, font100, "100% Form Font Size restored");
            });

            // 11. Test HasAnyCachedCourseware and Sync Bypass when empty
            RunTest("USBMonitor_HasAnyCachedCourseware_And_EmptySyncBypass", () =>
            {
                // 清理所有测试可能残留的暂存
                var roots = USBMonitor.GetAllPossibleCacheDirectories();
                foreach (var r in roots)
                {
                    if (Directory.Exists(r))
                    {
                        foreach (var d in Directory.GetDirectories(r))
                        {
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }
                }

                Assert(!USBMonitor.HasAnyCachedCourseware(), "No cached courseware initially");

                var monitor = new USBMonitor(@"\\non-existent-server\share", null, null);
                // 当没有暂存时，SyncLocalCacheToNas 应当立即返回 0 且不探测网络
                int synced = monitor.SyncLocalCacheToNas(@"\\non-existent-server\share");
                AssertEqual(0, synced, "Should return 0 when no cache to sync");
            });

            // 12. Test Exactly Single Notification on Offline USB Insertion (No Duplicates)
            RunTest("USBMonitor_SingleNotificationDelivery_NoDuplicates", () =>
            {
                int notifyCount = 0;
                string lastTitle = null;
                string lastMessage = null;

                var monitor = new USBMonitor(@"X:\OfflineServer\Target", null, (t, m) =>
                {
                    notifyCount++;
                    lastTitle = t;
                    lastMessage = m;
                });

                string fakeUsb = Path.Combine(Path.GetTempPath(), "FakeUSB_SingleNotify_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fakeUsb);
                File.WriteAllText(Path.Combine(fakeUsb, "课件.pptx"), "内容");

                try
                {
                    monitor.ProcessUSBAction(fakeUsb);

                    AssertEqual(1, notifyCount, "Exactly ONE notification should be delivered for offline insertion");
                    AssertEqual("获取Rick课件", lastTitle, "Notification title matches");
                    AssertEqual("云上春晖未连接，课件已暂存本地，等待网络恢复自动同步", lastMessage, "Exact offline notification message");
                }
                finally
                {
                    if (Directory.Exists(fakeUsb)) Directory.Delete(fakeUsb, true);
                    // 清理暂存
                    string cacheDir = USBMonitor.GetLocalCacheDirectory();
                    foreach (var d in Directory.GetDirectories(cacheDir))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            });

            // 13. Test DpiScaler Fractional 4K Scaling and Boundary Conditions
            RunTest("DpiScaler_Fractional4KScalingAndBoundaries", () =>
            {
                var baseSize = new Size(680, 630);
                // 250% scale (4K 典型缩放)
                var size250 = DpiScaler.CalculateScaledSize(baseSize, 2.5f);
                AssertEqual(1700, size250.Width, "250% Width");
                AssertEqual(1575, size250.Height, "250% Height");

                // 150% scale
                var size150 = DpiScaler.CalculateScaledSize(baseSize, 1.5f);
                AssertEqual(1020, size150.Width, "150% Width");
                AssertEqual(945, size150.Height, "150% Height");

                float font250 = DpiScaler.CalculateScaledFontSize(10f, 2.5f);
                AssertEqual(25f, font250, "250% Font size");
            });

            Console.WriteLine("========================================");
            Console.WriteLine($"测试完成: 共通过 {passed} 项, 失败 {failed} 项。");
            Console.WriteLine("========================================");

            return failed > 0 ? 1 : 0;
        }
    }
}
