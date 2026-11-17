// The window: the rail on the left (one entry per page, each with the live state of what the page is about), the
// selected page, the console at the bottom.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdgeControl;

sealed class MainForm : Form
{
    public readonly Repo Repo;
    public readonly Settings Settings = new Settings();
    public readonly Monitors Monitors;
    public ServerStatus Server = new ServerStatus();
    public HeadsetStatus Headset = new HeadsetStatus();
    public PcStatus Pc = new PcStatus();
    public readonly List<RequestRow> Requests = new List<RequestRow>();
    public event Action RequestsChanged;
    public DateTime LastWifiRequest = DateTime.MinValue;
    public string LastWifiClient;

    readonly Dictionary<ConsoleKind, ConsoleTab> consoles = new Dictionary<ConsoleKind, ConsoleTab>();
    readonly Dictionary<ConsoleKind, TabButton> consoleButtons = new Dictionary<ConsoleKind, TabButton>();
    readonly List<Page> pages = new List<Page>();
    readonly List<RailEntry> rail = new List<RailEntry>();
    readonly ToolTip railTips = new ToolTip { InitialDelay = 400, AutoPopDelay = 15000 };
    readonly List<string> history = new List<string>();
    int historyIndex;
    ConsoleTab activeConsole;
    Page currentPage;
    RailEntry railServer, railHeadset, railResults, railTools;
    string[] sessions = new string[0];
    DateTime sessionsCheckedAt = DateTime.MinValue;
    SplitContainer split;
    Panel consoleBody;
    SmoothListView requestsView;
    TabButton requestsButton;
    bool requestsShown;
    DateTime lastRequestAt = DateTime.MinValue;
    TextBox input;
    FlatButton btnInterrupt;
    readonly Timer flushTimer = new Timer { Interval = 80 };
    readonly Timer clockTimer = new Timer { Interval = 1000 };
    readonly Timer blinkTimer = new Timer { Interval = 500 };
    readonly string snapshotDir;
    bool closeAfterSession;

    public ServerPage ServerPage { get; private set; }
    public HeadsetPage HeadsetPage { get; private set; }
    public ResultsPage ResultsPage { get; private set; }

    public MainForm(Repo repo, string snapshotDir)
    {
        Repo = repo;
        this.snapshotDir = snapshotDir;
        Monitors = new Monitors(this, repo);
        Text = "EdgeControl - ondemand-mesh-vr";
        Font = Theme.Ui;
        BackColor = Theme.Cabinet;
        AutoScaleMode = AutoScaleMode.None;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { /* default icon */ }
        var area = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(Theme.S(1280), area.Width), Math.Min(Theme.S(900), area.Height));
        MinimumSize = new Size(Math.Min(Theme.S(980), area.Width), Math.Min(Theme.S(640), area.Height));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        foreach (var (kind, name) in new[] { (ConsoleKind.Server, "Server"), (ConsoleKind.Session, "Sessione"),
                                             (ConsoleKind.Logs, "Log visore"), (ConsoleKind.Commands, "Comandi") })
            consoles[kind] = new ConsoleTab(kind, name);

        SuspendLayout();
        // the splitter has the colour of the console bar under it: the console seems to start there
        split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.ConsoleBar,
            SplitterWidth = Theme.S(5), FixedPanel = FixedPanel.None, Panel1MinSize = Theme.S(260), Panel2MinSize = Theme.S(120),
        };
        split.Panel1.BackColor = Theme.Sheet;
        split.Panel2.BackColor = Theme.ConsoleBg;
        Controls.Add(split);

        ServerPage = new ServerPage(this);
        HeadsetPage = new HeadsetPage(this);
        ResultsPage = new ResultsPage(this);
        pages.AddRange(new Page[] { ServerPage, HeadsetPage, ResultsPage, new ToolsPage(this) });
        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sheet };
        foreach (var page in pages) pageHost.Controls.Add(page);
        split.Panel1.Controls.Add(pageHost);
        split.Panel1.Controls.Add(BuildRail());
        split.Panel2.Controls.Add(BuildConsole());
        ResumeLayout(true);

        Monitors.Server += OnServer;
        Monitors.Headset += OnHeadset;
        Monitors.Pc += OnPc;
        flushTimer.Tick += (s, e) => { foreach (var c in consoles.Values) c.Flush(); };
        clockTimer.Tick += (s, e) => { UpdateRail(); UpdateRequestsDot(); currentPage?.StatusChanged(); };
        blinkTimer.Tick += (s, e) =>
        {
            RailEntry.BlinkLit = !RailEntry.BlinkLit;
            foreach (var entry in rail.Where(r => r.Blinking)) entry.Invalidate();
        };

        var startPage = Settings.GetInt("page", 0);
        ShowPage(pages[Math.Max(0, Math.Min(pages.Count - 1, startPage))]);
        ShowConsole(ConsoleKind.Commands);
        UpdateRail();
        if (!Repo.HasVenv)
            consoles[ConsoleKind.Commands].Write($"{ConsoleTab.Failed}Ambiente Python non trovato ({Repo.VenvPython}).\n" +
                           "  Preparalo da Strumenti > Prepara questo PC.\n\n");
    }

    // ------------------------------------------------------------------ layout
    /// <summary>One entry per page, each with the live state of what the page is about (the server, the headset, the
    /// sessions saved, this PC), and the nameplate at the bottom.</summary>
    Control BuildRail()
    {
        var host = new Panel { Dock = DockStyle.Left, Width = Theme.S(216), BackColor = Theme.Cabinet };
        var entries = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Cabinet,
            Padding = new Padding(0, Theme.S(8), 0, 0), Margin = Padding.Empty,
        };
        railServer = new RailEntry("Server", 3, true);
        railHeadset = new RailEntry("Visore", 2, true);
        railResults = new RailEntry("Risultati", 2, false);
        railTools = new RailEntry("Strumenti", 2, true);
        rail.AddRange(new[] { railServer, railHeadset, railResults, railTools });
        for (int i = 0; i < pages.Count; i++)
        {
            var target = pages[i];
            rail[i].Width = host.Width;
            rail[i].Click += (s, e) => ShowPage(target);
            entries.Controls.Add(rail[i]);
        }
        var plate = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(46), BackColor = Theme.Cabinet };
        plate.Paint += (s, e) =>
        {
            var g = e.Graphics;
            int size = Theme.S(22), y = (plate.Height - size) / 2;
            Logo.Draw(g, new Rectangle(Theme.S(14), y, size, size));
            var name = TextRenderer.MeasureText("EdgeControl", Theme.Heading, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "EdgeControl", Theme.Heading, new Point(Theme.S(14) + size + Theme.S(9), (plate.Height - name.Height) / 2),
                                  Theme.Muted, TextFormatFlags.NoPadding);
        };
        // the rail's right edge, broken where the selected entry joins the page
        foreach (var panel in new Control[] { entries, plate })
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.Rule);
                e.Graphics.DrawLine(pen, panel.Width - 1, 0, panel.Width - 1, panel.Height);
            };
        host.Controls.Add(entries);
        host.Controls.Add(plate);
        return host;
    }

    Control BuildConsole()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ConsoleBg };
        var bar = new Panel { Dock = DockStyle.Top, Height = Theme.S(30), BackColor = Theme.ConsoleBar };
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.ConsoleBar, Padding = Padding.Empty };
        foreach (var tab in consoles.Values)
        {
            var button = new TabButton(tab.Name);
            var kind = tab.Kind;
            button.Click += (s, e) => ShowConsole(kind);
            consoleButtons[kind] = button;
            tabs.Controls.Add(button);
            if (kind == ConsoleKind.Server)
            {
                requestsButton = new TabButton("Richieste (000)") { Text = "Richieste" };
                requestsButton.Click += (s, e) => ShowRequests();
                tabs.Controls.Add(requestsButton);
            }
        }
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = Theme.ConsoleBar, Padding = new Padding(0, 0, Theme.S(4), 0),
        };
        btnInterrupt = ConsoleButton("Interrompi", InterruptActive, stops: true);
        tools.Controls.Add(btnInterrupt);
        tools.Controls.Add(ConsoleButton("Pulisci", () =>
        {
            if (requestsShown)
            {
                Requests.Clear();
                FillRequests();
            }
            else activeConsole.Clear();
        }));
        tools.Controls.Add(ConsoleButton("Copia", () =>
        {
            var text = requestsShown ? RequestsText("\t") : activeConsole.Box.Text;
            if (text.Length > 0) Clipboard.SetText(text);
        }));
        tools.Controls.Add(ConsoleButton("Salva…", SaveConsole));
        bar.Controls.Add(tabs);
        bar.Controls.Add(tools);

        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(30), BackColor = Theme.ConsoleBar, Padding = Theme.P(12, 7, 6, 4) };
        input = new TextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.ConsoleBar, ForeColor = Theme.ConsoleBright,
            Font = Theme.Mono,
        };
        input.KeyDown += InputKeyDown;
        var prompt = new Label
        {
            Dock = DockStyle.Left, Text = ">", Width = Theme.S(18), ForeColor = Theme.ConsoleDim, Font = Theme.Mono,
            TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, Theme.S(1), 0, 0),
        };
        inputRow.Controls.Add(input);
        inputRow.Controls.Add(prompt);
        input.BringToFront();

        consoleBody = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ConsoleBg, Padding = Theme.P(12, 8, 2, 4) };
        foreach (var tab in consoles.Values) consoleBody.Controls.Add(tab.Box);
        consoleBody.Controls.Add(requestsView = BuildRequestsView());
        host.Controls.Add(consoleBody);
        host.Controls.Add(inputRow);
        host.Controls.Add(bar);
        consoleBody.BringToFront();
        return host;
    }

    FlatButton ConsoleButton(string text, Action action, bool stops = false)
    {
        var button = new DarkButton(text, stops);
        button.Click += (s, e) => action();
        return button;
    }

    /// <summary>A word on the dark console bar that does something; the one that stops a process is red.</summary>
    sealed class DarkButton : FlatButton
    {
        readonly bool stops;

        public DarkButton(string text, bool stops) : base(text)
        {
            this.stops = stops;
            Margin = Padding.Empty;
            Font = Theme.Small;
            Size = new Size(TextRenderer.MeasureText(text, Theme.Small).Width + Theme.S(16), Theme.S(30));
        }

        public override Size GetPreferredSize(Size proposed) =>
            new Size(TextRenderer.MeasureText(Text, Theme.Small).Width + Theme.S(16), Theme.S(30));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var hot = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
            var back = hot ? Color.FromArgb(50, 55, 58) : Theme.ConsoleBar;
            g.Clear(back);
            var fore = !Enabled ? Color.FromArgb(88, 94, 91) : stops ? Theme.ConsoleBad : Theme.ConsoleText;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, back, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Theme.S(2), -Theme.S(4)), fore, back);
        }
    }

    // ------------------------------------------------------------------ navigation and console tabs
    public void ShowPage(Page page)
    {
        if (currentPage == page) return;
        currentPage = page;
        foreach (var p in pages) p.Visible = p == page;
        for (int i = 0; i < pages.Count; i++) rail[i].Selected = pages[i] == page;
        page.Showing();
        page.StatusChanged();
    }

    public void ShowConsole(ConsoleKind kind)
    {
        activeConsole = consoles[kind];
        requestsShown = false;
        requestsView.Visible = false;
        requestsButton.Selected = false;
        foreach (var c in consoles.Values) c.Box.Visible = c == activeConsole;
        foreach (var kv in consoleButtons) kv.Value.Selected = kv.Key == kind;
        activeConsole.Flush();
        UpdateConsoleBar();
    }

    /// <summary>The requests the server logged (its CSV log, fetched with GET /requests), newest first.</summary>
    public void ShowRequests()
    {
        requestsShown = true;
        foreach (var c in consoles.Values) c.Box.Visible = false;
        foreach (var b in consoleButtons.Values) b.Selected = false;
        requestsButton.Selected = true;
        requestsView.Visible = true;
        UpdateConsoleBar();
    }

    SmoothListView BuildRequestsView()
    {
        var view = new SmoothListView
        {
            Dock = DockStyle.Fill, Visible = false, BackColor = Theme.ConsoleBg, ForeColor = Theme.ConsoleText, OwnerDraw = true,
            Font = Theme.Ui, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        foreach (var (title, width, right) in new[] { ("Ora", 66, false), ("Oggetto", 92, false), ("Esito", 140, false), ("Server", 66, true),
                                                      ("Triangoli", 70, true), ("F-score", 60, true), ("Da", 160, false), ("Modello", 110, false),
                                                      ("Dettaglio", 420, false) })
            view.Columns.Add(title, Theme.S(width), right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        view.DrawColumnHeader += (s, e) =>
        {
            using (var back = new SolidBrush(Theme.ConsoleBar)) e.Graphics.FillRectangle(back, e.Bounds);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                        (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.Small, Rectangle.Inflate(e.Bounds, -Theme.S(4), 0),
                                  Theme.ConsoleDim, flags);
        };
        view.DrawItem += (s, e) => e.DrawDefault = true;
        view.DrawSubItem += (s, e) => e.DrawDefault = true;
        // the last column fills the width: no light strip of empty header on the right
        view.Resize += (s, e) => view.Columns[view.Columns.Count - 1].Width = -2;
        RequestsChanged += FillRequests;
        return view;
    }

    void FillRequests()
    {
        requestsView.BeginUpdate();
        try
        {
            requestsView.Items.Clear();
            foreach (var r in Enumerable.Reverse(Requests).Take(300))
                requestsView.Items.Add(new ListViewItem(RequestCells(r))
                {
                    ForeColor = r.Status == "ok" ? Theme.ConsoleText : Theme.ConsoleBad,
                });
        }
        finally
        {
            requestsView.EndUpdate();
        }
        requestsButton.Text = Requests.Count > 0 ? $"Richieste ({Requests.Count})" : "Richieste";
        UpdateRequestsDot();
    }

    /// <summary>The white lamp (information) of the Requests tab: a request arrived in the last 15 s.</summary>
    void UpdateRequestsDot() =>
        requestsButton.Dot = (DateTime.Now - lastRequestAt).TotalSeconds < 15 ? Theme.LampInfo : Color.Empty;

    static string[] RequestCells(RequestRow r)
    {
        double.TryParse(r.ServerMs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms);
        double.TryParse(r.FScore, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fscore);
        var status = r.Status == "ok" ? "ok" : r.Status.StartsWith("error 504") ? "504 tempo scaduto"
                   : r.Status.StartsWith("error ") ? "errore " + r.Status.Substring(6) : r.Status;
        return new[]
        {
            r.Time, r.Object, status, ms > 0 ? Format.Seconds(ms / 1000) : "", r.Triangles, r.FScore.Length > 0 ? fscore.ToString("0.00") : "",
            r.Client == "127.0.0.1" ? "questo PC o cavo USB" : r.Client.Length > 0 ? r.Client + " (rete)" : "", r.Model, r.Detail,
        };
    }

    string RequestsText(string separator)
    {
        if (Requests.Count == 0) return "";
        var lines = new List<string> { string.Join(separator, requestsView.Columns.Cast<ColumnHeader>().Select(c => c.Text)) };
        lines.AddRange(Enumerable.Reverse(Requests).Select(r => string.Join(separator, RequestCells(r))));
        return string.Join("\r\n", lines);
    }

    public ConsoleTab ConsoleOf(ConsoleKind kind) => consoles[kind];

    void UpdateConsoleBar()
    {
        foreach (var kv in consoleButtons)
        {
            var task = consoles[kv.Key].Task;
            kv.Value.Dot = task == null ? Color.Empty
                : !task.Done ? Theme.LampGo
                : task.Killed ? Color.Empty
                : task.ExitCode != 0 ? Theme.LampStop : Color.Empty;
        }
        bool running = !requestsShown && activeConsole.Running;
        btnInterrupt.Enabled = running;
    }

    // ------------------------------------------------------------------ tasks
    Dictionary<string, string> ChildEnvironment(bool venv)
    {
        var env = new Dictionary<string, string>
        {
            ["PYTHONUNBUFFERED"] = "1", ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1", ["NO_COLOR"] = "1",
        };
        if (venv && Repo.HasVenv)
        {
            env["VIRTUAL_ENV"] = Repo.VenvDir;
            env["PATH"] = Path.Combine(Repo.VenvDir, "Scripts") + ";" + System.Environment.GetEnvironmentVariable("PATH");
        }
        return env;
    }

    public bool IsRunning(ConsoleKind kind) => consoles[kind].Running;

    public RunningTask TaskOf(ConsoleKind kind) => consoles[kind].Task;

    /// <summary>Starts a process in a console tab; null (with the reason shown) if it cannot start now.</summary>
    public RunningTask Run(ConsoleKind kind, string title, string exe, IEnumerable<string> args, bool venv = true,
                           Action<RunningTask> exited = null)
    {
        var tab = consoles[kind];
        if (tab.Running)
        {
            MessageBox.Show(this, $"Nella scheda «{tab.Name}» della console è ancora in corso «{tab.Task.Title}».\n\n" +
                                  "Aspetta che finisca, oppure fermalo con «Interrompi» sopra la console.",
                            "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        var argList = args.ToList();
        // the adb server must be a child of this app, not of the task: a server started by quest.py would live in the
        // task's job and die with it (and with it the USB tunnel and Unity's connection to the headset)
        if (argList.Any(a => a.EndsWith("quest.py")) && Monitors.Adb != null) Monitors.Adb.Run("start-server", 8000);
        var shown = Cmd.Join(new[] { Display(exe) }.Concat(argList));
        tab.Write($"{ConsoleTab.Started}{title}\n{ConsoleTab.Command}{shown}\n");
        ShowConsole(kind);
        try
        {
            var task = RunningTask.Start(tab, title, exe, Cmd.Join(argList), Repo.Root, ChildEnvironment(venv), this);
            task.Exited = done =>
            {
                UpdateConsoleBar();
                RefreshStatus();
                Monitors.KickServer();
                exited?.Invoke(done);
            };
            tab.Task = task;
            UpdateConsoleBar();
            RefreshStatus();
            Monitors.KickServer();
            return task;
        }
        catch (Exception e)
        {
            tab.Write($"{ConsoleTab.Failed}non avviato: {e.Message}\n\n");
            return null;
        }
    }

    /// <summary>A script of the project with the project's Python (.venv).</summary>
    public RunningTask RunPython(ConsoleKind kind, string title, IEnumerable<string> args, Action<RunningTask> exited = null)
    {
        if (!Repo.HasVenv)
        {
            MessageBox.Show(this, $"Manca l'ambiente Python del progetto:\n{Repo.VenvPython}\n\n" +
                                  "Preparalo dalla pagina Strumenti > «Prepara questo PC» (fa quello che faceva setup_pc.bat).",
                            "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowPage(pages[3]);
            return null;
        }
        return Run(kind, title, Repo.VenvPython, args, true, exited);
    }

    /// <summary>A path as the console shows it: relative to the project when inside it.</summary>
    public string Display(string path)
    {
        if (path.Equals(Repo.VenvPython, StringComparison.OrdinalIgnoreCase)) return @".venv\Scripts\python.exe";
        return path.StartsWith(Repo.Root + "\\", StringComparison.OrdinalIgnoreCase) ? path.Substring(Repo.Root.Length + 1) : path;
    }

    public void Stop(ConsoleKind kind) => consoles[kind].Task?.Kill();

    public bool SendLine(ConsoleKind kind, string text) => consoles[kind].Task?.SendLine(text) == true;

    /// <summary>A line in the Commands console, for the actions the panel does itself (adb calls).</summary>
    public void Log(string text) => consoles[ConsoleKind.Commands].Write(text.EndsWith("\n") ? text : text + "\n");

    void InterruptActive()
    {
        var tab = activeConsole;
        if (requestsShown || !tab.Running) return;
        if (tab.Kind == ConsoleKind.Session &&
            MessageBox.Show(this, "Interrompere la sessione SENZA salvare i risultati?\n\n" +
                                  "Per salvarli usa invece «Termina e salva» nella pagina Server.",
                            "EdgeControl", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        tab.Task.Kill();
    }

    void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
        {
            if (history.Count == 0) return;
            historyIndex = Math.Max(0, Math.Min(history.Count, historyIndex + (e.KeyCode == Keys.Up ? -1 : 1)));
            input.Text = historyIndex < history.Count ? history[historyIndex] : "";
            input.SelectionStart = input.TextLength;
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode != Keys.Enter) return;
        e.Handled = e.SuppressKeyPress = true;
        var text = input.Text;
        input.Clear();
        if (requestsShown) ShowConsole(ConsoleKind.Commands);
        var tab = activeConsole;
        if (tab.Running)
        {
            tab.Write(text + "\n");
            tab.Task.SendLine(text);
            return;
        }
        if (text.Trim().Length == 0) return;
        history.Remove(text);
        history.Add(text);
        historyIndex = history.Count;
        Run(tab.Kind, "Comando", System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            new[] { "/d", "/s", "/c", text });
    }

    void SaveConsole()
    {
        var name = requestsShown ? "richieste" : activeConsole.Name.Replace(' ', '_').ToLowerInvariant();
        using var dialog = new SaveFileDialog
        {
            Filter = requestsShown ? "CSV (*.csv)|*.csv" : "Testo (*.txt)|*.txt",
            FileName = $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}" + (requestsShown ? ".csv" : ".txt"),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, requestsShown ? RequestsText(";") : activeConsole.Box.Text.Replace("\n", "\r\n"),
                          System.Text.Encoding.UTF8);
    }

    // ------------------------------------------------------------------ status
    void OnServer(ServerStatus status)
    {
        Server = status;
        if (status.Restarted) Requests.Clear();
        if (status.NewRows.Count > 0 || status.Restarted)
        {
            Requests.AddRange(status.NewRows);
            if (Requests.Count > 500) Requests.RemoveRange(0, Requests.Count - 500);
            if (status.NewRows.Count > 0) lastRequestAt = DateTime.Now;
            var wifi = status.NewRows.LastOrDefault(r => r.Client.Length > 0 && r.Client != "127.0.0.1");
            if (wifi != null)
            {
                LastWifiRequest = DateTime.Now;
                LastWifiClient = wifi.Client;
            }
            RequestsChanged?.Invoke();
        }
        RefreshStatus();
    }

    void OnHeadset(HeadsetStatus status)
    {
        Headset = status;
        RefreshStatus();
    }

    void OnPc(PcStatus status)
    {
        Pc = status;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        UpdateRail();
        UpdateConsoleBar();
        currentPage?.StatusChanged();
    }

    /// <summary>Who started the server that answers on the port.</summary>
    public string ServerOwner => IsRunning(ConsoleKind.Session) ? "session" : IsRunning(ConsoleKind.Server) ? "panel"
                               : Server.Up ? "outside" : "none";

    /// <summary>The state lines and lamps of the rail.</summary>
    void UpdateRail()
    {
        // server: green = answering, amber blinking = starting, off = off
        var owner = ServerOwner;
        if (Server.Up)
        {
            var where = Server.Lan == true ? "rete e questo PC" : Server.Lan == false ? "solo questo PC" : "";
            var notes = new List<string>();
            if (Server.Backend != "mock" && Server.Served > 0) notes.Add($"tipico {Format.Seconds(Server.TypicalS)}");
            if (owner == "outside") notes.Add("avviato fuori dall'app");
            if (owner == "session") notes.Add("sessione di misura");
            Show(railServer, Theme.LampGo, false, Server.Describe(),
                 string.Join(", ", new[] { where, Format.Count(Server.Served, "richiesta", "richieste") }.Where(p => p.Length > 0)),
                 string.Join(", ", notes));
        }
        else if (owner == "panel" || owner == "session")
        {
            var task = TaskOf(owner == "panel" ? ConsoleKind.Server : ConsoleKind.Session);
            Show(railServer, Theme.LampAmber, Theme.Blinks, $"in avvio da {Format.Duration(task.Elapsed)}",
                 ServerPage.LoadsModel ? "caricamento del modello" : "");
        }
        else Show(railServer, Color.Empty, false, "spento");

        // headset
        var h = Headset;
        switch (h.State)
        {
            case "device":
                var detail = new List<string>();
                if (h.BatteryLevel.HasValue) detail.Add($"batteria {h.BatteryLevel}%" + (h.Charging ? " in carica" : ""));
                if (h.Installed == false) detail.Add("app non installata");
                else if (h.Running == true) detail.Add("app aperta");
                else if (h.Installed == true) detail.Add("app chiusa");
                Show(railHeadset, Theme.LampGo, false, (string.IsNullOrEmpty(h.Model) ? "Quest" : h.Model) + " col cavo USB",
                     string.Join(", ", detail));
                break;
            case "unauthorized":
                Show(railHeadset, Theme.LampAmber, false, "da autorizzare", "nel visore: «Consenti debug USB»");
                break;
            case "offline":
                Show(railHeadset, Theme.LampAmber, false, "offline", "scollega e ricollega il cavo");
                break;
            case "noadb":
                Show(railHeadset, Theme.LampStop, false, "adb non trovato", "serve Android Build Support");
                break;
            case "starting":
                Show(railHeadset, Color.Empty, false, "controllo…");
                break;
            case "error":
                Show(railHeadset, Theme.LampAmber, false, "adb non risponde");
                break;
            default:
                // no cable: lit if it asked for meshes over Wi-Fi in the last 10 minutes
                if ((DateTime.Now - LastWifiRequest).TotalMinutes < 10)
                    Show(railHeadset, Theme.LampGo, false, $"Wi-Fi, {LastWifiClient}", $"ultima richiesta {Format.Ago(LastWifiRequest)}");
                else Show(railHeadset, Color.Empty, false, "cavo non collegato");
                break;
        }

        // the measured sessions saved
        if ((DateTime.Now - sessionsCheckedAt).TotalSeconds >= 10) SessionsChanged();
        var newest = sessions.OrderByDescending(name => name, StringComparer.Ordinal).FirstOrDefault();
        Show(railResults, Color.Empty, false, sessions.Length == 0 ? "nessuna sessione" : Format.Count(sessions.Length, "sessione", "sessioni"),
             newest != null && DateTime.TryParseExact(newest.Length >= 15 ? newest.Substring(0, 15) : newest, "yyyyMMdd_HHmmss",
                                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)
                 ? $"ultima {last:dd/MM HH:mm}" : "");

        // this PC: red = no Python environment, amber = no local network
        var ip = Pc.Addresses.FirstOrDefault();
        var gpu = Pc.HasGpu ? $"{Pc.GpuName.Replace("GeForce ", "")}, {Pc.GpuUsedMb}/{Pc.GpuTotalMb} MB, {Pc.GpuUtil}%" : "";
        Show(railTools, Repo.HasVenv ? (ip != null ? Theme.LampGo : Theme.LampAmber) : Theme.LampStop, false,
             ip != null ? $"IP {ip}" + (Pc.Addresses.Count > 1 ? $" (+{Pc.Addresses.Count - 1})" : "") : "nessuna rete locale",
             Repo.HasVenv ? gpu : "manca .venv: Prepara questo PC");
    }

    void Show(RailEntry entry, Color lamp, bool blink, params string[] lines)
    {
        if (entry.Set(lamp, blink, lines)) railTips.SetToolTip(entry, entry.StateText);
    }

    /// <summary>Reads again the folders of the measured sessions (the Results entry of the rail counts them).</summary>
    public void SessionsChanged()
    {
        sessionsCheckedAt = DateTime.Now;
        try
        {
            sessions = Directory.Exists(Repo.QuestResults)
                ? Directory.GetDirectories(Repo.QuestResults).Select(Path.GetFileName).ToArray() : new string[0];
        }
        catch (Exception)
        {
            // the folder is being written: next time
        }
    }

    // ------------------------------------------------------------------ lifetime
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // the console's height is what is remembered, not the splitter's position, which moves with the layout above
        int console = Settings.GetInt("console", 0);
        if (console < Theme.S(120) || split.Height - split.SplitterWidth - console < Theme.S(300)) console = Theme.S(200);
        try
        {
            split.SplitterDistance = Math.Max(Theme.S(260), split.Height - split.SplitterWidth - console);
        }
        catch (Exception)
        {
            // window too small for it
        }
        Monitors.Start();
        flushTimer.Start();
        clockTimer.Start();
        blinkTimer.Start();
        if (snapshotDir != null) TakeSnapshots();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var running = consoles.Values.Where(c => c.Running).ToList();
        if (running.Count > 0 && snapshotDir == null && e.CloseReason == CloseReason.UserClosing)
        {
            if (IsRunning(ConsoleKind.Session))
            {
                var answer = MessageBox.Show(this,
                    "C'è una sessione di misura in corso: chiudendo ora i suoi risultati non vengono salvati.\n\n" +
                    "Sì = termina e salva la sessione, poi chiudi\nNo = chiudi senza salvare\nAnnulla = non chiudere",
                    "EdgeControl", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (answer == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (answer == DialogResult.Yes)
                {
                    e.Cancel = true;
                    closeAfterSession = true;
                    ServerPage.EndSession();
                    return;
                }
            }
            else
            {
                var names = string.Join("\n", running.Select(c => $"  • {c.Task.Title}  (scheda {c.Name})"));
                if (MessageBox.Show(this, $"Sono ancora in corso:\n{names}\n\nChiudendo l'app vengono fermati. Chiudere?",
                                    "EdgeControl", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }
        foreach (var c in running) c.Task.Kill();
        Settings.Set("page", pages.IndexOf(currentPage));
        if (WindowState == FormWindowState.Normal) Settings.Set("console", split.Panel2.Height);
        if (snapshotDir == null)
        {
            ServerPage.SaveSettings();
            Settings.Save();
        }
        Monitors.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>Called when the measured session ends: close if the user asked to while it was running.</summary>
    public void SessionEnded()
    {
        if (closeAfterSession) BeginInvoke((Action)Close);
    }

    // ------------------------------------------------------------------ snapshots (development aid)
    /// <summary>EdgeControl.exe --snapshot DIR: one PNG per page (the window as it is on the screen), the Server page
    /// with its advanced options open and the requests list, then exit (nothing is saved in the settings).</summary>
    async void TakeSnapshots()
    {
        TopMost = true;
        consoles[ConsoleKind.Commands].Write($"{ConsoleTab.Started}Esempio\n{ConsoleTab.Command}python -m pytest -q tests\n" +
                                             "........................................  [100%]\n82 passed in 12.3s\n" +
                                             $"{ConsoleTab.Ok}Esempio: finito (13 s)\n");
        await System.Threading.Tasks.Task.Delay(4000);
        Directory.CreateDirectory(snapshotDir);
        async System.Threading.Tasks.Task Shot(string name)
        {
            await System.Threading.Tasks.Task.Delay(700);
            var bounds = Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bitmap.Save(Path.Combine(snapshotDir, name + ".png"), ImageFormat.Png);
        }
        for (int i = 0; i < pages.Count; i++)
        {
            ShowPage(pages[i]);
            await Shot($"{i + 1}_{pages[i].NavText}");
        }
        ShowPage(ServerPage);
        ServerPage.ToggleAdvanced(true);
        ShowRequests();
        await Shot("5_Opzioni_e_richieste");
        Close();
    }
}
