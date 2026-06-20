using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShowDesk
{
    internal static class Program
    {
        private static Mutex? mutex;
        private static NotifyIcon? trayIcon;
        private static ToolStripMenuItem autoStartMenuItem = null!;
        private static System.Windows.Forms.Timer keyTimer = null!;
        private static AppSettings settings = AppSettings.Default();
        private static SettingsForm? settingsForm;

        private static readonly ShortcutTracker desktopShortcutTracker = new();
        private static readonly ShortcutTracker showTrayShortcutTracker = new();

        private const string AppName = "ShowDesk";
        private const int MultiPressMilliseconds = 500;

        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName,
            "settings.json");

        [STAThread]
        static void Main()
        {
            mutex = new Mutex(true, "ShowDesk_Single_Instance", out bool createdNew);
            if (!createdNew) return;

            ApplicationConfiguration.Initialize();
            settings = LoadSettings();

            bool startupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

            if (!(startupLaunch && settings.HideTrayIconOnStartup))
                CreateTrayIcon();

            keyTimer = new System.Windows.Forms.Timer
            {
                Interval = 30
            };

            keyTimer.Tick += (_, _) => CheckShortcuts();
            keyTimer.Start();

            if (startupLaunch && settings.HideDesktopIconsOnStartup && AreDesktopIconsVisible())
            {
                ToggleDesktopIcons();
            }

            Application.Run();

            GC.KeepAlive(mutex);
        }

        private static void CreateTrayIcon()
        {
            if (trayIcon is not null)
            {
                trayIcon.Visible = true;
                return;
            }

            trayIcon = new NotifyIcon
            {
                Text = "ShowDesk",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)!,
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };

            trayIcon.ContextMenuStrip.Items.Add("隐藏/显示桌面图标", null, (_, _) =>
            {
                ToggleDesktopIcons();
            });

            trayIcon.ContextMenuStrip.Items.Add("隐藏右下角托盘图标", null, (_, _) =>
            {
                HideTrayIcon();
            });

            trayIcon.ContextMenuStrip.Items.Add("快捷键设置", null, (_, _) =>
            {
                ShowSettingsWindow();
            });

            autoStartMenuItem = new ToolStripMenuItem("开机自动启动")
            {
                Checked = IsAutoStartEnabled(),
                CheckOnClick = false
            };

            autoStartMenuItem.Click += (_, _) =>
            {
                bool enable = !IsAutoStartEnabled();
                SetAutoStart(enable);
                autoStartMenuItem.Checked = enable;
            };

            trayIcon.ContextMenuStrip.Items.Add(autoStartMenuItem);

            trayIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) =>
            {
                HideTrayIcon();
                keyTimer.Stop();
                Application.Exit();
            });
        }

        private static void HideTrayIcon()
        {
            NotifyIcon? icon = trayIcon;
            if (icon is null)
                return;

            trayIcon = null;
            icon.Visible = false;
            icon.Dispose();
        }

        private static void ShowTrayIcon()
        {
            CreateTrayIcon();
        }

        private static void ToggleTrayIcon()
        {
            if (trayIcon?.Visible == true)
                HideTrayIcon();
            else
                ShowTrayIcon();
        }

        private static void CheckShortcuts()
        {
            if (settings.ShowTrayShortcut.IsSameAs(settings.DesktopToggleShortcut))
            {
                if (CheckShortcutActivated(settings.ShowTrayShortcut, showTrayShortcutTracker))
                    ToggleTrayAndDesktopIconsTogether();

                desktopShortcutTracker.Reset();
                return;
            }

            if (CheckShortcutActivated(settings.ShowTrayShortcut, showTrayShortcutTracker))
                ToggleTrayIcon();

            if (!CheckShortcutActivated(settings.DesktopToggleShortcut, desktopShortcutTracker))
                return;

            if (AreDesktopIconsVisible() && !IsMouseOnDesktopArea())
                return;

            ToggleDesktopIcons();
        }

        private static void ToggleTrayAndDesktopIconsTogether()
        {
            bool trayVisible = trayIcon?.Visible == true;
            bool desktopVisible = AreDesktopIconsVisible();

            if (!trayVisible || !desktopVisible)
            {
                ShowTrayIcon();
                SetDesktopIconsVisible(true);
                return;
            }

            if (!IsMouseOnDesktopArea())
                return;

            HideTrayIcon();
            SetDesktopIconsVisible(false);
        }

        private static bool CheckShortcutActivated(Shortcut shortcut, ShortcutTracker tracker)
        {
            bool shortcutDown = IsShortcutDown(shortcut);

            if (!shortcutDown)
            {
                tracker.LastDown = false;
                return false;
            }

            if (tracker.LastDown)
                return false;

            tracker.LastDown = true;
            DateTime now = DateTime.Now;

            if ((now - tracker.LastPressTime).TotalMilliseconds <= MultiPressMilliseconds)
                tracker.PressCount++;
            else
                tracker.PressCount = 1;

            tracker.LastPressTime = now;

            if (tracker.PressCount < Math.Max(1, shortcut.PressCount))
                return false;

            tracker.PressCount = 0;
            tracker.LastPressTime = DateTime.MinValue;
            return true;
        }

        private static bool IsShortcutDown(Shortcut shortcut)
        {
            if (shortcut.Key == Keys.None)
                return false;

            bool keyDown = (GetAsyncKeyState((int)shortcut.Key) & 0x8000) != 0;
            if (!keyDown) return false;

            return IsModifierStateMatched(VK_CONTROL, shortcut.Ctrl) &&
                   IsModifierStateMatched(VK_MENU, shortcut.Alt) &&
                   IsModifierStateMatched(VK_SHIFT, shortcut.Shift);
        }

        private static bool IsModifierStateMatched(int virtualKey, bool required)
        {
            bool down = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            return required ? down : !down;
        }

        private static void ShowSettingsWindow()
        {
            if (settingsForm is { IsDisposed: false })
            {
                settingsForm.Activate();
                return;
            }

            settingsForm = new SettingsForm(settings);
            settingsForm.SettingsSaved += (_, newSettings) =>
            {
                settings = newSettings.Normalized();
                SaveSettings(settings);
                desktopShortcutTracker.Reset();
                showTrayShortcutTracker.Reset();
            };
            settingsForm.Show();
        }

        private static AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return AppSettings.Default();

                string json = File.ReadAllText(SettingsFilePath);
                return (JsonSerializer.Deserialize<AppSettings>(json) ?? AppSettings.Default()).Normalized();
            }
            catch
            {
                return AppSettings.Default();
            }
        }

        private static void SaveSettings(AppSettings value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);

            string json = JsonSerializer.Serialize(value.Normalized(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(SettingsFilePath, json);
        }

        private static bool IsAutoStartEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                false);

            string? value = key?.GetValue(AppName) as string;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        private static void SetAutoStart(bool enable)
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                true)!;

            if (enable)
                key.SetValue(AppName, $"\"{Application.ExecutablePath}\" --startup");
            else
                key.DeleteValue(AppName, false);
        }

        private static bool AreDesktopIconsVisible()
        {
            IntPtr icons = GetDesktopIconsHandle();

            if (icons == IntPtr.Zero)
                return false;

            return IsWindowVisible(icons);
        }

        private static bool IsMouseOnDesktopArea()
        {
            GetCursorPos(out POINT pt);

            IntPtr hwnd = WindowFromPoint(pt);
            string className = GetClassNameText(hwnd);

            return className == "WorkerW" ||
                   className == "Progman" ||
                   className == "SysListView32";
        }

        private static string GetClassNameText(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void ToggleDesktopIcons()
        {
            IntPtr icons = GetDesktopIconsHandle();

            if (icons == IntPtr.Zero)
                return;

            bool visible = IsWindowVisible(icons);
            ShowWindow(icons, visible ? SW_HIDE : SW_SHOW);
        }

        private static void SetDesktopIconsVisible(bool visible)
        {
            IntPtr icons = GetDesktopIconsHandle();

            if (icons == IntPtr.Zero)
                return;

            if (IsWindowVisible(icons) != visible)
                ShowWindow(icons, visible ? SW_SHOW : SW_HIDE);
        }

        private static IntPtr GetDesktopIconsHandle()
        {
            IntPtr result = IntPtr.Zero;

            EnumWindows((topHandle, _) =>
            {
                IntPtr shellView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);

                if (shellView != IntPtr.Zero)
                {
                    IntPtr listView = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", "FolderView");

                    if (listView != IntPtr.Zero)
                    {
                        result = listView;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (result != IntPtr.Zero)
                return result;

            IntPtr progman = FindWindow("Progman", null);
            IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

            if (defView != IntPtr.Zero)
                result = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");

            return result;
        }

        private sealed class ShortcutTracker
        {
            public bool LastDown { get; set; }
            public DateTime LastPressTime { get; set; } = DateTime.MinValue;
            public int PressCount { get; set; }

            public void Reset()
            {
                LastDown = false;
                LastPressTime = DateTime.MinValue;
                PressCount = 0;
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(
            IntPtr hWnd,
            StringBuilder lpClassName,
            int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(
            string lpClassName,
            string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(
            IntPtr hwndParent,
            IntPtr hwndChildAfter,
            string lpszClass,
            string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(
            EnumWindowsProc lpEnumFunc,
            IntPtr lParam);
    }

    internal sealed class AppSettings
    {
        public Shortcut DesktopToggleShortcut { get; set; } = new(Keys.Escape, false, false, false, 2);
        public Shortcut ShowTrayShortcut { get; set; } = new(Keys.S, true, true, false, 1);
        public bool HideDesktopIconsOnStartup { get; set; } = true;
        public bool HideTrayIconOnStartup { get; set; } = false;

        public static AppSettings Default() => new();

        public AppSettings Clone() => new()
        {
            DesktopToggleShortcut = DesktopToggleShortcut.Clone(),
            ShowTrayShortcut = ShowTrayShortcut.Clone(),
            HideDesktopIconsOnStartup = HideDesktopIconsOnStartup,
            HideTrayIconOnStartup = HideTrayIconOnStartup
        };

        public AppSettings Normalized()
        {
            DesktopToggleShortcut ??= new Shortcut(Keys.Escape, false, false, false, 2);
            ShowTrayShortcut ??= new Shortcut(Keys.S, true, true, false, 1);
            DesktopToggleShortcut.NormalizePressCount(2);
            ShowTrayShortcut.NormalizePressCount(1);
            return this;
        }
    }

    internal sealed class Shortcut
    {
        public Keys Key { get; set; }
        public bool Ctrl { get; set; }
        public bool Alt { get; set; }
        public bool Shift { get; set; }
        public int PressCount { get; set; }

        public Shortcut()
        {
        }

        public Shortcut(Keys key, bool ctrl, bool alt, bool shift, int pressCount = 1)
        {
            Key = key;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            PressCount = Math.Clamp(pressCount, 1, 5);
        }

        public bool HasModifiers => Ctrl || Alt || Shift;

        public Shortcut Clone() => new(Key, Ctrl, Alt, Shift, PressCount);

        public bool IsSameAs(Shortcut other) =>
            Key == other.Key &&
            Ctrl == other.Ctrl &&
            Alt == other.Alt &&
            Shift == other.Shift &&
            PressCount == other.PressCount;

        public void NormalizePressCount(int defaultPressCount)
        {
            if (PressCount < 1)
                PressCount = defaultPressCount;

            PressCount = Math.Clamp(PressCount, 1, 5);
        }

        public override string ToString()
        {
            if (Key == Keys.None)
                return "未设置";

            string text = string.Empty;
            if (Ctrl) text += "Ctrl + ";
            if (Alt) text += "Alt + ";
            if (Shift) text += "Shift + ";

            text += Key;

            if (!HasModifiers && PressCount > 1)
                text += $" x{PressCount}";

            return text;
        }
    }

    internal sealed class SettingsForm : Form
    {
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;

        private readonly TextBox desktopShortcutTextBox;
        private readonly TextBox showTrayShortcutTextBox;
        private readonly NumericUpDown desktopPressCountBox;
        private readonly NumericUpDown showTrayPressCountBox;
        private readonly CheckBox hideDesktopIconsOnStartupCheckBox;
        private readonly CheckBox hideTrayIconOnStartupCheckBox;
        private AppSettings editingSettings;

        public event EventHandler<AppSettings>? SettingsSaved;

        public SettingsForm(AppSettings currentSettings)
        {
            editingSettings = currentSettings.Clone().Normalized();

            Text = "ShowDesk 快捷键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(620, 300);

            Label desktopLabel = CreateLabel("桌面图标快捷键", 24, 22);
            desktopShortcutTextBox = CreateShortcutTextBox(24, 48);

            Label desktopPressLabel = CreateLabel("点击次数", 470, 50);
            desktopPressCountBox = CreatePressCountBox(540, 46);

            Label showTrayLabel = CreateLabel("隐藏/显示右下角图标快捷键", 24, 94);
            showTrayShortcutTextBox = CreateShortcutTextBox(24, 120);

            Label showTrayPressLabel = CreateLabel("点击次数", 470, 122);
            showTrayPressCountBox = CreatePressCountBox(540, 118);

            desktopShortcutTextBox.KeyDown += (_, e) => CaptureShortcut(e, value => editingSettings.DesktopToggleShortcut = value, desktopShortcutTextBox, desktopPressCountBox);
            desktopPressCountBox.ValueChanged += (_, _) => editingSettings.DesktopToggleShortcut.PressCount = (int)desktopPressCountBox.Value;
            showTrayShortcutTextBox.KeyDown += (_, e) => CaptureShortcut(e, value => editingSettings.ShowTrayShortcut = value, showTrayShortcutTextBox, showTrayPressCountBox);
            showTrayPressCountBox.ValueChanged += (_, _) => editingSettings.ShowTrayShortcut.PressCount = (int)showTrayPressCountBox.Value;

            hideDesktopIconsOnStartupCheckBox = new CheckBox
            {
                Text = "开机启动时隐藏桌面图标",
                AutoSize = true,
                Location = new System.Drawing.Point(24, 176),
                Checked = editingSettings.HideDesktopIconsOnStartup
            };

            hideTrayIconOnStartupCheckBox = new CheckBox
            {
                Text = "开机启动时隐藏右下角托盘图标",
                AutoSize = true,
                Location = new System.Drawing.Point(24, 206),
                Checked = editingSettings.HideTrayIconOnStartup
            };

            Button saveButton = new()
            {
                Text = "保存",
                DialogResult = DialogResult.None,
                Location = new System.Drawing.Point(400, 252),
                Size = new System.Drawing.Size(90, 32)
            };
            saveButton.Click += (_, _) => SaveAndClose();

            Button cancelButton = new()
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(506, 252),
                Size = new System.Drawing.Size(90, 32)
            };
            cancelButton.Click += (_, _) => Close();

            Controls.AddRange(new Control[]
            {
                desktopLabel,
                desktopShortcutTextBox,
                desktopPressLabel,
                desktopPressCountBox,
                showTrayLabel,
                showTrayShortcutTextBox,
                showTrayPressLabel,
                showTrayPressCountBox,
                hideDesktopIconsOnStartupCheckBox,
                hideTrayIconOnStartupCheckBox,
                saveButton,
                cancelButton
            });

            UpdateShortcutControls();
        }

        private static Label CreateLabel(string text, int x, int y) => new()
        {
            Text = text,
            AutoSize = true,
            Location = new System.Drawing.Point(x, y)
        };

        private static TextBox CreateShortcutTextBox(int x, int y) => new()
        {
            ReadOnly = true,
            Location = new System.Drawing.Point(x, y),
            Size = new System.Drawing.Size(420, 23),
            TabStop = true
        };

        private static NumericUpDown CreatePressCountBox(int x, int y) => new()
        {
            Location = new System.Drawing.Point(x, y),
            Minimum = 1,
            Maximum = 5,
            Size = new System.Drawing.Size(56, 23),
            TextAlign = HorizontalAlignment.Center
        };

        private static void CaptureShortcut(KeyEventArgs e, Action<Shortcut> setShortcut, TextBox textBox, NumericUpDown pressCountBox)
        {
            e.SuppressKeyPress = true;

            Keys key = e.KeyCode;
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
                return;

            int pressCount = e.Control || e.Alt || e.Shift ? 1 : (int)pressCountBox.Value;
            Shortcut shortcut = new(key, e.Control, e.Alt, e.Shift, pressCount);
            setShortcut(shortcut);
            UpdateShortcutControl(textBox, pressCountBox, shortcut);
        }

        private void SaveAndClose()
        {
            editingSettings.HideDesktopIconsOnStartup = hideDesktopIconsOnStartupCheckBox.Checked;
            editingSettings.HideTrayIconOnStartup = hideTrayIconOnStartupCheckBox.Checked;
            editingSettings.Normalized();

            string conflictMessage = GetGlobalHotKeyConflictMessage(editingSettings);
            if (!string.IsNullOrEmpty(conflictMessage))
            {
                DialogResult result = MessageBox.Show(
                    this,
                    conflictMessage + "\r\n\r\n仍然保存这些快捷键吗？",
                    "可能存在快捷键冲突",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes)
                    return;
            }

            SettingsSaved?.Invoke(this, editingSettings.Clone());
            Close();
        }

        private static string GetGlobalHotKeyConflictMessage(AppSettings settings)
        {
            string message = string.Empty;

            if (IsPossiblyRegisteredByAnotherApp(settings.DesktopToggleShortcut, 0x4001))
                message += $"桌面图标快捷键 {settings.DesktopToggleShortcut} 可能已被其他软件注册为全局快捷键。\r\n";

            if (IsPossiblyRegisteredByAnotherApp(settings.ShowTrayShortcut, 0x4002))
                message += $"隐藏/显示右下角图标快捷键 {settings.ShowTrayShortcut} 可能已被其他软件注册为全局快捷键。\r\n";

            if (!string.IsNullOrEmpty(message))
                message += "只能检测系统全局热键占用，检测不到其他软件内部自己的快捷键。";

            return message;
        }

        private static bool IsPossiblyRegisteredByAnotherApp(Shortcut shortcut, int id)
        {
            if (shortcut.Key == Keys.None)
                return false;

            uint modifiers = 0;
            if (shortcut.Alt) modifiers |= MOD_ALT;
            if (shortcut.Ctrl) modifiers |= MOD_CONTROL;
            if (shortcut.Shift) modifiers |= MOD_SHIFT;

            bool registered = RegisterHotKey(IntPtr.Zero, id, modifiers, (uint)shortcut.Key);
            if (registered)
                UnregisterHotKey(IntPtr.Zero, id);

            return !registered;
        }

        private void UpdateShortcutControls()
        {
            UpdateShortcutControl(desktopShortcutTextBox, desktopPressCountBox, editingSettings.DesktopToggleShortcut);
            UpdateShortcutControl(showTrayShortcutTextBox, showTrayPressCountBox, editingSettings.ShowTrayShortcut);
        }

        private static void UpdateShortcutControl(TextBox textBox, NumericUpDown pressCountBox, Shortcut shortcut)
        {
            shortcut.NormalizePressCount(1);
            pressCountBox.Value = shortcut.PressCount;
            pressCountBox.Enabled = !shortcut.HasModifiers;
            textBox.Text = shortcut.ToString();
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}










