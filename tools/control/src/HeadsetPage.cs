// The headset page: is the Quest connected (and how charged, which app version, which server it uses), install
// the app, start / restart / close it, point it at a server, live logs, copy its CSVs.
// It replaces tools\quest\check_headset.bat, install_app.bat, show_logs.bat and pull_results.bat.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EdgeControl;

sealed class HeadsetPage : Page
{
    public override string NavText => "Visore";
    public override string NavGlyph => Glyph.Headset;

    readonly Dictionary<string, Label> values = new Dictionary<string, Label>();
    readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
    Label lblApk, lblApkHint;
    FlatButton btnInstall, btnPickApk, btnStartApp, btnRestartApp, btnStopApp, btnRefresh, btnCheck, btnLinkWifi,
               btnLinkUsb, btnLinkOther, btnLogs, btnStopLogs, btnPull;
    TextBox tbOther;
    string apk;

    public HeadsetPage(MainForm app) : base(app)
    {
        apk = App.Settings.Get("apk", "");
        if (apk.Length == 0 || !File.Exists(apk)) apk = App.Repo.DefaultApk;
        Build();
    }

    void Build()
    {
        // state and diagnostics
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var key in new[] { "Collegamento", "Visore", "Batteria", "App", "Server dell'app", "Tunnel USB", "Wi-Fi del visore" })
        {
            var name = Make.Label(key, Theme.Ui, Theme.Muted);
            name.Margin = new Padding(0, Theme.S(2), Theme.S(14), Theme.S(2));
            var value = Make.Label("–", key == "Collegamento" ? Theme.Bold : Theme.Ui, Theme.Text, 420);
            value.Margin = new Padding(0, Theme.S(2), 0, Theme.S(2));
            values[key] = value;
            grid.Controls.Add(name);
            grid.Controls.Add(value);
        }
        btnRefresh = new FlatButton("Aggiorna", Look.Secondary, Glyph.Refresh);
        btnCheck = new FlatButton("Controllo completo", Look.Secondary, Glyph.Check);
        btnRefresh.Click += (s, e) => App.Monitors.KickHeadset();
        btnCheck.Click += (s, e) => Quest("Controllo del visore", "check");
        tips.SetToolTip(btnCheck, "quest.py check (era check_headset.bat): autorizzazione, versione di Android, app, indirizzo, tunnel, server.");
        btnLogs = new FlatButton("Log Unity dal vivo", Look.Secondary, Glyph.Log);
        btnStopLogs = new FlatButton("Ferma il log", Look.Ghost, Glyph.Stop);
        btnPull = new FlatButton("Copia i CSV", Look.Secondary, Glyph.Download);
        btnLogs.Click += (s, e) =>
        {
            App.ConsoleOf(ConsoleKind.Logs).Clear();
            App.RunPython(ConsoleKind.Logs, "Log Unity del visore", new[] { @"tools\quest\quest.py", "logs" });
        };
        btnStopLogs.Click += (s, e) => App.Stop(ConsoleKind.Logs);
        btnPull.Click += (s, e) => Pull(App);
        tips.SetToolTip(btnLogs, "quest.py logs: la Console di Unity dell'app sul visore, dal vivo, nella scheda «Log visore».");
        tips.SetToolTip(btnPull, @"quest.py pull: edge_timings.csv e ondemand_events.csv dal visore a benchmarks\results\quest\<data_ora>\.");
        var buttons = Make.Row(btnRefresh, btnCheck, btnLogs, btnStopLogs, btnPull);
        buttons.WrapContents = true;
        buttons.MaximumSize = new Size(Theme.S(520), 0);
        buttons.Margin = new Padding(0, Theme.S(6), 0, 0);
        var state = Make.CardOf("Stato", grid, buttons);

        // app
        lblApk = Make.Label("", Theme.Ui, Theme.Text, 420);
        lblApkHint = Make.Label("", Theme.Ui, Theme.Amber, 420);
        btnInstall = new FlatButton("Installa e avvia", Look.Primary, Glyph.Download);
        btnPickApk = new FlatButton("Altro APK…", Look.Ghost, Glyph.Folder);
        btnStartApp = new FlatButton("Avvia", Look.Secondary, Glyph.Play);
        btnRestartApp = new FlatButton("Riavvia", Look.Secondary, Glyph.Refresh);
        btnStopApp = new FlatButton("Chiudi", Look.Secondary, Glyph.Stop);
        btnInstall.Click += (s, e) => Install();
        btnPickApk.Click += (s, e) => PickApk();
        btnStartApp.Click += (s, e) => AppAction("Avvio dell'app", StartCommand());
        btnRestartApp.Click += (s, e) => AppAction("Riavvio dell'app", $"am force-stop {App.Monitors.Package}", StartCommand());
        btnStopApp.Click += (s, e) => AppAction("Chiusura dell'app", $"am force-stop {App.Monitors.Package}");
        tips.SetToolTip(btnInstall, "quest.py install --run (era install_app.bat): installa l'APK (circa un minuto) e avvia l'app.");
        var appCard = Make.CardOf("App sul visore", lblApk, lblApkHint, Make.Row(btnInstall, btnPickApk),
            Make.Row(btnStartApp, btnRestartApp, btnStopApp));

        // link
        btnLinkWifi = new FlatButton("Wi-Fi di questo PC", Look.Secondary, Glyph.Wifi);
        btnLinkUsb = new FlatButton("Cavo USB", Look.Secondary, Glyph.Usb);
        btnLinkOther = new FlatButton("Imposta", Look.Secondary, Glyph.Link);
        tbOther = Make.Text(190, App.Settings.Get("other_server", ""), "http://192.168.1.50:8000");
        btnLinkWifi.Click += (s, e) => LinkWifi(null);
        btnLinkUsb.Click += (s, e) => Quest("Collegamento via cavo USB", "usb");
        btnLinkOther.Click += (s, e) => LinkOther();
        tips.SetToolTip(btnLinkWifi, "quest.py wifi: scrive sul visore l'indirizzo di questo PC (edge_server.txt) e toglie il tunnel USB.");
        tips.SetToolTip(btnLinkUsb, "quest.py usb: tunnel attraverso il cavo (adb reverse); l'app torna all'indirizzo predefinito 127.0.0.1.");
        tips.SetToolTip(btnLinkOther, "Scrive sul visore l'indirizzo di un server su un altro PC (per esempio quello del laboratorio).");
        var linkCard = Make.CardOf("Dove l'app cerca il server",
            Make.Row(btnLinkWifi, btnLinkUsb), Make.Row(Make.Inline("Altro PC"), tbOther, btnLinkOther));

        var right = Make.Column((appCard, 0), (linkCard, 0));
        right.AutoSize = true;
        var root = Make.Column(
            (Header("Visore"), 0),
            (Make.Columns((state, 54), (right, 46)), 0),
            (new Panel { BackColor = Color.Transparent }, 100));
        Install(root, 940, 400);
    }

    string StartCommand() => $"monkey -p {App.Monitors.Package} -c android.intent.category.LAUNCHER 1";

    /// <summary>A quest.py command in the Commands console (the port and IP chosen on the Server page).</summary>
    void Quest(string title, string command, params string[] extra)
    {
        var args = new List<string> { @"tools\quest\quest.py", "--port", App.ServerPage.ChosenPort.ToString(), command };
        args.AddRange(extra);
        App.RunPython(ConsoleKind.Commands, title, args, done => App.Monitors.KickHeadset());
    }

    void Install()
    {
        if (!File.Exists(apk))
        {
            MessageBox.Show(App, $"APK non trovato:\n{apk}\n\nCostruiscilo in Unity: Tools > Quest > Prepare and build APK.",
                            "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var extra = apk.Equals(App.Repo.DefaultApk, StringComparison.OrdinalIgnoreCase) ? new string[0] : new[] { "--apk", apk };
        Quest("Installazione dell'app", "install", new[] { "--run" }.Concat(extra).ToArray());
    }

    void PickApk()
    {
        using var dialog = new OpenFileDialog { Filter = "App Android (*.apk)|*.apk", FileName = apk };
        try { dialog.InitialDirectory = Path.GetDirectoryName(apk); } catch (Exception) { /* default folder */ }
        if (dialog.ShowDialog(App) != DialogResult.OK) return;
        apk = dialog.FileName;
        App.Settings.Set("apk", apk.Equals(App.Repo.DefaultApk, StringComparison.OrdinalIgnoreCase) ? "" : apk);
        App.Settings.Save();
        StatusChanged();
    }

    void LinkWifi(string ip)
    {
        ip ??= App.ServerPage.ChosenIp;
        Quest("Collegamento via Wi-Fi", "wifi", ip.Length > 0 ? new[] { "--ip", ip } : new string[0]);
    }

    void LinkOther()
    {
        var m = Regex.Match(tbOther.Text.Trim(), @"^(?:https?://)?([^/:\s]+)(?::(\d+))?/?$");
        if (!m.Success)
        {
            MessageBox.Show(App, "Scrivi l'indirizzo del server, per esempio http://192.168.1.50:8000", "EdgeControl");
            return;
        }
        App.Settings.Set("other_server", tbOther.Text.Trim());
        App.Settings.Save();
        var port = m.Groups[2].Success ? m.Groups[2].Value : "8000";
        var args = new List<string> { @"tools\quest\quest.py", "--port", port, "wifi", "--ip", m.Groups[1].Value };
        App.RunPython(ConsoleKind.Commands, "Server su un altro PC", args, done => App.Monitors.KickHeadset());
    }

    /// <summary>quest.py pull: the app's CSVs to benchmarks\results\quest\&lt;timestamp&gt;\ (also from the Results page).</summary>
    public static void Pull(MainForm app)
    {
        app.RunPython(ConsoleKind.Commands, "Copia dei CSV dal visore", new[] { @"tools\quest\quest.py", "pull" },
                      done => app.ResultsPage.Reload());
    }

    /// <summary>adb shell commands on the headset, run by the panel itself and echoed in the Commands console.</summary>
    async void AppAction(string title, params string[] commands)
    {
        var adb = App.Monitors.Adb;
        var serial = App.Headset.Serial;
        if (adb == null || App.Headset.State != "device" || string.IsNullOrEmpty(App.Monitors.Package)) return;
        App.Log($"{ConsoleTab.Started}{title}");
        App.ShowConsole(ConsoleKind.Commands);
        foreach (var command in commands)
        {
            var args = $"-s {serial} shell {command}";
            App.Log($"{ConsoleTab.Command}adb {args}");
            var (code, output, error) = await Task.Run(() => adb.Run(args, 20000));
            var text = (output + error).Trim();
            if (text.Length > 0) App.Log(text);
            if (code != 0)
            {
                App.Log($"{ConsoleTab.Failed}{title}: errore (codice {code})\n");
                return;
            }
        }
        App.Log($"{ConsoleTab.Ok}{title}: fatto\n");
        await Task.Delay(1500);  // the app needs a moment before pidof sees it
        App.Monitors.KickHeadset();
    }

    public override void StatusChanged()
    {
        var h = App.Headset;
        bool device = h.State == "device";
        string connection;
        switch (h.State)
        {
            case "device": connection = "via cavo USB"; break;
            case "unauthorized": connection = "da autorizzare: nel visore accetta «Consenti debug USB»"; break;
            case "offline": connection = "offline: scollega e ricollega il cavo"; break;
            case "noadb": connection = "adb non trovato: in Unity Hub aggiungi il modulo Android Build Support"; break;
            case "none": connection = "cavo non collegato"; break;
            case "starting": connection = "controllo…"; break;
            default: connection = "adb non risponde"; break;
        }
        Set("Collegamento", connection, device ? Theme.Green : h.State == "none" || h.State == "starting" ? Theme.Muted : Theme.Amber);
        Set("Visore", device ? $"{h.Model} · Android {h.Android} (API {h.Api})" : "–");
        Set("Batteria", device && h.BatteryLevel.HasValue ? $"{h.BatteryLevel}%" + (h.Charging ? ", in carica" : "") : "–",
            device && h.BatteryLevel < 20 ? Theme.Red : Theme.Text);
        string appText = "–";
        if (device && h.Installed.HasValue)
            appText = h.Installed == false ? "non installata"
                : (h.Running == true ? "aperta" : "chiusa") + (h.Updated.HasValue ? $" · installata il {h.Updated:dd/MM HH:mm}" : "");
        Set("App", appText);
        Set("Server dell'app", !device || h.Override == null ? "–" : h.Override.Length > 0 ? h.Override : "http://127.0.0.1:8000");
        Set("Tunnel USB", !device ? "–" : string.IsNullOrEmpty(h.Reverse) ? "spento" : "acceso");
        Set("Wi-Fi del visore", device ? h.WifiIp ?? "non collegato" : "–");

        bool apkExists = File.Exists(apk);
        var apkName = apk.Equals(App.Repo.DefaultApk, StringComparison.OrdinalIgnoreCase) ? "APK" : Path.GetFileName(apk);
        lblApk.Text = apkExists ? $"{apkName} del {File.GetLastWriteTime(apk):dd/MM/yyyy HH:mm}"
                                : "APK non trovato: in Unity, Tools > Quest > Prepare and build APK";
        tips.SetToolTip(lblApk, App.Display(apk));
        string hint = "";
        if (device && apkExists && h.Installed == true && h.Updated.HasValue && File.GetLastWriteTime(apk) > h.Updated.Value.AddMinutes(2))
            hint = "più recente di quella installata";
        lblApkHint.Text = hint;
        lblApkHint.Visible = hint.Length > 0;

        bool commandsFree = !App.IsRunning(ConsoleKind.Commands);
        btnInstall.Enabled = device && apkExists && commandsFree;
        bool canDrive = device && h.Installed == true && App.Monitors.Package != null;
        btnStartApp.Enabled = canDrive && h.Running != true;
        btnRestartApp.Enabled = canDrive;
        btnStopApp.Enabled = canDrive && h.Running == true;
        btnCheck.Enabled = commandsFree && h.State != "noadb";
        btnLinkWifi.Enabled = btnLinkUsb.Enabled = btnLinkOther.Enabled = device && commandsFree;
        btnLogs.Enabled = device && !App.IsRunning(ConsoleKind.Logs);
        btnStopLogs.Enabled = App.IsRunning(ConsoleKind.Logs);
        btnPull.Enabled = device && commandsFree;
    }

    void Set(string key, string text, Color? color = null)
    {
        var label = values[key];
        if (label.Text != text) label.Text = text;
        label.ForeColor = color ?? Theme.Text;
    }
}
