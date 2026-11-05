using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace CiciPDA;

public enum ConnState { Offline, DetectingDevice, DeviceConnected, EstablishingTunnel, Connecting, Connected, Reconnecting, Disconnecting, Error, Limited }
public enum PathState { Down, Starting, Up, Failed }
public enum NetState { Unknown, Reachable, Limited, Unreachable }

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

    public Orchestrator(SettingsStore s, AdbService adb, DeviceStore d, HistoryStore h, RecoveryJournal j)
    { _settings = s; _adb = adb; _devices = d; _history = h; _journal = j; _adb.Locate(); }

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
        _session = null;
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
        if (_adb.AdbPath == null && _adb.Locate() == null) throw new FileNotFoundException("adb.exe was not found. Set its location in Settings.");

        await RefreshDevicesAsync(ct);
        var dev = PickDevice(method, serial);
        if (dev == null)
        {
            var unauth = Devices.FirstOrDefault(d => d.State == "unauthorized");
            if (unauth != null) throw new InvalidOperationException("The phone has not authorized this computer. Unlock the phone and tap Allow on the USB debugging prompt.");
            if (Devices.Any(d => d.State == "offline")) throw new InvalidOperationException("The phone is shown as offline. Reconnect the cable or restart ADB from the Devices page.");
            throw new InvalidOperationException(method == ConnectionMethod.Usb ? "No USB device found. Connect a data cable and enable USB debugging." : "No authorized Android device found. Enable USB debugging and connect the phone.");
        }
        Device = dev; DeviceName = dev.DisplayName;
        _devices.CountConnection(dev.Serial);
        Log.Ok("device", $"Using {dev.DisplayName} over {dev.TransportName}.");
        Set(ConnState.DeviceConnected, DeviceName);
        _session = new SessionRecord { Start = DateTimeOffset.Now, Method = method, Device = dev.DisplayName };

        Set(ConnState.EstablishingTunnel, "Starting the phone helper");
        Vpn = PathState.Starting;
        _agent = await _adb.StartAgentAsync(dev.Serial, ct);
        _journal.State.AdbSerial = dev.Serial; _journal.State.ForwardPort = _agent.LocalPort; _journal.Save();
        Log.Ok("agent", $"Phone helper running; encrypted channel on local port {_agent.LocalPort}.");

        // Prove the phone-side data path before touching Windows networking.
        var sw = Stopwatch.StartNew();
        using (await AgentChannel.ConnectAsync(_agent.LocalPort, _agent.Key, "1.1.1.1", 443, ct, TimeSpan.FromSeconds(15))) { }
        Log.Ok("agent", $"Phone can reach the internet (probe {sw.ElapsedMilliseconds} ms).");

        _socks = new SocksRelay(_agent.LocalPort, _agent.Key);
        _socks.Start();

        Set(ConnState.Connecting, "Creating virtual adapter");
        _net = new TunnelNetwork(_journal);
        await _net.StartAsync(_socks.Port, Cfg, dev.WifiAddress, ct);
        InterfaceName = TunnelNetwork.AdapterName;
        Vpn = PathState.Up;

        Set(ConnState.Connecting, "Verifying connectivity");
        await VerifyAsync(_net.IfIndex, tunnel: true, ct);
        StartMonitoring(tunnel: true);
        await FinishConnectAsync(ct);
    }

    async Task ConnectNativeAsync(ConnectionMethod method, CancellationToken ct)
    {
        var (ni, label) = method == ConnectionMethod.AndroidHotspot ? await FindWifiHotspotAsync() : await FindIphoneAsync();
        if (ni == null) throw new InvalidOperationException(label);
        DeviceName = label; InterfaceName = ni.Name; Device = null;
        _session = new SessionRecord { Start = DateTimeOffset.Now, Method = method, Device = label, Interface = ni.Name };
        string name = ni.Name;
        _nativeIface = () => WinNet.FindByName(name);
        Log.Ok("device", $"Using {label} on interface {ni.Name}.");
        Set(ConnState.Connecting, "Verifying connectivity");
        Vpn = PathState.Down;
        await VerifyAsync(WinNet.IndexOf(ni), tunnel: false, ct);
        StartMonitoring(tunnel: false);
        await FinishConnectAsync(ct);
    }

    async Task VerifyAsync(int ifIndex, bool tunnel, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        results.Add(await Verifier.DefaultRoute(ifIndex));
        results.Add(await Verifier.Dns());
        var http = Verifier.Http(); var https = Verifier.Https();
        results.AddRange(await Task.WhenAll(http, https));
        ct.ThrowIfCancellationRequested();
        results.Add(await Verifier.WindowsConnectivity(ifIndex));
        LastChecks = results;
        foreach (var r in results) Log.Write(r.Passed ? Severity.Success : (r.Required ? Severity.Error : Severity.Warning), "verify", $"{r.Title}: {(r.Passed ? "OK" : "FAILED")} — {r.Detail}");
        var req = results.Where(r => r.Required).ToList();
        if (req.All(r => !r.Passed)) throw new InvalidOperationException("Connectivity check failed: " + req.First().Detail + " " + (req.First().Fix ?? ""));
        Internet = req.All(r => r.Passed) ? NetState.Reachable : NetState.Limited;
        if (!req.All(r => r.Passed)) Log.Warn("verify", "Only part of the data path works; state will be Limited Connectivity.");
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
            var pass = Secret.Unprotect(Cfg.HotspotPasswordProtected);
            if (pass.Length < 8) Log.Warn("hotspot", "Wi-Fi sharing is on but no valid passphrase is saved. Set one in Settings.");
            else
            {
                var (ok, msg) = await Hotspot.StartAsync(Cfg.HotspotSsid, pass, ct);
                _journal.State.HotspotActive = Hotspot.Active; _journal.Save();
                if (ok) Log.Ok("hotspot", $"Wi-Fi hotspot \"{Cfg.HotspotSsid}\" started.");
                else Log.Warn("hotspot", "Wi-Fi sharing could not start: " + msg);
            }
        }
        _healthCts = new CancellationTokenSource();
        _ = Task.Run(() => HealthLoop(_healthCts.Token));
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
                else if (_net is { IsRunning: false }) problem = "The packet engine (tun2socks) stopped.";
                else
                {
                    try { var l = await _adb.ListDevicesAsync(ct); Devices = l; if (Device != null && !l.Any(d => d.Serial == Device.Serial && d.IsAuthorized)) problem = "The phone was disconnected."; } catch (OperationCanceledException) { return; } catch { }
                }
            }
            else if (_nativeIface?.Invoke() is not { OperationalStatus: OperationalStatus.Up }) problem = "The network interface went down.";

            if (problem == null && (_monitor?.RecentProbeFailures ?? 0) >= 3) problem = "The connection stopped responding.";
            if (problem == null) { bad = 0; if (State == ConnState.Limited && (_monitor?.RecentProbeFailures ?? 1) == 0) { Internet = NetState.Reachable; Set(ConnState.Connected, "Recovered"); } Changed?.Invoke(); continue; }
            if (++bad < (problem.StartsWith("The connection") ? 1 : 2)) continue;

            Log.Warn("health", problem);
            _ = Task.Run(() => HandleInterruptionAsync(problem));
            return;
        }
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
        if (Hotspot.Active) { try { await Hotspot.StopAsync(); Log.Info("hotspot", "Wi-Fi hotspot stopped."); } catch (Exception ex) { Log.Warn("hotspot", ex.Message); } _journal.State.HotspotActive = false; }
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
        var ni = FirstUp(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        return ni == null ? (null, "The Wi-Fi adapter has no IPv4 address yet. Wait a few seconds and try again.") : (ni, ssid);
    }

    async Task<(NetworkInterface?, string)> FindIphoneAsync()
    {
        var usb = FirstUp(n => n.Description.Contains("Apple Mobile Device", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("iPhone", StringComparison.OrdinalIgnoreCase));
        if (usb != null) return (usb, "iPhone (USB)");
        var ssid = await CurrentSsidAsync();
        var wifi = ssid == null ? null : FirstUp(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        if (wifi != null) return (wifi, ssid + " (Personal Hotspot)");
        return (null, "No iPhone connection found. Enable Personal Hotspot and either join it over Wi-Fi, or connect by USB with Apple's drivers installed (Apple Devices / iTunes) and tap Trust.");
    }
}
