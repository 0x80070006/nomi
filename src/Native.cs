using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Nomi
{
    // Win32 helpers. Every position here is in physical pixels (the process is per-monitor DPI aware).
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;

        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        public static IntPtr Handle(Window w) { return new WindowInteropHelper(w).Handle; }

        public static void AddExStyle(Window w, int style)
        {
            IntPtr h = Handle(w);
            if (h == IntPtr.Zero) return;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | style);
        }

        public static void RemoveExStyle(Window w, int style)
        {
            IntPtr h = Handle(w);
            if (h == IntPtr.Zero) return;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) & ~style);
        }

        public static void MoveTo(Window w, int x, int y)
        {
            IntPtr h = Handle(w);
            if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public static void SetBounds(Window w, int x, int y, int width, int height)
        {
            IntPtr h = Handle(w);
            if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // Re-asserts topmost without stealing focus (some full screen apps push it back).
        public static void KeepOnTop(Window w)
        {
            IntPtr h = Handle(w);
            if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static Int32Rect Bounds(Window w)
        {
            RECT r;
            IntPtr h = Handle(w);
            if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return Int32Rect.Empty;
            return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        public static Point Cursor()
        {
            POINT p;
            GetCursorPos(out p);
            return new Point(p.X, p.Y);
        }

        public static double Scale(Window w)
        {
            try
            {
                IntPtr h = Handle(w);
                if (h != IntPtr.Zero) { uint dpi = GetDpiForWindow(h); if (dpi > 0) return dpi / 96.0; }
            }
            catch (EntryPointNotFoundException) { }
            var source = PresentationSource.FromVisual(w);
            return source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
        }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        // Name of the process owning the topmost window of another app under a screen point
        // ("OUTLOOK", ...). Nomi's own windows (overlays, mascot) are skipped.
        public static string ProcessAt(int x, int y)
        {
            string name = "";
            try
            {
                uint self = (uint)Process.GetCurrentProcess().Id;
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r) || x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == self) return true;
                    try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            return name;
        }

        public static string ForegroundProcess()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return ""; }
        }

        public static void BringProcessToFront(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero) { SetForegroundWindow(h); return; }
            }
        }

        // ---- Job object: the model server dies with Nomi, even if Nomi crashes or is killed. ----

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr job;

        public static void TieToThisProcess(Process child)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ref info, Marshal.SizeOf(info));
                }
                AssignProcessToJobObject(job, child.Handle);
            }
            catch (Exception e) { Log.Write("Job object: " + e.Message); }
        }
    }
}
