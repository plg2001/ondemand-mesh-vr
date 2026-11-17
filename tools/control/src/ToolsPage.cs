// Tools: prepare this PC (setup_pc.bat), other project commands, shortcuts to folders and documents.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdgeControl;

sealed class ToolsPage : Page
{
    public override string NavText => "Strumenti";

    CheckBox chkNoWeights, chkNoTests, chkForceData, chkDryRun;
    TextBox tbFrom;
    FlatButton btnSetup, btnExport, btnRun;
    SmoothListView commands;

    /// <summary>Name, arguments for the project's Python (or a program on PATH), what it is for.</summary>
    static readonly (string Name, string[] Args, string Note)[] Commands =
    {
        ("Test del progetto", new[] { "-m", "pytest", "-q", "tests" }, "senza pesi né GPU, ~1 min"),
        ("Scena di prova", new[] { @"data\scenes\build_scene.py" }, "nuvole, riferimenti, copia in Unity"),
        ("Nuvole di test", new[] { @"data\inputs\generate_pointclouds.py" }, @"data\inputs, 100-8192 punti"),
        ("Codice e pesi dei modelli", new[] { @"models\setup_models.py" }, "controlla; scarica se mancano"),
        ("Metodi classici a confronto", new[] { "-m", "benchmarks.classical_baselines" }, "CPU, circa 2 minuti"),
        ("Tabelle dei requisiti", new[] { "-m", "benchmarks.requirements_tables", "--faces", "400", "800", "1500" }, "stime per Quest 2 / 3S ed Edge"),
        ("Stato della GPU", new[] { "nvidia-smi" }, "memoria e processi (nvidia-smi)"),
    };

    public ToolsPage(MainForm app) : base(app) => Build();

    void Build()
    {
        var tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        // setup_pc
        chkNoWeights = Make.Check("senza pesi");
        chkNoTests = Make.Check("senza test");
        chkForceData = Make.Check("rigenera i dati");
        chkDryRun = Make.Check("prova a vuoto");
        tips.SetToolTip(chkNoWeights, "--skip-weights: solo il codice dei modelli, senza scaricare gli 11 GB di pesi.");
        tips.SetToolTip(chkForceData, "--force-data: rigenera le nuvole di prova e la scena anche se ci sono già.");
        tips.SetToolTip(chkDryRun, "--dry-run: mostra soltanto cosa farebbe.");
        tbFrom = Make.Text(250, "", @"es. E:\checkpoints (vuoto = da internet)");
        var browse = new FlatButton("Sfoglia…");
        browse.Margin = new Padding(0, 0, 0, Theme.S(6));
        browse.Click += (s, e) =>
        {
            var dir = PickFolder("Cartella con la copia dei pesi (fatta con «Esporta i pesi»)");
            if (dir != null) tbFrom.Text = dir;
        };
        btnSetup = new FlatButton("Prepara / controlla il PC", Look.Start);
        btnExport = new FlatButton("Esporta i pesi…");
        btnSetup.Click += (s, e) => Setup();
        btnExport.Click += (s, e) => Export();
        tips.SetToolTip(btnSetup, "tools\\setup_pc.py (era setup_pc.bat).");
        tips.SetToolTip(btnExport, "Copia i 6 file dei pesi (10,6 GB) in una cartella, per esempio un disco esterno: sull'altro PC «Pesi da» quella cartella.");
        var actions = Make.Row(btnSetup, btnExport);
        actions.Margin = new Padding(0, Theme.S(8), 0, 0);
        var setupCard = Make.Section("Prepara questo PC",
            Make.Row(chkNoWeights, chkNoTests, chkForceData, chkDryRun),
            Make.Row(Make.Inline("Pesi da"), tbFrom, browse),
            actions);

        // other commands
        commands = new SmoothListView { ShowItemToolTips = true };
        commands.Columns.Add("Comando", Theme.S(190));
        commands.Columns.Add("A cosa serve", Theme.S(228));
        foreach (var c in Commands)
            commands.Items.Add(new ListViewItem(new[] { c.Name, c.Note })
            {
                ToolTipText = (c.Args[0] == "nvidia-smi" ? "" : "python ") + string.Join(" ", c.Args),
            });
        commands.Items[0].Selected = true;
        commands.DoubleClick += (s, e) => RunSelected();
        btnRun = new FlatButton("Esegui", Look.Start);
        btnRun.Margin = new Padding(0, Theme.S(8), 0, 0);
        btnRun.Click += (s, e) => RunSelected();
        var commandsCard = Make.Section("Altri comandi del progetto", Make.Framed(commands, new Size(Theme.S(440), Theme.S(166))), btnRun);

        // shortcuts
        var open = Make.Section("Scorciatoie", Make.Row(
            Shortcut("Cartella del progetto", () => Shell.Open(App.Repo.Root)),
            Shortcut("README", () => Shell.Open(App.Repo.At("README.md"))),
            Shortcut("Stato del progetto", () => Shell.Open(App.Repo.At("docs", "stato_progetto.md"))),
            Shortcut("API del server", () => Shell.Open(App.Monitors.BaseUrl + "/docs")),
            Shortcut("Log del server", OpenServerLog),
            Shortcut("Progetto Unity", OpenUnity)));
        open.Margin = new Padding(0, Theme.S(6), 0, 0);

        var root = Make.Column(
            (Make.Columns((setupCard, 52), (commandsCard, 48)), 0),
            (open, 0),
            (new Panel { BackColor = Color.Transparent }, 100));
        Install(root, 940, 360);
    }

    static FlatButton Shortcut(string text, Action action)
    {
        var link = new FlatButton(text, Look.Link);
        link.Click += (s, e) => action();
        return link;
    }

    string PickFolder(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true };
        return dialog.ShowDialog(App) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    bool PythonBusy()
    {
        if (!App.IsRunning(ConsoleKind.Server) && !App.IsRunning(ConsoleKind.Session)) return false;
        MessageBox.Show(App, "Ferma prima il server (o la sessione): la preparazione può reinstallare i pacchetti che sta usando.",
                        "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    void Setup()
    {
        if (PythonBusy()) return;
        var (python, first) = Repo.SystemPython();
        var args = first.Concat(new[] { @"tools\setup_pc.py" }).ToList();
        if (chkNoWeights.Checked) args.Add("--skip-weights");
        if (chkNoTests.Checked) args.Add("--no-tests");
        if (chkForceData.Checked) args.Add("--force-data");
        if (chkDryRun.Checked) args.Add("--dry-run");
        if (tbFrom.Text.Trim().Length > 0)
        {
            if (!Directory.Exists(tbFrom.Text.Trim()))
            {
                MessageBox.Show(App, $"Cartella non trovata:\n{tbFrom.Text.Trim()}", "EdgeControl");
                return;
            }
            args.AddRange(new[] { "--checkpoints-from", tbFrom.Text.Trim() });
        }
        App.Run(ConsoleKind.Commands, "Preparazione del PC", python, args, false, done => App.RefreshStatus());
    }

    void Export()
    {
        var dir = PickFolder("Dove copiare i pesi (per esempio un disco esterno): poi sull'altro PC «Pesi da» questa cartella");
        if (dir == null) return;
        var (python, first) = Repo.SystemPython();
        var args = first.Concat(new[] { @"tools\setup_pc.py", "--export-checkpoints", dir }).ToList();
        if (chkDryRun.Checked) args.Add("--dry-run");
        App.Run(ConsoleKind.Commands, "Esportazione dei pesi", python, args, false);
    }

    void RunSelected()
    {
        if (commands.SelectedItems.Count == 0) return;
        var c = Commands[commands.SelectedItems[0].Index];
        if (c.Args[0] == "nvidia-smi")
        {
            App.Run(ConsoleKind.Commands, c.Name, Shell.Which("nvidia-smi.exe") ?? "nvidia-smi.exe", new string[0]);
            return;
        }
        App.RunPython(ConsoleKind.Commands, c.Name, c.Args);
    }

    void OpenServerLog()
    {
        if (File.Exists(App.Repo.ServerLog)) Shell.Reveal(App.Repo.ServerLog);
        else MessageBox.Show(App, $"Il server non ha ancora registrato richieste:\n{App.Repo.ServerLog}", "EdgeControl");
    }

    void OpenUnity()
    {
        var editor = App.Repo.UnityEditor();
        if (editor == null)
        {
            MessageBox.Show(App, "Non trovo la versione di Unity del progetto installata da Unity Hub: apri il progetto da Unity Hub " +
                                 $"({App.Repo.UnityProject}).", "EdgeControl");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(editor, "-projectPath " + Cmd.Quote(App.Repo.UnityProject))
            {
                UseShellExecute = false,
            });
            App.Log($"{ConsoleTab.Started}Unity: {App.Repo.UnityProject} (si apre in qualche decina di secondi)");
        }
        catch (Exception e)
        {
            MessageBox.Show(App, $"Unity non si è avviato: {e.Message}", "EdgeControl");
        }
    }

    public override void StatusChanged()
    {
        bool free = !App.IsRunning(ConsoleKind.Commands);
        btnSetup.Enabled = btnExport.Enabled = btnRun.Enabled = free;
    }
}
