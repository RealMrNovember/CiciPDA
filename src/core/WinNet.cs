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
    public string? HotspotSsid { get; set; }
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
        if (s.HotspotActive) await new HotspotService().RecoverAsync(s.HotspotAdapter, s.HotspotSsid);
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
    /// <summary>True only if the IPv6 address AND both IPv6 half-routes were really installed.</summary>
    public bool Ipv6Protected { get; private set; }
    public string RollbackReport { get; private set; } = "";

    /// <summary>
    /// tun2socks (cobra/pflag) takes GNU-style options: long names need TWO dashes. A single dash makes pflag read "-loglevel" as the
    /// cluster of short flags -l -o -g ... and abort ("unknown shorthand flag"), which is what broke the first real-device run.
    /// The device scheme is <c>tun://</c>: tun2socks v2 uses Wintun automatically on Windows and has no <c>wintun://</c> driver
    /// ("unsupported driver: wintun" on the second real-device run).
    /// </summary>
    public static string[] BuildTun2SocksArgs(int socksPort) =>
        new[] { "--device", $"tun://{AdapterName}", "--proxy", $"socks5://127.0.0.1:{socksPort}", "--loglevel", "info" };

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
        _t2s = new ManagedProcess(AppPaths.Tun2Socks, BuildTun2SocksArgs(socksPort), workDir: AppPaths.ToolsDir);
        var errs = new List<string>();
        _t2s.OutLine += l => Log.Debug("tun2socks", l);
        _t2s.ErrLine += l => { lock (errs) errs.Add(l); Log.Debug("tun2socks", l); };
        _t2s.Start();
        _journal.State.Tun2SocksPid = _t2s.Id; _journal.Save();

        NetworkInterface? ni = null;
        for (int i = 0; i < 60 && ni == null; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (_t2s.HasExited) { string e; lock (errs) e = string.Join(" | ", errs.TakeLast(3)); throw new InvalidOperationException("The packet engine (tun2socks) could not start: " + Tun2SocksLog.Summarize(e)); }
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
            if (r.Ok)
            {
                bool all = true;
                foreach (var p in NetshPlan.V6HalfRoutes) { var rr = await WinNet.Netsh(NetshPlan.AddIpv6Route(p, idx)); if (!rr.Ok) { all = false; Log.Warn("route", $"IPv6 route {p} failed: {rr.Combined}"); } }
                Ipv6Protected = all;
            }
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
        var removed = _bypass.ToList();
        foreach (var b in removed)
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
        if (removed.Count > 0 || IfIndex > 0)
        {
            var (ok, detail) = await RollbackCheck.VerifyAsync(removed);
            RollbackReport = detail;
            if (ok) Log.Ok("rollback", detail); else Log.Warn("rollback", detail);
        }
        IfIndex = -1; Ipv6Protected = false;
        _journal.State.Tun2SocksPid = 0; _journal.State.BypassRoutes.Clear(); _journal.Save();
    }

    public void Dispose() { try { StopAsync().Wait(8000); } catch { } }
}

public sealed record CheckResult(string Id, string Title, bool Passed, bool Required, string Detail, string? Fix = null, long Ms = 0, string Scope = "Internet");

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

    /// <summary>LOCAL readiness only: the adapter is up, has an IPv4 address and a sane MTU. This says nothing about the internet.</summary>
    public static Task<CheckResult> AdapterReady(int ifIndex) => Time("adapter", "Adapter ready (local)", true,
        "The network adapter is not up or has no IPv4 address. Disconnect and connect again.", () =>
    {
        var ni = WinNet.FindByIndex(ifIndex);
        if (ni == null) return Task.FromResult((false, "The adapter is not present."));
        var v4 = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        int mtu = 0; try { mtu = ni.GetIPProperties().GetIPv4Properties().Mtu; } catch { }
        bool ok = ni.OperationalStatus == OperationalStatus.Up && v4 != null;
        return Task.FromResult((ok, $"{ni.Name}: {ni.OperationalStatus}, IPv4 {(v4?.Address.ToString() ?? "none")}, MTU {mtu}. (Local readiness only; internet is checked below.)"));
    }).ContinueWith(t => t.Result with { Scope = "Local" });

    /// <summary>Real UDP: a DNS query sent over UDP to the configured server must be answered (carried through the phone for tunnel methods).</summary>
    public static Task<CheckResult> UdpDns(string server, int port = 53) => Time("udp", "UDP connectivity (DNS over UDP)", false,
        "UDP did not get an answer. Some networks block UDP; browsers fall back to TCP, but QUIC and UDP apps will not work.", async () =>
    {
        var id = (ushort)Random.Shared.Next(1, 65535);
        using var udp = new System.Net.Sockets.UdpClient();
        udp.Connect(server, port);
        var q = DnsQuery.Build("example.com", id);
        await udp.SendAsync(q);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var r = await udp.ReceiveAsync(cts.Token);
        bool ok = DnsQuery.IsResponseFor(id, r.Buffer);
        return (ok, ok ? $"{server}:{port} answered a UDP DNS query ({r.Buffer.Length} bytes)" : "A UDP reply arrived but was not a valid DNS answer");
    });

    /// <summary>Reports IPv6 accurately as two separate facts: is the leak protection applied, and does IPv6 actually work end to end.</summary>
    public static Task<CheckResult> Ipv6(bool settingOn, bool protectionApplied) => Time("ipv6", "IPv6 behaviour", false, null, async () =>
    {
        bool hasV6 = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6UniqueLocal));
        var rtt = await TcpLatency("2606:4700:4700::1111", 443, TimeSpan.FromSeconds(4));
        string prot = !settingOn ? "leak protection is OFF in Settings (IPv6 traffic, if your PC has IPv6, bypasses the tunnel)"
                    : protectionApplied ? "leak protection is ACTIVE (IPv6 routes point into the tunnel)"
                    : "leak protection could NOT be applied (see Activity log); IPv6 traffic may bypass the tunnel";
        string e2e = rtt != null ? "IPv6 reaches the internet" : "IPv6 internet is not available through this connection (applications fall back to IPv4)";
        return (true, $"{prot}; {e2e}" + (hasV6 ? "." : " (this PC has no global IPv6 address)."));
    });

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


/// <summary>Minimal DNS message helpers used by the UDP connectivity check.</summary>
public static class DnsQuery
{
    public static byte[] Build(string name, ushort id)
    {
        var m = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };   // RD set, one question
        foreach (var label in name.Split('.')) { m.Add((byte)label.Length); m.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        m.AddRange(new byte[] { 0, 0, 1, 0, 1 });                                                       // end of name, type A, class IN
        return m.ToArray();
    }

    public static bool IsResponseFor(ushort id, byte[] buf) => buf.Length >= 12 && buf[0] == (byte)(id >> 8) && buf[1] == (byte)id && (buf[2] & 0x80) != 0;
}

public static class RollbackCheck
{
    /// <summary>Parses `netsh interface ipv4 show route` output for a prefix on an interface index.</summary>
    public static bool RouteListed(string netshOutput, string prefix, int ifIndex)
    {
        foreach (var line in netshOutput.Split('\n'))
        {
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 5 && cols.Any(c => c == prefix) && cols.Any(c => c == ifIndex.ToString())) return true;
        }
        return false;
    }

    /// <summary>After teardown: the tunnel adapter must be gone (taking its address, DNS and routes with it) and every pinned route must be deleted.</summary>
    public static async Task<(bool ok, string detail)> VerifyAsync(IEnumerable<BypassRoute> removed)
    {
        var problems = new List<string>();
        if (WinNet.FindByName(TunnelNetwork.AdapterName) != null) problems.Add("the CiciPDA adapter still exists");
        var show = await WinNet.Netsh("interface", "ipv4", "show", "route");
        foreach (var b in removed)
            if (RouteListed(show.Stdout, b.Prefix, b.IfIndex))
            {
                await WinNet.Netsh(NetshPlan.DeleteBypass(b.Prefix, b.IfIndex));                         // one retry
                var again = await WinNet.Netsh("interface", "ipv4", "show", "route");
                if (RouteListed(again.Stdout, b.Prefix, b.IfIndex)) problems.Add($"route {b.Prefix} (interface {b.IfIndex}) is still present");
            }
        int best = WinNet.BestInterfaceFor(System.Net.IPAddress.Parse("1.1.1.1"));
        var ni = WinNet.FindByIndex(best);
        if (ni?.Name == TunnelNetwork.AdapterName) problems.Add("internet traffic would still be routed into the CiciPDA adapter");
        return problems.Count == 0 ? (true, "Rollback verified: adapter, DNS and routes are gone; traffic uses " + (ni?.Name ?? "the normal interface") + ".") : (false, "Rollback incomplete: " + string.Join("; ", problems) + ".");
    }
}


/// <summary>Turns tun2socks' JSON log lines into one readable sentence for the UI.</summary>
public static class Tun2SocksLog
{
    public static string Summarize(string joinedLines)
    {
        foreach (var raw in joinedLines.Split(" | ", StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var line = raw.Trim();
            string msg = line;
            if (line.StartsWith("{"))
                try { using var d = System.Text.Json.JsonDocument.Parse(line); if (d.RootElement.TryGetProperty("msg", out var m)) msg = m.GetString() ?? line; }
                catch (System.Text.Json.JsonException) { }
            msg = msg.Replace("[ENGINE] ", "");
            if (msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)) return "Windows denied access to the network driver. Run CiciPDA as administrator. (" + msg + ")";
            if (msg.Contains("unsupported driver", StringComparison.OrdinalIgnoreCase)) return "internal driver setting is wrong (" + msg + "). Please update CiciPDA.";
            if (msg.Length > 0) return msg.Length > 240 ? msg[..240] : msg;
        }
        return "no details were reported.";
    }
}
