using System;
using System.Collections.Generic;
using System.IO;

namespace USBAutoCopy
{
    namespace Properties
    {
        public class Settings
        {
            private static readonly Settings _default = new Settings();
            public static Settings Default => _default;

            private static Dictionary<string, string> settings = new Dictionary<string, string>();
            private static string GetConfigFile()
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string localFile = Path.Combine(baseDir, "RickConfig.ini");
                try
                {
                    if (File.Exists(localFile))
                    {
                        return localFile;
                    }

                    // 探测目录写入权限
                    string testFile = Path.Combine(baseDir, ".test_" + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(testFile, "1");
                    File.Delete(testFile);
                    return localFile;
                }
                catch
                {
                    // 若程序所在目录受限无写权限，自动降级至用户本地数据目录
                    try
                    {
                        string userDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "GetRickCourseware");
                        if (!Directory.Exists(userDir))
                        {
                            Directory.CreateDirectory(userDir);
                        }
                        return Path.Combine(userDir, "RickConfig.ini");
                    }
                    catch
                    {
                        return localFile;
                    }
                }
            }

            private static string configFile => GetConfigFile();

            static Settings()
            {
                Load();
            }

            public string BackupPath
            {
                get => settings.ContainsKey("BackupPath") ? settings["BackupPath"] : "";
                set { settings["BackupPath"] = value; Save(); }
            }

            public bool AutoStart
            {
                get => settings.ContainsKey("AutoStart") && settings["AutoStart"] == "true";
                set { settings["AutoStart"] = value ? "true" : "false"; Save(); }
            }

            public string BlockedDrives
            {
                get => settings.ContainsKey("BlockedDrives") ? settings["BlockedDrives"] : "";
                set { settings["BlockedDrives"] = value; Save(); }
            }

            public List<string> GetBlockedList()
            {
                var raw = BlockedDrives;
                if (string.IsNullOrEmpty(raw)) return new List<string>();
                return new List<string>(raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }

            public void AddBlocked(string name)
            {
                var list = GetBlockedList();
                if (!list.Contains(name)) { list.Add(name); BlockedDrives = string.Join(",", list); }
            }

            public void RemoveBlocked(string name)
            {
                var list = GetBlockedList();
                list.Remove(name);
                BlockedDrives = string.Join(",", list);
            }
            
            private static void Load()
            {
                string path = configFile;
                // 若优先路径不存在，尝试探测另一侧路径
                if (!File.Exists(path))
                {
                    string fallbackUser = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GetRickCourseware", "RickConfig.ini");
                    if (File.Exists(fallbackUser))
                    {
                        path = fallbackUser;
                    }
                }

                if (File.Exists(path))
                {
                    try
                    {
                        foreach (var line in File.ReadAllLines(path))
                        {
                            var parts = line.Split(new[] { '=' }, 2);
                            if (parts.Length == 2)
                                settings[parts[0]] = parts[1];
                        }
                    }
                    catch { /* 静默失败，配置加载错误 */ }
                }
            }
            
            public static void Save()
            {
                try
                {
                    string target = configFile;
                    string dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var lines = new List<string>();
                    foreach (var kvp in settings)
                        lines.Add($"{kvp.Key}={kvp.Value}");
                    File.WriteAllLines(target, lines);
                }
                catch
                {
                    // 若首选路径保存失败，尝试写至本地用户数据目录
                    try
                    {
                        string userDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "GetRickCourseware");
                        if (!Directory.Exists(userDir)) Directory.CreateDirectory(userDir);
                        string fallbackTarget = Path.Combine(userDir, "RickConfig.ini");
                        var lines = new List<string>();
                        foreach (var kvp in settings) lines.Add($"{kvp.Key}={kvp.Value}");
                        File.WriteAllLines(fallbackTarget, lines);
                    }
                    catch { }
                }
            }
        }
    }
}
