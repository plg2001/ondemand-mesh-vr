// Win32 calls the .NET Framework does not wrap: job objects (a task's whole process tree is stopped at once, and
// with this app if it crashes), the owner of a listening TCP port, scrolling of the console without flicker.
using System;
using System.Runtime.InteropServices;

namespace EdgeControl;

static class Native
{
    // ------------------------------------------------------------------ job objects
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
                                               int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
                                                 int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    const int JobObjectBasicAccountingInformation = 1;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    /// <summary>A job whose processes all die when it is terminated or when its last handle closes (this app exits,
    /// even by a crash): no edge server is left running hidden. IntPtr.Zero if Windows refuses.</summary>
    public static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    /// <summary>Processes still alive in the job (-1 if unknown).</summary>
    public static int ActiveProcesses(IntPtr job)
    {
        return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                                         Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)), IntPtr.Zero)
            ? (int)info.ActiveProcesses : -1;
    }

    // ------------------------------------------------------------------ owner of a TCP port
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    /// <summary>Process id listening on this IPv4 TCP port, or 0.</summary>
    public static int ListeningPid(int port)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return 0;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) continue;
                int rows = Marshal.ReadInt32(buffer);
                for (int i = 0; i < rows; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid
                    var row = buffer + 4 + i * 24;
                    int localPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);  // network byte order
                    if (localPort == port) return Marshal.ReadInt32(row, 20);
                }
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ windows and scrolling
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    public static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    public const int WM_SETREDRAW = 0x000B;
    public const int WM_VSCROLL = 0x0115;
    public const int SB_BOTTOM = 7;
    public const int SB_VERT = 1;
    public const uint SIF_ALL = 0x17;
    public const int EM_GETSCROLLPOS = 0x04DD;
    public const int EM_SETSCROLLPOS = 0x04DE;
    public const int EM_SETCUEBANNER = 0x1501;
    public const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCROLLINFO
    {
        public uint cbSize, fMask;
        public int nMin, nMax;
        public uint nPage;
        public int nPos, nTrackPos;
    }
}
