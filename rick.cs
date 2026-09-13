// USBAutoCopy.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Management;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace USBAutoCopy
{
    public class USBAutoCopy : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private MainForm mainForm;
        private USBMonitor monitor;
        private bool isMonitoring = false;

        public USBAutoCopy()
        {
            // 初始化托盘图标
            try
            {
                trayIcon = new NotifyIcon()
                {
                    Icon = LoadAppIcon(),
                    Text = "获取Rick课件",
                    Visible = true
                };

                // 创建托盘菜单
                trayMenu = new ContextMenuStrip();
                trayMenu.Items.Add("显示主窗口", null, ShowMainForm);
                trayMenu.Items.Add("-");
                trayMenu.Items.Add("启动监控", null, StartMonitoring);
                trayMenu.Items.Add("停止监控", null, StopMonitoring);
                trayMenu.Items.Add("-");
                trayMenu.Items.Add("开机自启", null, ToggleAutoStart);
                trayMenu.Items.Add("-");
                trayMenu.Items.Add("退出", null, Exit);
                trayIcon.ContextMenuStrip = trayMenu;

                // 双击托盘图标显示主窗口
                trayIcon.DoubleClick += (s, e) => ShowMainForm(null, null);
            }
            catch (Exception ex)
            {
                Program.LogException("初始化托盘图标失败", ex);
            }

            // 创建主窗口
            mainForm = new MainForm(this);
            
            // 延迟启动，确保开机自启时系统环境就绪
            var startTimer = new System.Windows.Forms.Timer();
            startTimer.Interval = 3000;
            startTimer.Tick += (s, e) =>
            {
                try
                {
                    startTimer.Stop();
                    startTimer.Dispose();
                    if (trayIcon != null)
                    {
                        try
                        {
                            trayIcon.ShowBalloonTip(3000, "获取Rick课件", "程序已启动，正在监控U盘...", ToolTipIcon.Info);
                        }
                        catch { }
                    }
                    StartMonitoring(null, null);
                }
                catch (Exception ex)
                {
                    Program.LogException("定时启动监控失败", ex);
                }
            };
            startTimer.Start();
        }

        public static Icon LoadAppIcon()
        {
            // 1. 尝试从应用目录加载 app.ico
            try
            {
                string localIco = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(localIco))
                {
                    return new Icon(localIco);
                }
            }
            catch { }

            // 2. 尝试从嵌入资源加载 app.ico
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("USBAutoCopy.app.ico"))
                {
                    if (stream != null)
                    {
                        return new Icon(stream);
                    }
                }
            }
            catch { }

            // 3. 尝试从可执行文件提取关联图标
            try
            {
                string exePath = null;
                var prop = typeof(Environment).GetProperty("ProcessPath", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null)
                {
                    exePath = prop.GetValue(null) as string;
                }
                if (string.IsNullOrEmpty(exePath))
                {
                    exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                }
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Icon extracted = Icon.ExtractAssociatedIcon(exePath);
                    if (extracted != null) return extracted;
                }
            }
            catch { }

            // 4. 多层兜底
            return SystemIcons.Application ?? SystemIcons.Shield;
        }

        private void ShowMainForm(object sender, EventArgs e)
        {
            mainForm.Show();
            mainForm.WindowState = FormWindowState.Normal;
            mainForm.BringToFront();
        }

        public void StartMonitoring(object sender, EventArgs e)
        {
            if (!isMonitoring)
            {
                string backupPath = mainForm.GetBackupPath();
                if (string.IsNullOrEmpty(backupPath))
                {
                    mainForm.Show();
                    mainForm.AddLog("请先设置课件保存文件夹！");
                    return;
                }

                monitor = new USBMonitor(backupPath, mainForm.AddLog);
                monitor.Start();
                isMonitoring = true;
                mainForm.SetMonitoringStatus(true);
                UpdateTrayMenuStatus(true);
                trayIcon.ShowBalloonTip(1000, "获取Rick课件", "已开始监控U盘", ToolTipIcon.Info);
            }
        }

        public void StopMonitoring(object sender, EventArgs e)
        {
            if (isMonitoring && monitor != null)
            {
                monitor.Stop();
                isMonitoring = false;
                mainForm.SetMonitoringStatus(false);
                UpdateTrayMenuStatus(false);
                trayIcon.ShowBalloonTip(1000, "获取Rick课件", "已停止监控U盘", ToolTipIcon.Info);
            }
        }

        private void UpdateTrayMenuStatus(bool isRunning)
        {
            foreach (ToolStripItem item in trayMenu.Items)
            {
                if (item.Text == "启动监控")
                {
                    item.Enabled = !isRunning;
                }
                else if (item.Text == "停止监控")
                {
                    item.Enabled = isRunning;
                }
            }
        }

        private void ToggleAutoStart(object sender, EventArgs e)
        {
            bool isAutoStart = GetAutoStartStatus();
            SetAutoStart(!isAutoStart);
            
            ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
            if (menuItem != null)
            {
                menuItem.Checked = !isAutoStart;
            }
            
            mainForm.UpdateAutoStartStatus(!isAutoStart);
            string status = !isAutoStart ? "已开启" : "已关闭";
            trayIcon.ShowBalloonTip(1000, "获取Rick课件", $"开机自启{status}", ToolTipIcon.Info);
        }

        private bool GetAutoStartStatus()
        {
            try
            {
                string appPath = Application.ExecutablePath;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    string value = key?.GetValue("获取Rick课件") as string;
                    return value != null && value.Equals(appPath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                string appPath = Application.ExecutablePath;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                    {
                        key?.SetValue("获取Rick课件", appPath);
                        mainForm.AddLog("✓ 已设置开机自启");
                    }
                    else
                    {
                        key?.DeleteValue("获取Rick课件", false);
                        mainForm.AddLog("✗ 已取消开机自启");
                    }
                    Properties.Settings.Default.AutoStart = enable;
                }
            }
            catch (Exception ex)
            {
                mainForm.AddLog($"设置开机自启失败: {ex.Message}");
                MessageBox.Show($"设置开机自启失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            StopMonitoring(null, null);
            trayIcon.Visible = false;
            Application.Exit();
        }
    }

    public class MainForm : Form
    {
        private TextBox txtBackupPath;
        private Button btnBrowse, btnStart, btnStop;
        private ListBox lstLog;
        private Label lblStatus;
        private ProgressBar progressBar;
        private USBAutoCopy appContext;
        private Label lblDriveInfo;
        private CheckBox chkAutoStart;
        private Button btnClearLog;
        private ComboBox cmbDrives;
        private Button btnBlockDrive, btnManageBlock;
        private System.Windows.Forms.Timer driveRefreshTimer;
        private Dictionary<string, string> _driveMap = new Dictionary<string, string>(); // 显示文本 -> 唯一标识

        public MainForm(USBAutoCopy context)
        {
            appContext = context;
            InitializeComponent();
            LoadSettings();
            LoadAutoStartStatus();
        }

        private void InitializeComponent()
        {
            this.Text = "获取Rick课件 v2.5";
            this.Size = new Size(700, 660);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormClosing += MainForm_FormClosing;
            this.Icon = USBAutoCopy.LoadAppIcon();

            Label lblPath = new Label() 
            { 
                Text = "课件保存文件夹:", 
                Location = new Point(20, 20), 
                Size = new Size(120, 25), 
                Font = new Font("微软雅黑", 10, FontStyle.Bold) 
            };
            
            txtBackupPath = new TextBox() 
            { 
                Location = new Point(150, 18), 
                Size = new Size(400, 25), 
                ReadOnly = true 
            };
            
            btnBrowse = new Button() 
            { 
                Text = "浏览", 
                Location = new Point(560, 17), 
                Size = new Size(80, 30),
                BackColor = Color.LightBlue
            };
            btnBrowse.Click += BtnBrowse_Click;
            
            lblDriveInfo = new Label() 
            { 
                Text = "💡 提示：插入U盘后会自动复制，文件夹格式：日期_盘符_U盘名称", 
                Location = new Point(20, 60), 
                Size = new Size(650, 25),
                ForeColor = Color.Blue,
                Font = new Font("微软雅黑", 9)
            };
            
            chkAutoStart = new CheckBox()
            {
                Text = "开机自动启动",
                Location = new Point(20, 95),
                Size = new Size(120, 25),
                Font = new Font("微软雅黑", 9)
            };
            chkAutoStart.CheckedChanged += ChkAutoStart_CheckedChanged;
            
            btnStart = new Button() 
            { 
                Text = "启动监控", 
                Location = new Point(150, 92), 
                Size = new Size(100, 35), 
                BackColor = Color.LightGreen,
                FlatStyle = FlatStyle.Flat
            };
            btnStart.Click += BtnStart_Click;
            
            btnStop = new Button() 
            { 
                Text = "停止监控", 
                Location = new Point(260, 92), 
                Size = new Size(100, 35), 
                BackColor = Color.LightCoral, 
                Enabled = false,
                FlatStyle = FlatStyle.Flat
            };
            btnStop.Click += BtnStop_Click;
            
            lblStatus = new Label() 
            { 
                Text = "状态: 未监控", 
                Location = new Point(380, 100), 
                Size = new Size(150, 25), 
                ForeColor = Color.Red, 
                Font = new Font("微软雅黑", 9, FontStyle.Bold) 
            };

            // U盘选择行
            Label lblDriveSelect = new Label()
            {
                Text = "当前U盘:",
                Location = new Point(20, 140),
                Size = new Size(70, 25),
                Font = new Font("微软雅黑", 9, FontStyle.Bold)
            };

            cmbDrives = new ComboBox()
            {
                Location = new Point(95, 138),
                Size = new Size(320, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("微软雅黑", 9)
            };

            btnBlockDrive = new Button()
            {
                Text = "屏蔽此U盘",
                Location = new Point(425, 137),
                Size = new Size(100, 28),
                BackColor = Color.Orange,
                FlatStyle = FlatStyle.Flat
            };
            btnBlockDrive.Click += BtnBlockDrive_Click;

            btnManageBlock = new Button()
            {
                Text = "屏蔽管理",
                Location = new Point(535, 137),
                Size = new Size(100, 28),
                BackColor = Color.LightGray,
                FlatStyle = FlatStyle.Flat
            };
            btnManageBlock.Click += BtnManageBlock_Click;

            Label lblLog = new Label() 
            { 
                Text = "运行日志:", 
                Location = new Point(20, 178), 
                Size = new Size(80, 25), 
                Font = new Font("微软雅黑", 10, FontStyle.Bold) 
            };
            
            btnClearLog = new Button()
            {
                Text = "清空日志",
                Location = new Point(580, 176),
                Size = new Size(80, 25),
                BackColor = Color.LightGray
            };
            btnClearLog.Click += BtnClearLog_Click;
            
            lstLog = new ListBox() 
            { 
                Location = new Point(20, 208), 
                Size = new Size(640, 380), 
                Font = new Font("Consolas", 9),
                BackColor = Color.Black,
                ForeColor = Color.LightGreen
            };
            
            progressBar = new ProgressBar() 
            { 
                Location = new Point(20, 598), 
                Size = new Size(640, 20), 
                Style = ProgressBarStyle.Marquee, 
                Visible = false 
            };

            this.Controls.AddRange(new Control[] { 
                lblPath, txtBackupPath, btnBrowse, lblDriveInfo, 
                chkAutoStart, btnStart, btnStop, lblStatus,
                lblDriveSelect, cmbDrives, btnBlockDrive, btnManageBlock,
                lblLog, btnClearLog, lstLog, progressBar 
            });

            string savedPath = Properties.Settings.Default.BackupPath;
            if (!string.IsNullOrEmpty(savedPath) && Directory.Exists(savedPath))
            {
                txtBackupPath.Text = savedPath;
            }

            // 定时刷新 U 盘列表
            driveRefreshTimer = new System.Windows.Forms.Timer();
            driveRefreshTimer.Interval = 2000;
            driveRefreshTimer.Tick += (s, e) => RefreshDriveList();
            driveRefreshTimer.Start();
            RefreshDriveList();
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择课件保存文件夹";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtBackupPath.Text = dialog.SelectedPath;
                    SaveSettings();
                    AddLog($"📁 设置保存路径: {dialog.SelectedPath}");
                }
            }
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtBackupPath.Text))
            {
                MessageBox.Show("请先选择课件保存文件夹！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(txtBackupPath.Text))
            {
                try
                {
                    Directory.CreateDirectory(txtBackupPath.Text);
                    AddLog($"📁 创建保存目录: {txtBackupPath.Text}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法创建保存目录：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            appContext.StartMonitoring(null, null);
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            appContext.StopMonitoring(null, null);
        }

        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            lstLog.Items.Clear();
            AddLog("日志已清空");
        }

        private void RefreshDriveList()
        {
            var blocked = Properties.Settings.Default.GetBlockedList();
            _driveMap.Clear();

            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType == DriveType.Removable && d.IsReady)
                    {
                        string drivePath = d.Name.TrimEnd('\\');
                        string label = string.IsNullOrEmpty(d.VolumeLabel) ? "未命名U盘" : d.VolumeLabel;
                        string uniqueId = USBMonitor.GetDriveUniqueId(drivePath);
                        bool isBlocked = blocked.Contains(uniqueId);
                        string display = isBlocked ? $"{drivePath} - {label} [已屏蔽]" : $"{drivePath} - {label}";
                        _driveMap[display] = uniqueId;
                    }
                }
            }
            catch { }

            string selected = cmbDrives.SelectedItem as string;
            cmbDrives.Items.Clear();
            if (_driveMap.Count == 0)
            {
                cmbDrives.Items.Add("（无可移动设备）");
            }
            else
            {
                foreach (var key in _driveMap.Keys) cmbDrives.Items.Add(key);
            }

            if (selected != null && cmbDrives.Items.Contains(selected))
                cmbDrives.SelectedItem = selected;
            else if (cmbDrives.Items.Count > 0)
                cmbDrives.SelectedIndex = 0;
        }

        private void BtnBlockDrive_Click(object sender, EventArgs e)
        {
            string display = cmbDrives.SelectedItem as string;
            if (display == null || display == "（无可移动设备）") return;

            if (!_driveMap.TryGetValue(display, out string uniqueId)) return;

            // 提取友好显示名（去掉 [已屏蔽]）
            string friendlyName = display.Replace(" [已屏蔽]", "").Trim();

            var blocked = Properties.Settings.Default.GetBlockedList();
            if (blocked.Contains(uniqueId))
            {
                MessageBox.Show($"「{friendlyName}」已在屏蔽列表中。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show($"确定要屏蔽「{friendlyName}」吗？插入后将不再自动复制。",
                "确认屏蔽", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                Properties.Settings.Default.AddBlocked(uniqueId);
                AddLog($"🚫 已屏蔽 U 盘: {friendlyName}");
                RefreshDriveList();
            }
        }

        private void BtnManageBlock_Click(object sender, EventArgs e)
        {
            using (var form = new BlocklistForm())
            {
                form.ShowDialog(this);
                RefreshDriveList();
            }
        }

        private void ChkAutoStart_CheckedChanged(object sender, EventArgs e)
        {
            var method = appContext.GetType().GetMethod("SetAutoStart", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(appContext, new object[] { chkAutoStart.Checked });
            }
        }

        public string GetBackupPath()
        {
            return txtBackupPath.Text;
        }

        public void AddLog(string message)
        {
            if (lstLog.InvokeRequired)
            {
                lstLog.Invoke(new Action<string>(AddLog), message);
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            string entry = $"[{time}] {message}";
            lstLog.Items.Insert(0, entry);
            if (lstLog.Items.Count > 500)
                lstLog.Items.RemoveAt(lstLog.Items.Count - 1);

            try
            {
                string logsDir = GetLogsDirectory();
                string logFile = Path.Combine(logsDir, $"{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private static string GetLogsDirectory()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string logsDir = Path.Combine(exeDir, "logs");
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }
                return logsDir;
            }
            catch
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GetRickCourseware", "logs");
                if (!Directory.Exists(userDir))
                {
                    Directory.CreateDirectory(userDir);
                }
                return userDir;
            }
        }

        public void SetMonitoringStatus(bool isMonitoring)
        {
            if (btnStart.InvokeRequired)
            {
                btnStart.Invoke(new Action<bool>(SetMonitoringStatus), isMonitoring);
                return;
            }

            btnStart.Enabled = !isMonitoring;
            btnStop.Enabled = isMonitoring;
            lblStatus.Text = isMonitoring ? "状态: 监控中 ✓" : "状态: 未监控 ✗";
            lblStatus.ForeColor = isMonitoring ? Color.Green : Color.Red;
        }

        public void UpdateAutoStartStatus(bool isEnabled)
        {
            if (chkAutoStart.InvokeRequired)
            {
                chkAutoStart.Invoke(new Action<bool>(UpdateAutoStartStatus), isEnabled);
                return;
            }
            
            chkAutoStart.Checked = isEnabled;
        }

        public void ShowProgress(bool show)
        {
            if (progressBar.InvokeRequired)
            {
                progressBar.Invoke(new Action<bool>(ShowProgress), show);
                return;
            }

            progressBar.Visible = show;
        }

        private void LoadAutoStartStatus()
        {
            chkAutoStart.CheckedChanged -= ChkAutoStart_CheckedChanged;
            chkAutoStart.Checked = Properties.Settings.Default.AutoStart;
            chkAutoStart.CheckedChanged += ChkAutoStart_CheckedChanged;
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.BackupPath = txtBackupPath.Text;
        }

        private void LoadSettings()
        {
            // 目前设置已在InitializeComponent中加载
            // 未来可以在此加载其他设置项
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                AddLog("程序已最小化到托盘，双击图标可重新打开");
            }
        }
    }

    public class BlocklistForm : Form
    {
        private ListBox lstBlocked;
        private Button btnRemove, btnClose;

        public BlocklistForm()
        {
            this.Text = "屏蔽管理";
            this.Size = new Size(400, 340);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            Label lbl = new Label()
            {
                Text = "已屏蔽的 U 盘（选中后可解除屏蔽）:",
                Location = new Point(15, 15),
                Size = new Size(360, 20),
                Font = new Font("微软雅黑", 9)
            };

            lstBlocked = new ListBox()
            {
                Location = new Point(15, 40),
                Size = new Size(355, 200),
                Font = new Font("微软雅黑", 9)
            };

            btnRemove = new Button()
            {
                Text = "解除屏蔽",
                Location = new Point(15, 255),
                Size = new Size(110, 32),
                BackColor = Color.LightGreen,
                FlatStyle = FlatStyle.Flat
            };
            btnRemove.Click += BtnRemove_Click;

            btnClose = new Button()
            {
                Text = "关闭",
                Location = new Point(260, 255),
                Size = new Size(110, 32),
                BackColor = Color.LightGray,
                FlatStyle = FlatStyle.Flat
            };
            btnClose.Click += (s, e) => this.Close();

            this.Controls.AddRange(new Control[] { lbl, lstBlocked, btnRemove, btnClose });
            RefreshList();
        }

        private void RefreshList()
        {
            lstBlocked.Items.Clear();
            foreach (var item in Properties.Settings.Default.GetBlockedList())
            {
                // 显示卷标部分（| 前），序列号作为内部标识
                string display = item.Contains("|") ? item.Split('|')[0] + $"（序列号: {item.Split('|')[1]}）" : item;
                lstBlocked.Items.Add(new BlocklistItem(display, item));
            }
            if (lstBlocked.Items.Count == 0)
                lstBlocked.Items.Add(new BlocklistItem("（暂无屏蔽记录）", null));
        }

        private void BtnRemove_Click(object sender, EventArgs e)
        {
            var selected = lstBlocked.SelectedItem as BlocklistItem;
            if (selected == null || selected.UniqueId == null) return;

            Properties.Settings.Default.RemoveBlocked(selected.UniqueId);
            RefreshList();
        }

        private class BlocklistItem
        {
            public string Display { get; }
            public string UniqueId { get; }
            public BlocklistItem(string display, string uniqueId) { Display = display; UniqueId = uniqueId; }
            public override string ToString() => Display;
        }
    }



    public static class Program
    {
        [STAThread]
        static void Main()
        {
            // 全局未捕获异常拦截与处理
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) =>
            {
                LogException("UI线程未捕获异常", e.Exception);
                MessageBox.Show($"程序发生错误：\n{e.Exception.Message}\n\n详细信息已记录至 crash.log", 
                    "获取Rick课件 - 运行异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                LogException("非UI线程未捕获异常", ex);
                MessageBox.Show($"程序遇到未知严重错误：\n{ex?.Message}\n\n详细信息已记录至 crash.log", 
                    "获取Rick课件 - 崩溃拦截", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                using (var mutex = new System.Threading.Mutex(true, "获取Rick课件_SingleInstance", out bool createdNew))
                {
                    if (!createdNew)
                    {
                        MessageBox.Show("程序已在后台运行中！\n请查看屏幕右下角任务栏托盘图标（可能在折叠小箭头 ^ 内部）。", 
                            "获取Rick课件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    try
                    {
                        var method = typeof(Application).GetMethod("SetHighDpiMode",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (method != null)
                        {
                            var highDpiModeType = Type.GetType("System.Windows.Forms.HighDpiMode, System.Windows.Forms");
                            if (highDpiModeType != null)
                            {
                                var systemAware = Enum.Parse(highDpiModeType, "SystemAware");
                                method.Invoke(null, new object[] { systemAware });
                            }
                        }
                    }
                    catch { }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new USBAutoCopy());
                }
            }
            catch (Exception ex)
            {
                LogException("Main函数致命启动异常", ex);
                MessageBox.Show($"程序启动失败：\n{ex.Message}\n\n详细错误已记录至 crash.log", 
                    "获取Rick课件 - 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void LogException(string tag, Exception ex)
        {
            if (ex == null) return;
            string logText = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n";

            // 1. 优先尝试写入程序当前目录
            try
            {
                string localLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.AppendAllText(localLog, logText);
            }
            catch { }

            // 2. 尝试写入用户本地 AppData 目录
            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GetRickCourseware");
                if (!Directory.Exists(userDir))
                {
                    Directory.CreateDirectory(userDir);
                }
                string userLog = Path.Combine(userDir, "crash.log");
                File.AppendAllText(userLog, logText);
            }
            catch { }

            // 3. 尝试写入系统临时目录
            try
            {
                string tempLog = Path.Combine(Path.GetTempPath(), "RickCourseware_crash.log");
                File.AppendAllText(tempLog, logText);
            }
            catch { }
        }
    }
}