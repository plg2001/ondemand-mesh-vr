// adb, found where tools/quest/quest.py finds it (the same adb.exe: two adb versions kill each other's server).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EdgeControl;

sealed class Adb
{
    public readonly string Exe;
    public Adb(string exe) { Exe = exe; }

    /// <summary>adb from PATH, an SDK named in the environment, Unity's own (newest editor first), the default SDK
    /// folder, Meta Quest Developer Hub: quest.py's order (find_adb / adb_candidates).</summary>
    public static string Find()
    {
        var onPath = Shell.Which("adb.exe");
        if (onPath != null) return onPath;
        var candidates = new List<string>();
        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var sdk = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(sdk)) candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        try
        {
            var hub = Path.Combine(programFiles, "Unity", "Hub", "Editor");
            if (Directory.Exists(hub))
                candidates.AddRange(Directory.GetDirectories(hub)
                    .Select(d => Path.Combine(d, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "SDK",
                                              "platform-tools", "adb.exe"))
                    .OrderByDescending(p => p, StringComparer.Ordinal));
        }
        catch (Exception)
        {
            // unreadable Unity folder
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(programFiles, "Meta Quest Developer Hub", "resources", "bin", "adb.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public (int Code, string Out, string Err) Run(string args, int timeoutMs = 10000) =>
        Shell.Run(Exe, args, timeoutMs);

    /// <summary>('device' | 'unauthorized' | 'offline' | 'none' | 'error', serial): quest.py's Adb.device().</summary>
    public (string State, string Serial) Device()
    {
        var (code, output, _) = Run("devices", 8000);
        if (code != 0) return ("error", null);
        var rows = output.Split('\n').Skip(1).Select(l => l.Trim())
                         .Where(l => l.Length > 0 && !l.StartsWith("*"))
                         .Select(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                         .Where(r => r.Length >= 2).ToList();
        var ready = rows.FirstOrDefault(r => r[1] == "device");
        if (ready != null) return ("device", ready[0]);
        return rows.Count > 0 ? (rows[0][1], rows[0][0]) : ("none", null);
    }

    /// <summary>Everything the panel shows about the headset, with one adb shell call (plus the USB tunnel).</summary>
    public HeadsetStatus Details(string serial, string package)
    {
        var st = new HeadsetStatus { State = "device", Serial = serial, HasDetails = true };
        var script = new List<string>
        {
            "echo @model", "getprop ro.product.model", "echo @release", "getprop ro.build.version.release",
            "echo @sdk", "getprop ro.build.version.sdk", "echo @battery", "dumpsys battery",
            "echo @wlan", "ip -f inet addr show wlan0",
        };
        if (!string.IsNullOrEmpty(package))
            script.AddRange(new[]
            {
                "echo @installed", $"pm list packages {package}", "echo @pid", $"pidof {package}",
                "echo @updated", $"dumpsys package {package} | grep -m1 lastUpdateTime",
                "echo @override", $"cat /sdcard/Android/data/{package}/files/edge_server.txt 2>/dev/null",
            });
        script.Add("echo @end");
        var (code, output, _) = Run($"-s {serial} shell {Cmd.Quote(string.Join("; ", script))}", 10000);
        if (code != 0) return st;
        var sections = new Dictionary<string, string>();
        string key = null;
        var value = new List<string>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("@"))
            {
                if (key != null) sections[key] = string.Join("\n", value).Trim();
                key = raw.Substring(1).Trim();
                value.Clear();
            }
            else value.Add(raw);
        }
        string Section(string name) => sections.TryGetValue(name, out var s) ? s : "";

        st.Model = Section("model");
        st.Android = Section("release");
        st.Api = Section("sdk");
        var battery = Section("battery");
        var level = Regex.Match(battery, @"^\s*level:\s*(\d+)", RegexOptions.Multiline);
        if (level.Success) st.BatteryLevel = int.Parse(level.Groups[1].Value, CultureInfo.InvariantCulture);
        var status = Regex.Match(battery, @"^\s*status:\s*(\d+)", RegexOptions.Multiline);
        st.Charging = status.Success && (status.Groups[1].Value == "2" || status.Groups[1].Value == "5");
        var wlan = Regex.Match(Section("wlan"), @"inet (\d+\.\d+\.\d+\.\d+)");
        st.WifiIp = wlan.Success ? wlan.Groups[1].Value : null;
        if (!string.IsNullOrEmpty(package))
        {
            st.Installed = Section("installed").Split('\n').Any(l => l.Trim() == "package:" + package);
            st.Running = Regex.IsMatch(Section("pid"), @"^\d+");
            var updated = Regex.Match(Section("updated"), @"lastUpdateTime=(\S+ \S+)");
            if (updated.Success && DateTime.TryParseExact(updated.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                st.Updated = when;
            var address = Section("override").Trim();
            st.Override = address.StartsWith("http") ? address : "";
        }
        var reverse = Run($"-s {serial} reverse --list", 5000);
        st.Reverse = reverse.Code == 0
            ? string.Join("; ", reverse.Out.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
                                   .Select(l => string.Join(" ", l.Split(' ').Skip(1))))
            : null;
        return st;
    }
}
