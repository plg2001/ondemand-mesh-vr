// The project folder and the small helpers the pages share: settings, command lines, JSON / CSV, HTTP, network.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EdgeControl;

sealed class SceneObject
{
    public string Id = "", Shape = "", Label = "";
    public override string ToString() => Label.Length > 0 ? $"{Id}  ·  {Label}" : Id;
}

/// <summary>The ondemand-mesh-vr checkout this exe belongs to.</summary>
sealed class Repo
{
    public readonly string Root;
    Repo(string root) { Root = root; }

    /// <summary>The first folder, from start upwards, that holds edge/server.py and tools/quest/quest.py.</summary>
    public static Repo Find(string start)
    {
        try
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "edge", "server.py")) &&
                    File.Exists(Path.Combine(dir.FullName, "tools", "quest", "quest.py")))
                    return new Repo(dir.FullName.TrimEnd('\\', '/'));
        }
        catch (Exception)
        {
            // unreadable folder: not found
        }
        return null;
    }

    public string At(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
    public string VenvDir => At(".venv");
    public string VenvPython => At(".venv", "Scripts", "python.exe");
    public bool HasVenv => File.Exists(VenvPython);
    public string DefaultApk => At("unity", "OnDemandMeshVR", "Builds", "OnDemandMeshVR.apk");
    public string UnityProject => At("unity", "OnDemandMeshVR");
    public string QuestResults => At("benchmarks", "results", "quest");
    public string ServerLog => At("benchmarks", "results", "tmp", "edge_requests.csv");
    public const string SavedMeshes = "benchmarks/results/edge_meshes";  // relative, forward slashes: shlex-safe

    static readonly Dictionary<string, string> ShapeNames = new Dictionary<string, string>
    {
        ["box"] = "scatola", ["sphere"] = "sfera", ["bunny"] = "coniglio", ["tmg_cat"] = "gatto",
        ["fastmesh_001"] = "pupazzo", ["mav2_wand"] = "bacchetta",
    };

    /// <summary>The objects of the scene the app on the headset shows (its scene.json in StreamingAssets), else the
    /// YAML the scene is built from.</summary>
    public List<SceneObject> SceneObjects(string scene = "demo_room")
    {
        var found = new List<SceneObject>();
        try
        {
            var json = At("unity", "OnDemandMeshVR", "Assets", "StreamingAssets", "Scenes", scene, "scene.json");
            if (File.Exists(json))
            {
                var root = Json.Parse(File.ReadAllText(json));
                if (root != null && root.TryGetValue("objects", out var list) && list is object[] items)
                    foreach (var item in items.OfType<Dictionary<string, object>>())
                        found.Add(Make(Json.Str(item, "id"), Json.Str(item, "shape")));
            }
            var yaml = At("data", "scenes", scene + ".yaml");
            if (found.Count == 0 && File.Exists(yaml))
            {
                string id = null;
                foreach (var line in File.ReadAllLines(yaml))
                {
                    var m = Regex.Match(line, @"^\s*-\s*id:\s*(\S+)");
                    if (m.Success) id = m.Groups[1].Value;
                    m = Regex.Match(line, @"^\s*shape:\s*(\S+)");
                    if (m.Success && id != null)
                    {
                        found.Add(Make(id, m.Groups[1].Value));
                        id = null;
                    }
                }
            }
        }
        catch (Exception)
        {
            // a broken scene file: no list, the server still serves every object
        }
        return found.Where(o => o.Id.Length > 0).ToList();
    }

    static SceneObject Make(string id, string shape) =>
        new SceneObject { Id = id ?? "", Shape = shape ?? "", Label = ShapeNames.TryGetValue(shape ?? "", out var n) ? n : shape ?? "" };

    /// <summary>The Android package of the app, from the Unity project (same rule as quest.py).</summary>
    public string PackageName()
    {
        try
        {
            var text = File.ReadAllText(At("unity", "OnDemandMeshVR", "ProjectSettings", "ProjectSettings.asset"));
            var block = Regex.Match(text, @"^  applicationIdentifier:[ \t]*\r?\n((?:    .*\r?\n)*)", RegexOptions.Multiline);
            if (!block.Success) return null;
            var m = Regex.Match(block.Groups[1].Value, @"^    Android: (\S+)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Unity.exe of the version the project uses, installed by Unity Hub, or null.</summary>
    public string UnityEditor()
    {
        try
        {
            var text = File.ReadAllText(At("unity", "OnDemandMeshVR", "ProjectSettings", "ProjectVersion.txt"));
            var m = Regex.Match(text, @"m_EditorVersion:\s*(\S+)");
            if (!m.Success) return null;
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub",
                                   "Editor", m.Groups[1].Value, "Editor", "Unity.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A Python 3 to start tools/setup_pc.py with, the way setup_pc.bat does (the py launcher first).</summary>
    public static (string Exe, string[] Args) SystemPython()
    {
        var py = Shell.Which("py.exe");
        if (py != null) return (py, new[] { "-3" });
        var python = Shell.Which("python.exe");
        return (python ?? "python.exe", new string[0]);
    }
}

/// <summary>The choices of the panel, remembered between runs (%LOCALAPPDATA%\ondemand-mesh-vr\EdgeControl.ini).</summary>
sealed class Settings
{
    readonly string dir, path;
    readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Settings()
    {
        dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ondemand-mesh-vr");
        path = Path.Combine(dir, "EdgeControl.ini");
        try
        {
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1);
                }
        }
        catch (Exception)
        {
            // unreadable settings: defaults
        }
    }

    public string Get(string key, string fallback = "") => values.TryGetValue(key, out var v) ? v : fallback;
    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public decimal GetDecimal(string key, decimal fallback) =>
        decimal.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback) => Get(key) == "1" ? true : Get(key) == "0" ? false : fallback;
    public void Set(string key, string value) => values[key] = (value ?? "").Replace("\r", " ").Replace("\n", " ");
    public void Set(string key, bool value) => Set(key, value ? "1" : "0");
    public void Set(string key, decimal value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(path, values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // not saved: next run starts from the defaults
        }
    }
}

/// <summary>Command lines.</summary>
static class Cmd
{
    /// <summary>One argument quoted for a Windows command line (the C runtime rules, which Python follows).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            sb.Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        return sb.Append('"').ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    /// <summary>One argument quoted for shlex.split (POSIX rules): quest.py splits --server-args this way.</summary>
    public static string PosixQuote(string arg) =>
        Regex.IsMatch(arg, @"^[A-Za-z0-9_@%+=:,./-]+$") ? arg : "'" + arg.Replace("'", "'\"'\"'") + "'";

    /// <summary>Splits typed options the way a shell would (spaces, "double" and 'single' quotes).</summary>
    public static List<string> Split(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool any = false;
        char quote = '\0';
        foreach (char c in text ?? "")
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
                any = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    /// <summary>A number for a command line: always with a dot, whatever the Windows language.</summary>
    public static string Num(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

static class Json
{
    public static Dictionary<string, object> Parse(string text)
    {
        try
        {
            return new JavaScriptSerializer { MaxJsonLength = 1 << 24 }.DeserializeObject(text) as Dictionary<string, object>;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Str(Dictionary<string, object> d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    public static double? Num(Dictionary<string, object> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return null;
        try
        {
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

static class Csv
{
    /// <summary>Rows of a CSV text (RFC 4180 quoting), the first one being the header.</summary>
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (any || field.Length > 0) row.Add(field.ToString());
                    if (row.Count > 0) rows.Add(row.ToArray());
                    row.Clear();
                    field.Clear();
                    any = false;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }
        if (any || field.Length > 0) row.Add(field.ToString());
        if (row.Count > 0) rows.Add(row.ToArray());
        return rows;
    }
}

static class Http
{
    /// <summary>GET a small text resource; null if it does not answer in time or answers with an error.</summary>
    public static string Get(string url, int timeoutMs)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;  // a system proxy would only slow down requests to this PC and the LAN
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = false;
            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

static class Net
{
    /// <summary>This PC's IPv4 addresses on the local network, the one of the default route first: the same list,
    /// in the same order, as edge/netinfo.py (quest.py gives the first one to the headset).</summary>
    public static List<string> LanAddresses()
    {
        var found = new List<string>();
        try
        {
            // connect() on a UDP socket sends nothing: it only asks Windows which interface would be used
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            found.Add(((IPEndPoint)socket.LocalEndPoint).Address.ToString());
        }
        catch (Exception)
        {
            // no default route (offline)
        }
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                if (address.AddressFamily == AddressFamily.InterNetwork && !found.Contains(address.ToString()))
                    found.Add(address.ToString());
        }
        catch (Exception)
        {
            // no host name resolution: the default route address is enough
        }
        return found.Where(ip => !ip.StartsWith("127.") && !ip.StartsWith("169.254.") && !ip.StartsWith("0.")).ToList();
    }
}

static class Shell
{
    /// <summary>Opens a file, folder or URL with its default program (not as a child of this app).</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            System.Windows.Forms.MessageBox.Show($"Non riesco ad aprire:\n{target}\n\n{e.Message}", "EdgeControl");
        }
    }

    /// <summary>Explorer with the file selected.</summary>
    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Cmd.Quote(path)) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Open(Path.GetDirectoryName(path));
        }
    }

    /// <summary>Full path of an executable on PATH, or null.</summary>
    public static string Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // a malformed PATH entry
            }
        }
        return null;
    }

    /// <summary>A short command without a window: exit code (-1 if it could not run or timed out), stdout, stderr.</summary>
    public static (int Code, string Out, string Err) Run(string exe, string args, int timeoutMs, string workDir = null)
    {
        try
        {
            var info = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            if (workDir != null) info.WorkingDirectory = workDir;
            using var process = Process.Start(info);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch (Exception) { /* already gone */ }
                return (-1, "", "timeout");
            }
            System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { output, error }, 2000);
            return (process.ExitCode, output.IsCompleted ? output.Result : "", error.IsCompleted ? error.Result : "");
        }
        catch (Exception e)
        {
            return (-1, "", e.Message);
        }
    }
}
