// EdgeControl: the Windows control panel of ondemand-mesh-vr. One window for what the .bat launchers did: the edge
// server for the Unity simulator or the Quest (Wi-Fi / USB cable), with the mock, a generative model or a classical
// method and a choice of objects; the measured sessions; the headset (state, app install, logs, CSVs); the results;
// the PC setup. Everything runs the project's own scripts (edge.server, tools/quest/quest.py, tools/setup_pc.py), whose
// output appears in the console at the bottom. Built by tools/control/build.py into EdgeControl.exe.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

// Without this attribute (csc does not add it) the .NET Framework would run the program with the defaults of 4.0:
// old accessibility (buttons invisible to screen readers and UI Automation) and older behaviours elsewhere.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("EdgeControl - ondemand-mesh-vr")]
[assembly: System.Reflection.AssemblyProduct("ondemand-mesh-vr")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace EdgeControl;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
        ServicePointManager.DefaultConnectionLimit = 16;
        ServicePointManager.Expect100Continue = false;

        string snapshot = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--snapshot") snapshot = args[i + 1];
            if (args[i] != "--icon") continue;
            Logo.WriteIcon(args[i + 1]);  // development aid: tools\control\EdgeControl.ico is drawn by Logo.Draw
            return 0;
        }

        var repo = Repo.Find(AppDomain.CurrentDomain.BaseDirectory) ?? Repo.Find(Environment.CurrentDirectory);
        if (repo == null)
        {
            MessageBox.Show("EdgeControl.exe deve stare nella cartella del progetto ondemand-mesh-vr (quella con edge\\ e tools\\).",
                            "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var single = new Mutex(true, "ondemand-mesh-vr.EdgeControl", out bool first);
        if (!first && snapshot == null)
        {
            BringOtherToFront();
            return 0;
        }

        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Init(g.DpiX);
        Application.Run(new MainForm(repo, snapshot));
        GC.KeepAlive(single);
        return 0;
    }

    static void BringOtherToFront()
    {
        var me = Process.GetCurrentProcess();
        var other = Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero);
        if (other == null) return;
        if (Native.IsIconic(other.MainWindowHandle)) Native.ShowWindow(other.MainWindowHandle, Native.SW_RESTORE);
        Native.SetForegroundWindow(other.MainWindowHandle);
    }

    static void ShowError(Exception e)
    {
        try
        {
            MessageBox.Show("Errore inatteso di EdgeControl (i processi avviati continuano):\n\n" + e,
                            "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // nothing more to do
        }
    }
}
