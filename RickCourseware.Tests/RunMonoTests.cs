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

            // 14. BackupHistory JSON 序列化与转义测试
            RunTest("BackupHistory.JsonSerializationAndEscaping", () =>
            {
                var records = new List<BackupRecord>
                {
                    new BackupRecord
                    {
                        Id = "test-id-001",
                        Timestamp = new DateTime(2026, 9, 19, 21, 0, 0),
                        DriveLetter = "E:",
                        UsbName = "高三\"英语\"复习\\特辑",
                        FileCount = 42,
                        TargetFolder = @"D:\Courseware\20260919_210000_E_英语\附录",
                        Status = "成功"
                    },
                    new BackupRecord
                    {
                        Id = "test-id-002",
                        Timestamp = new DateTime(2026, 9, 19, 21, 15, 30),
                        DriveLetter = "F:",
                        UsbName = "物理公开课",
                        FileCount = 10,
                        TargetFolder = @"\\chunhui-nas\shares\课件\20260919_211530_F_物理",
                        Status = "本地暂存"
                    }
                };

                string json = BackupHistoryManager.SerializeJson(records);
                TestAssert.True(json.Contains("test-id-001"), "JSON 包含 id 1");
                TestAssert.True(json.Contains("高三\\\"英语\\\"复习\\\\特辑"), "JSON 包含正确转义字符串");

                var deserialized = BackupHistoryManager.DeserializeJson(json);
                TestAssert.Equal(2, deserialized.Count);
                TestAssert.Equal("test-id-001", deserialized[0].Id);
                TestAssert.Equal("高三\"英语\"复习\\特辑", deserialized[0].UsbName);
                TestAssert.Equal(42, deserialized[0].FileCount);
                TestAssert.Equal(@"D:\Courseware\20260919_210000_E_英语\附录", deserialized[0].TargetFolder);
                TestAssert.Equal("成功", deserialized[0].Status);

                TestAssert.Equal("test-id-002", deserialized[1].Id);
                TestAssert.Equal("物理公开课", deserialized[1].UsbName);
                TestAssert.Equal(10, deserialized[1].FileCount);
                TestAssert.Equal(@"\\chunhui-nas\shares\课件\20260919_211530_F_物理", deserialized[1].TargetFolder);
                TestAssert.Equal("本地暂存", deserialized[1].Status);
            });

            // 15. BackupHistoryManager 增删改查与文件持久化测试
            RunTest("BackupHistory.Add_Update_Delete_Clear_Persistence", () =>
            {
                string tempHistFile = Path.Combine(Path.GetTempPath(), "RickHistTest_" + Guid.NewGuid().ToString("N") + ".json");
                BackupHistoryManager.MockFilePath = tempHistFile;
                bool eventFired = false;
                Action handler = () => { eventFired = true; };
                BackupHistoryManager.OnHistoryChanged += handler;

                try
                {
                    // 1. 初始应为空
                    var list = BackupHistoryManager.LoadHistory();
                    TestAssert.Empty(list);

                    // 2. 添加记录
                    var r1 = new BackupRecord
                    {
                        Id = "rec-1",
                        Timestamp = DateTime.Now,
                        DriveLetter = "G:",
                        UsbName = "生物课件",
                        FileCount = 5,
                        TargetFolder = @"C:\Cache\20260919_G_生物",
                        Status = "本地暂存"
                    };
                    BackupHistoryManager.AddRecord(r1);
                    TestAssert.True(eventFired, "OnHistoryChanged 事件应触发");
                    eventFired = false;

                    list = BackupHistoryManager.LoadHistory();
                    TestAssert.Single(list);
                    TestAssert.Equal("rec-1", list[0].Id);
                    TestAssert.Equal("本地暂存", list[0].Status);

                    // 3. 按文件夹名称更新状态与新目标路径 (模拟后台 NAS 同步后更新)
                    bool updated = BackupHistoryManager.UpdateStatusByFolderName("20260919_G_生物", "已同步", @"\\nas\share\20260919_G_生物");
                    TestAssert.True(updated, "应成功更新状态");
                    TestAssert.True(eventFired, "更新状态时应触发事件");
                    eventFired = false;

                    list = BackupHistoryManager.LoadHistory();
                    TestAssert.Equal("已同步", list[0].Status);
                    TestAssert.Equal(@"\\nas\share\20260919_G_生物", list[0].TargetFolder);

                    // 4. 删除指定记录
                    bool deleted = BackupHistoryManager.DeleteRecord("rec-1");
                    TestAssert.True(deleted, "应成功删除记录");
                    list = BackupHistoryManager.LoadHistory();
                    TestAssert.Empty(list);

                    // 5. 添加多条并清空
                    BackupHistoryManager.AddRecord(new BackupRecord { Id = "rec-2", DriveLetter = "H:" });
                    BackupHistoryManager.AddRecord(new BackupRecord { Id = "rec-3", DriveLetter = "I:" });
                    TestAssert.Equal(2, BackupHistoryManager.LoadHistory().Count);

                    BackupHistoryManager.ClearHistory();
                    TestAssert.Empty(BackupHistoryManager.LoadHistory());
                }
                finally
                {
                    BackupHistoryManager.OnHistoryChanged -= handler;
                    BackupHistoryManager.MockFilePath = null;
                    if (File.Exists(tempHistFile)) File.Delete(tempHistFile);
                }
            });

            // 16. BackupHistory 损坏数据容错测试
            RunTest("BackupHistory.CorruptedJsonRecovery", () =>
            {
                string tempHistFile = Path.Combine(Path.GetTempPath(), "RickHistCorrupt_" + Guid.NewGuid().ToString("N") + ".json");
                BackupHistoryManager.MockFilePath = tempHistFile;

                try
                {
                    File.WriteAllText(tempHistFile, "{ invalid json content !!! @#$!@#$");
                    var list = BackupHistoryManager.LoadHistory();
                    // 遇到损坏文件应安全返回空列表，绝不抛出未捕获异常导致崩溃
                    TestAssert.Empty(list);

                    // 写入空文件
                    File.WriteAllText(tempHistFile, "");
                    list = BackupHistoryManager.LoadHistory();
                    TestAssert.Empty(list);
                }
                finally
                {
                    BackupHistoryManager.MockFilePath = null;
                    if (File.Exists(tempHistFile)) File.Delete(tempHistFile);
                }
            });

            // 17. USBMonitor 与 BackupHistory 全链路集成测试 (离线暂存 -> 恢复同步)
            RunTest("USBMonitor.BackupHistoryIntegration_OfflineAndSyncFlow", () =>
            {
                string testTemp = Path.Combine(Path.GetTempPath(), "RickTest_HistInteg_" + Guid.NewGuid().ToString("N"));
                string mockUsb = Path.Combine(testTemp, "USB");
                string mockNas = Path.Combine(testTemp, "NAS");
                string histFile = Path.Combine(testTemp, "history.json");
                Directory.CreateDirectory(testTemp);
                Directory.CreateDirectory(mockUsb);
                Directory.CreateDirectory(mockNas);

                BackupHistoryManager.MockFilePath = histFile;
                USBMonitor.MockIsPathReachableFunc = (path) => false; // 初始离线

                try
                {
                    File.WriteAllText(Path.Combine(mockUsb, "高考数学复习.pptx"), "数学试卷课件");
                    var monitor = new USBMonitor(mockNas, null);

                    // 1. 触发离线 U 盘备份
                    monitor.ProcessUSBAction(mockUsb);

                    var history = BackupHistoryManager.LoadHistory();
                    TestAssert.NotEmpty(history);
                    var entry = history[0];
                    TestAssert.Equal("本地暂存", entry.Status);
                    TestAssert.True(entry.FileCount >= 1, "备份文件数应 >= 1");
                    TestAssert.Contains("本地暂存", entry.Status);

                    string folderName = Path.GetFileName(entry.TargetFolder.TrimEnd('\\', '/'));

                    // 2. 模拟网络恢复，触发同步至 NAS
                    USBMonitor.MockIsPathReachableFunc = (path) => true;
                    int synced = monitor.SyncLocalCacheToNas(mockNas);
                    TestAssert.True(synced >= 1, "应成功同步");

                    // 3. 验证历史记录中的对应条目状态已自动更新为“已同步”，且目标路径指向 NAS
                    history = BackupHistoryManager.LoadHistory();
                    TestAssert.NotEmpty(history);
                    var updatedEntry = history[0];
                    TestAssert.Equal("已同步", updatedEntry.Status);
                    TestAssert.True(updatedEntry.TargetFolder.StartsWith(mockNas, StringComparison.OrdinalIgnoreCase), "目标路径应已迁移至 NAS 路径");
                }
                finally
                {
                    USBMonitor.MockIsPathReachableFunc = null;
                    BackupHistoryManager.MockFilePath = null;
                    try { Directory.Delete(testTemp, true); } catch { }
                    string cacheDir = USBMonitor.GetLocalCacheDirectory();
                    foreach (var d in Directory.GetDirectories(cacheDir))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            });

            // 18. BackupRecord DeviceDisplay 组合与空白字符边界测试
            RunTest("BackupRecord.DeviceDisplay_CombinationsAndWhitespace", () =>
            {
                var r1 = new BackupRecord { DriveLetter = "E:", UsbName = "金士顿U盘" };
                TestAssert.Equal("E: (金士顿U盘)", r1.DeviceDisplay);

                var r2 = new BackupRecord { DriveLetter = "F:", UsbName = null };
                TestAssert.Equal("F:", r2.DeviceDisplay);

                var r3 = new BackupRecord { DriveLetter = null, UsbName = "闪迪课件盘" };
                TestAssert.Equal("闪迪课件盘", r3.DeviceDisplay);

                var r4 = new BackupRecord { DriveLetter = null, UsbName = null };
                TestAssert.Equal("未知设备", r4.DeviceDisplay);

                var r5 = new BackupRecord { DriveLetter = "   ", UsbName = "   " };
                TestAssert.Equal("未知设备", r5.DeviceDisplay);

                var r6 = new BackupRecord { DriveLetter = " G: ", UsbName = " 语文备课 " };
                TestAssert.Equal("G: (语文备课)", r6.DeviceDisplay);
            });

            // 19. UpdateStatusByFolderName 精确末级目录匹配与同名后缀隔离
            RunTest("BackupHistory.UpdateStatusByFolderName_ExactIsolationAndSlashes", () =>
            {
                string tempHist = Path.Combine(Path.GetTempPath(), "MonoHistIso_" + Guid.NewGuid().ToString("N") + ".json");
                BackupHistoryManager.MockFilePath = tempHist;

                try
                {
                    var rA = new BackupRecord
                    {
                        Id = "rec-A",
                        TargetFolder = @"C:\Cache\20260919_数学课件",
                        Status = "本地暂存"
                    };
                    var rB = new BackupRecord
                    {
                        Id = "rec-B",
                        TargetFolder = @"C:\Cache\20260919_初中数学课件\",
                        Status = "本地暂存"
                    };
                    BackupHistoryManager.AddRecord(rA);
                    BackupHistoryManager.AddRecord(rB);

                    // 仅更新 "20260919_数学课件"，绝不能误伤 "20260919_初中数学课件"
                    bool updated = BackupHistoryManager.UpdateStatusByFolderName("20260919_数学课件", "已同步", @"\\nas\20260919_数学课件");
                    TestAssert.True(updated, "Update succeeded for exact folder");

                    var list = BackupHistoryManager.LoadHistory();
                    var loadedA = list.Find(x => x.Id == "rec-A");
                    var loadedB = list.Find(x => x.Id == "rec-B");

                    TestAssert.Equal("已同步", loadedA.Status);
                    TestAssert.Equal("本地暂存", loadedB.Status);
                }
                finally
                {
                    BackupHistoryManager.MockFilePath = null;
                    if (File.Exists(tempHist)) File.Delete(tempHist);
                }
            });

            // 20. BackupHistoryManager 多线程并发持久化安全性
            RunTest("BackupHistory.ConcurrentThreadSafety", () =>
            {
                string tempHist = Path.Combine(Path.GetTempPath(), "MonoHistConcurr_" + Guid.NewGuid().ToString("N") + ".json");
                BackupHistoryManager.MockFilePath = tempHist;

                try
                {
                    int threadCount = 6;
                    int itemsPerThread = 10;
                    var threads = new List<System.Threading.Thread>();

                    for (int t = 0; t < threadCount; t++)
                    {
                        int threadIndex = t;
                        var th = new System.Threading.Thread(() =>
                        {
                            for (int i = 0; i < itemsPerThread; i++)
                            {
                                BackupHistoryManager.AddRecord(new BackupRecord
                                {
                                    Id = $"t{threadIndex}_{i}",
                                    UsbName = $"Thread_{threadIndex}",
                                    FileCount = i,
                                    Status = "成功"
                                });
                            }
                        });
                        threads.Add(th);
                    }

                    foreach (var th in threads) th.Start();
                    foreach (var th in threads) th.Join();

                    var list = BackupHistoryManager.LoadHistory();
                    TestAssert.Equal(threadCount * itemsPerThread, list.Count);
                }
                finally
                {
                    BackupHistoryManager.MockFilePath = null;
                    if (File.Exists(tempHist)) File.Delete(tempHist);
                }
            });

            // 清理测试残留文件
            try
            {
                string localIni = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RickConfig.ini");
                if (File.Exists(localIni)) File.Delete(localIni);
                string localHist = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BackupHistory.json");
                if (File.Exists(localHist)) File.Delete(localHist);
            }
            catch { }

            Console.WriteLine("==================================================");
            Console.WriteLine($"测试执行完毕: 共 {passed + failed} 个用例，通过: {passed}，失败: {failed}");
            Console.WriteLine("==================================================");

            return failed == 0 ? 0 : 1;
        }
    }
}
