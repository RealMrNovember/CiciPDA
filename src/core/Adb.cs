using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CiciPDA;

public sealed record AdbDevice(string Serial, string State, string Model, string Product, string TransportId)
{
    public bool IsWifi => Serial.Contains(':') || Serial.Contains("_adb-tls");
    public bool IsEmulator => Serial.StartsWith("emulator-");
    public string TransportName => IsEmulator ? "Emulator" : IsWifi ? "Wi-Fi" : "USB";
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : Model.Replace('_', ' ');
    public bool IsAuthorized => State == "device";
    /// <summary>Phone IP when connected over ADB-over-Wi-Fi (ip:port serial); otherwise null.</summary>
    public IPAddress? WifiAddress
    {
        get
        {
            var i = Serial.LastIndexOf(':');
            return i > 0 && IPAddress.TryParse(Serial[..i], out var ip) ? ip : null;
        }
    }
}

public sealed record PhoneInfo(string Manufacturer, string Model, string AndroidVersion, string Sdk, string NetworkType, int? SignalLevel);

/// <summary>A running phone-side relay plus the adb port-forward that reaches it.</summary>
public sealed class AgentSession : IDisposable
{
    public string Serial { get; init; } = "";
    public int LocalPort { get; init; }
    public int RemotePort { get; init; }
    public byte[] Key { get; init; } = Array.Empty<byte>();
    public ManagedProcess? Process { get; init; }          // legacy app_process launcher only
    public string AdbPath { get; init; } = "";
    public bool IsCompanion { get; init; }
    public string PhoneStatusJson { get; set; } = "";
    public Action? OnStop { get; init; }                    // companion: stop the foreground service
    public bool IsAlive => Process == null || !Process.HasExited;

    public void Dispose()
    {
        // Closing stdin makes the agent exit; kill adb as well, then drop our forward.
        try { OnStop?.Invoke(); } catch { }
        Process?.Kill();
        try { Process?.Dispose(); } catch { }
        try { CryptographicOperations.ZeroMemory(Key); } catch { }
        _ = ProcessRunner.RunAsync(AdbPath, new[] { "-s", Serial, "forward", "--remove", $"tcp:{LocalPort}" }, TimeSpan.FromSeconds(5));
    }
}

public sealed class AdbService
{
    readonly Func<AppSettings> _settings;
    public AdbService(Func<AppSettings> settings) { _settings = settings; }

    public string? AdbPath { get; private set; }

    public string? Locate()
    {
        var cands = new List<string>();
        var cfg = _settings().AdbPath;
        if (!string.IsNullOrWhiteSpace(cfg)) cands.Add(cfg);
        cands.Add(Path.Combine(AppPaths.BaseDir, "adb", "adb.exe"));
        cands.Add(Path.Combine(AppPaths.DataDir, "adb", "adb.exe"));
        // development layout: walk up looking for data\adb\adb.exe
        var d = new DirectoryInfo(AppPaths.BaseDir);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent) cands.Add(Path.Combine(d.FullName, "third_party", "adb", "adb.exe"));
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk") })
            if (!string.IsNullOrEmpty(root)) cands.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { cands.Add(Path.Combine(dir.Trim(), "adb.exe")); } catch { }
        return AdbPath = cands.FirstOrDefault(File.Exists);
    }

    static readonly Regex DevLine = new(@"^(?<s>\S+)\s+(?<st>device|unauthorized|offline|authorizing|recovery|sideload|bootloader|no permissions[^\s]*(?:\s\[[^\]]*\])?)\s*(?<rest>.*)$", RegexOptions.Compiled);

    public static List<AdbDevice> ParseDevices(string output)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith("*")) continue;
            var m = DevLine.Match(line);
            if (!m.Success) continue;
            string Prop(string k) { var mm = Regex.Match(m.Groups["rest"].Value, $@"\b{k}:(\S+)"); return mm.Success ? mm.Groups[1].Value : ""; }
            list.Add(new AdbDevice(m.Groups["s"].Value, m.Groups["st"].Value.StartsWith("no permissions") ? "no permissions" : m.Groups["st"].Value,
                Prop("model"), Prop("product"), Prop("transport_id")));
        }
        return list;
    }

    public async Task<List<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var adb = AdbPath ?? Locate();
        if (adb == null) return new();
        var r = await ProcessRunner.RunAsync(adb, new[] { "devices", "-l" }, TimeSpan.FromSeconds(15), ct);
        if (!r.Ok) { Log.Warn("adb", "adb devices failed: " + r.Combined); return new(); }
        return ParseDevices(r.Stdout);
    }

    public async Task<ProcResult> RestartServerAsync(CancellationToken ct = default)
    {
        var adb = AdbPath ?? Locate() ?? throw new FileNotFoundException("adb.exe not found");
        await ProcessRunner.RunAsync(adb, new[] { "kill-server" }, TimeSpan.FromSeconds(10), ct);
        return await ProcessRunner.RunAsync(adb, new[] { "start-server" }, TimeSpan.FromSeconds(20), ct);
    }

    public async Task<PhoneInfo> GetPhoneInfoAsync(string serial, CancellationToken ct = default)
    {
        var adb = AdbPath ?? Locate() ?? throw new FileNotFoundException("adb.exe not found");
        async Task<string> Prop(string p) =>
            (await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "shell", "getprop", p }, TimeSpan.FromSeconds(8), ct)).Stdout.Trim();
        var man = await Prop("ro.product.manufacturer");
        var model = await Prop("ro.product.model");
        var ver = await Prop("ro.build.version.release");
        var sdk = await Prop("ro.build.version.sdk");
        var net = await Prop("gsm.network.type");
        return new PhoneInfo(man, model, ver, sdk, net, await GetSignalLevelAsync(serial, ct));
    }

    /// <summary>Best-effort mobile signal level (0-4) from the phone's telephony registry; null when not exposed.</summary>
    public async Task<int?> GetSignalLevelAsync(string serial, CancellationToken ct = default)
    {
        var adb = AdbPath ?? Locate();
        if (adb == null) return null;
        var r = await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "shell", "dumpsys", "telephony.registry" }, TimeSpan.FromSeconds(10), ct);
        if (!r.Ok) return null;
        return ParseSignalLevel(r.Stdout);
    }

    public static int? ParseSignalLevel(string dump)
    {
        var m = Regex.Match(dump, @"mSignalStrength=.*?\blevel=(\d)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(dump, @"\blevel=(\d)\b");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Pushes the helper, starts it on the phone (key sent over stdin, never argv), forwards a free local port to it.</summary>
    public async Task<AgentSession> StartAgentAsync(string serial, CancellationToken ct)
    {
        var adb = AdbPath ?? Locate() ?? throw new FileNotFoundException("adb.exe was not found. Set its path in Settings.");
        if (!File.Exists(AppPaths.AgentDex)) throw new FileNotFoundException("agent.dex is missing next to the application.");

        const string remoteDex = "/data/local/tmp/cicipda-agent.dex";
        var push = await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "push", AppPaths.AgentDex, remoteDex }, TimeSpan.FromSeconds(30), ct);
        if (!push.Ok) throw new InvalidOperationException("Could not copy the helper to the phone: " + push.Combined);

        var key = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(key).ToLowerInvariant();
        Log.RegisterSecret(hex);
        int remotePort = RandomNumberGenerator.GetInt32(20000, 60000);

        var proc = new ManagedProcess(adb, new[] { "-s", serial, "shell", $"CLASSPATH={remoteDex}", "app_process", "/system/bin", "app.cicibyte.cicipda.TunnelMain", remotePort.ToString() }, redirectStdin: true);
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errs = new List<string>();
        proc.OutLine += l =>
        {
            Log.Debug("agent", l);
            if (l.StartsWith("READY")) ready.TrySetResult(l);
            else if (l.StartsWith("ERR")) ready.TrySetException(new InvalidOperationException("Helper reported: " + l));
        };
        proc.ErrLine += l => { lock (errs) errs.Add(l); Log.Debug("agent", l); };
        proc.Start();
        try
        {
            await proc.WriteLineAsync(hex);
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(12));
            var exit = proc.WaitExitAsync(to.Token);
            var done = await Task.WhenAny(ready.Task, exit);
            if (done != ready.Task)
            {
                string e; lock (errs) e = string.Join(" ", errs.TakeLast(3));
                throw new InvalidOperationException("The phone helper exited or timed out. " + e);
            }
            await ready.Task;

            var fw = await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "forward", "tcp:0", $"tcp:{remotePort}" }, TimeSpan.FromSeconds(10), ct);
            if (!fw.Ok || !int.TryParse(fw.Stdout.Trim(), out var local))
                throw new InvalidOperationException("adb forward failed: " + fw.Combined);

            return new AgentSession { Serial = serial, LocalPort = local, RemotePort = remotePort, Key = key, Process = proc, AdbPath = adb };
        }
        catch { proc.Dispose(); CryptographicOperations.ZeroMemory(key); throw; }
    }

    public async Task RemoveForwardAsync(string serial, int localPort)
    {
        var adb = AdbPath ?? Locate();
        if (adb == null) return;
        await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "forward", "--remove", $"tcp:{localPort}" }, TimeSpan.FromSeconds(5));
    }
}
