// Background polling of what the panel shows at the top: the edge server (/health and the requests it logged),
// the headset (adb) and this PC (network addresses, GPU). Results are handed to the UI thread.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace EdgeControl;

sealed class RequestRow
{
    public double Epoch;
    public string Id = "", Time = "", Object = "", Status = "", Model = "", Client = "", Triangles = "", ServerMs = "",
                  FScore = "", Detail = "";
}

sealed class ServerStatus
{
    public bool Up;
    public Dictionary<string, object> Health;
    public bool? Lan;            // answers on this PC's LAN address too (started for the network, not only this PC)
    public bool Restarted;       // a different server than at the previous poll: drop its old rows
    public List<RequestRow> NewRows = new List<RequestRow>();

    public string Backend => Json.Str(Health, "backend") ?? "";
    public string Model => Json.Str(Health, "model") ?? "";
    public int Served => (int)(Json.Num(Health, "requests_served") ?? 0);
    public double Uptime => Json.Num(Health, "uptime_s") ?? 0;
    public double TypicalS => Json.Num(Health, "typical_s") ?? 0;
    public double BudgetS => Json.Num(Health, "budget_s") ?? 0;

    /// <summary>What reconstructs the meshes, in words.</summary>
    public string Describe()
    {
        if (!Up) return "";
        switch (Backend)
        {
            case "mock":
                var delay = Json.Num(Health, "simulated_inference_s") ?? 0;
                return $"Finto (involucro convesso), {delay:0.#} s";
            case "classical":
                return (Json.Str(Health, "label") ?? Model).Replace(" (classical)", "") + " (classico)";
            default:
                var device = Json.Str(Health, "device_name");
                return (Json.Str(Health, "label") ?? Model) + (string.IsNullOrEmpty(device) ? "" : " su " + device);
        }
    }

    /// <summary>The objects it serves ("all" or a list).</summary>
    public string Objects
    {
        get
        {
            if (Health == null || !Health.TryGetValue("objects", out var o)) return "";
            return o is object[] list ? string.Join(", ", list) : "tutti";
        }
    }
}

sealed class HeadsetStatus
{
    public string State = "starting";  // starting | noadb | none | unauthorized | offline | device | error
    public string Serial, Model, Android, Api, WifiIp, Override, Reverse;
    public int? BatteryLevel;
    public bool Charging;
    public bool? Installed, Running;
    public DateTime? Updated;
    public bool HasDetails;
    public string AdbPath;
}

sealed class PcStatus
{
    public List<string> Addresses = new List<string>();
    public string GpuName;
    public int GpuUsedMb, GpuTotalMb, GpuUtil;
    public bool HasGpu;
}

sealed class Monitors : IDisposable
{
    readonly Control ui;
    readonly Repo repo;
    readonly string package;
    readonly AutoResetEvent wakeServer = new AutoResetEvent(false), wakeAdb = new AutoResetEvent(false);
    volatile bool stopping, detailsWanted = true;
    volatile string baseUrl = "http://127.0.0.1:8000";
    Adb adb;

    public event Action<ServerStatus> Server;
    public event Action<HeadsetStatus> Headset;
    public event Action<PcStatus> Pc;

    public Monitors(Control ui, Repo repo)
    {
        this.ui = ui;
        this.repo = repo;
        package = repo.PackageName();
    }

    /// <summary>The server to watch: this PC's port, or the server on another PC during a session that uses it.</summary>
    public string BaseUrl
    {
        get => baseUrl;
        set
        {
            var url = (value ?? "").TrimEnd('/');
            if (url != baseUrl)
            {
                baseUrl = url;
                wakeServer.Set();
            }
        }
    }

    public Adb Adb => adb;
    public string Package => package;

    public void Start()
    {
        foreach (ThreadStart loop in new ThreadStart[] { ServerLoop, AdbLoop, PcLoop })
            new Thread(loop) { IsBackground = true, Name = loop.Method.Name }.Start();
    }

    /// <summary>Poll the server now (after starting or stopping something).</summary>
    public void KickServer() => wakeServer.Set();

    /// <summary>Read everything about the headset now (after an action on it).</summary>
    public void KickHeadset()
    {
        detailsWanted = true;
        wakeAdb.Set();
    }

    public void Dispose()
    {
        stopping = true;
        wakeServer.Set();
        wakeAdb.Set();
    }

    void Post(Action action)
    {
        if (stopping || ui.IsDisposed || !ui.IsHandleCreated) return;
        try
        {
            ui.BeginInvoke(action);
        }
        catch (Exception)
        {
            // the window is closing
        }
    }

    // ------------------------------------------------------------------ edge server
    void ServerLoop()
    {
        double lastStart = -1, maxEpoch = 0;
        var seen = new HashSet<string>();
        DateTime lanCheckedAt = DateTime.MinValue;
        bool? lan = null;
        while (!stopping)
        {
            var url = baseUrl;
            var st = new ServerStatus();
            var health = Json.Parse(Http.Get(url + "/health", 1500) ?? "");
            if (health != null && Json.Str(health, "status") == "ok")
            {
                st.Up = true;
                st.Health = health;
                double start = (Json.Num(health, "server_time") ?? 0) - st.Uptime;
                if (lastStart < 0 || Math.Abs(start - lastStart) > 3)
                {
                    st.Restarted = true;
                    lastStart = start;
                    maxEpoch = start - 1;
                    seen.Clear();
                    lan = null;
                    lanCheckedAt = DateTime.MinValue;
                }
                // rows are logged when a request ends but stamped when it arrived: look a few minutes back, skip
                // the ones already seen
                var since = (maxEpoch - 600).ToString("0.000", CultureInfo.InvariantCulture);
                var csv = Http.Get($"{url}/requests?since={since}", 3000);
                if (csv != null) st.NewRows = ParseRows(csv, seen, ref maxEpoch);
                if ((DateTime.Now - lanCheckedAt).TotalSeconds > 15)
                {
                    lanCheckedAt = DateTime.Now;
                    var local = url.StartsWith("http://127.0.0.1:") ? url.Substring("http://127.0.0.1:".Length) : null;
                    var ip = Net.LanAddresses().FirstOrDefault();
                    lan = local == null ? true : ip == null ? (bool?)null : Http.Get($"http://{ip}:{local}/health", 1500) != null;
                }
                st.Lan = lan;
            }
            else lastStart = -1;
            Post(() => Server?.Invoke(st));
            wakeServer.WaitOne(st.Up ? 2000 : 1500);
        }
    }

    static List<RequestRow> ParseRows(string csv, HashSet<string> seen, ref double maxEpoch)
    {
        var rows = new List<RequestRow>();
        var table = Csv.Parse(csv);
        if (table.Count < 2) return rows;
        var header = table[0].Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        string Get(string[] r, string name) => header.TryGetValue(name, out var i) && i < r.Length ? r[i] : "";
        foreach (var r in table.Skip(1))
        {
            var id = Get(r, "request_id");
            if (id.Length == 0 || !seen.Add(id)) continue;
            double.TryParse(Get(r, "t_epoch"), NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch);
            maxEpoch = Math.Max(maxEpoch, epoch);
            var stamp = Get(r, "timestamp");
            rows.Add(new RequestRow
            {
                Epoch = epoch, Id = id, Time = stamp.Length >= 19 ? stamp.Substring(11, 8) : stamp,
                Object = Get(r, "object_id"), Status = Get(r, "status"), Model = Get(r, "model"),
                Client = Get(r, "client"), Triangles = Get(r, "triangles"), ServerMs = Get(r, "server_ms"),
                FScore = Get(r, "fscore"), Detail = Get(r, "detail"),
            });
        }
        return rows;
    }

    // ------------------------------------------------------------------ headset
    void AdbLoop()
    {
        DateTime detailsAt = DateTime.MinValue, searchedAt = DateTime.MinValue;
        HeadsetStatus details = null;
        while (!stopping)
        {
            var st = new HeadsetStatus();
            if (adb == null && (DateTime.Now - searchedAt).TotalSeconds > 30)
            {
                searchedAt = DateTime.Now;
                var exe = Adb.Find();
                if (exe != null) adb = new Adb(exe);
            }
            if (adb == null) st.State = "noadb";
            else
            {
                // the first call also starts the adb server, as a child of this app and outside the tasks' jobs:
                // stopping a task never kills it (and with it the USB tunnel)
                var (state, serial) = adb.Device();
                st.State = state;
                st.Serial = serial;
                if (state == "device")
                {
                    bool stale = details == null || details.Serial != serial || detailsWanted ||
                                 (DateTime.Now - detailsAt).TotalSeconds > 10;
                    if (stale)
                    {
                        detailsWanted = false;
                        details = adb.Details(serial, package);
                        detailsAt = DateTime.Now;
                    }
                    st = details;
                }
                else details = null;
            }
            st.AdbPath = adb?.Exe;
            var snapshot = st;
            Post(() => Headset?.Invoke(snapshot));
            wakeAdb.WaitOne(2500);
        }
    }

    // ------------------------------------------------------------------ this PC
    void PcLoop()
    {
        var smi = Shell.Which("nvidia-smi.exe");
        int failures = 0;
        while (!stopping)
        {
            var st = new PcStatus { Addresses = Net.LanAddresses() };
            if (smi != null && failures < 3)
            {
                var (code, output, _) = Shell.Run(smi, "--query-gpu=name,memory.used,memory.total,utilization.gpu " +
                                                       "--format=csv,noheader,nounits", 5000);
                var parts = code == 0 ? output.Split('\n')[0].Split(',').Select(s => s.Trim()).ToArray() : new string[0];
                if (parts.Length >= 4)
                {
                    failures = 0;
                    st.HasGpu = true;
                    st.GpuName = parts[0].Replace("NVIDIA ", "");
                    int.TryParse(parts[1], out st.GpuUsedMb);
                    int.TryParse(parts[2], out st.GpuTotalMb);
                    int.TryParse(parts[3], out st.GpuUtil);
                }
                else failures++;
            }
            Post(() => Pc?.Invoke(st));
            Thread.Sleep(3000);
        }
    }
}
