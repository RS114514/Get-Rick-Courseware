using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace USBAutoCopy
{
    public class BackupRecord
    {
        public string Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string DriveLetter { get; set; }
        public string UsbName { get; set; }
        public int FileCount { get; set; }
        public string TargetFolder { get; set; }
        public string Status { get; set; } // "成功", "本地暂存", "已同步"

        public BackupRecord()
        {
            Id = Guid.NewGuid().ToString("N");
            Timestamp = DateTime.Now;
            DriveLetter = "";
            UsbName = "";
            FileCount = 0;
            TargetFolder = "";
            Status = "成功";
        }

        public string FormattedTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        public string DeviceDisplay
        {
            get
            {
                string drive = DriveLetter != null ? DriveLetter.Trim() : "";
                string name = UsbName != null ? UsbName.Trim() : "";
                bool hasDrive = !string.IsNullOrEmpty(drive);
                bool hasName = !string.IsNullOrEmpty(name);
                if (hasDrive && hasName) return $"{drive} ({name})";
                if (hasDrive) return drive;
                if (hasName) return name;
                return "未知设备";
            }
        }
    }

    public static class BackupHistoryManager
    {
        private static readonly object _fileLock = new object();
        public static string MockFilePath { get; set; } = null;
        public static Func<string, bool> MockIsDirectoryWritableFunc { get; set; } = null;

        public static event Action OnHistoryChanged;

        private static bool? _isBaseDirWritable = null;

        public static void ResetWritePermissionCache()
        {
            _isBaseDirWritable = null;
        }

        private static bool CanWriteToBaseDir()
        {
            if (_isBaseDirWritable.HasValue) return _isBaseDirWritable.Value;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            if (MockIsDirectoryWritableFunc != null)
            {
                bool mockResult = MockIsDirectoryWritableFunc(baseDir);
                _isBaseDirWritable = mockResult;
                return mockResult;
            }

            try
            {
                string testFile = Path.Combine(baseDir, ".test_hist_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "1");
                File.Delete(testFile);
                _isBaseDirWritable = true;
            }
            catch
            {
                _isBaseDirWritable = false;
            }
            return _isBaseDirWritable.Value;
        }

        public static string GetHistoryFilePath()
        {
            if (!string.IsNullOrEmpty(MockFilePath))
            {
                return MockFilePath;
            }

            if (CanWriteToBaseDir())
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BackupHistory.json");
            }

            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GetRickCourseware");
                if (!Directory.Exists(userDir))
                {
                    Directory.CreateDirectory(userDir);
                }
                return Path.Combine(userDir, "BackupHistory.json");
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BackupHistory.json");
            }
        }

        public static List<BackupRecord> LoadHistory()
        {
            lock (_fileLock)
            {
                try
                {
                    string path = GetHistoryFilePath();
                    if (!File.Exists(path) && string.IsNullOrEmpty(MockFilePath))
                    {
                        string fallbackUser = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "GetRickCourseware", "BackupHistory.json");
                        string baseFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BackupHistory.json");

                        if (File.Exists(fallbackUser))
                        {
                            path = fallbackUser;
                        }
                        else if (File.Exists(baseFile))
                        {
                            path = baseFile;
                        }
                    }

                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path, Encoding.UTF8);
                        return DeserializeJson(json);
                    }
                }
                catch
                {
                }
                return new List<BackupRecord>();
            }
        }

        public static void SaveHistory(List<BackupRecord> records)
        {
            lock (_fileLock)
            {
                string json = SerializeJson(records ?? new List<BackupRecord>());
                string primaryPath = GetHistoryFilePath();
                if (TryAtomicWrite(primaryPath, json))
                {
                    return;
                }

                if (string.IsNullOrEmpty(MockFilePath))
                {
                    try
                    {
                        _isBaseDirWritable = false;
                        string userDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "GetRickCourseware");
                        if (!Directory.Exists(userDir)) Directory.CreateDirectory(userDir);
                        string fallbackPath = Path.Combine(userDir, "BackupHistory.json");
                        TryAtomicWrite(fallbackPath, json);
                    }
                    catch { }
                }
            }
        }

        private static bool TryAtomicWrite(string targetPath, string content)
        {
            string tempFile = null;
            try
            {
                string dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                tempFile = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempFile, content, Encoding.UTF8);

                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { }
                }

                File.Move(tempFile, targetPath);
                return true;
            }
            catch
            {
                try
                {
                    File.WriteAllText(targetPath, content, Encoding.UTF8);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        public static void AddRecord(BackupRecord record)
        {
            if (record == null) return;
            lock (_fileLock)
            {
                var list = LoadHistory();
                list.Insert(0, record);
                if (list.Count > 1000)
                {
                    list.RemoveRange(1000, list.Count - 1000);
                }
                SaveHistory(list);
            }
            try { OnHistoryChanged?.Invoke(); } catch { }
        }

        public static bool UpdateStatusByFolderName(string folderName, string newStatus, string newTargetFolder = null)
        {
            if (string.IsNullOrEmpty(folderName)) return false;
            folderName = folderName.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(folderName)) return false;

            bool modified = false;
            lock (_fileLock)
            {
                var list = LoadHistory();

                foreach (var r in list)
                {
                    if (!string.IsNullOrEmpty(r.TargetFolder))
                    {
                        string trimmed = r.TargetFolder.TrimEnd('\\', '/');
                        int lastSep = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
                        string rFolder = lastSep >= 0 ? trimmed.Substring(lastSep + 1) : trimmed;

                        // 精确匹配末尾文件夹名称，避免 EndsWith 子串歧义
                        if (string.Equals(rFolder, folderName, StringComparison.OrdinalIgnoreCase))
                        {
                            r.Status = newStatus;
                            if (!string.IsNullOrEmpty(newTargetFolder))
                            {
                                r.TargetFolder = newTargetFolder;
                            }
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    SaveHistory(list);
                }
            }

            if (modified)
            {
                try { OnHistoryChanged?.Invoke(); } catch { }
            }
            return modified;
        }

        public static bool DeleteRecord(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            bool deleted = false;
            lock (_fileLock)
            {
                var list = LoadHistory();
                int removed = list.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    SaveHistory(list);
                    deleted = true;
                }
            }
            if (deleted)
            {
                try { OnHistoryChanged?.Invoke(); } catch { }
                return true;
            }
            return false;
        }

        public static void ClearHistory()
        {
            lock (_fileLock)
            {
                SaveHistory(new List<BackupRecord>());
            }
            try { OnHistoryChanged?.Invoke(); } catch { }
        }

        public static string EscapeJson(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char c = s[i];
                    switch (c)
                    {
                        case '\\': sb.Append('\\'); break;
                        case '\"': sb.Append('\"'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                string hex = s.Substring(i + 1, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

        public static string SerializeJson(List<BackupRecord> records)
        {
            if (records == null || records.Count == 0) return "[]";
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                sb.Append("  {");
                sb.AppendFormat("\"id\":\"{0}\",", EscapeJson(r.Id));
                sb.AppendFormat("\"timestamp\":\"{0}\",", r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendFormat("\"driveLetter\":\"{0}\",", EscapeJson(r.DriveLetter));
                sb.AppendFormat("\"usbName\":\"{0}\",", EscapeJson(r.UsbName));
                sb.AppendFormat("\"fileCount\":{0},", r.FileCount);
                sb.AppendFormat("\"targetFolder\":\"{0}\",", EscapeJson(r.TargetFolder));
                sb.AppendFormat("\"status\":\"{0}\"", EscapeJson(r.Status));
                sb.Append("}");
                if (i < records.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("]");
            return sb.ToString();
        }

        public static List<BackupRecord> DeserializeJson(string json)
        {
            var list = new List<BackupRecord>();
            if (string.IsNullOrEmpty(json)) return list;

            int i = 0;
            while (i < json.Length)
            {
                while (i < json.Length && json[i] != '{') i++;
                if (i >= json.Length) break;

                int objStart = i;
                int braceDepth = 0;
                bool inStr = false;
                bool isEscaped = false;

                while (i < json.Length)
                {
                    char ch = json[i];
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (ch == '\\' && inStr)
                    {
                        isEscaped = true;
                    }
                    else if (ch == '\"')
                    {
                        inStr = !inStr;
                    }
                    else if (!inStr)
                    {
                        if (ch == '{') braceDepth++;
                        else if (ch == '}')
                        {
                            braceDepth--;
                            if (braceDepth == 0)
                            {
                                i++;
                                break;
                            }
                        }
                    }
                    i++;
                }

                if (braceDepth == 0)
                {
                    string objJson = json.Substring(objStart, i - objStart);
                    var dict = ParseJsonObject(objJson);
                    var record = new BackupRecord();

                    if (dict.TryGetValue("id", out string id) && !string.IsNullOrEmpty(id))
                        record.Id = id;

                    if (dict.TryGetValue("timestamp", out string tsStr) && 
                        DateTime.TryParse(tsStr, out DateTime dt))
                        record.Timestamp = dt;

                    if (dict.TryGetValue("driveLetter", out string drive))
                        record.DriveLetter = drive;

                    if (dict.TryGetValue("usbName", out string usb))
                        record.UsbName = usb;

                    if (dict.TryGetValue("fileCount", out string fcStr) && int.TryParse(fcStr, out int fc))
                        record.FileCount = fc;

                    if (dict.TryGetValue("targetFolder", out string target))
                        record.TargetFolder = target;

                    if (dict.TryGetValue("status", out string status))
                        record.Status = status;

                    list.Add(record);
                }
            }

            return list;
        }

        private static Dictionary<string, string> ParseJsonObject(string objText)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(objText)) return dict;

            int i = 0;
            while (i < objText.Length)
            {
                while (i < objText.Length && objText[i] != '\"') i++;
                if (i >= objText.Length) break;
                i++; // skip opening quote of key

                int keyStart = i;
                bool keyEsc = false;
                while (i < objText.Length)
                {
                    if (keyEsc) { keyEsc = false; }
                    else if (objText[i] == '\\') { keyEsc = true; }
                    else if (objText[i] == '\"') { break; }
                    i++;
                }
                if (i >= objText.Length) break;
                string key = UnescapeJson(objText.Substring(keyStart, i - keyStart));
                i++; // skip closing quote of key

                while (i < objText.Length && objText[i] != ':') i++;
                if (i >= objText.Length) break;
                i++; // skip ':'

                while (i < objText.Length && char.IsWhiteSpace(objText[i])) i++;
                if (i >= objText.Length) break;

                string val = "";
                if (objText[i] == '\"')
                {
                    i++; // skip opening quote of val
                    int valStart = i;
                    bool esc = false;
                    while (i < objText.Length)
                    {
                        if (esc) { esc = false; }
                        else if (objText[i] == '\\') { esc = true; }
                        else if (objText[i] == '\"') { break; }
                        i++;
                    }
                    if (i < objText.Length)
                    {
                        val = UnescapeJson(objText.Substring(valStart, i - valStart));
                        i++; // skip closing quote of val
                    }
                }
                else
                {
                    int valStart = i;
                    while (i < objText.Length && objText[i] != ',' && objText[i] != '}' && !char.IsWhiteSpace(objText[i])) i++;
                    val = objText.Substring(valStart, i - valStart).Trim();
                }

                dict[key] = val;

                while (i < objText.Length && objText[i] != ',' && objText[i] != '}') i++;
                if (i < objText.Length && objText[i] == ',') i++;
            }
            return dict;
        }
    }
}
