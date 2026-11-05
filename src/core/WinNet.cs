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
            await WinNet.Netsh("interface", "ipv4", "delete", "route", $"prefix={r.Prefix}", $"interface={r.IfIndex}", "nexthop=0.0.0.0");
        if (s.ForwardPort > 0 && s.AdbSerial != null) await adb.RemoveForwardAsync(s.AdbSerial, s.ForwardPort);
        if (s.HotspotActive) await Hotspot.StopAsync();
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

        string idx = IfIndex.ToString();
        await Must("set address", "interface", "ipv4", "set", "address", $"name={idx}", "source=static", $"address={TunAddress}", "mask=255.255.255.0");
        await WinNet.Netsh("interface", "ipv4", "set", "interface", $"name={idx}", "metric=1");
        await WinNet.Netsh("interface", "ipv4", "set", "subinterface", $"interface={idx}", $"mtu={Math.Clamp(s.Mtu, 576, 1500)}", "store=active");
        Log.Ok("adapter", $"Adapter address {TunAddress}/24 set.");

        await Must("set dns", "interface", "ipv4", "set", "dnsservers", $"name={idx}", "source=static", $"address={s.DnsPrimary}", "validate=no");
        if (!string.IsNullOrWhiteSpace(s.DnsSecondary))
            await WinNet.Netsh("interface", "ipv4", "add", "dnsservers", $"name={idx}", $"address={s.DnsSecondary}", "index=2", "validate=no");
        Log.Ok("dns", $"DNS set to {s.DnsPrimary}{(string.IsNullOrWhiteSpace(s.DnsSecondary) ? "" : ", " + s.DnsSecondary)} (resolved through the phone).");

        if (bypassHost != null && origIf > 0)
        {
            var prefix = bypassHost + "/32";
            await Must("bypass route", "interface", "ipv4", "add", "route", $"prefix={prefix}", $"interface={origIf}", "nexthop=0.0.0.0", "metric=1", "store=active");
            var br = new BypassRoute(prefix, origIf);
            _bypass.Add(br); _journal.State.BypassRoutes.Add(br); _journal.Save();
            Log.Info("route", $"Transport host {bypassHost} pinned to its original path (prevents a routing loop).");
        }

        // Two /1 routes override the default route without deleting it, so cleanup is trivial and loss of the tunnel never strands the PC.
        foreach (var p in new[] { "0.0.0.0/1", "128.0.0.0/1" })
            await Must("add route " + p, "interface", "ipv4", "add", "route", $"prefix={p}", $"interface={idx}", "nexthop=0.0.0.0", "metric=1", "store=active");
        Log.Ok("route", "Default traffic now routed through the tunnel (0.0.0.0/1 + 128.0.0.0/1).");

        if (s.Ipv6LeakProtection)
        {
            // Send IPv6 into the tunnel too so it cannot bypass it; the phone side may not have IPv6, in which case apps fall back to IPv4.
            var r = await WinNet.Netsh("interface", "ipv6", "set", "address", $"interface={idx}", "address=fd66:6c1c:6f00::2/64", "store=active");
            if (r.Ok)
                foreach (var p in new[] { "::/1", "8000::/1" })
                    await WinNet.Netsh("interface", "ipv6", "add", "route", $"prefix={p}", $"interface={idx}", "nexthop=::", "metric=1", "store=active");
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
            var r = await WinNet.Netsh("interface", "ipv4", "delete", "route", $"prefix={b.Prefix}", $"interface={b.IfIndex}", "nexthop=0.0.0.0");
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
        "Another VPN or adapter has a lower metric. Disconnect other VPNs and retry.", () =>
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

public static class Hotspot
{
    public static bool Active { get; private set; }

    const string StartScript = @"
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
$null=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
$null=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
$asTask=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0]
function Await($op,$t){$m=$asTask.MakeGenericMethod($t);$x=$m.Invoke($null,@($op));$x.Wait(-1)|Out-Null;$x.Result}
$p=[Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if($null -eq $p){throw 'No internet connection profile is available to share.'}
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($p)
$cfg=New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
$cfg.Ssid=$env:CICI_SSID
$cfg.Passphrase=$env:CICI_PASS
$null=$m.ConfigureAccessPointAsync($cfg)
$r=Await ($m.StartTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
if($r.Status -ne 'Success'){throw ('Hotspot start failed: '+$r.Status+' '+$r.AdditionalErrorMessage)}
'OK'
";

    const string StopScript = @"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
$null=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
$asTask=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0]
$p=[Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if($null -ne $p){$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($p)
$op=$m.StopTetheringAsync();$t=[System.WindowsRuntimeSystemExtensions]::AsTask([Windows.Foundation.IAsyncOperation[Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]]$op);$t.Wait(-1)|Out-Null}
'OK'
";

    /// <summary>Starts Windows Mobile Hotspot with the active internet profile. Passphrase travels via environment, never the command line.</summary>
    public static async Task<(bool ok, string message)> StartAsync(string ssid, string passphrase, CancellationToken ct = default)
    {
        if (ssid.Length is < 1 or > 32) return (false, "SSID must be 1-32 characters.");
        if (passphrase.Length is < 8 or > 63) return (false, "Passphrase must be 8-63 characters.");
        Log.RegisterSecret(passphrase);
        var r = await ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", StartScript },
            TimeSpan.FromSeconds(40), ct, env: new Dictionary<string, string> { ["CICI_SSID"] = ssid, ["CICI_PASS"] = passphrase });
        Active = r.Ok && r.Stdout.Contains("OK");
        return (Active, Active ? "Hotspot started." : (r.Combined.Length > 0 ? r.Combined : "Hotspot start failed."));
    }

    public static async Task StopAsync()
    {
        var r = await ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", StopScript }, TimeSpan.FromSeconds(30));
        if (!r.Ok) Log.Warn("hotspot", "Stop reported: " + r.Combined);
        Active = false;
    }
}
