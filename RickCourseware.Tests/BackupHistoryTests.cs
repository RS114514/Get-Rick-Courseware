using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using USBAutoCopy;

namespace RickCourseware.Tests
{
    public class BackupHistoryTests : IDisposable
    {
        private readonly string _tempFile;

        public BackupHistoryTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), "RickXunitHist_" + Guid.NewGuid().ToString("N") + ".json");
            BackupHistoryManager.MockFilePath = _tempFile;
        }

        public void Dispose()
        {
            BackupHistoryManager.MockFilePath = null;
            if (File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
            }
        }

        [Fact]
        public void TestJsonSerializationAndEscaping()
        {
            var records = new List<BackupRecord>
            {
                new BackupRecord
                {
                    Id = "xunit-001",
                    Timestamp = new DateTime(2026, 9, 19, 21, 0, 0),
                    DriveLetter = "E:",
                    UsbName = "语文\"第一课\"教案\\资料",
                    FileCount = 18,
                    TargetFolder = @"D:\Rick\20260919_210000_E_教案",
                    Status = "成功"
                }
            };

            string json = BackupHistoryManager.SerializeJson(records);
            Assert.Contains("xunit-001", json);
            Assert.Contains("语文\\\"第一课\\\"教案\\\\资料", json);

            var list = BackupHistoryManager.DeserializeJson(json);
            Assert.Single(list);
            Assert.Equal("xunit-001", list[0].Id);
            Assert.Equal("语文\"第一课\"教案\\资料", list[0].UsbName);
            Assert.Equal(18, list[0].FileCount);
            Assert.Equal(@"D:\Rick\20260919_210000_E_教案", list[0].TargetFolder);
            Assert.Equal("成功", list[0].Status);
        }

        [Fact]
        public void TestAddUpdateDeleteClear()
        {
            bool notified = false;
            Action handler = () => { notified = true; };
            BackupHistoryManager.OnHistoryChanged += handler;

            try
            {
                Assert.Empty(BackupHistoryManager.LoadHistory());

                var r = new BackupRecord
                {
                    Id = "rec-abc",
                    DriveLetter = "U:",
                    UsbName = "化学实验",
                    FileCount = 12,
                    TargetFolder = @"C:\Cache\20260919_U_化学实验",
                    Status = "本地暂存"
                };

                BackupHistoryManager.AddRecord(r);
                Assert.True(notified);
                notified = false;

                var loaded = BackupHistoryManager.LoadHistory();
                Assert.Single(loaded);
                Assert.Equal("本地暂存", loaded[0].Status);

                bool updated = BackupHistoryManager.UpdateStatusByFolderName("20260919_U_化学实验", "已同步", @"\\nas\share\20260919_U_化学实验");
                Assert.True(updated);
                Assert.True(notified);
                notified = false;

                loaded = BackupHistoryManager.LoadHistory();
                Assert.Equal("已同步", loaded[0].Status);
                Assert.Equal(@"\\nas\share\20260919_U_化学实验", loaded[0].TargetFolder);

                bool deleted = BackupHistoryManager.DeleteRecord("rec-abc");
                Assert.True(deleted);
                Assert.Empty(BackupHistoryManager.LoadHistory());
            }
            finally
            {
                BackupHistoryManager.OnHistoryChanged -= handler;
            }
        }

        [Fact]
        public void TestCorruptedJsonHandling()
        {
            File.WriteAllText(_tempFile, "Corrupted syntax error [{} 123");
            var list = BackupHistoryManager.LoadHistory();
            Assert.Empty(list);
        }
    }
}
