// USBAutoCopy.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

[assembly: AssemblyTitle("获取Rick课件")]
[assembly: AssemblyProduct("获取Rick课件")]
[assembly: AssemblyCompany("RS基金会")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]

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
                trayMenu.Items.Add("查看备份历史", null, (s, e) =>
                {
                    ShowMainForm(null, null);
                    mainForm?.SwitchTab(1);
                });
                trayMenu.Items.Add("-");
                trayMenu.Items.Add("启动监控", null, StartMonitoring);
                trayMenu.Items.Add("停止监控", null, StopMonitoring);
                var autoStartMenuItem = new ToolStripMenuItem("开机自启", null, ToggleAutoStart)
                {
                    Checked = GetAutoStartStatus()
                };
                trayMenu.Items.Add(autoStartMenuItem);
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

            // 创建主窗口但绝不显示，开机彻底静默常驻托盘
            mainForm = new MainForm(this);
            try { _ = mainForm.Handle; } catch { }
            
            // 延迟启动，确保开机自启时系统环境就绪
            var startTimer = new System.Windows.Forms.Timer();
            startTimer.Interval = 3000;
            startTimer.Tick += (s, e) =>
            {
                try
                {
                    startTimer.Stop();
                    startTimer.Dispose();
                    StartMonitoring(null, null);
                }
                catch (Exception ex)
                {
                    Program.LogException("定时启动监控失败", ex);
                }
            };
            startTimer.Start();
        }

        private string lastNotificationMessage = null;
        private DateTime lastNotificationTime = DateTime.MinValue;

        public void ShowNotification(string title, string message)
        {
            try
            {
                if (mainForm != null && mainForm.InvokeRequired)
                {
                    mainForm.BeginInvoke(new Action(() => ShowNotification(title, message)));
                    return;
                }

                // 抑制短时间内相同内容的重复通知，避免 Win10 操作中心弹出重复卡片
                if (message == lastNotificationMessage && (DateTime.Now - lastNotificationTime).TotalSeconds < 5)
                {
                    return;
                }
                lastNotificationMessage = message;
                lastNotificationTime = DateTime.Now;

                // 1. 触发 WinForms 托盘气泡（作为任务栏右下角伴随提示）
                if (trayIcon != null)
                {
                    try
                    {
                        trayIcon.BalloonTipTitle = title;
                        trayIcon.BalloonTipText = message;
                        trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                        trayIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
                    }
                    catch { }
                }

                // 2. 触发 Windows 10/11 原生系统 Toast 通知（走 Win10 通知中心与屏幕右下角横幅）
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        USBMonitor.ShowWindowsToastNotification(title, message);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Program.LogException("显示通知失败", ex);
            }
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
            mainForm.ShowInTaskbar = true;
            mainForm.Show();
            mainForm.WindowState = FormWindowState.Normal;
            mainForm.BringToFront();
            mainForm.Activate();
        }

        public void StartMonitoring(object sender, EventArgs e)
        {
            if (!isMonitoring)
            {
                string backupPath = mainForm.GetBackupPath();
                if (string.IsNullOrEmpty(backupPath))
                {
                    // 绝不弹出主窗口，开机与启动阶段彻底静默
                    mainForm.AddLog("未配置课件保存路径，请双击托盘图标进行设置");
                    ShowNotification("获取Rick课件", "未配置课件保存路径，请双击托盘图标打开主界面设置");
                    return;
                }

                monitor = new USBMonitor(backupPath, mainForm.AddLog, ShowNotification);
                monitor.Start();
                isMonitoring = true;
                mainForm.SetMonitoringStatus(true);
                UpdateTrayMenuStatus(true);
                ShowNotification("获取Rick课件", "程序已启动常驻后台，正在监控U盘...");
            }
        }

        public void UpdateBackupPath(string newPath)
        {
            if (monitor != null)
            {
                monitor.UpdateBackupPath(newPath);
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
                ShowNotification("获取Rick课件", "已停止监控U盘");
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

    public class DpiScaler
    {
        private readonly Form form;
        private readonly Size baseClientSize;
        private readonly string formFontName;
        private readonly float formFontSize;
        private readonly FontStyle formFontStyle;
        private readonly Dictionary<Control, ControlLayoutInfo> controlLayouts = new Dictionary<Control, ControlLayoutInfo>();
        private float currentScale = 1.0f;

        public float CurrentScale => currentScale;
        public event Action<float> OnScaleApplied;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private class ControlLayoutInfo
        {
            public Rectangle Bounds;
            public string FontName;
            public float FontSize;
            public FontStyle FontStyle;
        }

        public DpiScaler(Form form)
        {
            this.form = form;
            this.form.AutoScaleMode = AutoScaleMode.None;
            this.baseClientSize = form.ClientSize;
            this.formFontName = form.Font != null ? form.Font.FontFamily.Name : "微软雅黑";
            this.formFontSize = form.Font != null ? form.Font.Size : 9f;
            this.formFontStyle = form.Font != null ? form.Font.Style : FontStyle.Regular;
            RecordLayout(form);
        }

        private void RecordLayout(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                controlLayouts[c] = new ControlLayoutInfo
                {
                    Bounds = c.Bounds,
                    FontName = c.Font != null ? c.Font.FontFamily.Name : "微软雅黑",
                    FontSize = c.Font != null ? c.Font.Size : 9f,
                    FontStyle = c.Font != null ? c.Font.Style : FontStyle.Regular
                };
                if (c.HasChildren)
                {
                    RecordLayout(c);
                }
            }
        }

        public static Rectangle CalculateScaledBounds(Rectangle orig, float scale)
        {
            return new Rectangle(
                (int)Math.Round(orig.X * scale),
                (int)Math.Round(orig.Y * scale),
                (int)Math.Round(orig.Width * scale),
                (int)Math.Round(orig.Height * scale)
            );
        }

        public static Size CalculateScaledSize(Size orig, float scale)
        {
            return new Size(
                (int)Math.Round(orig.Width * scale),
                (int)Math.Round(orig.Height * scale)
            );
        }

        public static float CalculateScaledFontSize(float origSize, float scale)
        {
            return origSize * scale;
        }

        public void ApplyScale(float scale)
        {
            if (scale <= 0) scale = 1.0f;
            currentScale = scale;
            form.SuspendLayout();

            // 临时重置 MinimumSize 和 MaximumSize，防止向下缩放或多次缩放时尺寸被死锁
            form.MinimumSize = Size.Empty;
            if (form.FormBorderStyle == FormBorderStyle.FixedDialog)
            {
                form.MaximumSize = Size.Empty;
            }

            var anchors = new Dictionary<Control, AnchorStyles>();
            foreach (var c in controlLayouts.Keys)
            {
                anchors[c] = c.Anchor;
                c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            }

            form.ClientSize = CalculateScaledSize(baseClientSize, scale);

            try
            {
                form.Font = new Font(formFontName, CalculateScaledFontSize(formFontSize, scale), formFontStyle);
            }
            catch { }

            foreach (var kvp in controlLayouts)
            {
                var c = kvp.Key;
                var info = kvp.Value;

                var newBounds = CalculateScaledBounds(info.Bounds, scale);
                c.SetBounds(newBounds.X, newBounds.Y, newBounds.Width, newBounds.Height);

                try
                {
                    c.Font = new Font(info.FontName, CalculateScaledFontSize(info.FontSize, scale), c.Font != null ? c.Font.Style : info.FontStyle);
                }
                catch { }
            }

            foreach (var kvp in anchors)
            {
                kvp.Key.Anchor = kvp.Value;
            }

            form.MinimumSize = form.Size;
            if (form.FormBorderStyle == FormBorderStyle.FixedDialog)
            {
                form.MaximumSize = form.Size;
            }

            form.ResumeLayout(true);
            try { OnScaleApplied?.Invoke(scale); } catch { }
        }

        public static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

#if WINDOWS
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const int LOGPIXELSX = 88;
#endif

        public static float GetDpiScale(Form form)
        {
            // 需求要求：使用 CreateGraphics().DpiX / 96.0f 动态计算缩放比率
            try
            {
                if (form != null)
                {
                    using (Graphics g = form.CreateGraphics())
                    {
                        if (g != null && g.DpiX > 0)
                        {
                            return g.DpiX / 96.0f;
                        }
                    }
                }
            }
            catch { }

#if WINDOWS
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    if (form != null && form.IsHandleCreated)
                    {
                        try
                        {
                            uint dpi = GetDpiForWindow(form.Handle);
                            if (dpi > 0) return dpi / 96.0f;
                        }
                        catch { }
                    }

                    IntPtr hdc = GetDC(IntPtr.Zero);
                    if (hdc != IntPtr.Zero)
                    {
                        try
                        {
                            int dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                            if (dpiX > 0) return dpiX / 96.0f;
                        }
                        finally
                        {
                            ReleaseDC(IntPtr.Zero, hdc);
                        }
                    }
                }
            }
            catch { }
#endif
            return 1.0f;
        }
    }

    public class MainForm : Form
    {
        private Label lblPath;
        private TextBox txtBackupPath;
        private Button btnBrowse, btnStart, btnStop;
        private ListBox lstLog;
        private Label lblStatus;
        private ProgressBar progressBar;
        private USBAutoCopy appContext;
        private Label lblDriveInfo;
        private CheckBox chkAutoStart;
        private Button btnClearLog;
        private Label lblDriveSelect;
        private ComboBox cmbDrives;
        private Button btnBlockDrive, btnManageBlock;
        private Label lblLog;
        private System.Windows.Forms.Timer driveRefreshTimer;
        private Dictionary<string, string> _driveMap = new Dictionary<string, string>();
        private DpiScaler dpiScaler;

        // 现代化卡片式容器与历史记录控件
        private Panel pnlHeader;
        private PictureBox picLogo;
        private Label lblAppTitle;
        private Label lblAppSubtitle;
        private Panel pnlStatusBadge;

        private Panel pnlNav;
        private Button btnTabMonitor;
        private Button btnTabHistory;

        private Panel pnlMonitorView;
        private Panel cardConfig;
        private Panel cardLog;
        private Button btnOpenLogs;

        private Panel pnlHistoryView;
        private Panel cardHistory;
        private Label lblHistorySummary;
        private Label lblHistoryHint;
        private Button btnOpenHistoryFolder;
        private Button btnRefreshHistory;
        private Button btnClearHistory;
        private ListView lvHistory;

        private static readonly int[] BaseHistoryColumnWidths = new int[] { 140, 120, 70, 80, 202 };
        private bool _isMonitoringCurrently = false;
        private Action _historyChangeHandler;
        private int _sortColumn = -1;
        private bool _sortAsc = true;

        public MainForm(USBAutoCopy context)
        {
            appContext = context;
            InitializeComponent();
            LoadSettings();
            LoadAutoStartStatus();
        }

        private void InitializeComponent()
        {
            this.Text = "获取Rick课件";
            this.ClientSize = new Size(680, 630);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormClosing += MainForm_FormClosing;
            this.Icon = USBAutoCopy.LoadAppIcon();
            this.ShowInTaskbar = false;
            this.BackColor = ColorTranslator.FromHtml("#F8FAFC");
            this.Font = new Font("微软雅黑", 9f, FontStyle.Regular);

            // 1. 顶部现代化品牌与状态横幅
            pnlHeader = new Panel()
            {
                Location = new Point(0, 0),
                Size = new Size(680, 64),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            pnlHeader.Paint += (s, pe) =>
            {
                using (var pen = new Pen(ColorTranslator.FromHtml("#E2E8F0"), 1))
                {
                    pe.Graphics.DrawLine(pen, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1);
                }
            };

            picLogo = new PictureBox()
            {
                Location = new Point(16, 14),
                Size = new Size(36, 36),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            try
            {
                var icon = USBAutoCopy.LoadAppIcon();
                if (icon != null) picLogo.Image = icon.ToBitmap();
            }
            catch { }

            lblAppTitle = new Label()
            {
                Text = "获取Rick课件",
                Location = new Point(60, 12),
                Size = new Size(180, 22),
                Font = new Font("微软雅黑", 12f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#0F172A")
            };

            lblAppSubtitle = new Label()
            {
                Text = "春晖中学课件智能备份与同步系统 v3.0",
                Location = new Point(60, 36),
                Size = new Size(280, 18),
                Font = new Font("微软雅黑", 8.5f, FontStyle.Regular),
                ForeColor = ColorTranslator.FromHtml("#64748B")
            };

            pnlStatusBadge = new Panel()
            {
                Location = new Point(504, 16),
                Size = new Size(158, 32),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            pnlStatusBadge.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = dpiScaler != null ? dpiScaler.CurrentScale : 1.0f;
                int radius = (int)Math.Round(12 * scale);
                var rect = new Rectangle(0, 0, pnlStatusBadge.Width - 1, pnlStatusBadge.Height - 1);
                using (var path = DpiScaler.CreateRoundedRectanglePath(rect, radius))
                using (var brush = new SolidBrush(_isMonitoringCurrently ? ColorTranslator.FromHtml("#DEF7EC") : ColorTranslator.FromHtml("#F1F5F9")))
                using (var pen = new Pen(_isMonitoringCurrently ? ColorTranslator.FromHtml("#A7F3D0") : ColorTranslator.FromHtml("#CBD5E1"), 1))
                {
                    pe.Graphics.FillPath(brush, path);
                    pe.Graphics.DrawPath(pen, path);
                }
            };

            lblStatus = new Label()
            {
                Text = "○ 监控已停止",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                BackColor = Color.Transparent
            };
            pnlStatusBadge.Controls.Add(lblStatus);
            pnlHeader.Controls.AddRange(new Control[] { picLogo, lblAppTitle, lblAppSubtitle, pnlStatusBadge });

            // 2. 导航切换选项卡 (Segmented Switcher)
            pnlNav = new Panel()
            {
                Location = new Point(18, 70),
                Size = new Size(644, 34),
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            btnTabMonitor = new Button()
            {
                Text = "⚡ 实时监控",
                Location = new Point(0, 0),
                Size = new Size(115, 32),
                BackColor = Color.White,
                ForeColor = ColorTranslator.FromHtml("#0F172A"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnTabMonitor.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");
            btnTabMonitor.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#F8FAFC");
            btnTabMonitor.Click += (s, e) => SwitchTab(0);

            btnTabHistory = new Button()
            {
                Text = "📋 备份历史 (0)",
                Location = new Point(122, 0),
                Size = new Size(135, 32),
                BackColor = ColorTranslator.FromHtml("#F1F5F9"),
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9.5f, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            btnTabHistory.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#E2E8F0");
            btnTabHistory.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#E2E8F0");
            btnTabHistory.Click += (s, e) => SwitchTab(1);

            pnlNav.Controls.AddRange(new Control[] { btnTabMonitor, btnTabHistory });

            // 3. 监控视图容器
            pnlMonitorView = new Panel()
            {
                Location = new Point(18, 110),
                Size = new Size(644, 506),
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // 3.1 监控配置卡片
            cardConfig = new Panel()
            {
                Location = new Point(0, 0),
                Size = new Size(644, 156),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            cardConfig.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = dpiScaler != null ? dpiScaler.CurrentScale : 1.0f;
                int radius = (int)Math.Round(8 * scale);
                var rect = new Rectangle(0, 0, cardConfig.Width - 1, cardConfig.Height - 1);
                using (var path = DpiScaler.CreateRoundedRectanglePath(rect, radius))
                using (var pen = new Pen(ColorTranslator.FromHtml("#E2E8F0"), 1))
                {
                    pe.Graphics.DrawPath(pen, path);
                }
            };

            lblPath = new Label()
            {
                Text = "保存目录:",
                Location = new Point(16, 16),
                Size = new Size(72, 22),
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#334155")
            };

            txtBackupPath = new TextBox()
            {
                Location = new Point(90, 14),
                Size = new Size(450, 24),
                ReadOnly = true,
                BackColor = ColorTranslator.FromHtml("#F8FAFC"),
                ForeColor = ColorTranslator.FromHtml("#1E293B"),
                Font = new Font("微软雅黑", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };

            btnBrowse = new Button()
            {
                Text = "浏览...",
                Location = new Point(548, 13),
                Size = new Size(80, 26),
                BackColor = ColorTranslator.FromHtml("#EFF6FF"),
                ForeColor = ColorTranslator.FromHtml("#1D4ED8"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnBrowse.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#BFDBFE");
            btnBrowse.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#DBEAFE");
            btnBrowse.Click += BtnBrowse_Click;

            lblDriveSelect = new Label()
            {
                Text = "当前U盘:",
                Location = new Point(16, 50),
                Size = new Size(72, 22),
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#334155")
            };

            cmbDrives = new ComboBox()
            {
                Location = new Point(90, 48),
                Size = new Size(340, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("微软雅黑", 9f)
            };

            btnBlockDrive = new Button()
            {
                Text = "🚫 屏蔽此盘",
                Location = new Point(438, 47),
                Size = new Size(98, 26),
                BackColor = ColorTranslator.FromHtml("#FEF3C7"),
                ForeColor = ColorTranslator.FromHtml("#92400E"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f),
                Cursor = Cursors.Hand
            };
            btnBlockDrive.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#FDE68A");
            btnBlockDrive.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#FDE68A");
            btnBlockDrive.Click += BtnBlockDrive_Click;

            btnManageBlock = new Button()
            {
                Text = "屏蔽管理",
                Location = new Point(544, 47),
                Size = new Size(84, 26),
                BackColor = ColorTranslator.FromHtml("#F1F5F9"),
                ForeColor = ColorTranslator.FromHtml("#475569"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f),
                Cursor = Cursors.Hand
            };
            btnManageBlock.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");
            btnManageBlock.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#E2E8F0");
            btnManageBlock.Click += BtnManageBlock_Click;

            chkAutoStart = new CheckBox()
            {
                Text = "开机自动启动",
                Location = new Point(16, 86),
                Size = new Size(110, 24),
                Font = new Font("微软雅黑", 9f),
                ForeColor = ColorTranslator.FromHtml("#334155")
            };
            chkAutoStart.CheckedChanged += ChkAutoStart_CheckedChanged;

            btnStart = new Button()
            {
                Text = "▶ 启动监控",
                Location = new Point(135, 82),
                Size = new Size(100, 32),
                BackColor = ColorTranslator.FromHtml("#2563EB"),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#1D4ED8");
            btnStart.Click += BtnStart_Click;

            btnStop = new Button()
            {
                Text = "⏹ 停止监控",
                Location = new Point(245, 82),
                Size = new Size(100, 32),
                BackColor = ColorTranslator.FromHtml("#EF4444"),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9.5f, FontStyle.Bold),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            btnStop.FlatAppearance.BorderSize = 0;
            btnStop.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#DC2626");
            btnStop.Click += BtnStop_Click;

            lblDriveInfo = new Label()
            {
                Text = "💡 插入U盘即自动静默备份；网络离线自动暂存并在恢复后自动同步",
                Location = new Point(16, 124),
                Size = new Size(612, 18),
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                Font = new Font("微软雅黑", 8.5f)
            };

            lblPath.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            txtBackupPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblDriveSelect.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            cmbDrives.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnBlockDrive.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnManageBlock.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkAutoStart.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnStart.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnStop.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            lblDriveInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            cardConfig.Controls.AddRange(new Control[] {
                lblPath, txtBackupPath, btnBrowse,
                lblDriveSelect, cmbDrives, btnBlockDrive, btnManageBlock,
                chkAutoStart, btnStart, btnStop, lblDriveInfo
            });

            // 3.2 运行日志卡片 (现代浅灰底，深蓝灰文字)
            cardLog = new Panel()
            {
                Location = new Point(0, 164),
                Size = new Size(644, 332),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            cardLog.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = dpiScaler != null ? dpiScaler.CurrentScale : 1.0f;
                int radius = (int)Math.Round(8 * scale);
                var rect = new Rectangle(0, 0, cardLog.Width - 1, cardLog.Height - 1);
                using (var path = DpiScaler.CreateRoundedRectanglePath(rect, radius))
                using (var pen = new Pen(ColorTranslator.FromHtml("#E2E8F0"), 1))
                {
                    pe.Graphics.DrawPath(pen, path);
                }
            };

            lblLog = new Label()
            {
                Text = "运行日志",
                Location = new Point(16, 10),
                Size = new Size(80, 20),
                Font = new Font("微软雅黑", 9.5f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#1E293B"),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            btnOpenLogs = new Button()
            {
                Text = "打开日志目录",
                Location = new Point(440, 7),
                Size = new Size(100, 24),
                BackColor = ColorTranslator.FromHtml("#F8FAFC"),
                ForeColor = ColorTranslator.FromHtml("#475569"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 8.5f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOpenLogs.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#E2E8F0");
            btnOpenLogs.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#E2E8F0");
            btnOpenLogs.Click += (s, e) =>
            {
                try
                {
                    string dir = GetLogsDirectory();
                    OpenFolderInExplorer(dir);
                }
                catch { }
            };

            btnClearLog = new Button()
            {
                Text = "清空日志",
                Location = new Point(548, 7),
                Size = new Size(80, 24),
                BackColor = ColorTranslator.FromHtml("#F8FAFC"),
                ForeColor = ColorTranslator.FromHtml("#475569"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 8.5f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnClearLog.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#E2E8F0");
            btnClearLog.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#E2E8F0");
            btnClearLog.Click += BtnClearLog_Click;

            lstLog = new ListBox()
            {
                Location = new Point(16, 36),
                Size = new Size(612, 282),
                Font = new Font("Consolas", 9f),
                BackColor = ColorTranslator.FromHtml("#F8FAFC"),
                ForeColor = ColorTranslator.FromHtml("#1E293B"),
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            cardLog.Controls.AddRange(new Control[] { lblLog, btnOpenLogs, btnClearLog, lstLog });

            progressBar = new ProgressBar()
            {
                Location = new Point(0, 500),
                Size = new Size(644, 6),
                Style = ProgressBarStyle.Marquee,
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            pnlMonitorView.Controls.AddRange(new Control[] { cardConfig, cardLog, progressBar });

            // 4. 备份历史视图容器
            pnlHistoryView = new Panel()
            {
                Location = new Point(18, 110),
                Size = new Size(644, 506),
                BackColor = Color.Transparent,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            cardHistory = new Panel()
            {
                Location = new Point(0, 0),
                Size = new Size(644, 506),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            cardHistory.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = dpiScaler != null ? dpiScaler.CurrentScale : 1.0f;
                int radius = (int)Math.Round(8 * scale);
                var rect = new Rectangle(0, 0, cardHistory.Width - 1, cardHistory.Height - 1);
                using (var path = DpiScaler.CreateRoundedRectanglePath(rect, radius))
                using (var pen = new Pen(ColorTranslator.FromHtml("#E2E8F0"), 1))
                {
                    pe.Graphics.DrawPath(pen, path);
                }
            };

            lblHistorySummary = new Label()
            {
                Text = "累计备份 0 次 · 成功 0 · 本地暂存 0 · 已同步 0",
                Location = new Point(16, 12),
                Size = new Size(320, 22),
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#334155"),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            btnOpenHistoryFolder = new Button()
            {
                Text = "📂 打开所在文件夹",
                Location = new Point(340, 8),
                Size = new Size(130, 26),
                BackColor = ColorTranslator.FromHtml("#2563EB"),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOpenHistoryFolder.FlatAppearance.BorderSize = 0;
            btnOpenHistoryFolder.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#1D4ED8");
            btnOpenHistoryFolder.Click += (s, e) => OpenSelectedHistoryFolder();

            btnRefreshHistory = new Button()
            {
                Text = "🔄 刷新",
                Location = new Point(478, 8),
                Size = new Size(70, 26),
                BackColor = ColorTranslator.FromHtml("#F1F5F9"),
                ForeColor = ColorTranslator.FromHtml("#334155"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnRefreshHistory.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");
            btnRefreshHistory.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#E2E8F0");
            btnRefreshHistory.Click += (s, e) => RefreshHistoryView();

            btnClearHistory = new Button()
            {
                Text = "🧹 清空",
                Location = new Point(556, 8),
                Size = new Size(72, 26),
                BackColor = ColorTranslator.FromHtml("#FEE2E2"),
                ForeColor = ColorTranslator.FromHtml("#DC2626"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnClearHistory.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#FECACA");
            btnClearHistory.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#FEE2E2");
            btnClearHistory.Click += (s, e) =>
            {
                if (MessageBox.Show("确定要清空所有课件备份历史记录吗？\n（注：这不会删除磁盘中已备份的课件文件）",
                    "清空历史确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    BackupHistoryManager.ClearHistory();
                    RefreshHistoryView();
                }
            };

            lvHistory = new ListView()
            {
                Location = new Point(16, 42),
                Size = new Size(612, 434),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = true,
                Font = new Font("微软雅黑", 9f),
                BackColor = Color.White,
                ForeColor = ColorTranslator.FromHtml("#1E293B"),
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            SetDoubleBuffered(lvHistory);
            lvHistory.Columns.Add("备份时间", 140);
            lvHistory.Columns.Add("设备 / U盘", 120);
            lvHistory.Columns.Add("文件数", 70);
            lvHistory.Columns.Add("状态", 80);
            lvHistory.Columns.Add("目标文件夹路径", 202);
            lvHistory.DoubleClick += (s, e) => OpenSelectedHistoryFolder();
            lvHistory.Resize += (s, e) => AutoResizeHistoryColumns();
            lvHistory.ColumnClick += LvHistory_ColumnClick;

            var historyMenu = new ContextMenuStrip();
            historyMenu.Items.Add("📂 打开所在文件夹", null, (s, e) => OpenSelectedHistoryFolder());
            historyMenu.Items.Add("📋 复制目标路径", null, (s, e) =>
            {
                if (lvHistory.SelectedItems.Count > 0)
                {
                    var record = lvHistory.SelectedItems[0].Tag as BackupRecord;
                    if (record != null && !string.IsNullOrEmpty(record.TargetFolder))
                    {
                        try { Clipboard.SetText(record.TargetFolder); } catch { }
                    }
                }
            });
            historyMenu.Items.Add("🗑 删除此条记录", null, (s, e) =>
            {
                if (lvHistory.SelectedItems.Count > 0)
                {
                    var record = lvHistory.SelectedItems[0].Tag as BackupRecord;
                    if (record != null)
                    {
                        BackupHistoryManager.DeleteRecord(record.Id);
                        RefreshHistoryView();
                    }
                }
            });
            historyMenu.Items.Add("-");
            historyMenu.Items.Add("🔄 刷新列表", null, (s, e) => RefreshHistoryView());
            historyMenu.Items.Add("🧹 清空所有历史", null, (s, e) =>
            {
                if (MessageBox.Show("确定要清空所有课件备份历史记录吗？", "清空确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    BackupHistoryManager.ClearHistory();
                    RefreshHistoryView();
                }
            });
            lvHistory.ContextMenuStrip = historyMenu;

            lblHistoryHint = new Label()
            {
                Text = "💡 双击条目可直接打开对应课件文件夹；网络连通后本地暂存课件会自动同步至云上春晖",
                Location = new Point(16, 482),
                Size = new Size(612, 18),
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                Font = new Font("微软雅黑", 8.5f),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            cardHistory.Controls.AddRange(new Control[] {
                lblHistorySummary, btnOpenHistoryFolder, btnRefreshHistory, btnClearHistory,
                lvHistory, lblHistoryHint
            });
            pnlHistoryView.Controls.Add(cardHistory);

            this.Controls.AddRange(new Control[] { pnlHeader, pnlNav, pnlMonitorView, pnlHistoryView });

            dpiScaler = new DpiScaler(this);
            dpiScaler.OnScaleApplied += (scale) =>
            {
                if (lvHistory != null && lvHistory.Columns.Count >= BaseHistoryColumnWidths.Length)
                {
                    for (int i = 0; i < BaseHistoryColumnWidths.Length; i++)
                    {
                        lvHistory.Columns[i].Width = (int)Math.Round(BaseHistoryColumnWidths[i] * scale);
                    }
                    AutoResizeHistoryColumns();
                }
            };

            // 监听历史记录数据变更
            _historyChangeHandler = () =>
            {
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    try { this.BeginInvoke(new Action(RefreshHistoryView)); } catch { }
                }
            };
            BackupHistoryManager.OnHistoryChanged += _historyChangeHandler;
            RefreshHistoryView();

            // 定时刷新 U 盘列表
            driveRefreshTimer = new System.Windows.Forms.Timer();
            driveRefreshTimer.Interval = 2000;
            driveRefreshTimer.Tick += (s, e) => RefreshDriveList();
            driveRefreshTimer.Start();
            RefreshDriveList();
        }

        public void SwitchTab(int tabIndex)
        {
            if (tabIndex == 0)
            {
                pnlMonitorView.Visible = true;
                pnlHistoryView.Visible = false;
                btnTabMonitor.BackColor = Color.White;
                btnTabMonitor.ForeColor = ColorTranslator.FromHtml("#0F172A");
                btnTabMonitor.Font = new Font(btnTabMonitor.Font, FontStyle.Bold);
                btnTabMonitor.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");

                btnTabHistory.BackColor = ColorTranslator.FromHtml("#F1F5F9");
                btnTabHistory.ForeColor = ColorTranslator.FromHtml("#64748B");
                btnTabHistory.Font = new Font(btnTabHistory.Font, FontStyle.Regular);
                btnTabHistory.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#E2E8F0");
            }
            else
            {
                pnlMonitorView.Visible = false;
                pnlHistoryView.Visible = true;
                btnTabHistory.BackColor = Color.White;
                btnTabHistory.ForeColor = ColorTranslator.FromHtml("#0F172A");
                btnTabHistory.Font = new Font(btnTabHistory.Font, FontStyle.Bold);
                btnTabHistory.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");

                btnTabMonitor.BackColor = ColorTranslator.FromHtml("#F1F5F9");
                btnTabMonitor.ForeColor = ColorTranslator.FromHtml("#64748B");
                btnTabMonitor.Font = new Font(btnTabMonitor.Font, FontStyle.Regular);
                btnTabMonitor.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#E2E8F0");

                pnlHistoryView.PerformLayout();
                cardHistory.PerformLayout();
                RefreshHistoryView();
            }
        }

        private void AutoResizeHistoryColumns()
        {
            if (lvHistory != null && lvHistory.Columns.Count >= 5)
            {
                float scale = dpiScaler != null ? dpiScaler.CurrentScale : 1.0f;
                int minPathWidth = (int)Math.Round(BaseHistoryColumnWidths[4] * scale);
                int totalWidth = lvHistory.ClientSize.Width;
                int fixedWidths = lvHistory.Columns[0].Width + lvHistory.Columns[1].Width + 
                                   lvHistory.Columns[2].Width + lvHistory.Columns[3].Width;
                int targetWidth = totalWidth - fixedWidths - 4;
                lvHistory.Columns[4].Width = Math.Max(minPathWidth, targetWidth);
            }
        }

        public void RefreshHistoryView()
        {
            if (lvHistory == null) return;
            if (lvHistory.InvokeRequired)
            {
                try { lvHistory.BeginInvoke(new Action(RefreshHistoryView)); } catch { }
                return;
            }

            var records = BackupHistoryManager.LoadHistory();
            lvHistory.BeginUpdate();
            var prevSorter = lvHistory.ListViewItemSorter;
            lvHistory.ListViewItemSorter = null;
            lvHistory.Items.Clear();

            int successCount = 0;
            int cachedCount = 0;
            int syncedCount = 0;

            foreach (var r in records)
            {
                var item = new ListViewItem(r.FormattedTime);
                item.SubItems.Add(r.DeviceDisplay);
                item.SubItems.Add($"{r.FileCount} 个文件");

                var statusSub = item.SubItems.Add(r.Status);
                item.SubItems.Add(r.TargetFolder);
                item.Tag = r;

                item.UseItemStyleForSubItems = false;
                if (string.Equals(r.Status, "成功", StringComparison.OrdinalIgnoreCase))
                {
                    statusSub.ForeColor = ColorTranslator.FromHtml("#059669");
                    successCount++;
                }
                else if (string.Equals(r.Status, "本地暂存", StringComparison.OrdinalIgnoreCase))
                {
                    statusSub.ForeColor = ColorTranslator.FromHtml("#D97706");
                    cachedCount++;
                }
                else if (string.Equals(r.Status, "已同步", StringComparison.OrdinalIgnoreCase))
                {
                    statusSub.ForeColor = ColorTranslator.FromHtml("#2563EB");
                    syncedCount++;
                }
                else if (string.Equals(r.Status, "失败", StringComparison.OrdinalIgnoreCase))
                {
                    statusSub.ForeColor = ColorTranslator.FromHtml("#DC2626");
                }

                lvHistory.Items.Add(item);
            }

            if (prevSorter != null)
            {
                lvHistory.ListViewItemSorter = prevSorter;
                lvHistory.Sort();
            }
            lvHistory.EndUpdate();

            int total = records.Count;
            lblHistorySummary.Text = $"累计备份 {total} 次 · 成功 {successCount} · 本地暂存 {cachedCount} · 已同步 {syncedCount}";
            btnTabHistory.Text = $"📋 备份历史 ({total})";
            AutoResizeHistoryColumns();
        }

        public static void OpenFolderInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
            {
                throw new DirectoryNotFoundException($"目标文件夹当前不存在或网络不可达：\n{path}");
            }

            string safeTarget = target.TrimEnd('\\', '/');
            if (safeTarget.Length == 2 && safeTarget[1] == ':')
            {
                safeTarget += "\\";
            }

            try
            {
                bool isWin = Environment.OSVersion.Platform == PlatformID.Win32NT;
                if (isWin)
                {
                    string args = safeTarget.EndsWith("\\") ? $"\"{safeTarget}\\\"" : $"\"{safeTarget}\"";
                    Process.Start("explorer.exe", args);
                }
                else if (File.Exists("/usr/bin/open"))
                {
                    Process.Start("/usr/bin/open", $"\"{safeTarget}\"");
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                }
            }
            catch
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
        }

        private void OpenSelectedHistoryFolder()
        {
            if (lvHistory.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先在列表中选中一条课件备份记录！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var record = lvHistory.SelectedItems[0].Tag as BackupRecord;
            if (record == null || string.IsNullOrEmpty(record.TargetFolder))
            {
                MessageBox.Show("所选记录的目标文件夹路径为空！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                OpenFolderInExplorer(record.TargetFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开文件夹失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void SetDoubleBuffered(Control control)
        {
            try
            {
                var prop = typeof(Control).GetProperty("DoubleBuffered", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                prop?.SetValue(control, true, null);
            }
            catch { }
        }

        private void LvHistory_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == _sortColumn)
            {
                _sortAsc = !_sortAsc;
            }
            else
            {
                _sortColumn = e.Column;
                _sortAsc = true;
            }

            lvHistory.ListViewItemSorter = new ListViewItemComparer(_sortColumn, _sortAsc);
            lvHistory.Sort();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            float scale = DpiScaler.GetDpiScale(this);
            if (dpiScaler != null && (Math.Abs(scale - dpiScaler.CurrentScale) > 0.01f || Math.Abs(scale - 1.0f) > 0.01f))
            {
                dpiScaler.ApplyScale(scale);
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (short)(m.WParam.ToInt32() & 0xFFFF);
                if (newDpi > 0 && dpiScaler != null)
                {
                    if (m.LParam != IntPtr.Zero)
                    {
                        try
                        {
                            var rect = (DpiScaler.RECT)Marshal.PtrToStructure(m.LParam, typeof(DpiScaler.RECT));
                            this.SetBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        }
                        catch { }
                    }
                    dpiScaler.ApplyScale(newDpi / 96.0f);
                }
            }
            base.WndProc(ref m);
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
                    appContext.UpdateBackupPath(dialog.SelectedPath);
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

            if (!USBMonitor.IsPathReachable(txtBackupPath.Text))
            {
                AddLog($"⚠ 目标路径当前离线或不可达: {txtBackupPath.Text}。已开启监控，插入U盘将暂存本地并在网络恢复后自动同步。");
            }

            appContext.StartMonitoring(sender, e);
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
                        string drivePath = d.Name.TrimEnd(new char[] { '\\' });
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

            _isMonitoringCurrently = isMonitoring;
            btnStart.Enabled = !isMonitoring;
            btnStop.Enabled = isMonitoring;

            if (isMonitoring)
            {
                lblStatus.Text = "● 正在实时监控";
                lblStatus.ForeColor = ColorTranslator.FromHtml("#03543F");
                pnlStatusBadge.BackColor = ColorTranslator.FromHtml("#DEF7EC");
            }
            else
            {
                lblStatus.Text = "○ 监控已停止";
                lblStatus.ForeColor = ColorTranslator.FromHtml("#64748B");
                pnlStatusBadge.BackColor = ColorTranslator.FromHtml("#F1F5F9");
            }
            pnlStatusBadge.Invalidate();
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

        public void ShowNotification(string title, string message)
        {
            appContext?.ShowNotification(title, message);
        }

        private void LoadAutoStartStatus()
        {
            chkAutoStart.CheckedChanged -= ChkAutoStart_CheckedChanged;
            chkAutoStart.Checked = Properties.Settings.Default.AutoStart;
            chkAutoStart.CheckedChanged += ChkAutoStart_CheckedChanged;
        }

        private void SaveSettings()
        {
            if (!string.IsNullOrEmpty(txtBackupPath.Text))
            {
                Properties.Settings.Default.BackupPath = txtBackupPath.Text;
            }
        }

        private void LoadSettings()
        {
            string savedPath = Properties.Settings.Default.BackupPath;
            if (!string.IsNullOrEmpty(savedPath))
            {
                txtBackupPath.Text = savedPath;
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (this.Visible)
            {
                RefreshHistoryView();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                this.ShowInTaskbar = false;
                AddLog("程序已最小化到托盘，双击图标可重新打开");
            }
        }
    }

    public class BlocklistForm : Form
    {
        private ListBox lstBlocked;
        private Button btnRemove, btnClose;
        private DpiScaler dpiScaler;

        public BlocklistForm()
        {
            this.Text = "屏蔽管理";
            this.ClientSize = new Size(385, 300);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = ColorTranslator.FromHtml("#F8FAFC");
            this.Font = new Font("微软雅黑", 9f, FontStyle.Regular);

            Label lbl = new Label()
            {
                Text = "已屏蔽的 U 盘（选中后可解除屏蔽）:",
                Location = new Point(15, 15),
                Size = new Size(360, 20),
                Font = new Font("微软雅黑", 9.5f, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#1E293B")
            };

            lstBlocked = new ListBox()
            {
                Location = new Point(15, 40),
                Size = new Size(355, 200),
                Font = new Font("微软雅黑", 9f),
                BackColor = Color.White,
                ForeColor = ColorTranslator.FromHtml("#1E293B"),
                BorderStyle = BorderStyle.FixedSingle
            };

            btnRemove = new Button()
            {
                Text = "解除屏蔽",
                Location = new Point(15, 255),
                Size = new Size(110, 32),
                BackColor = ColorTranslator.FromHtml("#DEF7EC"),
                ForeColor = ColorTranslator.FromHtml("#03543F"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRemove.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#A7F3D0");
            btnRemove.Click += BtnRemove_Click;

            btnClose = new Button()
            {
                Text = "关闭",
                Location = new Point(260, 255),
                Size = new Size(110, 32),
                BackColor = ColorTranslator.FromHtml("#F1F5F9"),
                ForeColor = ColorTranslator.FromHtml("#475569"),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("微软雅黑", 9f),
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CBD5E1");
            btnClose.Click += (s, e) => this.Close();

            lbl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lstBlocked.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            btnRemove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            this.Controls.AddRange(new Control[] { lbl, lstBlocked, btnRemove, btnClose });
            dpiScaler = new DpiScaler(this);
            RefreshList();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            float scale = DpiScaler.GetDpiScale(this);
            if (dpiScaler != null && (Math.Abs(scale - dpiScaler.CurrentScale) > 0.01f || Math.Abs(scale - 1.0f) > 0.01f))
            {
                dpiScaler.ApplyScale(scale);
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (short)(m.WParam.ToInt32() & 0xFFFF);
                if (newDpi > 0 && dpiScaler != null)
                {
                    if (m.LParam != IntPtr.Zero)
                    {
                        try
                        {
                            var rect = (DpiScaler.RECT)Marshal.PtrToStructure(m.LParam, typeof(DpiScaler.RECT));
                            this.SetBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        }
                        catch { }
                    }
                    dpiScaler.ApplyScale(newDpi / 96.0f);
                }
            }
            base.WndProc(ref m);
        }

        private void RefreshList()
        {
            lstBlocked.Items.Clear();
            foreach (var item in Properties.Settings.Default.GetBlockedList())
            {
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

    public class ListViewItemComparer : System.Collections.IComparer
    {
        private readonly int _col;
        private readonly bool _asc;

        public ListViewItemComparer(int column, bool ascending)
        {
            _col = column;
            _asc = ascending;
        }

        public int Column => _col;
        public bool Ascending => _asc;

        public int Compare(object x, object y)
        {
            var itemX = x as ListViewItem;
            var itemY = y as ListViewItem;
            if (itemX == null && itemY == null) return 0;
            if (itemX == null) return _asc ? -1 : 1;
            if (itemY == null) return _asc ? 1 : -1;

            var recX = itemX.Tag as BackupRecord;
            var recY = itemY.Tag as BackupRecord;

            int result = 0;
            if (recX != null && recY != null)
            {
                switch (_col)
                {
                    case 0:
                        result = DateTime.Compare(recX.Timestamp, recY.Timestamp);
                        break;
                    case 1:
                        result = string.Compare(recX.DeviceDisplay, recY.DeviceDisplay, StringComparison.CurrentCultureIgnoreCase);
                        break;
                    case 2:
                        result = recX.FileCount.CompareTo(recY.FileCount);
                        break;
                    case 3:
                        result = string.Compare(recX.Status, recY.Status, StringComparison.CurrentCultureIgnoreCase);
                        break;
                    case 4:
                        result = string.Compare(recX.TargetFolder, recY.TargetFolder, StringComparison.CurrentCultureIgnoreCase);
                        break;
                    default:
                        result = 0;
                        break;
                }
            }
            else
            {
                string textX = _col < itemX.SubItems.Count ? itemX.SubItems[_col].Text : "";
                string textY = _col < itemY.SubItems.Count ? itemY.SubItems[_col].Text : "";

                if (_col == 0 && DateTime.TryParse(textX, out DateTime dtX) && DateTime.TryParse(textY, out DateTime dtY))
                {
                    result = DateTime.Compare(dtX, dtY);
                }
                else if (_col == 2)
                {
                    int numX = ExtractFirstNumber(textX);
                    int numY = ExtractFirstNumber(textY);
                    result = numX.CompareTo(numY);
                }
                else
                {
                    result = string.Compare(textX, textY, StringComparison.CurrentCultureIgnoreCase);
                }
            }

            return _asc ? result : -result;
        }

        private static int ExtractFirstNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int num = 0;
            bool found = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    num = num * 10 + (text[i] - '0');
                    found = true;
                }
                else if (found)
                {
                    break;
                }
            }
            return num;
        }
    }

    public static class Program
    {
#if WINDOWS
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = (IntPtr)(-3);
#endif

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

#if WINDOWS
                    try
                    {
                        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                        {
                            SetCurrentProcessExplicitAppUserModelID("RS114514.GetRickCourseware");
                            EnsureStartMenuShortcut();

                            // 优先调用 Windows 10 (1703+) 原生 PerMonitorV2 DPI 感知 API
                            // 彻底禁用 DWM 双线性位图缩放拉伸，实现 4K 高分屏原生清晰字体渲染
                            bool dpiSet = false;
                            try
                            {
                                dpiSet = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                                if (!dpiSet)
                                {
                                    dpiSet = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
                                }
                            }
                            catch { }

                            if (!dpiSet)
                            {
                                try
                                {
                                    // Windows 8.1+ Per-Monitor DPI Aware
                                    SetProcessDpiAwareness(2);
                                    dpiSet = true;
                                }
                                catch { }
                            }

                            if (!dpiSet)
                            {
                                try
                                {
                                    // Windows Vista / 7 System DPI Aware
                                    SetProcessDPIAware();
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
#endif

                    try
                    {
                        var method = typeof(Application).GetMethod("SetHighDpiMode",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (method != null)
                        {
                            var highDpiModeType = Type.GetType("System.Windows.Forms.HighDpiMode, System.Windows.Forms");
                            if (highDpiModeType != null)
                            {
                                object mode = null;
                                try { mode = Enum.Parse(highDpiModeType, "PerMonitorV2"); }
                                catch { }
                                if (mode == null)
                                {
                                    try { mode = Enum.Parse(highDpiModeType, "SystemAware"); } catch { }
                                }
                                if (mode != null)
                                {
                                    method.Invoke(null, new object[] { mode });
                                }
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

#if WINDOWS
        private static void EnsureStartMenuShortcut()
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrEmpty(programs) || !Directory.Exists(programs)) return;

                string shortcutPath = Path.Combine(programs, "获取Rick课件.lnk");
                if (File.Exists(shortcutPath)) return;

                string exePath = Application.ExecutablePath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    if (shortcut != null)
                    {
                        Type scType = shortcut.GetType();
                        scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                        scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath) });
                        scType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "获取Rick课件 - 智能备份与同步系统" });
                        scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                    }
                }
            }
            catch { }
        }
#endif
    }
}