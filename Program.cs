using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShowDesk
{
    internal static class Program
    {
        private static Mutex? mutex;
        private static NotifyIcon trayIcon = null!;
        private static ToolStripMenuItem autoStartMenuItem = null!;
        private static System.Windows.Forms.Timer keyTimer = null!;

        private static bool lastKeyDown = false;
        private static DateTime lastPressTime = DateTime.MinValue;
        private static int pressCount = 0;

        private const string AppName = "ShowDesk";

        private const int HotKey = VK_ESCAPE;
        private const int DoublePressMilliseconds = 500;

        private const int VK_ESCAPE = 0x1B;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [STAThread]
        static void Main()
        {
            mutex = new Mutex(true, "ShowDesk_Single_Instance", out bool createdNew);
            if (!createdNew) return;

            ApplicationConfiguration.Initialize();

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
                trayIcon.Visible = false;
                keyTimer.Stop();
                Application.Exit();
            });

            keyTimer = new System.Windows.Forms.Timer
            {
                Interval = 30
            };

            keyTimer.Tick += (_, _) => CheckDoubleKeyPress();
            keyTimer.Start();

            bool startupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

            if (startupLaunch && AreDesktopIconsVisible())
            {
                ToggleDesktopIcons();
            }

            Application.Run();

            GC.KeepAlive(mutex);
        }

        private static void CheckDoubleKeyPress()
        {
            bool keyDown = (GetAsyncKeyState(HotKey) & 0x8000) != 0;

            if (keyDown && !lastKeyDown)
            {
                bool iconsVisible = AreDesktopIconsVisible();

                if (iconsVisible && !IsMouseOnDesktopArea())
                {
                    pressCount = 0;
                    lastPressTime = DateTime.MinValue;
                    lastKeyDown = keyDown;
                    return;
                }

                DateTime now = DateTime.Now;

                if ((now - lastPressTime).TotalMilliseconds <= DoublePressMilliseconds)
                    pressCount++;
                else
                    pressCount = 1;

                lastPressTime = now;

                if (pressCount >= 2)
                {
                    pressCount = 0;
                    lastPressTime = DateTime.MinValue;
                    ToggleDesktopIcons();
                }
            }

            lastKeyDown = keyDown;
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
}