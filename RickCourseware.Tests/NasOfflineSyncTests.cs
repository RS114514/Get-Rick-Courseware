using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;
using USBAutoCopy;

namespace RickCourseware.Tests
{
    public class NasOfflineSyncTests : IDisposable
    {
        private readonly string _tempTestDir;
        private readonly string _mockNasDir;
        private readonly string _mockUsbDir;

        public NasOfflineSyncTests()
        {
            _tempTestDir = Path.Combine(Path.GetTempPath(), "RickTest_" + Guid.NewGuid().ToString("N"));
            _mockNasDir = Path.Combine(_tempTestDir, "NAS_Target");
            _mockUsbDir = Path.Combine(_tempTestDir, "USB_Source");

            Directory.CreateDirectory(_tempTestDir);
            Directory.CreateDirectory(_mockNasDir);
            Directory.CreateDirectory(_mockUsbDir);
        }

        public void Dispose()
        {
            // 重置静态 Mock 委托
            USBMonitor.MockIsPathReachableFunc = null;
            USBMonitor.MockResolveNetworkPathFunc = null;
            USBMonitor.MockNotifyAction = null;

            try
            {
                if (Directory.Exists(_tempTestDir))
                {
                    Directory.Delete(_tempTestDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void TestResolveNetworkPath_DirectUncReturnsDirectly()
        {
            string unc = @"\\chunhui-nas\shares\课件";
            string resolved = USBMonitor.ResolveNetworkPath(unc);
            Assert.Equal(unc, resolved);
        }

        [Fact]
        public void TestResolveNetworkPath_WithMockFunc()
        {
            USBMonitor.MockResolveNetworkPathFunc = (path) =>
            {
                if (path.StartsWith("Z:", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\chunhui-nas\shares\课件";
                }
                return path;
            };

            string resolved = USBMonitor.ResolveNetworkPath(@"Z:\课件");
            Assert.Equal(@"\\chunhui-nas\shares\课件", resolved);

            bool isNet = USBMonitor.IsNetworkDrive(@"Z:\课件", out string uncResult);
            Assert.True(isNet);
            Assert.Equal(@"\\chunhui-nas\shares\课件", uncResult);
        }

        [Fact]
        public void TestOfflineFallback_WhenNasUnreachable_SavesToLocalCacheAndNotifies()
        {
            var logs = new List<string>();
            var notifications = new List<Tuple<string, string>>();

            USBMonitor.MockIsPathReachableFunc = (path) => false;
            USBMonitor.MockNotifyAction = (title, msg) =>
            {
                lock (notifications)
                {
                    notifications.Add(Tuple.Create(title, msg));
                }
            };

            // 创建模拟 U 盘中的测试文件
            string testFile1 = Path.Combine(_mockUsbDir, "语文第一课.pptx");
            File.WriteAllText(testFile1, "课件内容测试1");
            string subDir = Path.Combine(_mockUsbDir, "附带素材");
            Directory.CreateDirectory(subDir);
            string testFile2 = Path.Combine(subDir, "背景音频.mp3");
            File.WriteAllText(testFile2, "课件音频测试2");

            var monitor = new USBMonitor(_mockNasDir, (msg) => logs.Add(msg));

            // 执行 U 盘处理
            monitor.ProcessUSBAction(_mockUsbDir);

            // 1. 验证通知触发且文案精确符合要求
            lock (notifications)
            {
                Assert.NotEmpty(notifications);
                Assert.Contains(notifications, n => n.Item2 == "云上春晖未连接，课件已暂存本地，等待网络恢复自动同步");
            }

            // 2. 验证本地暂存目录中已生成备份文件夹
            string localCache = USBMonitor.GetLocalCacheDirectory();
            Assert.True(Directory.Exists(localCache));
            string[] subDirs = Directory.GetDirectories(localCache);
            Assert.NotEmpty(subDirs);

            // 3. 验证暂存目录包含测试文件
            bool foundFile1 = false;
            foreach (var dir in subDirs)
            {
                if (File.Exists(Path.Combine(dir, "语文第一课.pptx")))
                {
                    foundFile1 = true;
                    // 清理测试产物
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            Assert.True(foundFile1, "暂存目录中未找到复制的课件文件");
        }

        [Fact]
        public void TestBackgroundSync_WhenNasRestored_MigratesFilesAndCleansCache()
        {
            var logs = new List<string>();
            var notifications = new List<Tuple<string, string>>();

            USBMonitor.MockNotifyAction = (title, msg) =>
            {
                lock (notifications)
                {
                    notifications.Add(Tuple.Create(title, msg));
                }
            };

            // 准备本地暂存文件夹
            string localCache = USBMonitor.GetLocalCacheDirectory();
            string mockCacheFolder = Path.Combine(localCache, "20260918_120000_E_数学课件测试");
            Directory.CreateDirectory(mockCacheFolder);
            File.WriteAllText(Path.Combine(mockCacheFolder, "几何公式.pptx"), "数学课件内容");
            string nested = Path.Combine(mockCacheFolder, "文档");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "习题.docx"), "习题内容");

            var monitor = new USBMonitor(_mockNasDir, (msg) => logs.Add(msg));

            // 此时春晖 NAS 已恢复可达
            USBMonitor.MockIsPathReachableFunc = (path) => true;

            int syncedCount = monitor.SyncLocalCacheToNas(_mockNasDir);

            // 验证迁移数量 >= 1
            Assert.True(syncedCount >= 1);

            // 验证源本地暂存目录已被清理
            Assert.False(Directory.Exists(mockCacheFolder), "迁移完成后本地暂存文件夹应被删除");

            // 验证目标 NAS 目录已收到文件
            string migratedDest = Path.Combine(_mockNasDir, "20260918_120000_E_数学课件测试");
            Assert.True(Directory.Exists(migratedDest));
            Assert.True(File.Exists(Path.Combine(migratedDest, "几何公式.pptx")));
            Assert.True(File.Exists(Path.Combine(migratedDest, "文档", "习题.docx")));

            // 验证发出迁移完成通知
            lock (notifications)
            {
                Assert.Contains(notifications, n => n.Item2.Contains("已成功同步至网络盘"));
            }
        }

        [Fact]
        public void TestMoveOrMergeDirectory_DeepRecursiveVerification()
        {
            string src = Path.Combine(_tempTestDir, "SourceDir");
            string dst = Path.Combine(_tempTestDir, "DestDir");
            Directory.CreateDirectory(Path.Combine(src, "Level1", "Level2"));
            File.WriteAllText(Path.Combine(src, "fileA.txt"), "A");
            File.WriteAllText(Path.Combine(src, "Level1", "fileB.txt"), "B");
            File.WriteAllText(Path.Combine(src, "Level1", "Level2", "fileC.txt"), "C");

            USBMonitor.MoveOrMergeDirectory(src, dst);

            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Combine(dst, "fileA.txt")));
            Assert.True(File.Exists(Path.Combine(dst, "Level1", "fileB.txt")));
            Assert.True(File.Exists(Path.Combine(dst, "Level1", "Level2", "fileC.txt")));
        }
    }
}
