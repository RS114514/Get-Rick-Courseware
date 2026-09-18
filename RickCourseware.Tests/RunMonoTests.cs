using System;
using System.IO;
using System.Collections.Generic;
using USBAutoCopy;
using USBAutoCopy.Properties;

namespace RickCourseware.Tests
{
    public static class TestAssert
    {
        public static void True(bool condition, string msg = "")
        {
            if (!condition) throw new Exception("Assert.True failed: " + msg);
        }

        public static void False(bool condition, string msg = "")
        {
            if (condition) throw new Exception("Assert.False failed: " + msg);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(string.Format("Assert.Equal failed. Expected: '{0}', Actual: '{1}'", expected, actual));
        }

        public static void Empty(System.Collections.IEnumerable collection)
        {
            var e = collection.GetEnumerator();
            if (e.MoveNext()) throw new Exception("Assert.Empty failed: collection has elements.");
        }

        public static void NotEmpty(System.Collections.IEnumerable collection)
        {
            var e = collection.GetEnumerator();
            if (!e.MoveNext()) throw new Exception("Assert.NotEmpty failed: collection is empty.");
        }

        public static void Single(System.Collections.IEnumerable collection)
        {
            int count = 0;
            foreach (var item in collection) count++;
            if (count != 1) throw new Exception("Assert.Single failed: count was " + count);
        }

        public static void Contains(string expectedSubstring, string actualString)
        {
            if (actualString == null || !actualString.Contains(expectedSubstring))
                throw new Exception(string.Format("Assert.Contains failed: '{0}' not in '{1}'", expectedSubstring, actualString));
        }

        public static void Contains<T>(T expected, ICollection<T> collection)
        {
            if (!collection.Contains(expected))
                throw new Exception("Assert.Contains failed: item not found in collection.");
        }

        public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
        {
            bool found = false;
            foreach (var item in collection)
            {
                if (filter(item)) { found = true; break; }
            }
            if (!found) throw new Exception("Assert.Contains predicate failed: no match.");
        }

        public static void DoesNotContain<T>(T expected, ICollection<T> collection)
        {
            if (collection.Contains(expected))
                throw new Exception("Assert.DoesNotContain failed: item was found.");
        }
    }

    public class Program
    {
        public static int Main(string[] args)
        {
            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action action)
            {
                Console.Write($"[TEST] {name} ... ");
                try
                {
                    action();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("PASSED");
                    Console.ResetColor();
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAILED: " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    Console.ResetColor();
                    failed++;
                }
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("开始运行 Rick 课件全套自动化测试");
            Console.WriteLine("==================================================");

            // 1. SanitizeFolderName 测试
            RunTest("SanitizeFolderName.BasicAndEdges", () =>
            {
                TestAssert.Equal("未命名", USBMonitor.SanitizeFolderName(null));
                TestAssert.Equal("未命名", USBMonitor.SanitizeFolderName(""));
                TestAssert.Equal("未命名", USBMonitor.SanitizeFolderName("   "));
                TestAssert.Equal("正常名称", USBMonitor.SanitizeFolderName("正常名称"));
                TestAssert.Equal("名称 带有 空格", USBMonitor.SanitizeFolderName("名称 带有 空格"));
                TestAssert.Equal("带有非法字符_________", USBMonitor.SanitizeFolderName("带有非法字符\\/:*?\"<>|"));
                TestAssert.Equal("首尾带空格名称", USBMonitor.SanitizeFolderName("   首尾带空格名称   "));
                TestAssert.Equal("a_b_c_d", USBMonitor.SanitizeFolderName("a:b\\c/d"));
            });

            // 2. Settings 测试
            RunTest("Settings.BackupPath_And_BlockedList", () =>
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RickConfig.ini");
                if (File.Exists(configPath))
                {
                    if (File.Exists(configPath + ".bak")) File.Delete(configPath + ".bak");
                    File.Move(configPath, configPath + ".bak");
                }

                try
                {
                    string testPath = @"C:\TestBackupFolder";
                    Settings.Default.BackupPath = testPath;
                    TestAssert.Equal(testPath, Settings.Default.BackupPath);

                    Settings.Default.BlockedDrives = "";
                    TestAssert.Empty(Settings.Default.GetBlockedList());

                    Settings.Default.AddBlocked("U_Disk_1");
                    Settings.Default.AddBlocked("U_Disk_2");
                    var list = Settings.Default.GetBlockedList();
                    TestAssert.Equal(2, list.Count);
                    TestAssert.Contains("U_Disk_1", list);
                    TestAssert.Contains("U_Disk_2", list);

                    Settings.Default.RemoveBlocked("U_Disk_1");
                    list = Settings.Default.GetBlockedList();
                    TestAssert.Single(list);
                    TestAssert.Contains("U_Disk_2", list);
                }
                finally
                {
                    if (File.Exists(configPath)) File.Delete(configPath);
                    if (File.Exists(configPath + ".bak")) File.Move(configPath + ".bak", configPath);
                }
            });

            // 3. USBMonitor 基础监测测试
            RunTest("USBMonitor.IgnoresPreInsertedDrives", () =>
            {
                var processedDrives = new List<string>();
                var drives = new HashSet<string> { "D:" };
                var monitor = new USBMonitor("/tmp/backup", null);
                monitor.GetDrivesFunc = () => { lock (drives) return new HashSet<string>(drives); };
                monitor.ProcessUSBAction = (drive) => { lock (processedDrives) processedDrives.Add(drive); };

                monitor.Start();
                System.Threading.Thread.Sleep(500);

                lock (processedDrives)
                {
                    TestAssert.Empty(processedDrives);
                }

                lock (drives) { drives.Add("E:"); }
                System.Threading.Thread.Sleep(2500);

                lock (processedDrives)
                {
                    TestAssert.Single(processedDrives);
                    TestAssert.Contains("E:", processedDrives);
                    TestAssert.DoesNotContain("D:", processedDrives);
                }
                monitor.Stop();
            });

            // 4. 春晖 NAS 网络驱动器 UNC 解析测试
            RunTest("NasOfflineSync.ResolveNetworkPath_DirectUnc", () =>
            {
                string unc = @"\\chunhui-nas\shares\课件";
                string resolved = USBMonitor.ResolveNetworkPath(unc);
                TestAssert.Equal(unc, resolved);
            });

            RunTest("NasOfflineSync.ResolveNetworkPath_WithMockFunc", () =>
            {
                USBMonitor.MockResolveNetworkPathFunc = (path) =>
                {
                    if (path.StartsWith("Z:", StringComparison.OrdinalIgnoreCase))
                    {
                        return @"\\chunhui-nas\shares\课件";
                    }
                    return path;
                };

                try
                {
                    string resolved = USBMonitor.ResolveNetworkPath(@"Z:\课件");
                    TestAssert.Equal(@"\\chunhui-nas\shares\课件", resolved);

                    bool isNet = USBMonitor.IsNetworkDrive(@"Z:\课件", out string uncResult);
                    TestAssert.True(isNet, "应识别为网络驱动器");
                    TestAssert.Equal(@"\\chunhui-nas\shares\课件", uncResult);
                }
                finally
                {
                    USBMonitor.MockResolveNetworkPathFunc = null;
                }
            });

            // 5. 离线暂存降级及 Win10 系统通知测试
            RunTest("NasOfflineSync.OfflineFallback_NotificationText_And_LocalCache", () =>
            {
                string testTemp = Path.Combine(Path.GetTempPath(), "RickTest_Offline_" + Guid.NewGuid().ToString("N"));
                string mockUsb = Path.Combine(testTemp, "USB");
                string mockNas = Path.Combine(testTemp, "NAS");
                Directory.CreateDirectory(testTemp);
                Directory.CreateDirectory(mockUsb);
                Directory.CreateDirectory(mockNas);

                var notifications = new List<Tuple<string, string>>();
                var logs = new List<string>();

                USBMonitor.MockIsPathReachableFunc = (path) => false; // 模拟春晖 NAS 离线
                USBMonitor.MockNotifyAction = (title, msg) =>
                {
                    lock (notifications) notifications.Add(Tuple.Create(title, msg));
                };

                try
                {
                    File.WriteAllText(Path.Combine(mockUsb, "春晖历史第一单元.pptx"), "课件演示数据");
                    string sub = Path.Combine(mockUsb, "教案");
                    Directory.CreateDirectory(sub);
                    File.WriteAllText(Path.Combine(sub, "教案设计.docx"), "教案内容");

                    var monitor = new USBMonitor(mockNas, (msg) => logs.Add(msg));
                    monitor.ProcessUSBAction(mockUsb);

                    // 1. 验证通知文本必须为：“云上春晖未连接，课件已暂存本地，等待网络恢复自动同步”
                    lock (notifications)
                    {
                        TestAssert.NotEmpty(notifications);
                        TestAssert.Contains(notifications, n => n.Item2 == "云上春晖未连接，课件已暂存本地，等待网络恢复自动同步");
                    }

                    // 2. 验证保存在本地暂存目录中
                    string cacheDir = USBMonitor.GetLocalCacheDirectory();
                    TestAssert.True(Directory.Exists(cacheDir));

                    string[] cachedDirs = Directory.GetDirectories(cacheDir);
                    bool foundFile = false;
                    foreach (var d in cachedDirs)
                    {
                        if (File.Exists(Path.Combine(d, "春晖历史第一单元.pptx")))
                        {
                            foundFile = true;
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }
                    TestAssert.True(foundFile, "在本地暂存目录中未找到已复制的文件");
                }
                finally
                {
                    USBMonitor.MockIsPathReachableFunc = null;
                    USBMonitor.MockNotifyAction = null;
                    try { Directory.Delete(testTemp, true); } catch { }
                }
            });

            // 6. 后台恢复连通自动迁移测试
            RunTest("NasOfflineSync.BackgroundSync_Restored_MigratesAndNotifies", () =>
            {
                string testTemp = Path.Combine(Path.GetTempPath(), "RickTest_Sync_" + Guid.NewGuid().ToString("N"));
                string mockNas = Path.Combine(testTemp, "NAS_Target");
                Directory.CreateDirectory(testTemp);
                Directory.CreateDirectory(mockNas);

                var notifications = new List<Tuple<string, string>>();
                var logs = new List<string>();

                USBMonitor.MockNotifyAction = (title, msg) =>
                {
                    lock (notifications) notifications.Add(Tuple.Create(title, msg));
                };

                string localCache = USBMonitor.GetLocalCacheDirectory();
                string mockFolder = Path.Combine(localCache, "20260918_143000_E_英语高三复习测试");
                Directory.CreateDirectory(mockFolder);
                File.WriteAllText(Path.Combine(mockFolder, "听力材料.mp3"), "MP3音频模拟数据");
                string docSub = Path.Combine(mockFolder, "讲义");
                Directory.CreateDirectory(docSub);
                File.WriteAllText(Path.Combine(docSub, "复习讲义.pdf"), "PDF模拟数据");

                try
                {
                    var monitor = new USBMonitor(mockNas, (msg) => logs.Add(msg));

                    // 模拟 NAS 连通状态恢复
                    USBMonitor.MockIsPathReachableFunc = (path) => true;

                    int synced = monitor.SyncLocalCacheToNas(mockNas);
                    TestAssert.True(synced >= 1, "应至少成功迁移 1 个暂存文件夹");

                    // 验证源暂存文件夹已被清理
                    TestAssert.False(Directory.Exists(mockFolder), "本地暂存文件夹在成功迁移后应被删除");

                    // 验证 NAS 目标目录已存在迁移过来的文件
                    string destTarget = Path.Combine(mockNas, "20260918_143000_E_英语高三复习测试");
                    TestAssert.True(Directory.Exists(destTarget), "NAS 目标目录应已接收迁移文件夹");
                    TestAssert.True(File.Exists(Path.Combine(destTarget, "听力材料.mp3")));
                    TestAssert.True(File.Exists(Path.Combine(destTarget, "讲义", "复习讲义.pdf")));

                    // 验证发出迁移完成通知
                    lock (notifications)
                    {
                        TestAssert.Contains(notifications, n => n.Item2.Contains("已成功同步至网络盘"));
                    }
                }
                finally
                {
                    USBMonitor.MockIsPathReachableFunc = null;
                    USBMonitor.MockNotifyAction = null;
                    try { Directory.Delete(testTemp, true); } catch { }
                }
            });

            // 7. MoveOrMergeDirectory 递归跨目录迁移与嵌套验证
            RunTest("NasOfflineSync.MoveOrMergeDirectory_DeepRecursive", () =>
            {
                string testTemp = Path.Combine(Path.GetTempPath(), "RickTest_Move_" + Guid.NewGuid().ToString("N"));
                string src = Path.Combine(testTemp, "Src");
                string dst = Path.Combine(testTemp, "Dst");
                Directory.CreateDirectory(Path.Combine(src, "Dir1", "Dir2"));
                File.WriteAllText(Path.Combine(src, "1.txt"), "1");
                File.WriteAllText(Path.Combine(src, "Dir1", "2.txt"), "2");
                File.WriteAllText(Path.Combine(src, "Dir1", "Dir2", "3.txt"), "3");

                USBMonitor.MoveOrMergeDirectory(src, dst);

                TestAssert.False(Directory.Exists(src), "源目录迁移后应已被删除");
                TestAssert.True(File.Exists(Path.Combine(dst, "1.txt")));
                TestAssert.True(File.Exists(Path.Combine(dst, "Dir1", "2.txt")));
                TestAssert.True(File.Exists(Path.Combine(dst, "Dir1", "Dir2", "3.txt")));

                try { Directory.Delete(testTemp, true); } catch { }
            });

            // 8. MoveOrMergeDirectory 同目录与嵌套目录防自毁安全测试
            RunTest("NasOfflineSync.MoveOrMergeDirectory_SameOrNestedDir_Safe", () =>
            {
                string testTemp = Path.Combine(Path.GetTempPath(), "RickTest_SafeMove_" + Guid.NewGuid().ToString("N"));
                string src = Path.Combine(testTemp, "Src");
                Directory.CreateDirectory(src);
                string testFile = Path.Combine(src, "important.pptx");
                File.WriteAllText(testFile, "重大课件数据");

                // 传入相同目录，绝不可自删
                USBMonitor.MoveOrMergeDirectory(src, src);
                TestAssert.True(Directory.Exists(src), "相同目录时源目录不应被删除");
                TestAssert.True(File.Exists(testFile), "相同目录时文件不应被删除");
                TestAssert.Equal("重大课件数据", File.ReadAllText(testFile));

                // 传入子目录，绝不可无限递归
                string nested = Path.Combine(src, "SubNested");
                USBMonitor.MoveOrMergeDirectory(src, nested);
                TestAssert.True(File.Exists(testFile), "嵌套目录时源文件不应被删除");

                try { Directory.Delete(testTemp, true); } catch { }
            });

            // 9. SyncLocalCacheToNas 目标为本地暂存目录本身时防自删与死锁测试
            RunTest("NasOfflineSync.SyncLocalCacheToNas_TargetIsSelfCache_SkippedSafely", () =>
            {
                string localCache = USBMonitor.GetLocalCacheDirectory();
                string testFolder = Path.Combine(localCache, "20260918_SelfTargetTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testFolder);
                string testFile = Path.Combine(testFolder, "重要课件.pptx");
                File.WriteAllText(testFile, "保留课件内容");

                try
                {
                    var monitor = new USBMonitor(localCache, null);
                    USBMonitor.MockIsPathReachableFunc = (p) => true;

                    // 目标路径即为暂存目录自身，应安全跳过，不得删除自身文件
                    int synced = monitor.SyncLocalCacheToNas(localCache);
                    TestAssert.Equal(0, synced);
                    TestAssert.True(Directory.Exists(testFolder), "暂存目录自身不应被删除");
                    TestAssert.True(File.Exists(testFile), "暂存文件不应被删除");
                }
                finally
                {
                    USBMonitor.MockIsPathReachableFunc = null;
                    try { Directory.Delete(testFolder, true); } catch { }
                }
            });

            // 10. ProcessUSB 当目标创建失败时（例如网络路径挂起或不可写）自动降级本地暂存
            RunTest("NasOfflineSync.OfflineFallback_WhenTargetDirectoryCreateFails", () =>
            {
                string testTemp = Path.Combine(Path.GetTempPath(), "RickTest_FallbackCreate_" + Guid.NewGuid().ToString("N"));
                string mockUsb = Path.Combine(testTemp, "USB");
                Directory.CreateDirectory(mockUsb);
                File.WriteAllText(Path.Combine(mockUsb, "教案.docx"), "教案内容");

                var notifications = new List<Tuple<string, string>>();
                USBMonitor.MockIsPathReachableFunc = (p) => true; // 假定初始可达探测通过
                USBMonitor.MockNotifyAction = (t, m) =>
                {
                    lock (notifications) notifications.Add(Tuple.Create(t, m));
                };

                // 使用一个无法创建目录的非法目标路径（非Windows环境下的非法UNC或保留字符路径）
                string invalidDest = "/dev/null/impossible_dir_" + Guid.NewGuid().ToString("N");
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    invalidDest = @"Z:\NonExistentShare\InvalidSub";
                }

                try
                {
                    var monitor = new USBMonitor(invalidDest, null);
                    monitor.ProcessUSBAction(mockUsb);

                    // 应自动降级至本地暂存并触发离线通知
                    lock (notifications)
                    {
                        TestAssert.NotEmpty(notifications);
                        TestAssert.Contains(notifications, n => n.Item2 == "云上春晖未连接，课件已暂存本地，等待网络恢复自动同步");
                    }

                    string cacheDir = USBMonitor.GetLocalCacheDirectory();
                    bool found = false;
                    foreach (var d in Directory.GetDirectories(cacheDir))
                    {
                        if (File.Exists(Path.Combine(d, "教案.docx")))
                        {
                            found = true;
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }
                    TestAssert.True(found, "目标创建失败时，课件应降级保存至本地暂存目录");
                }
                finally
                {
                    USBMonitor.MockIsPathReachableFunc = null;
                    USBMonitor.MockNotifyAction = null;
                    try { Directory.Delete(testTemp, true); } catch { }
                }
            });

            // 12. 验证本地暂存检测与无缓存时跳过同步
            RunTest("USBMonitor.HasAnyCachedCourseware_And_EmptySyncBypass", () =>
            {
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

                TestAssert.False(USBMonitor.HasAnyCachedCourseware(), "初始无暂存课件");

                var monitor = new USBMonitor(@"\\non-existent\share", null);
                int synced = monitor.SyncLocalCacheToNas(@"\\non-existent\share");
                TestAssert.Equal(0, synced);
            });

            // 13. 验证离线 U 盘插入通知严格单次分发（无多重或重复通知）
            RunTest("USBMonitor.SingleNotificationDelivery_NoDuplicates", () =>
            {
                int notifyCount = 0;
                string lastTitle = null;
                string lastMsg = null;

                var monitor = new USBMonitor(@"X:\OfflineServer\Target", null, (t, m) =>
                {
                    notifyCount++;
                    lastTitle = t;
                    lastMsg = m;
                });

                string fakeUsb = Path.Combine(Path.GetTempPath(), "FakeUSB_SingleNotify_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fakeUsb);
                File.WriteAllText(Path.Combine(fakeUsb, "课件.pptx"), "内容");

                try
                {
                    monitor.ProcessUSBAction(fakeUsb);

                    TestAssert.Equal(1, notifyCount);
                    TestAssert.Equal("获取Rick课件", lastTitle);
                    TestAssert.Equal("云上春晖未连接，课件已暂存本地，等待网络恢复自动同步", lastMsg);
                }
                finally
                {
                    if (Directory.Exists(fakeUsb)) Directory.Delete(fakeUsb, true);
                    string cacheDir = USBMonitor.GetLocalCacheDirectory();
                    foreach (var d in Directory.GetDirectories(cacheDir))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            });

            Console.WriteLine("==================================================");
            Console.WriteLine($"测试执行完毕: 共 {passed + failed} 个用例，通过: {passed}，失败: {failed}");
            Console.WriteLine("==================================================");

            return failed == 0 ? 0 : 1;
        }
    }
}
