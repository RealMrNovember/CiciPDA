using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CiciPDA;

public static class WinNet
{
    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint dest, out uint ifIndex);

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int BestInterfaceFor(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return GetBestInterface(BitConverter.ToUInt32(b, 0), out var idx) == 0 ? (int)idx : -1;
    }

    public static NetworkInterface? FindByName(string name) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static NetworkInterface? FindByIndex(int idx)
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            try { if (n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties().Index == idx) return n; } catch { }
        return null;
    }

    public static int IndexOf(NetworkInterface n) { try { return n.GetIPProperties().GetIPv4Properties().Index; } catch { return -1; } }

    public static Task<ProcResult> Netsh(params string[] args) => ProcessRunner.RunAsync("netsh.exe", args, TimeSpan.FromSeconds(20));
}

/// <summary>Remembers every machine-level change so it can be undone after a crash or restart.</summary>
public sealed class JournalState
{
    public int Tun2SocksPid { get; set; }
    public string? AdbSerial { get; set; }
    public int ForwardPort { get; set; }
    public List<BypassRoute> BypassRoutes { get; set; } = new();
    public bool HotspotActive { get; set; }
    public string? HotspotAdapter { get; set; }
    public bool IsEmpty => Tun2SocksPid == 0 && ForwardPort == 0 && BypassRoutes.Count == 0 && !HotspotActive;
}
public sealed record BypassRoute(string Prefix, int IfIndex);

public sealed class RecoveryJournal
{
    readonly string _path = AppPaths.DataFile("journal.json");
    public JournalState State { get; private set; }
    public RecoveryJournal() { State = Json.LoadOrNew<JournalState>(_path); }
    public void Save() { try { if (State.IsEmpty) { if (File.Exists(_path)) File.Delete(_path); } else Json.Save(_path, State); } catch (Exception ex) { Log.Warn("journal", ex.Message); } }
    public void Reset() { State = new JournalState(); Save(); }

    /// <summary>Undo whatever a previous run left behind (orphan tun2socks, stray routes, adb forward, hotspot).</summary>
    public async Task RecoverAsync(AdbService adb)
    {
        var s = State;
        if (s.IsEmpty) return;
        Log.Warn("recovery", "Found leftovers from a previous session; cleaning up.");
        if (s.Tun2SocksPid > 0)
        {
            try { var p = Process.GetProcessById(s.Tun2SocksPid); if (p.ProcessName.StartsWith("tun2socks", StringComparison.OrdinalIgnoreCase)) { p.Kill(true); Log.Info("recovery", "Stopped orphan tun2socks."); } } catch { }
        }
        foreach (var r in s.BypassRoutes)
            await WinNet.Netsh(NetshPlan.DeleteBypass(r.Prefix, r.IfIndex));
        if (s.ForwardPort > 0 && s.AdbSerial != null) await adb.RemoveForwardAsync(s.AdbSerial, s.ForwardPort);
        if (s.HotspotActive) await new HotspotService().StopAsync();
        Reset();
    }
}

/// <summary>Owns the tun2socks process (which creates the Wintun adapter) and the Windows-side IP/route/DNS configuration.</summary>
public sealed class TunnelNetwork : IDisposable
{
    public const string AdapterName = "CiciPDA";
    public const string TunAddress = "10.66.0.2";
    readonly RecoveryJournal _journal;
    ManagedProcess? _t2s;
    readonly List<BypassRoute> _bypass = new();
    public int IfIndex { get; private set; } = -1;
    public NetworkInterface? Interface => WinNet.FindByName(AdapterName);
    public bool IsRunning => _t2s is { HasExited: false };
    public string? LastError { get; private set; }

    public TunnelNetwork(RecoveryJournal journal) { _journal = journal; }

    public async Task StartAsync(int socksPort, AppSettings s, IPAddress? bypassHost, CancellationToken ct, Action? onAdapter = null)
    {
        if (!WinNet.IsElevated()) throw new InvalidOperationException("Administrator rights are required to create the virtual adapter.");
        if (!File.Exists(AppPaths.Tun2Socks)) throw new FileNotFoundException("tun2socks.exe is missing from the tools folder. Run fetch-tools.ps1.");
        if (!File.Exists(AppPaths.WintunDll)) throw new FileNotFoundException("wintun.dll is missing from the tools folder. Run fetch-tools.ps1.");
        if (WinNet.FindByName(AdapterName) != null)
            throw new InvalidOperationException($"A network adapter named {AdapterName} already exists (another instance or a stale adapter). Close it and retry.");

        // Where would traffic to the phone go today? Capture before we change routes so the transport never loops through the tunnel.
        int origIf = bypassHost != null ? WinNet.BestInterfaceFor(bypassHost) : -1;

        Log.Info("tunnel", "Starting tun2socks (Wintun adapter + packet engine).");
        _t2s = new ManagedProcess(AppPaths.Tun2Socks, new[] { "-device", $"wintun://{AdapterName}", "-proxy", $"socks5://127.0.0.1:{socksPort}", "-loglevel", "info" }, workDir: AppPaths.ToolsDir);
        var errs = new List<string>();
        _t2s.OutLine += l => Log.Debug("tun2socks", l);
        _t2s.ErrLine += l => { lock (errs) errs.Add(l); Log.Debug("tun2socks", l); };
        _t2s.Start();
        _journal.State.Tun2SocksPid = _t2s.Id; _journal.Save();

        NetworkInterface? ni = null;
        for (int i = 0; i < 60 && ni == null; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (_t2s.HasExited) { string e; lock (errs) e = string.Join(" | ", errs.TakeLast(3)); throw new InvalidOperationException("tun2socks exited immediately: " + e); }
            ni = WinNet.FindByName(AdapterName);
            if (ni == null) await Task.Delay(250, ct);
        }
        if (ni == null) throw new TimeoutException("The Wintun adapter did not appear within 15 seconds.");
        IfIndex = WinNet.IndexOf(ni);
        Log.Ok("adapter", $"Virtual adapter {AdapterName} created (interface {IfIndex}).");
        onAdapter?.Invoke();

        int idx = IfIndex;
        string? bypassPrefix = bypassHost != null && origIf > 0 ? bypassHost + "/32" : null;
        foreach (var (what, args, required) in NetshPlan.Apply(s, idx, TunAddress, bypassPrefix, origIf))
        {
            if (required) await Must(what, args); else { var r0 = await WinNet.Netsh(args); if (!r0.Ok) Log.Warn("network", $"{what} failed: {r0.Combined}"); }
            if (what == "bypass route")
            {
                var br = new BypassRoute(bypassPrefix!, origIf);
                _bypass.Add(br); _journal.State.BypassRoutes.Add(br); _journal.Save();      // journal immediately: recovery must know about it
                Log.Info("route", $"Transport host {bypassHost} pinned to its original path (prevents a routing loop).");
            }
        }
        Log.Ok("adapter", $"Adapter address {TunAddress}/24, MTU {NetshPlan.ClampMtu(s.Mtu)} set.");
        Log.Ok("dns", $"DNS set to {s.DnsPrimary}{(string.IsNullOrWhiteSpace(s.DnsSecondary) ? "" : ", " + s.DnsSecondary)} (resolved through the phone).");
        Log.Ok("route", "Default traffic now routed through the tunnel (0.0.0.0/1 + 128.0.0.0/1).");

        if (s.Ipv6LeakProtection)
        {
            // Send IPv6 into the tunnel too so it cannot bypass it; the phone side may not have IPv6, in which case apps fall back to IPv4.
            var r = await WinNet.Netsh(NetshPlan.SetIpv6Address(idx));
            if (r.Ok) foreach (var p in NetshPlan.V6HalfRoutes) await WinNet.Netsh(NetshPlan.AddIpv6Route(p, idx));
            else Log.Warn("route", "IPv6 leak protection could not be applied: " + r.Combined);
        }
    }

    async Task Must(string what, params string[] args)
    {
        var r = await WinNet.Netsh(args);
        if (!r.Ok) { LastError = $"{what} failed: {r.Combined}"; throw new InvalidOperationException(LastError); }
    }

    public async Task StopAsync()
    {
        foreach (var b in _bypass.ToList())
        {
            var r = await WinNet.Netsh(NetshPlan.DeleteBypass(b.Prefix, b.IfIndex));
            if (!r.Ok) Log.Warn("route", $"Could not remove route {b.Prefix}: {r.Combined}");
        }
        _bypass.Clear();
        if (_t2s != null)
        {
            _t2s.Kill();   // removes the Wintun adapter, which removes its addresses, DNS and routes with it
            for (int i = 0; i < 20 && WinNet.FindByName(AdapterName) != null; i++) await Task.Delay(250);
            _t2s.Dispose(); _t2s = null;
            if (WinNet.FindByName(AdapterName) != null) Log.Warn("adapter", "Adapter is still present after stopping tun2socks.");
            else Log.Info("adapter", "Virtual adapter removed.");
        }
        IfIndex = -1;
        _journal.State.Tun2SocksPid = 0; _journal.State.BypassRoutes.Clear(); _journal.Save();
    }

    public void Dispose() { try { StopAsync().Wait(8000); } catch { } }
}

public sealed record CheckResult(string Id, string Title, bool Passed, bool Required, string Detail, string? Fix = null, long Ms = 0);

/// <summary>Real data-path verification. Nothing here is simulated or registry-forced.</summary>
public static class Verifier
{
    static HttpClient NewHttp(int timeoutSec = 8) => new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(timeoutSec) }) { Timeout = TimeSpan.FromSeconds(timeoutSec + 2) };

    static async Task<CheckResult> Time(string id, string title, bool required, string? fix, Func<Task<(bool ok, string detail)>> f)
    {
        var sw = Stopwatch.StartNew();
        try { var (ok, d) = await f(); return new CheckResult(id, title, ok, required, d, ok ? null : fix, sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new CheckResult(id, title, false, required, ex.Message, fix, sw.ElapsedMilliseconds); }
    }

    public static Task<CheckResult> DefaultRoute(int expectedIfIndex) => Time("route", "Default route", true,
        "Another adapter or VPN has priority for internet traffic. Disconnect that VPN, or for hotspot modes unplug Ethernet / disable other Wi-Fi adapters, then retry.", () =>
    {
        int best = WinNet.BestInterfaceFor(IPAddress.Parse("1.1.1.1"));
        var ni = WinNet.FindByIndex(best);
        return Task.FromResult((best == expectedIfIndex, $"Best route to the internet uses interface {best} ({ni?.Name ?? "unknown"}); expected {expectedIfIndex}."));
    });

    public static Task<CheckResult> Dns(string host = "www.msftconnecttest.com") => Time("dns", "DNS resolution", true,
        "DNS lookups through the phone failed. Check the phone's connection and the DNS servers in Settings.", async () =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ips = await System.Net.Dns.GetHostAddressesAsync(host, cts.Token);
        return (ips.Length > 0, $"{host} → {string.Join(", ", ips.Take(2))}");
    });

    public static Task<CheckResult> Http() => Time("http", "HTTP reachability", true,
        "Plain HTTP did not return the expected content. The phone may have no data connection or a captive portal.", async () =>
    {
        using var h = NewHttp();
        var t = (await h.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt")).Trim();
        return (t == "Microsoft Connect Test", t == "Microsoft Connect Test" ? "connecttest.txt matched" : "Unexpected response (captive portal or proxy?)");
    });

    public static Task<CheckResult> Https() => Time("https", "HTTPS reachability", true,
        "HTTPS failed. Check that the phone has working mobile data or Wi-Fi.", async () =>
    {
        using var h = NewHttp();
        using var r = await h.GetAsync("https://www.cloudflare.com/cdn-cgi/trace");
        return (r.IsSuccessStatusCode, $"HTTP {(int)r.StatusCode}");
    });

    /// <summary>What Windows itself thinks (NCSI). Informational: it can lag a few seconds and is never forced.</summary>
    public static Task<CheckResult> WindowsConnectivity(int ifIndex) => Time("ncsi", "Windows connectivity status", false,
        "Windows has not marked the network as Internet yet; this can take a few seconds.", async () =>
    {
        var r = await ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"(Get-NetConnectionProfile -InterfaceIndex {ifIndex} -ErrorAction SilentlyContinue).IPv4Connectivity" }, TimeSpan.FromSeconds(12));
        var v = r.Stdout.Trim();
        return (v.Equals("Internet", StringComparison.OrdinalIgnoreCase), string.IsNullOrEmpty(v) ? "No network profile reported yet" : $"IPv4Connectivity = {v}");
    });

    public static async Task<TimeSpan?> TcpLatency(string host, int port, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await c.ConnectAsync(host, port, cts.Token);
            return sw.Elapsed;
        }
        catch { return null; }
    }
}
