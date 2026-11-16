// The main page: who uses the server (Unity simulator, Quest over Wi-Fi or through the cable), how it reconstructs
// (mock, generative model, classical method), which objects; then start it, or start a measured session with it.
// It replaces tools\edge\start_edge_server.bat, start_model_server.bat and tools\quest\server_*.bat, session_*.bat.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EdgeControl;

/// <summary>An entry of a combo box: the value for the command line, the text for the user.</summary>
sealed class Choice
{
    public readonly string Value, Text;
    public Choice(string value, string text) { Value = value; Text = text; }
    public override string ToString() => Text;
}

sealed class ServerPage : Page
{
    public override string NavText => "Server";
    public override string NavGlyph => Glyph.Server;

    readonly List<SceneObject> objects;
    readonly List<CheckBox> objectChecks = new List<CheckBox>();
    readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
    RadioButton rbSim, rbWifi, rbUsb, rbMock, rbModel, rbClassic, rbAll, rbOnly;
    ComboBox cbIp, cbModel, cbMethod, cbDevice, cbPrecision, cbVariant;
    NumericUpDown nDelay, nBudget, nFaces, nTokens, nPort;
    CheckBox chkFaces, chkQuality, chkSave, chkRestart, chkRemote;
    TextBox tbExtra, tbCommand, tbRemote;
    FlatButton btnStart, btnStop, btnDocs, btnSessionStart, btnSessionEnd, btnSessionResults;
    Label lblSession;
    Card advanced;
    FlatButton lnkAdvanced;
    TableLayoutPanel root;
    bool loading = true, sessionEnding;
    string lastSessionDir, sessionFps = "";
    DateTime sessionStarted;

    static readonly Choice[] Models =
    {
        new Choice("meshanything", "MeshAnything V2"), new Choice("fastmesh", "FastMesh"), new Choice("treemeshgpt", "TreeMeshGPT"),
    };

    static readonly Choice[] Methods =
    {
        new Choice("ball_pivoting", "Ball Pivoting"), new Choice("poisson", "Poisson"),
        new Choice("alpha", "Alpha shape"), new Choice("hull", "Involucro convesso"),
    };

    public ServerPage(MainForm app) : base(app)
    {
        objects = app.Repo.SceneObjects();
        Build();
        LoadSettings();
        loading = false;
        UpdateForm();
        App.ConsoleOf(ConsoleKind.Session).Line += OnSessionLine;
    }

    /// <summary>The server being started loads a generative model (it takes a while).</summary>
    public bool LoadsModel { get; private set; }

    int Port => (int)nPort.Value;

    // ------------------------------------------------------------------ layout
    static FlowLayoutPanel Indent(params Control[] controls)
    {
        var row = Make.Row(controls);
        row.Margin = new Padding(Theme.S(19), 0, 0, Theme.S(2));
        return row;
    }

    void Build()
    {
        // 1. who uses the server
        rbSim = Make.Radio("Simulatore Unity su questo PC");
        rbWifi = Make.Radio("Quest via Wi-Fi");
        rbUsb = Make.Radio("Quest via cavo USB");
        cbIp = Make.Combo(190);
        var who = Make.CardOf("1 · Chi usa il server", rbSim, rbWifi, Indent(Make.Inline("IP del PC", Theme.Muted), cbIp), rbUsb);
        tips.SetToolTip(rbSim, "python -m edge.server: il server ascolta solo su 127.0.0.1 (l'editor Unity con l'XR Device Simulator).");
        tips.SetToolTip(rbWifi, "quest.py wifi --serve: scrive l'indirizzo di questo PC sul visore (se è collegato col cavo; poi il cavo si può " +
                                "staccare) e avvia il server raggiungibile dalla rete. Serve anche il simulatore.");
        tips.SetToolTip(rbUsb, "quest.py usb --serve: il visore raggiunge il server attraverso il cavo (adb reverse). Il cavo resta collegato.");

        // 2. how it reconstructs
        rbMock = Make.Radio("Finto (involucro convesso)");
        nDelay = Make.Number(0, 600, 2, 56, 1);
        rbModel = Make.Radio("Modello generativo");
        cbModel = Make.Combo(136, Models.Cast<object>().ToArray());
        nBudget = Make.Number(10, 7200, 300, 60);
        rbClassic = Make.Radio("Metodo classico");
        cbMethod = Make.Combo(136, Methods.Cast<object>().ToArray());
        chkFaces = Make.Check("riduci a");
        chkFaces.Margin = new Padding(0, Theme.S(4), Theme.S(4), 0);
        nFaces = Make.Number(50, 200000, 1000, 70);
        var how = Make.CardOf("2 · Come ricostruisce",
            Make.Row(rbMock, nDelay, Make.Inline("s", Theme.Muted)),
            Make.Row(rbModel, cbModel),
            Indent(Make.Inline("rinuncia dopo", Theme.Muted), nBudget, Make.Inline("s", Theme.Muted)),
            Make.Row(rbClassic, cbMethod),
            Indent(chkFaces, nFaces, Make.Inline("triangoli", Theme.Muted)));
        tips.SetToolTip(rbMock, "Il backend mock: l'involucro convesso dei punti, dopo l'attesa scelta (simula un modello occupato).");
        tips.SetToolTip(rbModel, "MeshAnything V2, FastMesh o TreeMeshGPT (pesi in checkpoints\\). Il caricamento dura da 20 s a un minuto; " +
                                 "un oggetto che supera il limite riceve 504 e resta punti.");
        tips.SetToolTip(rbClassic, "Ricostruzione classica con Open3D, decimi di secondo per oggetto: Ball Pivoting, Poisson, alpha shape, " +
                                   "involucro convesso.");

        // 3. which objects
        rbAll = Make.Radio("Tutti");
        rbOnly = Make.Radio("Solo questi:");
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
            Margin = new Padding(Theme.S(19), 0, 0, Theme.S(2)),
        };
        foreach (var o in objects)
        {
            var check = Make.Check(o.Label.Length > 0 ? o.Label : o.Id);
            check.Margin = new Padding(0, Theme.S(1), Theme.S(10), Theme.S(1));
            check.Tag = o;
            tips.SetToolTip(check, o.Id);
            objectChecks.Add(check);
            grid.Controls.Add(check);
        }
        var which = Make.CardOf("3 · Quali oggetti", rbAll, rbOnly, grid);

        // start / stop, and the measured session
        btnStart = new FlatButton("Avvia server", Look.Primary, Glyph.Play);
        btnStop = new FlatButton("Ferma server", Look.Danger, Glyph.Stop);
        btnDocs = new FlatButton("API", Look.Ghost, Glyph.Web);
        btnStart.Click += (s, e) => StartServer();
        btnStop.Click += (s, e) => StopServer();
        btnDocs.Click += (s, e) => Shell.Open(App.Monitors.BaseUrl + "/docs");
        tips.SetToolTip(btnDocs, "Documentazione interattiva delle API del server (/docs), nel browser.");
        btnSessionStart = new FlatButton("Sessione di misura", Look.Primary, Glyph.Record);
        btnSessionEnd = new FlatButton("Termina e salva", Look.Danger, Glyph.Save);
        btnSessionResults = new FlatButton("Riassunto", Look.Ghost, Glyph.Document) { Visible = false };
        btnSessionStart.Click += (s, e) => StartSession();
        btnSessionEnd.Click += (s, e) => EndSession();
        btnSessionResults.Click += (s, e) =>
        {
            App.ShowPage(App.ResultsPage);
            App.ResultsPage.Reload(lastSessionDir);
        };
        chkRestart = Make.Check("riavvia l'app", true);
        chkRemote = Make.Check("server su un altro PC:");
        chkRemote.Margin = new Padding(0, Theme.S(4), Theme.S(4), Theme.S(2));
        tbRemote = Make.Text(180, "", "http://192.168.1.50:8000");
        tips.SetToolTip(chkRemote, "Il server è già acceso su un altro PC (per esempio quello del laboratorio, avviato con --host 0.0.0.0): " +
                                   "la sessione non ne avvia uno qui e alla fine scarica da lì le righe del log e le mesh.");
        lblSession = Make.Inline("", Theme.Muted);  // only while a session runs: elapsed time, fps, requests
        var serverHalf = Make.Row(btnStart, btnStop, btnDocs);
        var sessionHalf = Make.Stack(Make.Row(btnSessionStart, btnSessionEnd, btnSessionResults, lblSession),
                                     Make.Row(chkRestart, chkRemote, tbRemote));
        var divider = new Panel { Size = new Size(1, Theme.S(20)), BackColor = Theme.Border, Margin = new Padding(Theme.S(4), Theme.S(2), Theme.S(16), Theme.S(2)) };
        var actionsTable = Make.Columns((serverHalf, 0), (divider, 0), (sessionHalf, 100));
        actionsTable.Dock = DockStyle.None;
        var actions = Make.CardOf(null, actionsTable);

        // the command that will run, and the advanced options
        lnkAdvanced = new FlatButton("Opzioni avanzate", Look.Ghost, Glyph.Settings) { Margin = new Padding(0, 0, Theme.S(12), 0) };
        lnkAdvanced.Click += (s, e) => ToggleAdvanced(!advanced.Visible);
        tips.SetToolTip(lnkAdvanced, "Porta, dispositivo e precisione del modello, accuratezza, salvataggio delle mesh, altre opzioni.");
        tbCommand = new TextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Mono, BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(55, 65, 81), Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(3), Theme.S(6), 0),
        };
        tips.SetToolTip(tbCommand, "Il comando che «Avvia server» esegue nella cartella del progetto (si può copiare in un terminale).");
        var copy = new FlatButton("Copia", Look.Ghost, Glyph.Copy);
        copy.Click += (s, e) => { if (tbCommand.TextLength > 0) Clipboard.SetText(tbCommand.Text); };
        var commandRow = Make.Columns((lnkAdvanced, 0), (Make.Inline("Comando", Theme.Muted), 0), (tbCommand, 100), (copy, 0));
        commandRow.Margin = new Padding(0, 0, Theme.S(10), Theme.S(6));

        nPort = Make.Number(1024, 65535, 8000, 66);
        cbDevice = Make.Combo(66, "auto", "cuda", "cpu");
        cbPrecision = Make.Combo(88, "auto", "fp32", "fp16", "bf16", "fp16-full", "bf16-full");
        cbVariant = Make.Combo(104);
        nTokens = Make.Number(0, 100000, 0, 66);
        chkQuality = Make.Check("misura l'accuratezza", true);
        chkSave = Make.Check("salva le mesh");
        tips.SetToolTip(chkSave, @"--save-meshes benchmarks\results\edge_meshes");
        tbExtra = Make.Text(230, "", "altre opzioni di edge.server");
        tips.SetToolTip(nTokens, "Tronca la generazione dopo N token (mesh parziali). 0 = nessun limite.");
        tips.SetToolTip(tbExtra, "Opzioni in più per python -m edge.server, per esempio --scene altra_scena.");
        advanced = Make.CardOf(null,
            Make.Row(Make.Inline("Porta"), nPort, Make.Inline("   Modello generativo:", Theme.Muted), Make.Inline("dispositivo"), cbDevice,
                     Make.Inline("precisione"), cbPrecision, Make.Inline("variante"), cbVariant, Make.Inline("max token"), nTokens),
            Make.Row(chkQuality, chkSave, Make.Inline("Altro"), tbExtra));
        advanced.Visible = false;

        root = Make.Column(
            (Header("Server Edge"), 0),
            (Make.Columns((who, 30), (how, 37), (which, 33)), 0),
            (actions, 0),
            (commandRow, 0),
            (advanced, 0),
            (new Panel { BackColor = Color.Transparent }, 100));
        Install(root, 960, 420);

        // WinForms makes radio buttons exclusive only inside one container: these sit in different rows
        foreach (var group in new[] { new[] { rbSim, rbWifi, rbUsb }, new[] { rbMock, rbModel, rbClassic }, new[] { rbAll, rbOnly } })
            foreach (var radio in group)
                radio.CheckedChanged += (s, e) =>
                {
                    if (!((RadioButton)s).Checked) return;
                    foreach (var other in group)
                        if (other != s) other.Checked = false;
                };
        foreach (var radio in new[] { rbSim, rbWifi, rbUsb, rbMock, rbModel, rbClassic, rbAll, rbOnly })
            radio.CheckedChanged += (s, e) => UpdateForm();
        foreach (var combo in new[] { cbIp, cbModel, cbMethod, cbDevice, cbPrecision, cbVariant })
            combo.SelectedIndexChanged += (s, e) => UpdateForm();
        foreach (var number in new[] { nDelay, nBudget, nFaces, nTokens, nPort })
            number.ValueChanged += (s, e) => UpdateForm();
        foreach (var check in new[] { chkFaces, chkQuality, chkSave, chkRestart, chkRemote })
            check.CheckedChanged += (s, e) => UpdateForm();
        foreach (var check in objectChecks)
            check.CheckedChanged += (s, e) =>
            {
                if (!loading && ((CheckBox)s).Checked) rbOnly.Checked = true;
                UpdateForm();
            };
        tbExtra.TextChanged += (s, e) => UpdateForm();
        tbRemote.TextChanged += (s, e) => UpdateForm();
        cbModel.SelectedIndexChanged += (s, e) => FillVariants();
        // a click on a control of an option selects that option
        nDelay.Enter += (s, e) => rbMock.Checked = true;
        cbModel.Enter += (s, e) => rbModel.Checked = true;
        nBudget.Enter += (s, e) => rbModel.Checked = true;
        cbMethod.Enter += (s, e) => rbClassic.Checked = true;
        chkFaces.Enter += (s, e) => rbClassic.Checked = true;
        nFaces.Enter += (s, e) => rbClassic.Checked = true;
        cbIp.Enter += (s, e) => rbWifi.Checked = true;
    }

    void ToggleAdvanced(bool show)
    {
        advanced.Visible = show;
        lnkAdvanced.Text = show ? "Nascondi le opzioni" : "Opzioni avanzate";
        // the page gets longer (it scrolls if the window is short) and shows the options just opened
        int extra = show ? advanced.GetPreferredSize(Size.Empty).Height + Theme.S(16) : 0;
        root.MinimumSize = new Size(Theme.S(960), Theme.S(420) + extra);
        AutoScrollMinSize = root.MinimumSize;
        if (show && IsHandleCreated) BeginInvoke((Action)(() => ScrollControlIntoView(advanced)));
    }

    void FillVariants()
    {
        var model = Value(cbModel);
        var current = Value(cbVariant);
        cbVariant.Items.Clear();
        cbVariant.Items.Add(new Choice("", "predefinita"));
        if (model == "fastmesh") cbVariant.Items.AddRange(new object[] { new Choice("V1K", "V1K"), new Choice("V4K", "V4K") });
        if (model == "treemeshgpt") cbVariant.Items.AddRange(new object[] { new Choice("7bit", "7bit"), new Choice("9bit", "9bit") });
        SelectValue(cbVariant, current);
    }

    void FillAddresses()
    {
        var current = Value(cbIp);
        var addresses = App.Pc.Addresses;
        var wanted = new List<Choice> { new Choice("", "automatico" + (addresses.Count > 0 ? $" ({addresses[0]})" : "")) };
        wanted.AddRange(addresses.Select(a => new Choice(a, a)));
        if (current.Length > 0 && !addresses.Contains(current)) wanted.Add(new Choice(current, current + " (non trovato)"));
        if (cbIp.Items.Count == wanted.Count && wanted.Select((c, i) => c.Text == cbIp.Items[i].ToString()).All(x => x)) return;
        cbIp.Items.Clear();
        cbIp.Items.AddRange(wanted.Cast<object>().ToArray());
        SelectValue(cbIp, current);
    }

    static string Value(ComboBox combo) => combo.SelectedItem is Choice c ? c.Value : combo.SelectedItem?.ToString() ?? "";

    static void SelectValue(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            var item = combo.Items[i];
            if ((item is Choice c ? c.Value : item.ToString()) == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    List<string> SelectedObjects() => objectChecks.Where(c => c.Checked).Select(c => ((SceneObject)c.Tag).Id).ToList();

    // ------------------------------------------------------------------ command lines
    /// <summary>Options for python -m edge.server (without --host / --port, which the launcher sets).</summary>
    List<string> ServerArgs(bool forSession)
    {
        var a = new List<string>();
        if (rbModel.Checked)
        {
            a.AddRange(new[] { "--backend", "model", "--model", Value(cbModel), "--time-budget-s", Cmd.Num(nBudget.Value) });
            if (Value(cbVariant).Length > 0) a.AddRange(new[] { "--variant", Value(cbVariant) });
            if (Value(cbDevice) != "auto") a.AddRange(new[] { "--device", Value(cbDevice) });
            if (Value(cbPrecision) != "auto") a.AddRange(new[] { "--precision", Value(cbPrecision) });
            if (nTokens.Value > 0) a.AddRange(new[] { "--max-new-tokens", Cmd.Num(nTokens.Value) });
        }
        else if (rbClassic.Checked)
        {
            a.AddRange(new[] { "--backend", "classical", "--method", Value(cbMethod) });
            if (chkFaces.Checked) a.AddRange(new[] { "--target-faces", Cmd.Num(nFaces.Value) });
        }
        else a.AddRange(new[] { "--backend", "mock", "--mock-delay-s", Cmd.Num(nDelay.Value) });
        var chosen = SelectedObjects();
        if (rbOnly.Checked && chosen.Count > 0) a.AddRange(new[] { "--objects", string.Join(",", chosen) });
        if (!chkQuality.Checked) a.Add("--no-quality");
        if (chkSave.Checked && !forSession) a.AddRange(new[] { "--save-meshes", Repo.SavedMeshes });  // a session saves its own
        a.AddRange(Cmd.Split(tbExtra.Text));
        return a;
    }

    static string ServerArgsOption(List<string> args) => "--server-args=" + string.Join(" ", args.Select(Cmd.PosixQuote));

    (List<string> Args, string Title) ServerCommand()
    {
        var port = Port.ToString(CultureInfo.InvariantCulture);
        if (rbSim.Checked)
            return (new List<string> { "-m", "edge.server", "--port", port }.Concat(ServerArgs(false)).ToList(),
                    "Server per il simulatore");
        var a = new List<string> { @"tools\quest\quest.py", "--port", port, rbWifi.Checked ? "wifi" : "usb", "--serve" };
        if (rbWifi.Checked && Value(cbIp).Length > 0) a.AddRange(new[] { "--ip", Value(cbIp) });
        a.Add(ServerArgsOption(ServerArgs(false)));
        return (a, rbWifi.Checked ? "Server per il Quest via Wi-Fi" : "Server per il Quest via cavo USB");
    }

    List<string> SessionCommand()
    {
        var a = new List<string> { @"tools\quest\quest.py", "--port", Port.ToString(CultureInfo.InvariantCulture), "session",
                                   rbUsb.Checked ? "usb" : "wifi" };
        if (rbWifi.Checked && Value(cbIp).Length > 0) a.AddRange(new[] { "--ip", Value(cbIp) });
        if (chkRemote.Checked) a.AddRange(new[] { "--server", tbRemote.Text.Trim() });
        else a.Add(ServerArgsOption(ServerArgs(true)));
        if (!chkRestart.Checked) a.Add("--no-restart");
        return a;
    }

    /// <summary>The ip / port of the headset's link and the options for the Visore page.</summary>
    public string ChosenIp => Value(cbIp);
    public int ChosenPort => Port;

    void UpdateForm()
    {
        if (loading) return;
        cbIp.Enabled = rbWifi.Checked;
        nDelay.Enabled = rbMock.Checked;
        cbModel.Enabled = nBudget.Enabled = rbModel.Checked;
        cbMethod.Enabled = chkFaces.Enabled = rbClassic.Checked;
        nFaces.Enabled = rbClassic.Checked && chkFaces.Checked;
        cbDevice.Enabled = cbPrecision.Enabled = cbVariant.Enabled = nTokens.Enabled = rbModel.Checked;
        tbRemote.Enabled = chkRemote.Checked;
        if (!App.IsRunning(ConsoleKind.Server) && !App.IsRunning(ConsoleKind.Session))
            App.Monitors.BaseUrl = $"http://127.0.0.1:{Port}";
        var (args, _) = ServerCommand();
        tbCommand.Text = Cmd.Join(new[] { @".venv\Scripts\python.exe" }.Concat(args));
        tips.SetToolTip(btnSessionStart, "Collegamento, server con un log suo, riavvio dell'app, fps del visore; con «Termina e salva» " +
                                         "copia tutto in benchmarks\\results\\quest\\ e scrive summary.md. Esegue:\n" +
                                         Cmd.Join(new[] { @".venv\Scripts\python.exe" }.Concat(SessionCommand())));
        StatusChanged();
    }

    // ------------------------------------------------------------------ status
    public override void Showing() => FillAddresses();

    public override void StatusChanged()
    {
        if (loading) return;
        FillAddresses();
        bool server = App.IsRunning(ConsoleKind.Server), session = App.IsRunning(ConsoleKind.Session);
        var owner = App.ServerOwner;
        btnStart.Enabled = !server && !session;
        btnStop.Enabled = server || owner == "outside";
        btnDocs.Enabled = App.Server.Up;
        btnSessionStart.Enabled = !session;
        btnSessionEnd.Enabled = session && !sessionEnding;
        btnSessionResults.Visible = !session && lastSessionDir != null && Directory.Exists(lastSessionDir);
        string text = "";
        if (session)
        {
            var task = App.TaskOf(ConsoleKind.Session);
            var rows = App.Requests.Count(r => r.Epoch >= ToEpoch(sessionStarted) - 2);
            text = sessionEnding ? "salvataggio…"
                : $"{Format.Duration(task.Elapsed)}" + (sessionFps.Length > 0 ? $" · {sessionFps} fps" : "") +
                  $" · {Format.Count(rows, "richiesta", "richieste")}";
        }
        if (lblSession.Text != text) lblSession.Text = text;
    }

    static double ToEpoch(DateTime local) => (local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    // ------------------------------------------------------------------ actions
    void Info(string text) => MessageBox.Show(App, text, "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Information);

    async void StartServer()
    {
        if (App.IsRunning(ConsoleKind.Server) || App.IsRunning(ConsoleKind.Session)) return;
        if (rbOnly.Checked && SelectedObjects().Count == 0)
        {
            Info("Scegli almeno un oggetto, oppure «Tutti».");
            return;
        }
        if (rbUsb.Checked && App.Headset.State != "device")
        {
            Info("Per il collegamento via cavo il visore deve essere collegato con il cavo USB e autorizzato " +
                 "(nel visore: «Consenti debug USB»).");
            return;
        }
        App.Monitors.BaseUrl = $"http://127.0.0.1:{Port}";
        if (App.Server.Up || Native.ListeningPid(Port) != 0)
        {
            if (!await StopOutsideServer("Prima di avviarne uno nuovo va fermato quello già acceso.", true)) return;
        }
        var (args, title) = ServerCommand();
        SaveSettings();
        LoadsModel = rbModel.Checked;
        App.RunPython(ConsoleKind.Server, title, args);
    }

    async void StopServer()
    {
        if (App.IsRunning(ConsoleKind.Server))
        {
            App.Stop(ConsoleKind.Server);
            return;
        }
        if (App.IsRunning(ConsoleKind.Session))
        {
            Info("Il server fa parte della sessione di misura: per fermarlo usa «Termina e salva».");
            return;
        }
        if (App.Server.Up) await StopOutsideServer("Fermare il server?", true);
    }

    /// <summary>Stops the process listening on the port (a server started outside this app). True if the port is free.</summary>
    async Task<bool> StopOutsideServer(string reason, bool ask)
    {
        int port = Port, pid = Native.ListeningPid(port);
        if (pid == 0) return true;
        string name = "?";
        try { name = Process.GetProcessById(pid).ProcessName + ".exe"; } catch (Exception) { /* gone */ }
        var commandLine = await Task.Run(() => CommandLineOf(pid));
        var shortLine = commandLine.Length > 220 ? commandLine.Substring(0, 220) + "…" : commandLine;
        bool looksLikeServer = commandLine.Contains("edge.server") || commandLine.Contains("quest.py");
        if (ask && MessageBox.Show(App,
                $"{reason}\n\nLa porta {port} è usata da un programma avviato fuori da questa app:\n{name} (PID {pid})\n{shortLine}\n\n" +
                (looksLikeServer ? "Fermarlo?" : "ATTENZIONE: non sembra il server Edge. Fermarlo comunque?"),
                "EdgeControl", MessageBoxButtons.YesNo, looksLikeServer ? MessageBoxIcon.Question : MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;
        try
        {
            Process.GetProcessById(pid).Kill();
        }
        catch (Exception e)
        {
            Info($"Non riesco a fermarlo: {e.Message}");
            return false;
        }
        App.Log($"{ConsoleTab.Stopped}fermato {name} (PID {pid}) che usava la porta {port}");
        bool free = await WaitPortFree(port, 8000);
        App.Monitors.KickServer();
        return free;
    }

    static string CommandLineOf(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject o in searcher.Get()) return o["CommandLine"] as string ?? "";
        }
        catch (Exception)
        {
            // WMI unavailable
        }
        return "";
    }

    static async Task<bool> WaitPortFree(int port, int timeoutMs)
    {
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (Native.ListeningPid(port) != 0)
        {
            if (DateTime.Now > until) return false;
            await Task.Delay(150);
        }
        return true;
    }

    async void StartSession()
    {
        if (App.IsRunning(ConsoleKind.Session)) return;
        if (rbSim.Checked)
        {
            Info("La sessione di misura si fa con il visore: nel riquadro 1 scegli «Quest via Wi-Fi» o «Quest via cavo USB».");
            return;
        }
        bool remote = chkRemote.Checked;
        if (remote && !rbWifi.Checked)
        {
            Info("Un server su un altro PC si raggiunge solo via Wi-Fi: nel riquadro 1 scegli «Quest via Wi-Fi».");
            return;
        }
        if (remote && !Regex.IsMatch(tbRemote.Text.Trim(), @"^https?://[^/\s]+"))
        {
            Info("Scrivi l'indirizzo del server sull'altro PC, per esempio http://192.168.1.50:8000");
            return;
        }
        if (App.Headset.State != "device")
        {
            Info("Collega il visore con il cavo USB e autorizzalo: la sessione registra gli fps attraverso il cavo, " +
                 "anche quando le richieste passano dal Wi-Fi.");
            return;
        }
        if (App.Headset.Installed == false)
        {
            Info("L'app non è installata sul visore: installala dalla pagina Visore.");
            return;
        }
        if (!remote && rbOnly.Checked && SelectedObjects().Count == 0)
        {
            Info("Scegli almeno un oggetto, oppure «Tutti».");
            return;
        }
        if (!remote)
        {
            if (App.IsRunning(ConsoleKind.Server))
            {
                if (MessageBox.Show(App, "Il server avviato da questa pagina verrà fermato: la sessione ne avvia uno suo, con le " +
                                         "stesse impostazioni, che salva log e mesh nella cartella della sessione.",
                                    "EdgeControl", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                    return;
                App.Stop(ConsoleKind.Server);
                if (!await WaitPortFree(Port, 10000))
                {
                    Info("Il server non si è ancora fermato: riprova tra qualche secondo.");
                    return;
                }
            }
            else if (App.Server.Up || Native.ListeningPid(Port) != 0)
            {
                var answer = MessageBox.Show(App,
                    "È acceso un server avviato fuori da questa app.\n\n" +
                    "Sì = fermalo: la sessione ne avvia uno suo, con le impostazioni di questa pagina\n" +
                    "No = usalo così com'è (le sue mesh non finiscono nella cartella della sessione)\n" +
                    "Annulla = non iniziare", "EdgeControl", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes && !await StopOutsideServer("", false)) return;
            }
        }
        SaveSettings();
        LoadsModel = !remote && rbModel.Checked;
        sessionStarted = DateTime.Now;
        sessionFps = "";
        sessionEnding = false;
        lastSessionDir = null;
        App.Monitors.BaseUrl = remote ? tbRemote.Text.Trim() : $"http://127.0.0.1:{Port}";
        var task = App.RunPython(ConsoleKind.Session, "Sessione di misura", SessionCommand(), SessionFinished);
        if (task == null) App.Monitors.BaseUrl = $"http://127.0.0.1:{Port}";
        StatusChanged();
    }

    /// <summary>The Enter that ends the session: quest.py copies the results and writes the summary.</summary>
    public void EndSession()
    {
        if (!App.IsRunning(ConsoleKind.Session) || sessionEnding) return;
        sessionEnding = true;
        App.ConsoleOf(ConsoleKind.Session).Write("[EdgeControl] Invio: fine della sessione\n");
        App.SendLine(ConsoleKind.Session, "");
        App.ShowConsole(ConsoleKind.Session);
        StatusChanged();
    }

    void OnSessionLine(string line)
    {
        var fps = Regex.Match(line, @"app fps (\S+)");
        if (fps.Success) sessionFps = fps.Groups[1].Value;
        if (line.StartsWith("saved in ")) lastSessionDir = line.Substring("saved in ".Length).Trim();
    }

    void SessionFinished(RunningTask task)
    {
        sessionEnding = false;
        App.Monitors.BaseUrl = $"http://127.0.0.1:{Port}";
        if (lastSessionDir != null && Directory.Exists(lastSessionDir)) App.ResultsPage.Reload(lastSessionDir);
        StatusChanged();
        App.SessionEnded();
    }

    // ------------------------------------------------------------------ settings
    void LoadSettings()
    {
        var s = App.Settings;
        (s.Get("link", "sim") == "wifi" ? rbWifi : s.Get("link") == "usb" ? rbUsb : rbSim).Checked = true;
        (s.Get("backend", "mock") == "model" ? rbModel : s.Get("backend") == "classical" ? rbClassic : rbMock).Checked = true;
        nDelay.Value = Clamp(nDelay, s.GetDecimal("mock_delay", 2));
        SelectValue(cbModel, s.Get("model", "meshanything"));
        FillVariants();
        SelectValue(cbVariant, s.Get("variant", ""));
        nBudget.Value = Clamp(nBudget, s.GetDecimal("budget", 300));
        SelectValue(cbMethod, s.Get("method", "ball_pivoting"));
        chkFaces.Checked = s.GetBool("faces_on", false);
        nFaces.Value = Clamp(nFaces, s.GetDecimal("faces", 1000));
        var chosen = new HashSet<string>(s.Get("objects", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        foreach (var check in objectChecks) check.Checked = chosen.Contains(((SceneObject)check.Tag).Id);
        (s.Get("objects_mode", "all") == "only" && chosen.Count > 0 ? rbOnly : rbAll).Checked = true;
        if (objects.Count == 0) rbOnly.Enabled = false;
        nPort.Value = Clamp(nPort, s.GetDecimal("port", 8000));
        SelectValue(cbDevice, s.Get("device", "auto"));
        SelectValue(cbPrecision, s.Get("precision", "auto"));
        nTokens.Value = Clamp(nTokens, s.GetDecimal("tokens", 0));
        chkQuality.Checked = s.GetBool("quality", true);
        chkSave.Checked = s.GetBool("save_meshes", false);
        tbExtra.Text = s.Get("extra", "");
        FillAddresses();
        SelectValue(cbIp, s.Get("ip", ""));
        chkRestart.Checked = s.GetBool("restart_app", true);
        chkRemote.Checked = s.GetBool("remote", false);
        tbRemote.Text = s.Get("remote_url", "");
        ToggleAdvanced(s.GetBool("advanced", false));
    }

    static decimal Clamp(NumericUpDown n, decimal v) => Math.Max(n.Minimum, Math.Min(n.Maximum, v));

    public void SaveSettings()
    {
        var s = App.Settings;
        s.Set("link", rbWifi.Checked ? "wifi" : rbUsb.Checked ? "usb" : "sim");
        s.Set("backend", rbModel.Checked ? "model" : rbClassic.Checked ? "classical" : "mock");
        s.Set("mock_delay", nDelay.Value);
        s.Set("model", Value(cbModel));
        s.Set("variant", Value(cbVariant));
        s.Set("budget", nBudget.Value);
        s.Set("method", Value(cbMethod));
        s.Set("faces_on", chkFaces.Checked);
        s.Set("faces", nFaces.Value);
        s.Set("objects_mode", rbOnly.Checked ? "only" : "all");
        s.Set("objects", string.Join(",", SelectedObjects()));
        s.Set("port", nPort.Value);
        s.Set("device", Value(cbDevice));
        s.Set("precision", Value(cbPrecision));
        s.Set("tokens", nTokens.Value);
        s.Set("quality", chkQuality.Checked);
        s.Set("save_meshes", chkSave.Checked);
        s.Set("extra", tbExtra.Text);
        s.Set("ip", Value(cbIp));
        s.Set("restart_app", chkRestart.Checked);
        s.Set("remote", chkRemote.Checked);
        s.Set("remote_url", tbRemote.Text);
        s.Set("advanced", advanced.Visible);
        s.Save();
    }
}
