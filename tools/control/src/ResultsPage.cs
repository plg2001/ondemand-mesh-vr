// The results of the measured sessions (benchmarks\results\quest): list, summary.md preview, regenerate.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdgeControl;

sealed class ResultsPage : Page
{
    public override string NavText => "Risultati";

    SmoothListView list;
    RichTextBox preview;
    FlatButton btnFolder, btnSummary, btnRegenerate, btnPull, btnDelete;
    bool loaded, filling;

    public ResultsPage(MainForm app) : base(app) => Build();

    void Build()
    {
        list = new SmoothListView { Dock = DockStyle.Fill, CheckBoxes = true };  // the boxes choose what «Cancella» deletes
        foreach (var (title, width) in new[] { ("Data", 150), ("Collegamento", 96), ("Server", 196), ("Riassunto", 70) })
            list.Columns.Add(title, Theme.S(width));
        list.SelectedIndexChanged += (s, e) => ShowSelected();
        list.ItemChecked += (s, e) => { if (!filling) UpdateDelete(); };
        list.DoubleClick += (s, e) => { if (Selected != null) Shell.Open(Selected); };
        // the preview is a text box with a white margin of its own, inside the same thin frame as the list
        preview = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Sheet, Font = Theme.Mono,
            WordWrap = false, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Both,
        };
        var previewHost = new Panel { BackColor = Theme.Sheet, Padding = Theme.P(10, 8, 0, 0) };
        previewHost.Controls.Add(preview);
        var listFrame = Make.Framed(list);
        listFrame.Margin = Theme.P(0, 0, 12, 0);
        var previewFrame = Make.Framed(previewHost);
        previewFrame.Margin = Theme.P(0, 0, 10, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(540)));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(listFrame, 0, 0);
        body.Controls.Add(previewFrame, 1, 0);

        btnFolder = new FlatButton("Apri la cartella");
        btnSummary = new FlatButton("Apri summary.md");
        btnRegenerate = new FlatButton("Rigenera il riassunto");
        btnPull = new FlatButton("Copia i CSV dal visore");
        var btnAll = new FlatButton("Tutte le sessioni", Look.Link);
        var btnReload = new FlatButton("Aggiorna");
        btnDelete = new FlatButton("Cancella");
        btnDelete.Click += (s, e) => DeleteChecked();
        btnFolder.Click += (s, e) => { if (Selected != null) Shell.Open(Selected); };
        btnSummary.Click += (s, e) => { if (Selected != null) Shell.Open(Path.Combine(Selected, "summary.md")); };
        btnRegenerate.Click += (s, e) => Regenerate();
        btnPull.Click += (s, e) => HeadsetPage.Pull(App);
        btnAll.Click += (s, e) =>
        {
            Directory.CreateDirectory(App.Repo.QuestResults);
            Shell.Open(App.Repo.QuestResults);
        };
        btnReload.Click += (s, e) => Reload(Selected);
        // what acts on the selected session, then what does not
        btnRegenerate.Margin = new Padding(0, 0, Theme.S(24), Theme.S(6));
        btnPull.Margin = new Padding(0, 0, Theme.S(24), Theme.S(6));
        var actions = Make.Row(btnFolder, btnSummary, btnRegenerate, btnDelete, btnPull, btnReload, btnAll);
        actions.Margin = new Padding(0, Theme.S(10), 0, 0);

        var root = Make.Column(
            (body, 100),
            (actions, 0));
        Install(root, 900, 280);
    }

    string Selected => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null;

    public override void Showing()
    {
        if (!loaded) Reload();
    }

    /// <summary>Reads the folders again; selects the given one (or keeps the selection).</summary>
    public void Reload(string select = null)
    {
        loaded = true;
        select ??= Selected;
        var checkedDirs = new HashSet<string>(Checked, StringComparer.OrdinalIgnoreCase);
        filling = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            var dirs = Directory.Exists(App.Repo.QuestResults)
                ? Directory.GetDirectories(App.Repo.QuestResults).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList()
                : new List<string>();
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                var when = DateTime.TryParseExact(name.Length >= 15 ? name.Substring(0, 15) : name, "yyyyMMdd_HHmmss",
                                                  CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t.ToString("dd/MM/yyyy HH:mm") : name;
                var link = name.EndsWith("_wifi") ? "Wi-Fi" : name.EndsWith("_usb") ? "cavo USB" : "–";
                var item = new ListViewItem(new[] { when, link, Describe(dir), File.Exists(Path.Combine(dir, "summary.md")) ? "sì" : "–" })
                {
                    Tag = dir, ToolTipText = dir, Checked = checkedDirs.Contains(dir),
                };
                list.Items.Add(item);
                if (select != null && dir.Equals(select, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
            }
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
        }
        finally
        {
            list.EndUpdate();
            filling = false;
        }
        if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
        ShowSelected();
        UpdateDelete();
        App.SessionsChanged();
    }

    List<string> Checked => list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();

    void UpdateDelete()
    {
        int n = list.CheckedItems.Count;
        btnDelete.Text = n > 0 ? $"Cancella ({n})" : "Cancella";
        btnDelete.Enabled = n > 0 && !App.IsRunning(ConsoleKind.Session);  // the running session's folder is in the list
    }

    /// <summary>Moves the checked session folders to the Recycle Bin, after a confirmation.</summary>
    void DeleteChecked()
    {
        var dirs = Checked;
        if (dirs.Count == 0 || App.IsRunning(ConsoleKind.Session)) return;
        var names = string.Join("\n", dirs.Take(12).Select(d => "  " + Path.GetFileName(d))) +
                    (dirs.Count > 12 ? $"\n  … e altre {dirs.Count - 12}" : "");
        if (MessageBox.Show(App, $"Cancellare {(dirs.Count == 1 ? "questa sessione" : $"queste {dirs.Count} sessioni")}?\n\n{names}\n\n" +
                                 "Le cartelle vanno nel Cestino di Windows.",
                            "EdgeControl", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var root = Path.GetFullPath(App.Repo.QuestResults).TrimEnd('\\') + "\\";
        var failed = new List<string>();
        foreach (var dir in dirs)
        {
            try
            {
                if (!Path.GetFullPath(dir).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;  // only session folders
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
            {
                failed.Add($"{Path.GetFileName(dir)}: {ex.Message}");
            }
        }
        App.Log($"Risultati: {dirs.Count - failed.Count} sessioni nel Cestino" + (failed.Count > 0 ? $", {failed.Count} non cancellate" : ""));
        Reload();
        if (failed.Count > 0)
            MessageBox.Show(App, "Non cancellate:\n\n" + string.Join("\n", failed), "EdgeControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>What the server reconstructed with, from session.json.</summary>
    static string Describe(string dir)
    {
        var session = Path.Combine(dir, "session.json");
        if (!File.Exists(session)) return "solo CSV del visore";
        var json = Json.Parse(File.ReadAllText(session));
        var args = Json.Str(json, "server_args") ?? "";
        if (args.Contains("another PC")) return "server su un altro PC";
        var parts = Cmd.Split(args);
        string Option(string name)
        {
            int i = parts.IndexOf(name);
            return i >= 0 && i + 1 < parts.Count ? parts[i + 1] : null;
        }
        var backend = Option("--backend") ?? "mock";
        var text = backend == "model" ? Option("--model") ?? "modello"
                 : backend == "classical" ? "classico: " + (Option("--method") ?? "ball_pivoting")
                 : "finto, " + (Option("--mock-delay-s") ?? "0") + " s";
        var objects = Option("--objects");
        return objects != null ? $"{text}, solo {objects}" : text;
    }

    void ShowSelected()
    {
        var dir = Selected;
        btnFolder.Enabled = dir != null;
        var summary = dir == null ? null : Path.Combine(dir, "summary.md");
        btnSummary.Enabled = summary != null && File.Exists(summary);
        btnRegenerate.Enabled = dir != null && !App.IsRunning(ConsoleKind.Commands);
        preview.Clear();
        if (dir == null) return;
        if (!File.Exists(summary))
        {
            preview.Text = "Nessun summary.md.\n\n" +
                           string.Join("\n", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
            return;
        }
        // a light rendering of the markdown: headings in bold, the rest as it is (tables stay aligned)
        foreach (var line in File.ReadAllLines(summary))
        {
            var heading = line.StartsWith("#");
            preview.SelectionFont = heading ? Theme.MonoBold : Theme.Mono;
            preview.SelectionColor = Theme.Ink;
            preview.AppendText(line.Replace("**", "") + "\n");
        }
        preview.Select(0, 0);
        preview.ScrollToCaret();
    }

    void Regenerate()
    {
        var dir = Selected;
        if (dir == null) return;
        App.RunPython(ConsoleKind.Commands, "Riassunto della sessione", new[] { "-m", "benchmarks.quest_report", dir },
                      done => Reload(dir));
    }

    public override void StatusChanged()
    {
        btnRegenerate.Enabled = Selected != null && !App.IsRunning(ConsoleKind.Commands);
        btnPull.Enabled = App.Headset.State == "device" && !App.IsRunning(ConsoleKind.Commands);
        UpdateDelete();
    }
}
