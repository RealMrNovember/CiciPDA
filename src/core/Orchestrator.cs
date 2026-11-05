using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace CiciPDA;

public enum ConnState { Offline, DetectingDevice, DeviceConnected, EstablishingTunnel, Connecting, Connected, Reconnecting, Disconnecting, Error, Limited }
public enum PathState { Down, Starting, Up, Failed }
public enum NetState { Unknown, Reachable, Limited, Unreachable }
public enum StageState { Pending, Active, Done, Warn, Failed, Skipped }

public sealed class StageInfo
{
    public string Id { get; init; } = ""; public string Title { get; init; } = "";
    public StageState State { get; set; } = StageState.Pending; public string Detail { get; set; } = "";
}

/// <summary>The independent facts the UI must never merge into one "Connected" light.</summary>
public sealed record Indicators(bool DeviceConnected, bool CompanionInstalled, bool CompanionRunning, bool TunnelConnected, bool NetworkConfigured, bool InternetVerified);

public sealed class TrafficSnapshot
{
    public double DownBps, UpBps;
    public long TotalDown, TotalUp;
    public double? LatencyMs;
    public double? LossPct;
    public int? SignalLevel;
    public string? NetworkType;
    public TimeSpan Uptime;
    public double[] DownHistory = Array.Empty<double>();
    public double[] UpHistory = Array.Empty<double>();
    public string Interface = "";
    public long Connections;
    public long UdpDropped;
    public static TrafficSnapshot Empty => new();
}

/// <summary>Samples real interface counters once per second and probes latency/loss on a slower cadence.</summary>
public sealed class TrafficMonitor : IDisposable
{
    const int HistLen = 120;
    readonly Func<NetworkInterface?> _iface;
    readonly Func<CancellationToken, Task<TimeSpan?>> _probe;
    readonly Func<CancellationToken, Task<(int? level, string? type)>>? _signal;
    readonly CancellationTokenSource _cts = new();
    readonly Queue<double> _down = new(), _up = new();
    readonly Queue<bool> _probes = new();
    long _baseRx = -1, _baseTx = -1, _lastRx, _lastTx;
    double? _latency; int? _sig; string? _netType;
    DateTime _start = DateTime.UtcNow, _lastSample;
    public TrafficSnapshot Latest { get; private set; } = TrafficSnapshot.Empty;
    public event Action<TrafficSnapshot>? Updated;
    public Func<(long conns, long udpDropped)>? Extra { get; set; }

    public TrafficMonitor(Func<NetworkInterface?> iface, Func<CancellationToken, Task<TimeSpan?>> probe, Func<CancellationToken, Task<(int? level, string? type)>>? signal = null)
    { _iface = iface; _probe = probe; _signal = signal; }

    public void Start()
    {
        _start = DateTime.UtcNow;
        _ = Task.Run(SampleLoop);
        _ = Task.Run(ProbeLoop);
        if (_signal != null) _ = Task.Run(SignalLoop);
    }

    async Task SampleLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ni = _iface();
                if (ni != null)
                {
                    var st = ni.GetIPv4Statistics();
                    long rx = st.BytesReceived, tx = st.BytesSent; var now = DateTime.UtcNow;
                    double dt = _lastSample == default ? 1 : Math.Max(0.2, (now - _lastSample).TotalSeconds);
                    if (_baseRx < 0) { _baseRx = rx; _baseTx = tx; _lastRx = rx; _lastTx = tx; }
                    double dn = Math.Max(0, rx - _lastRx) / dt, up = Math.Max(0, tx - _lastTx) / dt;
                    _lastRx = rx; _lastTx = tx; _lastSample = now;
                    Push(_down, dn); Push(_up, up);
                    double? loss;
                    lock (_probes) loss = _probes.Count >= 3 ? 100.0 * _probes.Count(x => !x) / _probes.Count : null;
                    var ex = Extra?.Invoke() ?? (0, 0);
                    Latest = new TrafficSnapshot
                    {
                        DownBps = dn, UpBps = up, TotalDown = rx - _baseRx, TotalUp = tx - _baseTx,
                        LatencyMs = _latency, LossPct = loss, SignalLevel = _sig, NetworkType = _netType,
                        Uptime = DateTime.UtcNow - _start, DownHistory = _down.ToArray(), UpHistory = _up.ToArray(),
                        Interface = ni.Name, Connections = ex.Item1, UdpDropped = ex.Item2,
                    };
                    Updated?.Invoke(Latest);
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException) { }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
        }
    }

    static void Push(Queue<double> q, double v) { q.Enqueue(v); while (q.Count > HistLen) q.Dequeue(); }

    async Task ProbeLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var t = await _probe(ct);
                lock (_probes) { _probes.Enqueue(t != null); while (_probes.Count > 20) _probes.Dequeue(); }
                if (t != null) _latency = t.Value.TotalMilliseconds;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug("monitor", "probe: " + ex.Message); }
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { }
        }
    }

    async Task SignalLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { var (l, t) = await _signal!(ct); _sig = l; _netType = t; } catch { }
            try { await Task.Delay(30000, ct); } catch (OperationCanceledException) { }
        }
    }

    public int RecentProbeFailures { get { lock (_probes) return _probes.Reverse().TakeWhile(x => !x).Count(); } }
    public void Dispose() { _cts.Cancel(); }
}

/// <summary>
/// Connection lifecycle. The state only becomes Connected after the data path has been verified:
/// agent reachable -> adapter up -> default route uses the tunnel -> DNS resolves -> HTTP and HTTPS succeed.
/// </summary>
public sealed class Orchestrator
{
    readonly SettingsStore _settings;
    readonly AdbService _adb;
    readonly DeviceStore _devices;
    readonly HistoryStore _history;
    readonly RecoveryJournal _journal;
    readonly CompanionManager _comp;
    readonly SemaphoreSlim _gate = new(1, 1);
    CancellationTokenSource? _opCts, _healthCts;
    AgentSession? _agent;
    SocksRelay? _socks;
    TunnelNetwork? _net;
    TrafficMonitor? _monitor;
    SessionRecord? _session;
    Func<NetworkInterface?>? _nativeIface;
    bool _userDisconnect;

    public ConnState State { get; private set; } = ConnState.Offline;
    public string Detail { get; private set; } = "";
    public ConnectionMethod Method { get; private set; }
    public AdbDevice? Device { get; private set; }
    public string DeviceName { get; private set; } = "";
    public DateTimeOffset? ConnectedAt { get; private set; }
    public PathState Vpn { get; private set; } = PathState.Down;
    public NetState Internet { get; private set; } = NetState.Unknown;
    public string InterfaceName { get; private set; } = "";
    public string? LastError { get; private set; }
    public bool NeedsElevation { get; private set; }
    public IReadOnlyList<AdbDevice> Devices { get; private set; } = Array.Empty<AdbDevice>();
    public IReadOnlyList<CheckResult> LastChecks { get; private set; } = Array.Empty<CheckResult>();
    public TrafficSnapshot Traffic => _monitor?.Latest ?? TrafficSnapshot.Empty;
    public long SessionBytes => Traffic.TotalDown + Traffic.TotalUp;
    public bool IsBusy => State is ConnState.EstablishingTunnel or ConnState.Connecting or ConnState.Reconnecting or ConnState.Disconnecting or ConnState.DetectingDevice;
    public bool IsActive => State is ConnState.Connected or ConnState.Limited;
    public bool AdbMissing => _adb.AdbPath == null;

    public event Action? Changed;
    public event Action<TrafficSnapshot>? TrafficUpdated;

    public Orchestrator(SettingsStore s, AdbService adb, DeviceStore d, HistoryStore h, RecoveryJournal j, CompanionManager comp)
    { _settings = s; _adb = adb; _devices = d; _history = h; _journal = j; _comp = comp; _adb.Locate(); ResetStages(); }

    // ---------- pipeline stages ----------
    public IReadOnlyList<StageInfo> Stages { get; private set; } = Array.Empty<StageInfo>();
    public CompanionInspection? CompanionInfo { get; private set; }
    public string PhoneNetwork { get; private set; } = "";
    public bool CompanionInstalled { get; private set; }
    public bool CompanionRunning => _agent is { IsAlive: true } && Stages.FirstOrDefault(x => x.Id == "service")?.State == StageState.Done;
    public Indicators Indicators => new(Device != null && Device.IsAuthorized || (Method is ConnectionMethod.AndroidHotspot or ConnectionMethod.IphoneHotspot && IsActive),
        CompanionInstalled, CompanionRunning, Vpn == PathState.Up, Stages.FirstOrDefault(x => x.Id == "network")?.State == StageState.Done, Internet == NetState.Reachable);

    void ResetStages()
    {
        Stages = new List<StageInfo>
        {
            new() { Id = "detect", Title = "Detect phone" }, new() { Id = "authorize", Title = "Check USB debugging authorization" },
            new() { Id = "companion", Title = "Check Android companion" }, new() { Id = "install", Title = "Install / update companion" },
            new() { Id = "service", Title = "Start companion service" }, new() { Id = "tunnel", Title = "Establish secure tunnel" },
            new() { Id = "adapter", Title = "Create virtual adapter" }, new() { Id = "network", Title = "Configure routes and DNS" },
            new() { Id = "internet", Title = "Verify internet access" },
        };
        CompanionInfo = null; CompanionInstalled = false; PhoneNetwork = "";
    }

    void Step(string id, StageState st, string detail = "")
    {
        var x = Stages.First(y => y.Id == id); x.State = st; x.Detail = detail;
        if (st == StageState.Failed) Log.Error("stage", $"{x.Title}: {detail}"); else if (st != StageState.Active) Log.Info("stage", $"{x.Title}: {st}{(detail.Length > 0 ? " — " + detail : "")}");
        Changed?.Invoke();
    }

    AppSettings Cfg => _settings.Current;

    void Set(ConnState st, string detail)
    {
        if (st != State) Log.Info("state", $"{State} → {st}: {detail}");
        State = st; Detail = detail; Changed?.Invoke();
    }

    // ---------- device discovery ----------
    public async Task<IReadOnlyList<AdbDevice>> RefreshDevicesAsync(CancellationToken ct = default)
    {
        if (_adb.AdbPath == null && _adb.Locate() == null) { Devices = Array.Empty<AdbDevice>(); Changed?.Invoke(); return Devices; }
        var list = await _adb.ListDevicesAsync(ct);
        bool changed = list.Count != Devices.Count || list.Zip(Devices).Any(p => p.First != p.Second);
        Devices = list;
        foreach (var d in list) _devices.Observe(d);
        if (changed)
        {
            foreach (var d in list) Log.Info("device", d.IsAuthorized ? $"Device detected: {d.DisplayName} ({d.TransportName})" : $"Device {d.Serial} is {d.State}");
        }
        if (State is ConnState.Offline or ConnState.DeviceConnected)
        {
            bool any = list.Any(d => d.IsAuthorized);
            var next = any ? ConnState.DeviceConnected : ConnState.Offline;
            if (next != State) Set(next, any ? "Device ready" : "No device");
            else if (changed) Changed?.Invoke();
        }
        else if (changed) Changed?.Invoke();
        return list;
    }

    public AdbDevice? PickDevice(ConnectionMethod m, string? serial)
    {
        var cands = Devices.Where(d => d.IsAuthorized && !d.IsEmulator).ToList();
        if (m == ConnectionMethod.Usb) cands = cands.Where(d => !d.IsWifi).ToList();
        else if (Cfg.Transport == TransportPref.Usb) cands = cands.Where(d => !d.IsWifi).ToList();
        else if (Cfg.Transport == TransportPref.Wifi) cands = cands.Where(d => d.IsWifi).ToList();
        if (serial != null) return cands.FirstOrDefault(d => d.Serial == serial);
        return cands.FirstOrDefault(d => d.Serial == Cfg.PreferredDeviceSerial) ?? cands.FirstOrDefault();
    }

    // ---------- connect ----------
    public async Task ConnectAsync(ConnectionMethod method, string? serial = null)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (State is ConnState.Connected or ConnState.Limited or ConnState.Connecting or ConnState.EstablishingTunnel) return;
            _userDisconnect = false;
            await RunConnectAsync(method, serial, isReconnect: false);
        }
        finally { _gate.Release(); }
    }

    async Task RunConnectAsync(ConnectionMethod method, string? serial, bool isReconnect)
    {
        _opCts = new CancellationTokenSource();
        var ct = _opCts.Token;
        Method = method; LastError = null; NeedsElevation = false; LastChecks = Array.Empty<CheckResult>();
        Vpn = PathState.Down; Internet = NetState.Unknown; InterfaceName = ""; Device = null; DeviceName = "";
        _session = null; ResetStages();
        try
        {
            Set(isReconnect ? ConnState.Reconnecting : ConnState.DetectingDevice, "Looking for your phone");
            if (method is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb) await ConnectTunnelAsync(method, serial, ct);
            else await ConnectNativeAsync(method, ct);
        }
        catch (OperationCanceledException) { await TeardownAsync("Cancelled", failed: false); Set(ConnState.Offline, "Cancelled"); }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("connect", ex, "Connection failed");
            await TeardownAsync(ex.Message, failed: true);
            Set(ConnState.Error, ex.Message);
        }
    }

    async Task ConnectTunnelAsync(ConnectionMethod method, string? serial, CancellationToken ct)
    {
        if (!WinNet.IsElevated()) { NeedsElevation = true; throw new InvalidOperationException("Administrator rights are required to create the virtual adapter and change routes."); }
        if (_adb.AdbPath == null && _adb.Locate() == null) throw new FileNotFoundException("adb.exe was not found. Reinstall CiciPDA or set its location in Settings.");

        Step("detect", StageState.Active, "Looking for your phone");
        await RefreshDevicesAsync(ct);
        var dev = PickDevice(method, serial);
        if (dev == null)
        {
            Step("detect", Devices.Count > 0 ? StageState.Done : StageState.Failed, Devices.Count > 0 ? "Phone seen, not authorized" : "No phone found");
            var unauth = Devices.FirstOrDefault(d => d.State == "unauthorized");
            if (unauth != null) { Step("authorize", StageState.Failed, "Waiting for you to tap Allow on the phone"); throw new InvalidOperationException("The phone has not authorized this computer. Unlock the phone and tap Allow on the 'Allow USB debugging?' prompt (tick Always allow), then press Connect again."); }
            if (Devices.Any(d => d.State == "offline")) throw new InvalidOperationException("The phone is shown as offline. Reconnect the cable or restart ADB from the Devices page.");
            throw new InvalidOperationException(method == ConnectionMethod.Usb ? "No USB device found. Connect a data cable and enable USB debugging (Settings > Developer options)." : "No authorized Android device found. Enable USB debugging and connect the phone.");
        }
        Device = dev; DeviceName = dev.DisplayName;
        Step("detect", StageState.Done, $"{dev.DisplayName} ({dev.TransportName})");
        Step("authorize", StageState.Done, "USB debugging authorized");
        _devices.CountConnection(dev.Serial);
        Set(ConnState.DeviceConnected, DeviceName);
        _session = new SessionRecord { Start = DateTimeOffset.Now, Method = method, Device = dev.DisplayName };

        Set(ConnState.EstablishingTunnel, "Preparing the phone");
        Vpn = PathState.Starting;
        if (Cfg.UseCompanion) await PrepareCompanionAsync(dev, ct);
        else { Step("companion", StageState.Skipped, "Companion disabled in Settings"); Step("install", StageState.Skipped); }

        Step("service", StageState.Active, "Starting the phone service");
        try
        {
            _agent = Cfg.UseCompanion ? await StartCompanionServiceAsync(dev, ct) : await _adb.StartAgentAsync(dev.Serial, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Cfg.UseCompanion && Cfg.AllowLegacyFallback)
        {
            Log.Warn("companion", "Companion unavailable (" + ex.Message + "); using the no-install shell helper.");
            _agent = await _adb.StartAgentAsync(dev.Serial, ct);
        }
        _journal.State.AdbSerial = dev.Serial; _journal.State.ForwardPort = _agent.LocalPort; _journal.Save();
        Step("service", StageState.Done, _agent.IsCompanion ? "Companion service running" : "Shell helper running");

        Step("tunnel", StageState.Active, "Testing the phone's internet path");
        var sw = Stopwatch.StartNew();
        using (await AgentChannel.ConnectAsync(_agent.LocalPort, _agent.Key, "1.1.1.1", 443, ct, TimeSpan.FromSeconds(15))) { }
        Step("tunnel", StageState.Done, $"Encrypted channel up; phone reached the internet in {sw.ElapsedMilliseconds} ms");

        _socks = new SocksRelay(_agent.LocalPort, _agent.Key);
        _socks.Start();

        Set(ConnState.Connecting, "Creating virtual adapter");
        Step("adapter", StageState.Active);
        _net = new TunnelNetwork(_journal);
        await _net.StartAsync(_socks.Port, Cfg, dev.WifiAddress, ct, onAdapter: () => Step("adapter", StageState.Done, "Wintun adapter up"));
        Step("network", StageState.Done, "Routes, DNS and MTU configured");
        InterfaceName = TunnelNetwork.AdapterName;
        Vpn = PathState.Up;

        Set(ConnState.Connecting, "Verifying connectivity");
        Step("internet", StageState.Active, "Checking route, DNS, HTTP, HTTPS");
        await VerifyAsync(_net.IfIndex, tunnel: true, ct);
        Step("internet", Internet == NetState.Reachable ? StageState.Done : StageState.Warn, Internet == NetState.Reachable ? "Verified" : "Partial");
        StartMonitoring(tunnel: true);
        await FinishConnectAsync(ct);
    }

    async Task PrepareCompanionAsync(AdbDevice dev, CancellationToken ct)
    {
        Step("companion", StageState.Active, "Verifying the trusted companion release");
        var release = _comp.LoadBestRelease();
        if (Cfg.AutoUpdateCompanion)
        {
            try { var up = await _comp.DownloadUpdateAsync(release.Meta.VersionCode, ct); if (up != null) release = up; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Info("companion", "No update applied: " + ex.Message); }
        }
        var info = await _comp.InspectAsync(dev.Serial, release, ct);
        CompanionInfo = info; CompanionInstalled = info.Installed;
        Step("companion", StageState.Done, info.Installed ? $"Installed {info.InstalledName} ({info.Status}); Android {info.AndroidVersion}" : $"Not installed; Android {info.AndroidVersion}");
        if (info.Status is CompanionStatus.UpToDate or CompanionStatus.NewerThanBundled) { Step("install", StageState.Skipped, "Already up to date"); return; }

        Step("install", StageState.Active, (info.Installed ? "Updating to " : "Installing ") + release.Meta.VersionName + " - confirm on the phone if it asks");
        var r = await _comp.InstallAsync(dev.Serial, release, ct);
        if (!r.Ok) { Step("install", StageState.Failed, r.Message); throw new InvalidOperationException(r.Message); }
        CompanionInstalled = true;
        Step("install", StageState.Done, release.Meta.VersionName + " installed");
        if (!info.Installed) await _comp.ShowOnPhoneAsync(dev.Serial, ct);   // first run: lets the user read what it does and allow notifications
        if (info.Sdk >= 33 && !info.NotificationsGranted)
            Log.Warn("companion", "Notifications are not allowed for the companion yet. It still works, but open it on the phone and tap Allow notifications so its sharing notice is visible.");
    }

    async Task<AgentSession> StartCompanionServiceAsync(AdbDevice dev, CancellationToken ct)
    {
        var key = CompanionManager.NewSessionKey();
        int remote = System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000, 60000);
        await _comp.StartServiceAsync(dev.Serial, key, remote, ct);
        var (local, json) = await _comp.WaitReadyAsync(dev.Serial, key, remote, ct);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.GetProperty("protocol").GetInt32() != AgentChannel.Protocol) throw new InvalidOperationException("The phone companion speaks a different protocol version. Update CiciPDA or the companion.");
            PhoneNetwork = DescribePhoneNetwork(root);
            Log.Ok("companion", $"Companion {root.GetProperty("version").GetString()} ready. Phone network: {PhoneNetwork}");
            if (root.TryGetProperty("network", out var n) && n.TryGetProperty("validated", out var v) && !v.GetBoolean())
                Log.Warn("companion", "The phone reports no validated internet connection. Check its mobile data or Wi-Fi.");
        }
        var adb = _adb.AdbPath!; var serial = dev.Serial;
        return new AgentSession { Serial = serial, LocalPort = local, RemotePort = remote, Key = key, AdbPath = adb, IsCompanion = true, PhoneStatusJson = json, OnStop = () => { _ = _comp.StopServiceAsync(serial); } };
    }

    static string DescribePhoneNetwork(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("network", out var n)) return "unknown";
        return $"{n.GetProperty("transport").GetString()}{(n.GetProperty("vpn").GetBoolean() ? " + VPN" : "")}, {(n.GetProperty("validated").GetBoolean() ? "validated" : "not validated")}{(n.GetProperty("metered").GetBoolean() ? ", metered" : "")}";
    }

    async Task ConnectNativeAsync(ConnectionMethod method, CancellationToken ct)
    {
        foreach (var id in new[] { "authorize", "companion", "install", "service", "tunnel", "adapter", "network" }) Step(id, StageState.Skipped, "Not used for this method");
        Step("detect", StageState.Active, "Looking for the phone hotspot");
        var (ni, label) = method == ConnectionMethod.AndroidHotspot ? await FindWifiHotspotAsync() : await FindIphoneAsync();
        if (ni == null) throw new InvalidOperationException(label);
        Step("detect", StageState.Done, label);
        DeviceName = label; InterfaceName = ni.Name; Device = null;
        _session = new SessionRecord { Start = DateTimeOffset.Now, Method = method, Device = label, Interface = ni.Name };
        string name = ni.Name;
        _nativeIface = () => WinNet.FindByName(name);
        Log.Ok("device", $"Using {label} on interface {ni.Name}.");
        Set(ConnState.Connecting, "Verifying connectivity");
        Vpn = PathState.Down;
        Step("internet", StageState.Active);
        await VerifyAsync(WinNet.IndexOf(ni), tunnel: false, ct);
        Step("internet", Internet == NetState.Reachable ? StageState.Done : StageState.Warn);
        StartMonitoring(tunnel: false);
        await FinishConnectAsync(ct);
    }

    async Task VerifyAsync(int ifIndex, bool tunnel, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        results.Add(await Verifier.AdapterReady(ifIndex));        // local readiness (not evidence of internet access)
        results.Add(await Verifier.DefaultRoute(ifIndex));
        results.Add(await Verifier.DnsWithRetry(flushFirst: tunnel));
        var http = Verifier.Http(); var https = Verifier.Https();
        results.AddRange(await Task.WhenAll(http, https));
        ct.ThrowIfCancellationRequested();
        results.Add(await Verifier.WindowsConnectivity(ifIndex));    // observed from Windows, never forced
        results.Add(await Verifier.UdpDns(tunnel ? Cfg.DnsPrimary : "1.1.1.1"));
        if (tunnel) results.Add(await Verifier.Ipv6(Cfg.Ipv6LeakProtection, _net?.Ipv6Protected ?? false));
        LastChecks = results;
        foreach (var r in results) Log.Write(r.Passed ? Severity.Success : (r.Required ? Severity.Error : Severity.Warning), "verify", $"{r.Title}: {(r.Passed ? "OK" : "FAILED")} — {r.Detail}");
        var req = results.Where(r => r.Required).ToList();
        // Local readiness (adapter, route) is NOT internet reachability: if DNS, HTTP and HTTPS all fail the connection is unusable
        // no matter how healthy the adapter looks, so the attempt fails instead of showing a green adapter.
        var inet = results.Where(r => r.Id is "dns" or "http" or "https").ToList();
        if (inet.All(r => !r.Passed))
        {
            var diag = await IsolateFailureAsync(tunnel, results);
            Log.Error("verify", "Isolation: " + diag);
            throw new InvalidOperationException("No internet access through this connection: " + inet[0].Detail + " " + (inet[0].Fix ?? "") + " [" + diag + "]");
        }
        Internet = req.All(r => r.Passed) ? NetState.Reachable : NetState.Limited;
        if (!req.All(r => r.Passed)) Log.Warn("verify", "Only part of the data path works; state will be Limited Connectivity.");
    }

    /// <summary>When every internet check fails, find out WHICH layer is broken instead of reporting a generic failure.</summary>
    async Task<string> IsolateFailureAsync(bool tunnel, List<CheckResult> results)
    {
        var parts = new List<string>();
        var tcpIp = await Verifier.TcpLatency("1.1.1.1", 443, TimeSpan.FromSeconds(5));          // no DNS involved
        parts.Add(tcpIp != null ? $"TCP to 1.1.1.1:443 works ({tcpIp.Value.TotalMilliseconds:0} ms) so the TCP path is fine and name resolution is the problem"
                                : "TCP to 1.1.1.1:443 FAILS so the TCP path through the connection is broken");
        var udp = results.FirstOrDefault(r => r.Id == "udp");
        if (udp != null) parts.Add("UDP DNS to the configured server " + (udp.Passed ? "works" : "FAILS (" + udp.Detail + ")"));
        if (tunnel && _socks != null)
            parts.Add($"relay: TCP connections {_socks.TotalConnections} (failed {_socks.FailedConnections}), UDP datagrams forwarded {_socks.UdpForwarded}, dropped {_socks.UdpDropped}, DNS queries seen {_socks.DnsQueries}");
        if (tunnel && _net != null)
        {
            var ni = _net.Interface;
            if (ni != null) { var st = ni.GetIPv4Statistics(); parts.Add($"adapter counters: sent {st.BytesSent} B, received {st.BytesReceived} B"); }
        }
        return string.Join("; ", parts) + ".";
    }

    void StartMonitoring(bool tunnel)
    {
        Func<NetworkInterface?> iface = tunnel ? () => _net?.Interface : _nativeIface!;
        Func<CancellationToken, Task<TimeSpan?>> probe;
        Func<CancellationToken, Task<(int?, string?)>>? signal = null;
        if (tunnel)
        {
            var a = _agent!;
            probe = async ct =>
            {
                var sw = Stopwatch.StartNew();
                try { using var c = await AgentChannel.ConnectAsync(a.LocalPort, a.Key, "1.1.1.1", 443, ct, TimeSpan.FromSeconds(5)); return sw.Elapsed; }
                catch (AgentConnectException) { return null; }
            };
            var serial = a.Serial;
            signal = async ct => { var info = await _adb.GetPhoneInfoAsync(serial, ct); return (info.SignalLevel, string.IsNullOrWhiteSpace(info.NetworkType) ? null : info.NetworkType); };
        }
        else probe = ct => Verifier.TcpLatency("1.1.1.1", 443, TimeSpan.FromSeconds(5));
        _monitor = new TrafficMonitor(iface, probe, signal);
        if (tunnel) _monitor.Extra = () => (_socks?.ActiveConnections ?? 0, _socks?.UdpDropped ?? 0);
        _monitor.Updated += s => { TrafficUpdated?.Invoke(s); };
        _monitor.Start();
    }

    async Task FinishConnectAsync(CancellationToken ct)
    {
        ConnectedAt = DateTimeOffset.Now;
        bool full = Internet == NetState.Reachable;
        Set(full ? ConnState.Connected : ConnState.Limited, full ? "Internet verified" : "Some checks failed");
        if (full) Log.Ok("connect", $"Connected via {Method}. Internet connectivity verified.");

        if (Method is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb && Cfg.ShareWifi)
        {
            if (!full) Log.Warn("hotspot", "Wi-Fi sharing was not started because the connection did not pass full internet verification.");
            else await StartSharingAsync(ct);
        }
        _healthCts = new CancellationTokenSource();
        _ = Task.Run(() => HealthLoop(_healthCts.Token));
    }

    /// <summary>Explicit "forget this PC" on a phone: ends any session on it, then makes the companion delete its key.</summary>
    public async Task UnpairAsync(string serial)
    {
        if (Device?.Serial == serial && (IsActive || IsBusy)) await DisconnectAsync();
        await _comp.UnpairAsync(serial);
        Log.Ok("companion", "Companion pairing removed on the phone: the stored session key and its Keystore wrapper were deleted.");
    }

    // ---------- Wi-Fi sharing ----------
    public HotspotService Hotspot { get; } = new();

    /// <summary>Shares ONLY the CiciPDA adapter. If Windows cannot do that, say so; never fall back to another connection.</summary>
    async Task StartSharingAsync(CancellationToken ct)
    {
        var pass = Secret.Unprotect(Cfg.HotspotPasswordProtected);
        if (pass.Length < 8) { Log.Warn("hotspot", "Wi-Fi sharing is on but no valid passphrase is saved. Set one in Settings."); return; }
        var guid = HotspotService.AdapterGuid(TunnelNetwork.AdapterName);
        if (guid == null) { Log.Warn("hotspot", "Wi-Fi sharing skipped: the CiciPDA adapter GUID could not be determined."); return; }
        // Windows creates the connection profile for a new adapter a moment after it comes up.
        HotspotCheck chk = await Hotspot.CheckAsync(guid);
        for (int i = 0; i < 8 && chk.Capability == "NoProfile"; i++) { await Task.Delay(1000, ct); chk = await Hotspot.CheckAsync(guid); }
        if (!chk.CanShare)
        {
            Log.Warn("hotspot", "Wi-Fi sharing unavailable for the CiciPDA connection: " + chk.Detail + " Supported alternative: enable Internet Connection Sharing manually (CiciPDA adapter > Properties > Sharing) or connect the other device to a Wi-Fi network that routes through this PC.");
            Changed?.Invoke(); return;
        }
        _journal.State.HotspotAdapter = guid; _journal.State.HotspotSsid = Cfg.HotspotSsid; _journal.State.HotspotActive = true; _journal.Save();   // journal first: recovery must know even if we crash mid-start
        Changed?.Invoke();
        var ok = await Hotspot.StartAsync(guid, Cfg.HotspotSsid, pass, null, ct);
        if (!ok) { _journal.State.HotspotActive = false; _journal.State.HotspotAdapter = null; _journal.State.HotspotSsid = null; _journal.Save(); }
        else Log.Ok("hotspot", $"Wi-Fi hotspot \"{Cfg.HotspotSsid}\" is sharing the CiciPDA connection (profile \"{chk.Profile}\").");
        Changed?.Invoke();
    }

    // ---------- health & reconnect ----------
    async Task HealthLoop(CancellationToken ct)
    {
        int bad = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            string? problem = null;
            if (Method is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb)
            {
                if (_agent is { IsAlive: false }) problem = "The phone helper stopped.";
                else if (_agent is { IsCompanion: true } ag && await CompanionPollFailedAsync(ag, ct)) problem = "The phone companion stopped responding (it may have been closed or restricted by the phone's battery settings).";
                else if (_net is { IsRunning: false }) problem = "The packet engine (tun2socks) stopped.";
                else
                {
                    try { var l = await _adb.ListDevicesAsync(ct); Devices = l; if (Device != null && !l.Any(d => d.Serial == Device.Serial && d.IsAuthorized)) problem = l.Any(d => d.Serial == Device.Serial && d.State == "unauthorized") ? "USB debugging authorization was revoked on the phone." : "The phone was disconnected."; } catch (OperationCanceledException) { return; } catch { }
                }
            }
            else if (_nativeIface?.Invoke() is not { OperationalStatus: OperationalStatus.Up }) problem = "The network interface went down.";

            if (Hotspot.UpstreamGuid != null) { try { await Hotspot.RefreshAsync(); Changed?.Invoke(); } catch (Exception ex) { Log.Debug("hotspot", ex.Message); } }
            if (problem == null && (_monitor?.RecentProbeFailures ?? 0) >= 3) problem = "The connection stopped responding.";
            if (problem == null) { bad = 0; if (State == ConnState.Limited && (_monitor?.RecentProbeFailures ?? 1) == 0) { Internet = NetState.Reachable; Set(ConnState.Connected, "Recovered"); } Changed?.Invoke(); continue; }
            if (++bad < (problem.StartsWith("The connection") ? 1 : 2)) continue;

            Log.Warn("health", problem);
            _ = Task.Run(() => HandleInterruptionAsync(problem));
            return;
        }
    }

    /// <summary>Authenticated health poll of the companion; also refreshes the phone's network description. True when it did not answer.</summary>
    async Task<bool> CompanionPollFailedAsync(AgentSession ag, CancellationToken ct)
    {
        try
        {
            var json = await AgentChannel.GetStatusAsync(ag.LocalPort, ag.Key, ct, TimeSpan.FromSeconds(4));
            ag.PhoneStatusJson = json;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            PhoneNetwork = DescribePhoneNetwork(doc.RootElement);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentConnectException) { return true; }
    }

    async Task HandleInterruptionAsync(string reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (_userDisconnect || !IsActive) return;
            var method = Method; var serial = Device?.Serial;
            if (_session != null) { _session.Result = "Interrupted"; _session.Error = reason; }
            await TeardownAsync(reason, failed: true, keepSessionResult: true);
            if (!Cfg.AutoReconnect) { LastError = reason; Set(ConnState.Error, reason); return; }
            for (int i = 1; i <= Math.Max(1, Cfg.ReconnectAttempts) && !_userDisconnect; i++)
            {
                Set(ConnState.Reconnecting, $"Attempt {i} of {Cfg.ReconnectAttempts}");
                Log.Info("reconnect", $"Attempt {i}/{Cfg.ReconnectAttempts} in {Cfg.ReconnectDelaySec}s");
                try { await Task.Delay(TimeSpan.FromSeconds(Cfg.ReconnectDelaySec)); } catch { }
                await RunConnectAsync(method, serial, isReconnect: true);
                if (IsActive) { Log.Ok("reconnect", "Automatic recovery completed."); return; }
            }
            if (!IsActive && State != ConnState.Offline) Set(ConnState.Error, "Could not reconnect: " + (LastError ?? reason));
        }
        finally { _gate.Release(); }
    }

    // ---------- disconnect / cleanup ----------
    public async Task DisconnectAsync()
    {
        _userDisconnect = true;
        _opCts?.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (State == ConnState.Offline || State == ConnState.DeviceConnected) return;
            Set(ConnState.Disconnecting, "Restoring network settings");
            await TeardownAsync("User disconnected", failed: false);
            await RefreshDevicesAsync();
            if (State == ConnState.Disconnecting) Set(Devices.Any(d => d.IsAuthorized) ? ConnState.DeviceConnected : ConnState.Offline, "Disconnected");
            Log.Ok("connect", "Disconnected. Network settings restored.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Idempotent ordered cleanup; every step is isolated so one failure cannot block the rest.</summary>
    async Task TeardownAsync(string reason, bool failed, bool keepSessionResult = false)
    {
        _healthCts?.Cancel();
        var snap = Traffic;
        try { _monitor?.Dispose(); } catch { }
        _monitor = null; _nativeIface = null;
        if (Hotspot.UpstreamGuid != null) { try { await Hotspot.StopAsync(); } catch (Exception ex) { Log.Warn("hotspot", ex.Message); } _journal.State.HotspotActive = false; _journal.State.HotspotAdapter = null; _journal.State.HotspotSsid = null; }
        try { if (_net != null) await _net.StopAsync(); } catch (Exception ex) { Log.Error("cleanup", ex, "Network cleanup"); }
        _net = null;
        try { _socks?.Dispose(); } catch { }
        _socks = null;
        try { _agent?.Dispose(); } catch (Exception ex) { Log.Warn("cleanup", "agent: " + ex.Message); }
        _agent = null;
        _journal.Reset();

        if (_session != null)
        {
            _session.End = DateTimeOffset.Now;
            _session.BytesDown = snap.TotalDown; _session.BytesUp = snap.TotalUp;
            if (!keepSessionResult) _session.Result = failed ? "Failed" : "Completed";
            _session.Error ??= failed ? reason : null;
            if (ConnectedAt != null || failed) _history.Add(_session);
            _session = null;
        }
        ConnectedAt = null; Vpn = PathState.Down; Internet = NetState.Unknown; InterfaceName = "";
        Changed?.Invoke();
    }

    public async Task ShutdownAsync()
    {
        _userDisconnect = true; _opCts?.Cancel();
        await _gate.WaitAsync(TimeSpan.FromSeconds(10));
        try { await TeardownAsync("Application closing", failed: false); } finally { try { _gate.Release(); } catch { } }
    }

    // ---------- native (hotspot) helpers ----------
    static readonly Regex SsidRe = new(@"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex StateRe = new(@"^\s*State\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task<string?> CurrentSsidAsync()
    {
        var r = await ProcessRunner.RunAsync("netsh.exe", new[] { "wlan", "show", "interfaces" }, TimeSpan.FromSeconds(8));
        if (!r.Ok) return null;
        var st = StateRe.Match(r.Stdout);
        if (!st.Success || !st.Groups[1].Value.Equals("connected", StringComparison.OrdinalIgnoreCase)) return null;
        var m = SsidRe.Match(r.Stdout);
        return m.Success ? m.Groups[1].Value : null;
    }

    static NetworkInterface? FirstUp(Func<NetworkInterface, bool> f) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && f(n)
            && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254")));

    async Task<(NetworkInterface?, string)> FindWifiHotspotAsync()
    {
        var ssid = await CurrentSsidAsync();
        if (ssid == null) return (null, "Windows is not connected to a Wi-Fi network. Turn on the phone's hotspot and join it from the Windows Wi-Fi menu.");
        var sel = NativeLinks.SelectWifi(NativeLinks.Snapshot(), ssid);
        return sel == null ? (null, "The Wi-Fi adapter has no IPv4 address yet. Wait a few seconds and try again.") : (WinNet.FindByName(sel.Name), ssid);
    }

    public IphoneMode IphoneLink { get; private set; }

    async Task<(NetworkInterface?, string)> FindIphoneAsync()
    {
        var ssid = await CurrentSsidAsync();
        var snap = NativeLinks.Snapshot();
        var (sel, mode) = NativeLinks.SelectIphone(snap, ssid);
        IphoneLink = mode;
        if (sel != null)
            return (WinNet.FindByName(sel.Name), mode == IphoneMode.UsbAppleEthernet ? "iPhone (USB, Apple Ethernet adapter)" : ssid + " (iPhone Personal Hotspot over Wi-Fi)");
        if (NativeLinks.AppleAdapterPresentButIdle(snap))
            return (null, "Windows sees the iPhone's USB adapter but it has no network address yet. Turn on Personal Hotspot on the iPhone, unlock it and tap Trust This Computer, then try again.");
        return (null, "No iPhone connection found. Either join the iPhone's Personal Hotspot from the Windows Wi-Fi menu, or connect by USB with Apple's drivers installed (Apple Devices app / iTunes), enable Personal Hotspot and tap Trust. Note: iPhone modes use Windows' own connection; CiciPDA does not tunnel or encrypt this traffic.");
    }
}
