// The console: one tab per kind of task, each showing the output of the process it runs (what the .bat windows
// used to show), with the lines coloured, carriage returns honoured (progress bars) and a line to type into.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace EdgeControl;

enum ConsoleKind { Server, Session, Logs, Commands }

sealed class ConsoleTab
{
    public readonly ConsoleKind Kind;
    public readonly string Name;
    public readonly RichTextBox Box;
    public RunningTask Task;  // the one running, or the last one

    readonly object gate = new object();
    readonly StringBuilder pending = new StringBuilder();
    readonly StringBuilder partial = new StringBuilder();  // the line being written (no newline yet)
    int partialStart = -1;                                  // where it starts in Box, -1 = not shown
    bool crPending;

    static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]", RegexOptions.Compiled);

    /// <summary>Each complete line, on the UI thread.</summary>
    public event Action<string> Line;

    public ConsoleTab(ConsoleKind kind, string name)
    {
        Kind = kind;
        Name = name;
        Box = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.ConsoleBg,
            ForeColor = Theme.ConsoleText, Font = Theme.Mono, WordWrap = true, DetectUrls = true, HideSelection = false,
            ScrollBars = RichTextBoxScrollBars.Vertical, Visible = false, ShortcutsEnabled = true,
        };
        Box.LinkClicked += (s, e) => Shell.Open(e.LinkText);
    }

    public bool Running => Task != null && !Task.Done;

    /// <summary>Text to show; from any thread (shown by Flush on the UI thread).</summary>
    public void Write(string text)
    {
        lock (gate) pending.Append(text);
    }

    public void Clear()
    {
        lock (gate) pending.Clear();
        partial.Clear();
        partialStart = -1;
        crPending = false;
        Box.Clear();
    }

    public void Flush()
    {
        string chunk;
        lock (gate)
        {
            if (pending.Length == 0) return;
            chunk = pending.ToString();
            pending.Clear();
        }
        chunk = Ansi.Replace(chunk, "");
        var done = new List<string>();
        foreach (char c in chunk)
        {
            if (crPending)
            {
                crPending = false;
                if (c == '\n')
                {
                    done.Add(partial.ToString());
                    partial.Clear();
                    continue;
                }
                partial.Clear();  // a bare carriage return: the line is written again (progress bars)
            }
            if (c == '\r') crPending = true;
            else if (c == '\n')
            {
                done.Add(partial.ToString());
                partial.Clear();
            }
            else if (c == '\b')
            {
                if (partial.Length > 0) partial.Length--;
            }
            else if (c >= ' ' || c == '\t') partial.Append(c);
        }
        Render(done);
        foreach (var line in done) Line?.Invoke(line);
    }

    void Render(List<string> done)
    {
        var handle = Box.Handle;
        var scroll = new Native.POINT();
        Native.SendMessage(handle, Native.EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
        bool atBottom = AtBottom();
        int selStart = Box.SelectionStart, selLength = Box.SelectionLength;
        // WM_SETREDRAW TRUE shows a hidden window (it works by toggling WS_VISIBLE): only for the tab on screen
        bool visible = Box.Visible;
        if (visible) Native.SendMessage(handle, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (partialStart >= 0)
            {
                Box.Select(partialStart, Box.TextLength - partialStart);
                Box.SelectedText = "";
                partialStart = -1;
            }
            var run = new StringBuilder();
            var runColor = Color.Empty;
            foreach (var line in done)
            {
                var color = ColorFor(line);
                if (run.Length > 0 && color != runColor)
                {
                    Append(run.ToString(), runColor);
                    run.Clear();
                }
                runColor = color;
                run.Append(line).Append('\n');
            }
            if (run.Length > 0) Append(run.ToString(), runColor);
            int removed = Trim();
            selStart = Math.Max(0, selStart - removed);
            if (partial.Length > 0)
            {
                partialStart = Box.TextLength;
                Append(partial.ToString(), ColorFor(partial.ToString()));
            }
        }
        finally
        {
            if (selLength > 0 && selStart + selLength <= Box.TextLength) Box.Select(selStart, selLength);
            else Box.Select(Box.TextLength, 0);
            if (atBottom) Native.SendMessage(handle, Native.WM_VSCROLL, (IntPtr)Native.SB_BOTTOM, IntPtr.Zero);
            else Native.SendMessage(handle, Native.EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
            if (visible)
            {
                Native.SendMessage(handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                Box.Invalidate();
            }
        }
    }

    void Append(string text, Color color)
    {
        bool empty = Box.TextLength == 0;
        Box.Select(Box.TextLength, 0);
        Box.SelectionFont = Theme.Mono;
        Box.SelectionColor = color;
        Box.SelectedText = text;
        if (!empty) return;
        // text streamed into an empty RichEdit takes its default font (a small system font), not the one set above
        Box.SelectAll();
        Box.SelectionFont = Theme.Mono;
        Box.Select(Box.TextLength, 0);
    }

    /// <summary>Keeps the console under ~600k characters: drops the oldest lines.</summary>
    int Trim()
    {
        const int max = 600_000, keep = 450_000;
        if (Box.TextLength <= max) return 0;
        var text = Box.Text;
        int cut = text.IndexOf('\n', text.Length - keep);
        if (cut < 0) return 0;
        Box.Select(0, cut + 1);
        Box.SelectedText = "";
        return cut + 1;
    }

    bool AtBottom()
    {
        var info = new Native.SCROLLINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.SCROLLINFO)), fMask = Native.SIF_ALL };
        if (!Native.GetScrollInfo(Box.Handle, Native.SB_VERT, ref info)) return true;
        return info.nPos + (int)info.nPage >= info.nMax - Theme.S(30);
    }

    // the panel's own lines start with one of these symbols
    public const string Started = "► ", Ok = "✓ ", Failed = "× ", Stopped = "■ ", Command = "› ";

    static readonly string[] Bad = { "traceback", "error", "exception", "failed", "problem", "not found", "cannot ",
                                     "could not", "errore", "impossibile" };
    static readonly string[] Warn = { "warning", "note:", "attenzione" };
    static readonly string[] Good = { "uvicorn running", "success", " passed", "saved in", "installed.", "tunnel on",
                                      "app restarted", "start requested" };

    static Color ColorFor(string line)
    {
        if (line.StartsWith(Started)) return Color.FromArgb(96, 165, 250);
        if (line.StartsWith(Command)) return Color.FromArgb(167, 139, 250);
        if (line.StartsWith(Ok)) return Color.FromArgb(74, 222, 128);
        if (line.StartsWith(Failed)) return Color.FromArgb(248, 113, 113);
        if (line.StartsWith(Stopped)) return Color.FromArgb(161, 161, 170);
        var lower = line.ToLowerInvariant();
        foreach (var w in Bad) if (lower.Contains(w)) return Color.FromArgb(252, 129, 129);
        foreach (var w in Warn) if (lower.Contains(w)) return Color.FromArgb(250, 204, 21);
        foreach (var w in Good) if (lower.Contains(w)) return Color.FromArgb(134, 239, 172);
        return Theme.ConsoleText;
    }
}

/// <summary>A process started by the panel: its output goes to a console tab, its input can be typed there, and
/// it lives in a job object, so that stopping it stops everything it started (quest.py -> edge server -> ...).</summary>
sealed class RunningTask
{
    public readonly string Title;
    public readonly ConsoleTab Tab;
    public readonly DateTime Started = DateTime.Now;
    public Process Process { get; private set; }
    public bool Done { get; private set; }
    public bool Killed { get; private set; }
    public int ExitCode { get; private set; }
    public Action<RunningTask> Exited;  // on the UI thread

    IntPtr job;
    StreamWriter input;

    RunningTask(ConsoleTab tab, string title)
    {
        Tab = tab;
        Title = title;
    }

    public TimeSpan Elapsed => DateTime.Now - Started;

    public static RunningTask Start(ConsoleTab tab, string title, string exe, string arguments, string workDir,
                                    IDictionary<string, string> environment, Control ui)
    {
        var task = new RunningTask(tab, title);
        var utf8 = new UTF8Encoding(false);
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var kv in environment) info.EnvironmentVariables[kv.Key] = kv.Value;
        var process = new Process { StartInfo = info };
        process.Start();
        task.Process = process;
        task.job = Native.CreateKillOnCloseJob();
        if (task.job != IntPtr.Zero && !Native.AssignProcessToJobObject(task.job, process.Handle))
        {
            Native.CloseHandle(task.job);
            task.job = IntPtr.Zero;
        }
        task.input = new StreamWriter(process.StandardInput.BaseStream, utf8) { AutoFlush = true, NewLine = "\n" };
        var readers = new[] { Reader(process.StandardOutput, tab), Reader(process.StandardError, tab) };
        new Thread(() =>
        {
            process.WaitForExit();
            foreach (var r in readers) r.Join(3000);
            int code;
            try { code = process.ExitCode; } catch (Exception) { code = -1; }
            try
            {
                ui.BeginInvoke((Action)(() => task.Finish(code)));
            }
            catch (Exception)
            {
                // the window is gone
            }
        }) { IsBackground = true }.Start();
        return task;
    }

    static Thread Reader(StreamReader reader, ConsoleTab tab)
    {
        var thread = new Thread(() =>
        {
            var buffer = new char[4096];
            try
            {
                int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0) tab.Write(new string(buffer, 0, n));
            }
            catch (Exception)
            {
                // pipe closed
            }
        }) { IsBackground = true };
        thread.Start();
        return thread;
    }

    void Finish(int code)
    {
        Done = true;
        ExitCode = code;
        if (job != IntPtr.Zero && Native.ActiveProcesses(job) == 0)
        {
            Native.CloseHandle(job);
            job = IntPtr.Zero;
        }
        // else: something it started is still alive (e.g. an adb server): the job is closed, and it stopped, when
        // this app exits
        var time = Format.Duration(Elapsed);
        if (Killed) Tab.Write($"{ConsoleTab.Stopped}{Title}: fermato ({time})\n\n");
        else if (code == 0) Tab.Write($"{ConsoleTab.Ok}{Title}: finito ({time})\n\n");
        else Tab.Write($"{ConsoleTab.Failed}{Title}: finito con errore (codice {code}, {time})\n\n");
        Exited?.Invoke(this);
    }

    /// <summary>A line on the process's standard input (e.g. the Enter that ends a measured session).</summary>
    public bool SendLine(string text)
    {
        if (Done) return false;
        try
        {
            input.WriteLine(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stops the process and everything it started.</summary>
    public void Kill()
    {
        if (Done) return;
        Killed = true;
        if (job != IntPtr.Zero && Native.TerminateJobObject(job, 1)) return;
        try
        {
            Shell.Run("taskkill.exe", $"/PID {Process.Id} /T /F", 5000);
        }
        catch (Exception)
        {
            // already gone
        }
    }
}

static class Format
{
    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min"
                                                                       : $"{(int)t.TotalHours} h {t.Minutes:00} min";

    /// <summary>"1 richiesta", "3 richieste".</summary>
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static string Seconds(double s) => s < 1 ? $"{s * 1000:0} ms" : s < 10 ? $"{s:0.0} s" : $"{s:0} s";

    public static string Ago(DateTime when)
    {
        var t = DateTime.Now - when;
        return t.TotalSeconds < 60 ? $"{Math.Max(0, t.TotalSeconds):0} s fa" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0} min fa"
                                                                             : when.ToString("dd/MM HH:mm");
    }
}
