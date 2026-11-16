// The window: status cards at the top, navigation on the left, the selected page, the console at the bottom.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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
    readonly List<NavButton> navButtons = new List<NavButton>();
    readonly List<string> history = new List<string>();
    int historyIndex;
    ConsoleTab activeConsole;
    Page currentPage;
    StatusCard cardServer, cardHeadset, cardPc;
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
        BackColor = Theme.Bg;
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
        split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Border,
            SplitterWidth = Theme.S(5), FixedPanel = FixedPanel.None, Panel1MinSize = Theme.S(260), Panel2MinSize = Theme.S(120),
        };
        split.Panel1.BackColor = Theme.Bg;
        split.Panel2.BackColor = Theme.ConsoleBg;
        Controls.Add(split);
        Controls.Add(BuildHeader());

        ServerPage = new ServerPage(this);
        HeadsetPage = new HeadsetPage(this);
        ResultsPage = new ResultsPage(this);
        pages.AddRange(new Page[] { ServerPage, HeadsetPage, ResultsPage, new ToolsPage(this) });
        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        foreach (var page in pages) pageHost.Controls.Add(page);
        split.Panel1.Controls.Add(pageHost);
        split.Panel1.Controls.Add(BuildNavigation());
        split.Panel2.Controls.Add(BuildConsole());
        ResumeLayout(true);

        Monitors.Server += OnServer;
        Monitors.Headset += OnHeadset;
        Monitors.Pc += OnPc;
        flushTimer.Tick += (s, e) => { foreach (var c in consoles.Values) c.Flush(); };
        clockTimer.Tick += (s, e) => { UpdateCards(); UpdateRequestsDot(); currentPage?.StatusChanged(); };

        var startPage = Settings.GetInt("page", 0);
        ShowPage(pages[Math.Max(0, Math.Min(pages.Count - 1, startPage))]);
        ShowConsole(ConsoleKind.Commands);
        UpdateCards();
        if (!Repo.HasVenv)
            consoles[ConsoleKind.Commands].Write($"{ConsoleTab.Failed}Ambiente Python non trovato ({Repo.VenvPython}).\n" +
                           "  Preparalo da Strumenti > Prepara questo PC.\n\n");
    }

    // ------------------------------------------------------------------ layout
    Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(76), BackColor = Color.White, Padding = Theme.P(14, 8, 6, 8) };
        header.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        var brand = new Panel { Dock = DockStyle.Left, Width = Theme.S(196), BackColor = Color.White };
        brand.Paint += (s, e) =>
        {
            var g = e.Graphics;
            int size = Theme.S(40), y = (brand.Height - size) / 2;
            Logo.Draw(g, new Rectangle(0, y, size, size));
            int x = size + Theme.S(12);
            TextRenderer.DrawText(g, "EdgeControl", Theme.Subtitle, new Point(x, y - Theme.S(1)), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "ondemand-mesh-vr", Theme.Small, new Point(x, y + Theme.S(23)), Theme.Muted, TextFormatFlags.NoPadding);
        };
        cardServer = new StatusCard("Server Edge", Glyph.Server) { Dock = DockStyle.Fill };
        cardHeadset = new StatusCard("Visore", Glyph.Headset) { Dock = DockStyle.Fill };
        cardPc = new StatusCard("Questo PC", Glyph.Tools) { Dock = DockStyle.Fill };
        cardServer.Click += (s, e) => ShowPage(ServerPage);
        cardHeadset.Click += (s, e) => ShowPage(HeadsetPage);
        cardPc.Click += (s, e) => ShowPage(pages[3]);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.White };
        for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cards.Controls.Add(cardServer, 0, 0);
        cards.Controls.Add(cardHeadset, 1, 0);
        cards.Controls.Add(cardPc, 2, 0);
        header.Controls.Add(cards);
        header.Controls.Add(brand);
        return header;
    }

    Control BuildNavigation()
    {
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = Theme.S(170), BackColor = Theme.Nav, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(0, Theme.S(10), 0, 0),
        };
        foreach (var page in pages)
        {
            var button = new NavButton(page.NavText, page.NavGlyph) { Width = nav.Width };
            var target = page;
            button.Click += (s, e) => ShowPage(target);
            navButtons.Add(button);
            nav.Controls.Add(button);
        }
        return nav;
    }

    Control BuildConsole()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ConsoleBg };
        var bar = new Panel { Dock = DockStyle.Top, Height = Theme.S(36), BackColor = Theme.ConsoleBar };
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
            BackColor = Theme.ConsoleBar, Padding = new Padding(0, Theme.S(3), Theme.S(4), 0),
        };
        btnInterrupt = ConsoleButton("Interrompi", Glyph.Stop, InterruptActive);
        tools.Controls.Add(btnInterrupt);
        tools.Controls.Add(ConsoleButton("Pulisci", Glyph.Delete, () =>
        {
            if (requestsShown)
            {
                Requests.Clear();
                FillRequests();
            }
            else activeConsole.Clear();
        }));
        tools.Controls.Add(ConsoleButton("Copia", Glyph.Copy, () =>
        {
            var text = requestsShown ? RequestsText("\t") : activeConsole.Box.Text;
            if (text.Length > 0) Clipboard.SetText(text);
        }));
        tools.Controls.Add(ConsoleButton("Salva", Glyph.Save, SaveConsole));
        bar.Controls.Add(tabs);
        bar.Controls.Add(tools);

        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(34), BackColor = Theme.ConsoleBar, Padding = Theme.P(8, 5, 6, 5) };
        input = new TextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.ConsoleBar, ForeColor = Color.White,
            Font = Theme.Mono,
        };
        input.KeyDown += InputKeyDown;
        var prompt = new Label
        {
            Dock = DockStyle.Left, Text = ">", Width = Theme.S(18), ForeColor = Color.FromArgb(96, 165, 250), Font = Theme.Mono,
            TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, Theme.S(1), 0, 0),
        };
        inputRow.Controls.Add(input);
        inputRow.Controls.Add(prompt);
        input.BringToFront();

        consoleBody = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ConsoleBg, Padding = Theme.P(10, 6, 2, 4) };
        foreach (var tab in consoles.Values) consoleBody.Controls.Add(tab.Box);
        consoleBody.Controls.Add(requestsView = BuildRequestsView());
        host.Controls.Add(consoleBody);
        host.Controls.Add(inputRow);
        host.Controls.Add(bar);
        consoleBody.BringToFront();
        return host;
    }

    FlatButton ConsoleButton(string text, string glyph, Action action)
    {
        var button = new DarkButton(text, glyph);
        button.Click += (s, e) => action();
        return button;
    }

    /// <summary>A small button on the dark console bar.</summary>
    sealed class DarkButton : FlatButton
    {
        public DarkButton(string text, string glyph) : base(text, Look.Ghost, glyph)
        {
            Margin = new Padding(0, 0, Theme.S(2), 0);
            Height = Theme.S(30);
            Font = Theme.Small;
            Size = new Size(GetPreferredSize(Size.Empty).Width, Theme.S(30));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            var back = hot && Enabled ? Color.FromArgb(63, 63, 70) : Theme.ConsoleBar;
            g.Clear(back);
            var fore = Enabled ? Color.FromArgb(212, 212, 216) : Color.FromArgb(100, 100, 108);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine;
            int x = Theme.S(6);
            if (Theme.HasIcons)
            {
                TextRenderer.DrawText(g, Icon, Theme.Icon, new Rectangle(x, 1, Theme.S(18), Height), fore, back, flags);
                x += Theme.S(20);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore, back, flags);
        }
    }

    // ------------------------------------------------------------------ navigation and console tabs
    public void ShowPage(Page page)
    {
        if (currentPage == page) return;
        currentPage = page;
        foreach (var p in pages) p.Visible = p == page;
        for (int i = 0; i < pages.Count; i++) navButtons[i].Selected = pages[i] == page;
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
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.SmallBold, Rectangle.Inflate(e.Bounds, -Theme.S(4), 0),
                                  Color.FromArgb(161, 161, 170), flags);
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
                    ForeColor = r.Status == "ok" ? Theme.ConsoleText : Color.FromArgb(252, 129, 129),
                });
        }
        finally
        {
            requestsView.EndUpdate();
        }
        requestsButton.Text = Requests.Count > 0 ? $"Richieste ({Requests.Count})" : "Richieste";
        UpdateRequestsDot();
    }

    void UpdateRequestsDot() =>
        requestsButton.Dot = (DateTime.Now - lastRequestAt).TotalSeconds < 15 ? Color.FromArgb(96, 165, 250) : Color.Empty;

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
                : !task.Done ? Color.FromArgb(74, 222, 128)
                : task.Killed ? Color.Empty
                : task.ExitCode != 0 ? Color.FromArgb(248, 113, 113) : Color.Empty;
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
        UpdateCards();
        UpdateConsoleBar();
        currentPage?.StatusChanged();
    }

    /// <summary>Who started the server that answers on the port.</summary>
    public string ServerOwner => IsRunning(ConsoleKind.Session) ? "session" : IsRunning(ConsoleKind.Server) ? "panel"
                               : Server.Up ? "outside" : "none";

    void UpdateCards()
    {
        // server
        var owner = ServerOwner;
        if (Server.Up)
        {
            var where = Server.Lan == true ? "rete e questo PC" : Server.Lan == false ? "solo questo PC" : "";
            var parts = new List<string> { where, Format.Count(Server.Served, "richiesta", "richieste") };
            if (Server.Backend != "mock" && Server.Served > 0) parts.Add($"tipico {Format.Seconds(Server.TypicalS)}");
            if (owner == "outside") parts.Add("avviato fuori dall'app");
            if (owner == "session") parts.Add("sessione di misura");
            cardServer.Set(Theme.Green, Server.Describe(), string.Join(" · ", parts.Where(p => p.Length > 0)));
        }
        else if (owner == "panel" || owner == "session")
        {
            var task = TaskOf(owner == "panel" ? ConsoleKind.Server : ConsoleKind.Session);
            cardServer.Set(Theme.Amber, $"Avvio in corso… {Format.Duration(task.Elapsed)}",
                           ServerPage.LoadsModel ? "caricamento del modello" : "");
        }
        else cardServer.Set(Theme.Gray, "Spento", "");

        // headset
        var h = Headset;
        switch (h.State)
        {
            case "device":
                var detail = new List<string>();
                if (h.BatteryLevel.HasValue) detail.Add($"batteria {h.BatteryLevel}%" + (h.Charging ? " (in carica)" : ""));
                if (h.Installed == false) detail.Add("app non installata");
                else if (h.Running == true) detail.Add("app aperta");
                else if (h.Installed == true) detail.Add("app installata");
                cardHeadset.Set(Theme.Green, (string.IsNullOrEmpty(h.Model) ? "Quest" : h.Model) + " collegato (cavo USB)",
                                string.Join(" · ", detail));
                break;
            case "unauthorized":
                cardHeadset.Set(Theme.Amber, "Da autorizzare", "Nel visore accetta «Consenti debug USB»");
                break;
            case "offline":
                cardHeadset.Set(Theme.Amber, "Offline", "Scollega e ricollega il cavo");
                break;
            case "noadb":
                cardHeadset.Set(Theme.Red, "adb non trovato", "Serve il modulo Android Build Support di Unity");
                break;
            case "starting":
                cardHeadset.Set(Theme.Gray, "Controllo…", "");
                break;
            case "error":
                cardHeadset.Set(Theme.Amber, "adb non risponde", "");
                break;
            default:
                var recent = (DateTime.Now - LastWifiRequest).TotalMinutes < 10;
                cardHeadset.Set(recent ? Theme.Accent : Theme.Gray, "Cavo non collegato",
                                recent ? $"Wi-Fi: ultima richiesta {Format.Ago(LastWifiRequest)} da {LastWifiClient}" : "");
                break;
        }

        // this PC
        var pcParts = new List<string>();
        if (Pc.HasGpu) pcParts.Add($"{Pc.GpuName} · {Pc.GpuUsedMb}/{Pc.GpuTotalMb} MB · {Pc.GpuUtil}%");
        if (!Repo.HasVenv) pcParts.Insert(0, "manca .venv: Strumenti > Prepara questo PC");
        var ip = Pc.Addresses.FirstOrDefault();
        cardPc.Set(Repo.HasVenv ? (ip != null ? Theme.Green : Theme.Amber) : Theme.Red,
                   ip != null ? $"IP {ip}" + (Pc.Addresses.Count > 1 ? $" (+{Pc.Addresses.Count - 1})" : "") : "Nessuna rete locale",
                   string.Join(" · ", pcParts));
    }

    // ------------------------------------------------------------------ lifetime
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        int distance = Settings.GetInt("splitter", 0);
        try
        {
            split.SplitterDistance = distance > Theme.S(260) && distance < split.Height - Theme.S(120)
                ? distance : Math.Max(Theme.S(380), split.Height - Theme.S(200));
        }
        catch (Exception)
        {
            // window too small for the remembered position
        }
        Monitors.Start();
        flushTimer.Start();
        clockTimer.Start();
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
        if (WindowState == FormWindowState.Normal) Settings.Set("splitter", split.SplitterDistance);
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
    /// <summary>EdgeControl.exe --snapshot DIR: one PNG per page (the window as it is on the screen), then exit.</summary>
    async void TakeSnapshots()
    {
        TopMost = true;
        consoles[ConsoleKind.Commands].Write($"{ConsoleTab.Started}Esempio\n{ConsoleTab.Command}python -m pytest -q tests\n" +
                                             "........................................  [100%]\n82 passed in 12.3s\n" +
                                             $"{ConsoleTab.Ok}Esempio: finito (13 s)\n");
        await System.Threading.Tasks.Task.Delay(4000);
        Directory.CreateDirectory(snapshotDir);
        for (int i = 0; i < pages.Count; i++)
        {
            ShowPage(pages[i]);
            await System.Threading.Tasks.Task.Delay(700);
            var bounds = Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bitmap.Save(Path.Combine(snapshotDir, $"{i + 1}_{pages[i].NavText}.png"), ImageFormat.Png);
        }
        Close();
    }
}
