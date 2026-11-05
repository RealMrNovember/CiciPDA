using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CiciPDA.App;

public sealed class ChecklistItem : Observable
{
    ItemState _state; string _title = "", _hint = "";
    public ItemState State { get => _state; set => Set(ref _state, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Hint { get => _hint; set => Set(ref _hint, value); }
}

public sealed class DiagRow : Observable
{
    public string Id { get; init; } = ""; string _title = "";
    ItemState _state = ItemState.Pending; string _detail = "Not run yet", _fix = ""; bool _running; string _ms = "";
    public string Title { get => _title; set => Set(ref _title, value); }
    public ItemState State { get => _state; set => Set(ref _state, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Fix { get => _fix; set => Set(ref _fix, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }
    public string Ms { get => _ms; set => Set(ref _ms, value); }
    public ICommand? Run { get; set; }
}

public sealed class DeviceRow
{
    public string Serial { get; init; } = "";
    public string Name { get; init; } = "";
    public string Transport { get; init; } = "";
    public string Trust { get; init; } = "";
    public string LastSeen { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Connected { get; init; }
    public bool Trusted { get; init; }
    public bool Preferred { get; init; }
    public ICommand? Forget { get; init; }
    public ICommand? Prefer { get; init; }
    public ICommand? Unpair { get; init; }
}

public sealed class SessionRow
{
    public string Start { get; init; } = "";
    public string End { get; init; } = "";
    public string Method { get; init; } = "";
    public string Device { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Data { get; init; } = "";
    public string Result { get; init; } = "";
    public string Error { get; init; } = "";
    public ItemState ResultState { get; init; }
}

public sealed class MethodOption : Observable
{
    public ConnectionMethod Method { get; init; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconKey { get; init; } = "";
    public bool Recommended { get; init; }
    string _avail = ""; ItemState _availState; bool _selected; string _note = "";
    public string Availability { get => _avail; set => Set(ref _avail, value); }
    public ItemState AvailState { get => _availState; set => Set(ref _availState, value); }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
}

public sealed class StageRow : Observable
{
    string _title = "", _detail = ""; ItemState _state; bool _active, _skipped;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public ItemState State { get => _state; set => Set(ref _state, value); }
    public bool Active { get => _active; set => Set(ref _active, value); }
    public bool Skipped { get => _skipped; set => Set(ref _skipped, value); }
}

public sealed class IndicatorRow : Observable
{
    bool _on; public string Title { get; init; } = "";
    public bool On { get => _on; set { if (Set(ref _on, value)) Raise(nameof(State)); } }
    public ItemState State => _on ? ItemState.Ok : ItemState.Pending;
}

public sealed class ToastRow { public Severity Severity { get; init; } public string Text { get; init; } = ""; }

public sealed class ShellViewModel : Observable
{
    public readonly Orchestrator Orch;
    public readonly SettingsStore Settings;
    public readonly HistoryStore History;
    public readonly DeviceStore Devices;
    public readonly DiagnosticsService Diag;
    public readonly AdbService Adb;
    readonly Dispatcher _ui = Application.Current.Dispatcher;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    int _ticks; bool _polling, _refreshQueued;
    string? _ssid;

    public Loc L { get; } = new();
    public bool IsElevated { get; } = WinNet.IsElevated();
    public string ElevationText => IsElevated ? "Running as administrator" : "Standard user (limited)";
    public string Version { get; } = "v" + (typeof(ShellViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0");

    // ---- navigation ----
    string _page = "Connect";
    public string Page { get => _page; set { if (Set(ref _page, value)) { if (value == "Activity") UnreadAlerts = 0; if (value == "Settings") LoadEdit(); if (value == "History") RebuildHistory(); } } }
    public ICommand Navigate { get; }

    // ---- connection summary ----
    string _statusText = "", _buttonText = "", _methodText = "", _deviceText = "—", _durationText = "—", _vpnText = "—", _netText = "—", _ifaceText = "—", _detailText = "";
    Brush _statusBrush = Theme.Brush("Muted");
    OrbState _orb;
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public string ButtonText { get => _buttonText; set => Set(ref _buttonText, value); }
    public OrbState Orb { get => _orb; set => Set(ref _orb, value); }
    public string MethodText { get => _methodText; set => Set(ref _methodText, value); }
    public string DeviceText { get => _deviceText; set => Set(ref _deviceText, value); }
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }
    public string VpnText { get => _vpnText; set => Set(ref _vpnText, value); }
    public string NetText { get => _netText; set => Set(ref _netText, value); }
    public string IfaceText { get => _ifaceText; set => Set(ref _ifaceText, value); }
    public bool NeedsAdminBanner { get; private set; }
    public bool ShowAdminBanner { get => _showAdmin; private set => Set(ref _showAdmin, value); }
    bool _showAdmin;
    public bool CanChangeMethod => !Orch.IsBusy && !Orch.IsActive;
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    string _errorText = "";

    // ---- traffic ----
    string _down = "—", _up = "—", _totDown = "—", _totUp = "—", _tot = "—", _lat = "—", _loss = "—", _sig = "—", _uptime = "—", _conns = "—";
    double[] _dh = Array.Empty<double>(), _uh = Array.Empty<double>();
    public string DownText { get => _down; set => Set(ref _down, value); }
    public string UpText { get => _up; set => Set(ref _up, value); }
    public string TotDownText { get => _totDown; set => Set(ref _totDown, value); }
    public string TotUpText { get => _totUp; set => Set(ref _totUp, value); }
    public string TotText { get => _tot; set => Set(ref _tot, value); }
    public string LatencyText { get => _lat; set => Set(ref _lat, value); }
    public string LossText { get => _loss; set => Set(ref _loss, value); }
    public string SignalText { get => _sig; set => Set(ref _sig, value); }
    public string UptimeText { get => _uptime; set => Set(ref _uptime, value); }
    public string ConnsText { get => _conns; set => Set(ref _conns, value); }
    public double[] DownHistory { get => _dh; set => Set(ref _dh, value); }
    public double[] UpHistory { get => _uh; set => Set(ref _uh, value); }
    public string ClockText { get; private set; } = "";
    public bool TrafficLive { get => _live; private set => Set(ref _live, value); }
    bool _live;
    public string DataAlert { get => _alert; private set => Set(ref _alert, value); }
    string _alert = "";
    int _alertLevel;

    // ---- collections ----
    public ObservableCollection<MethodOption> Methods { get; } = new();
    public ObservableCollection<ChecklistItem> Checklist { get; } = new();
    public ObservableCollection<StageRow> Stages { get; } = new();
    public ObservableCollection<IndicatorRow> Indicators { get; } = new()
    {
        new IndicatorRow { Title = "Device connected" }, new IndicatorRow { Title = "Companion installed" }, new IndicatorRow { Title = "Companion running" },
        new IndicatorRow { Title = "Tunnel connected" }, new IndicatorRow { Title = "Windows network configured" }, new IndicatorRow { Title = "Internet verified" },
    };
    // ---- Wi-Fi sharing card (Connect page) ----
    public bool ShareEnabled => _demo || Settings.Current.ShareWifi;
    public string ShareSsid => Settings.Current.HotspotSsid;
    bool _showPass;
    public bool ShowSharePassword { get => _showPass; set { if (Set(ref _showPass, value)) Raise(nameof(SharePasswordDisplay)); } }
    public string SharePasswordDisplay { get { if (_demo) return "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022"; var p = Secret.Unprotect(Settings.Current.HotspotPasswordProtected); return p.Length == 0 ? "(not set)" : _showPass ? p : new string('\u2022', Math.Min(p.Length, 12)); } }
    public string ShareStateText { get => _shState; private set => Set(ref _shState, value); }
    string _shState = "";
    public Brush ShareStateBrush { get => _shBrush; private set => Set(ref _shBrush, value); }
    Brush _shBrush = Theme.Brush("Muted");
    public string ShareClientsText { get => _shClients; private set => Set(ref _shClients, value); }
    string _shClients = "";
    public bool CanStartSharing => Orch.CanShareNow && !Orch.IsSharing && Settings.Current.ShareWifi;
    public bool CanStopSharing => Orch.IsSharing;
    public string HotspotText { get => _hs; private set => Set(ref _hs, value); }
    string _hs = "";
    public string PhoneNetworkText { get => _pn; private set => Set(ref _pn, value); }
    string _pn = "—";
    public bool ShowStages { get => _showStages; private set => Set(ref _showStages, value); }
    bool _showStages;
    public ObservableCollection<ActivityEvent> Events { get; } = new();
    public ObservableCollection<DeviceRow> DeviceRows { get; } = new();
    public ObservableCollection<DeviceRow> KnownRows { get; } = new();
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    public ObservableCollection<DiagRow> DiagRows { get; } = new();
    public ObservableCollection<ToastRow> Toasts { get; } = new();
    public int UnreadAlerts { get => _unread; set => Set(ref _unread, value); }
    int _unread;
    string _activityFilter = "All";
    public string ActivityFilter { get => _activityFilter; set { if (Set(ref _activityFilter, value)) RebuildEvents(); } }
    public string[] ActivityFilters { get; } = { "All", "Success", "Info", "Warning", "Error", "Debug" };

    // ---- commands ----
    public ICommand ToggleConnect { get; }
    public ICommand SelectMethod { get; }
    public ICommand RefreshDevices { get; }
    public ICommand RestartAdb { get; }
    public ICommand RunAllDiag { get; }
    public ICommand ClearHistory { get; }
    public ICommand ClearActivity { get; }
    public ICommand SaveSettings { get; }
    public ICommand RestartAdmin { get; }
    public ICommand OpenData { get; }
    public ICommand OpenLink { get; }
    public ICommand GenPassword { get; }
    public ICommand DismissToast { get; }
    public ICommand ToggleShare { get; }
    public ICommand StartShare { get; }
    public ICommand StopShare { get; }
    public ICommand CopySharePassword { get; }
    public ICommand ToggleSharePasswordVisible { get; }
    public ICommand CheckUpdates { get; }
    public ICommand InstallUpdate { get; }

    // ---- settings editing ----
    public AppSettings Edit { get; private set; } = new();
    public string HotspotPassword { get => _hp; set => Set(ref _hp, value); }
    string _hp = "";
    public string[] Languages { get; } = { "en", "tr" };
    public string[] Themes { get; } = { "Graphite", "Midnight" };
    public string[] LogLevels { get; } = { "Debug", "Info", "Warning", "Error" };
    public TransportPref[] Transports { get; } = Enum.GetValues<TransportPref>();

    public ShellViewModel(Orchestrator o, SettingsStore s, HistoryStore h, DeviceStore d, DiagnosticsService diag, AdbService adb)
    {
        Orch = o; Settings = s; History = h; Devices = d; Diag = diag; Adb = adb;
        L.Language = s.Current.Language;
        Navigate = new RelayCommand(p => Page = p?.ToString() ?? "Connect");
        SelectMethod = new RelayCommand(p => { if (p is MethodOption m && CanChangeMethod) { Settings.Current.LastMethod = m.Method; Settings.Save(); RefreshAll(); _ = PollAsync(); } });
        ToggleConnect = new RelayCommand(OnToggle);
        RefreshDevices = new RelayCommand(async _ => { await Orch.RefreshDevicesAsync(); RebuildDevices(); RebuildChecklist(); });
        RestartAdb = new RelayCommand(async _ =>
        {
            if (!Dialogs.Confirm("Restart ADB", "This restarts the ADB server. An active connection that depends on ADB will be interrupted.", "Restart", Orch.IsActive)) return;
            var r = await Adb.RestartServerAsync(); Notifier.Show(r.Ok ? Severity.Success : Severity.Error, r.Ok ? "ADB server restarted." : "Could not restart ADB: " + r.Combined);
            await Orch.RefreshDevicesAsync(); RebuildDevices();
        });
        RunAllDiag = new RelayCommand(async _ => { foreach (var r in DiagRows) await RunDiag(r); });
        ClearHistory = new RelayCommand(_ => { if (Dialogs.Confirm("Clear history", "Delete all recorded sessions?", "Delete", true)) History.Clear(); });
        ClearActivity = new RelayCommand(_ => { Events.Clear(); });
        SaveSettings = new RelayCommand(OnSaveSettings);
        RestartAdmin = new RelayCommand(OnRestartAdmin);
        OpenData = new RelayCommand(_ => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDir) { UseShellExecute = true }));
        OpenLink = new RelayCommand(p => { if (p is string u && (u.StartsWith("https://") )) Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); });
        GenPassword = new RelayCommand(_ => HotspotPassword = Secret.GeneratePassphrase());
        ToggleShare = new RelayCommand(OnToggleShare);
        StartShare = new RelayCommand(async _ => { await Orch.StartSharingNowAsync(); RefreshAll(); });
        StopShare = new RelayCommand(async _ => { await Orch.StopSharingNowAsync(); RefreshAll(); });
        CopySharePassword = new RelayCommand(_ => { var p = Secret.Unprotect(Settings.Current.HotspotPasswordProtected); if (p.Length > 0) { Clipboard.SetText(p); Notifier.Show(Severity.Success, "Wi-Fi password copied to the clipboard."); } });
        ToggleSharePasswordVisible = new RelayCommand(_ => ShowSharePassword = !ShowSharePassword);
        CheckUpdates = new RelayCommand(_ => CheckForUpdatesAsync(manual: true));
        InstallUpdate = new RelayCommand(OnInstallUpdate);
        DismissToast = new RelayCommand(p => { if (p is ToastRow t) Toasts.Remove(t); });

        foreach (var (id, title) in DiagnosticsService.Tests)
        {
            var row = new DiagRow { Id = id, Title = title };
            row.Run = new RelayCommand(async _ => await RunDiag(row));
            DiagRows.Add(row);
        }
        BuildMethods();

        Orch.Changed += () => QueueRefresh();
        Log.Written += e => _ui.BeginInvoke(() =>
        {
            Events.Add(e); while (Events.Count > 500) Events.RemoveAt(0);
            if (e.Severity >= Severity.Warning && Page != "Activity") UnreadAlerts++;
            if (!Passes(e)) Events.Remove(e);
        });
        foreach (var e in Log.Snapshot()) Events.Add(e);
        Notifier.Shown += t => _ui.BeginInvoke(() => AddToast(t));
        History.Changed += () => _ui.BeginInvoke(RebuildHistory);
        Devices.Changed += () => _ui.BeginInvoke(RebuildDevices);
        Settings.Changed += () => _ui.BeginInvoke(() => { L.Language = Settings.Current.Language; Theme.Apply(Settings.Current.Theme); BuildMethods(); RefreshAll(); });

        var upd = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        upd.Tick += (_, _) => _ = CheckForUpdatesAsync(manual: false);
        upd.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first.Stop(); _ = CheckForUpdatesAsync(manual: false); };
        first.Start();
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        RefreshAll(); RebuildHistory(); RebuildDevices();
        _ = PollAsync();
    }

    bool Passes(ActivityEvent e) => _activityFilter == "All" || e.Severity.ToString() == _activityFilter;
    void RebuildEvents() { Events.Clear(); foreach (var e in Log.Snapshot().Where(Passes)) Events.Add(e); }

    void AddToast(Toast t)
    {
        var row = new ToastRow { Severity = t.Severity, Text = t.Text };
        Toasts.Add(row); while (Toasts.Count > 3) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(t.Severity == Severity.Error ? 9 : 5) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(row); };
        timer.Start();
    }

    void BuildMethods()
    {
        var sel = Settings.Current.LastMethod;
        Methods.Clear();
        Methods.Add(new MethodOption { Method = ConnectionMethod.PhoneVpn, Title = L["m.vpn"], Description = L["m.vpn.d"], IconKey = "IconShield", Recommended = true });
        Methods.Add(new MethodOption { Method = ConnectionMethod.Usb, Title = L["m.usb"], Description = L["m.usb.d"], IconKey = "IconUsb" });
        Methods.Add(new MethodOption { Method = ConnectionMethod.AndroidHotspot, Title = L["m.and"], Description = L["m.and.d"], IconKey = "IconWifi" });
        Methods.Add(new MethodOption { Method = ConnectionMethod.IphoneHotspot, Title = L["m.ios"], Description = L["m.ios.d"], IconKey = "IconApple" });
        foreach (var m in Methods) m.Selected = m.Method == sel;
    }

    // ---- toggle ----
    async Task OnToggle(object? _)
    {
        if (Orch.IsBusy && Orch.State != ConnState.Reconnecting) { if (Orch.State != ConnState.Disconnecting) await Orch.DisconnectAsync(); return; }
        if (Orch.State == ConnState.Reconnecting) { await Orch.DisconnectAsync(); return; }
        if (Orch.IsActive) { await Orch.DisconnectAsync(); return; }
        var m = Settings.Current.LastMethod;
        if (m is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb && !IsElevated)
        {
            if (Dialogs.Confirm("Administrator rights needed", "Creating the virtual adapter, routes and DNS requires administrator rights. Restart CiciPDA as administrator now?", "Restart as administrator", false)) OnRestartAdmin(null);
            return;
        }
        await Orch.ConnectAsync(m);
    }

    /// <summary>Quick on/off switch for Wi-Fi sharing. Turning it on confirms first and makes sure a valid passphrase exists.</summary>
    async Task OnToggleShare(object? arg)
    {
        var c = Settings.Current;
        if (c.ShareWifi)
        {
            if (Orch.IsSharing) await Orch.StopSharingNowAsync();
            c.ShareWifi = false; Settings.Save(); RefreshAll(); return;
        }
        if (!Dialogs.Confirm("Share this connection over Wi-Fi?",
            $"CiciPDA will start the Windows Mobile Hotspot \"{c.HotspotSsid}\" and share ONLY the CiciPDA connection after it has been verified. Other devices can then join it. Your current hotspot settings are saved and restored afterwards.", "Turn on", true)) return;
        if (Secret.Unprotect(c.HotspotPasswordProtected).Length < 8) c.HotspotPasswordProtected = Secret.Protect(Secret.GeneratePassphrase());
        c.ShareWifi = true; Settings.Save(); RefreshAll();
        if (Orch.CanShareNow) { await Orch.StartSharingNowAsync(); RefreshAll(); }
    }

    void OnRestartAdmin(object? arg)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { Notifier.Show(Severity.Warning, "Elevation was cancelled."); }
    }

    // ---- settings ----
    void LoadEdit()
    {
        var c = Settings.Current;
        Edit = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(c, Json.Opts), Json.Opts)!;
        HotspotPassword = Secret.Unprotect(c.HotspotPasswordProtected);
        Raise(nameof(Edit));
    }

    void OnSaveSettings(object? arg)
    {
        var c = Settings.Current; var e = Edit;
        if (!System.Net.IPAddress.TryParse(e.DnsPrimary, out _)) { Notifier.Show(Severity.Error, "Primary DNS is not a valid IP address."); return; }
        if (!string.IsNullOrWhiteSpace(e.DnsSecondary) && !System.Net.IPAddress.TryParse(e.DnsSecondary, out _)) { Notifier.Show(Severity.Error, "Secondary DNS is not a valid IP address."); return; }
        if (e.Mtu is < 576 or > 1500) { Notifier.Show(Severity.Error, "MTU must be between 576 and 1500."); return; }
        if (e.ShareWifi && (HotspotPassword.Length is < 8 or > 63 || e.HotspotSsid.Length is < 1 or > 32)) { Notifier.Show(Severity.Error, "Wi-Fi sharing needs an SSID (1-32) and a passphrase (8-63 characters)."); return; }
        if (!string.IsNullOrWhiteSpace(e.AdbPath) && !File.Exists(e.AdbPath)) { Notifier.Show(Severity.Error, "The adb path does not exist."); return; }

        var risky = new List<string>();
        if (e.DnsPrimary != c.DnsPrimary || e.DnsSecondary != c.DnsSecondary) risky.Add("DNS servers");
        if (e.Mtu != c.Mtu) risky.Add("MTU");
        if (e.Ipv6LeakProtection != c.Ipv6LeakProtection) risky.Add("IPv6 routing");
        if (e.ShareWifi && !c.ShareWifi) risky.Add("Wi-Fi sharing (starts a hotspot)");
        if (e.AdbPath != c.AdbPath) risky.Add("adb location");
        if (risky.Count > 0 && !Dialogs.Confirm("Apply network changes?", "These settings change how CiciPDA configures Windows networking: " + string.Join(", ", risky) + ". They take effect on the next connection.", "Apply", true)) return;

        e.HotspotPasswordProtected = Secret.Protect(HotspotPassword);
        e.LastMethod = c.LastMethod; e.PreferredDeviceSerial = c.PreferredDeviceSerial;
        foreach (var p in typeof(AppSettings).GetProperties()) p.SetValue(c, p.GetValue(e));
        Settings.Save();
        Notifier.Show(Severity.Success, "Settings saved.");
    }

    // ---- diagnostics ----
    async Task RunDiag(DiagRow r)
    {
        r.Running = true; r.State = ItemState.Pending; r.Detail = "Running…"; r.Fix = "";
        try
        {
            var res = await Task.Run(() => Diag.RunAsync(r.Id));
            r.State = res.Passed ? ItemState.Ok : ItemState.Fail; r.Detail = res.Detail; r.Fix = res.Fix ?? ""; r.Ms = res.Ms + " ms";
        }
        finally { r.Running = false; }
    }

    // ---- periodic ----
    void OnTick()
    {
        _ticks++;
        ClockText = DateTime.Now.ToString("HH:mm:ss"); Raise(nameof(ClockText));
        UpdateTraffic();
        if (Orch.ConnectedAt is { } t) DurationText = Fmt.Duration(DateTimeOffset.Now - t);
        if (_ticks % 3 == 0) _ = PollAsync();
    }

    async Task PollAsync()
    {
        if (_demo) return;
        if (_polling || Orch.IsBusy) return;
        _polling = true;
        try
        {
            if (!Orch.IsActive) await Orch.RefreshDevicesAsync();
            var m = Settings.Current.LastMethod;
            if (m is ConnectionMethod.AndroidHotspot or ConnectionMethod.IphoneHotspot && !Orch.IsActive) _ssid = await Orchestrator.CurrentSsidAsync();
            RebuildChecklist(); RebuildDevices(); RefreshAll();
        }
        catch (Exception ex) { Log.Debug("ui", "poll: " + ex.Message); }
        finally { _polling = false; }
    }

    void QueueRefresh()
    {
        if (_refreshQueued) return; _refreshQueued = true;
        _ui.BeginInvoke(() => { _refreshQueued = false; RefreshAll(); RebuildChecklist(); RebuildDevices(); });
    }

    void UpdateTraffic()
    {
        if (_demo) return;
        var t = Orch.Traffic;
        TrafficLive = Orch.IsActive;
        if (!Orch.IsActive)
        {
            DownText = UpText = TotDownText = TotUpText = TotText = LatencyText = LossText = SignalText = UptimeText = ConnsText = "—";
            DownHistory = UpHistory = Array.Empty<double>(); return;
        }
        DownText = Fmt.Rate(t.DownBps); UpText = Fmt.Rate(t.UpBps);
        TotDownText = Fmt.Bytes(t.TotalDown); TotUpText = Fmt.Bytes(t.TotalUp); TotText = Fmt.Bytes(t.TotalDown + t.TotalUp);
        LatencyText = t.LatencyMs is { } l ? $"{l:0} ms" : "measuring…";
        LossText = t.LossPct is { } p ? $"{p:0}%" : "measuring…";
        SignalText = t.SignalLevel is { } s ? $"{s}/4{(t.NetworkType != null ? " · " + t.NetworkType : "")}" : "not available";
        UptimeText = Fmt.Duration(t.Uptime);
        ConnsText = t.Connections.ToString();
        DownHistory = t.DownHistory; UpHistory = t.UpHistory;

        long mb = (t.TotalDown + t.TotalUp) / 1048576; var c = Settings.Current;
        int lvl = mb >= c.DataCriticalMb && c.DataCriticalMb > 0 ? 2 : mb >= c.DataWarningMb && c.DataWarningMb > 0 ? 1 : 0;
        if (lvl > _alertLevel) { Notifier.Show(lvl == 2 ? Severity.Error : Severity.Warning, $"Data usage this session reached {Fmt.Bytes(t.TotalDown + t.TotalUp)}."); Log.Warn("usage", $"Data usage threshold {(lvl == 2 ? "critical" : "warning")} reached."); }
        _alertLevel = Orch.IsActive ? lvl : 0;
        DataAlert = lvl == 2 ? "Critical data usage" : lvl == 1 ? "High data usage" : "";
    }

    // ---- snapshot of orchestrator into properties ----
    public void RefreshAll()
    {
        if (_demo) return;
        var st = Orch.State;
        StatusText = L["st." + st];
        StatusBrush = st switch
        {
            ConnState.Connected => Theme.Brush("Green"), ConnState.Error => Theme.Brush("Red"), ConnState.Limited => Theme.Brush("Amber"),
            ConnState.Offline => Theme.Brush("Muted"), _ => Theme.Brush("Cyan"),
        };
        Orb = st switch
        {
            ConnState.Connected or ConnState.Limited => OrbState.Connected, ConnState.Error => OrbState.Error,
            ConnState.DetectingDevice or ConnState.EstablishingTunnel => OrbState.Preparing, ConnState.Connecting or ConnState.Reconnecting => OrbState.Connecting,
            ConnState.Disconnecting => OrbState.Disconnecting, _ => OrbState.Disconnected,
        };
        ButtonText = Orch.IsActive ? L["btn.disconnect"] : Orch.IsBusy ? L["btn.cancel"] : L["btn.connect"];
        DetailText = st == ConnState.Error ? Orch.LastError ?? Orch.Detail : Orch.Detail;
        ErrorText = st == ConnState.Error ? Orch.LastError ?? "" : "";
        MethodText = MethodName(Orch.IsActive || Orch.IsBusy ? Orch.Method : Settings.Current.LastMethod);
        DeviceText = string.IsNullOrEmpty(Orch.DeviceName) ? (Orch.Devices.FirstOrDefault(d => d.IsAuthorized)?.DisplayName ?? "—") : Orch.DeviceName;
        VpnText = Orch.Vpn switch { PathState.Up => "Active (encrypted)", PathState.Starting => "Starting…", PathState.Failed => "Failed", _ => Orch.IsActive && Orch.Method is ConnectionMethod.AndroidHotspot or ConnectionMethod.IphoneHotspot ? "Not used" : "Off" };
        NetText = Orch.Internet switch { NetState.Reachable => "Reachable (verified)", NetState.Limited => "Limited", NetState.Unreachable => "Unreachable", _ => "—" };
        IfaceText = string.IsNullOrEmpty(Orch.InterfaceName) ? "—" : Orch.InterfaceName;
        if (!Orch.IsActive) DurationText = "—";
        ShowAdminBanner = !IsElevated && Settings.Current.LastMethod is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb;
        Raise(nameof(CanChangeMethod)); Raise(nameof(IsActive)); Raise(nameof(IsBusy));
        foreach (var m in Methods) m.Selected = m.Method == Settings.Current.LastMethod;
        SyncStages();
        UpdateAvailability();
        Raise(nameof(GlobalNet)); Raise(nameof(GlobalVpn)); Raise(nameof(GlobalType)); Raise(nameof(GlobalUsage));
    }

    // ---- application updates ----
    readonly AppUpdater _updater = new();
    UpdateInfo? _update;
    string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool IsCheckingUpdates { get => _checking; private set => Set(ref _checking, value); }
    bool _checking;
    public bool UpdateAvailable { get => _updateAvail; private set => Set(ref _updateAvail, value); }
    bool _updateAvail;
    // Update progress shown in the header pill. While busy the pill is disabled so the button cannot be pressed twice.
    public bool UpdateBusy { get => _updBusy; private set { if (Set(ref _updBusy, value)) { Raise(nameof(UpdateBanner)); Raise(nameof(UpdateTooltip)); } } }
    bool _updBusy;
    public double UpdateProgress { get => _updProgress; private set => Set(ref _updProgress, value); }
    double _updProgress;
    public bool UpdateIndeterminate { get => _updIndet; private set => Set(ref _updIndet, value); }
    bool _updIndet;
    public string UpdateChip { get => _updChip; private set => Set(ref _updChip, value); }
    string _updChip = "Update";
    string _updPhase = "";
    string UpdateVersion => _update?.Manifest.Version ?? "";
    public string UpdateBanner => _demo && !UpdateBusy ? "CiciPDA 2.2.0 is available" : _update == null ? "" : UpdateBusy ? $"{_updPhase} CiciPDA {UpdateVersion}" : $"CiciPDA {UpdateVersion} is available";
    public string UpdateTooltip => UpdateBusy ? "The update is in progress - please wait" : "Download and install the update";

    void SetUpdatePhase(string phase, string chip, double progress, bool indeterminate)
    {
        _updPhase = phase; UpdateChip = chip; UpdateProgress = progress; UpdateIndeterminate = indeterminate; Raise(nameof(UpdateBanner));
    }
    public string CurrentVersionText => AppUpdater.CurrentVersion.ToString();

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && !Settings.Current.AutoCheckUpdates) return;
        if (IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        try
        {
            UpdateStatus = "Checking for updates...";
            _update = await _updater.CheckAsync(CancellationToken.None);
            UpdateAvailable = _update != null; Raise(nameof(UpdateBanner));
            UpdateStatus = _update == null ? "You have the latest version." : $"Version {_update.Manifest.Version} is available. {_update.Manifest.Notes}";
            if (_update != null) Log.Info("update", "Update available: " + _update.Manifest.Version);
            if (manual) Notifier.Show(_update == null ? Severity.Success : Severity.Info, _update == null ? $"CiciPDA is up to date ({CurrentVersionText})." : $"CiciPDA {_update.Manifest.Version} is available. Click Update in the header to install it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateStatus = manual ? "Could not check for updates: " + (ex is HttpRequestException ? "no connection to the release server (or no release published yet)." : ex.Message) : "";
            Log.Info("update", "Update check failed: " + ex.Message);
            if (manual) Notifier.Show(Severity.Warning, UpdateStatus);
        }
        finally { IsCheckingUpdates = false; }
    }

    async Task OnInstallUpdate(object? arg)
    {
        if (_update == null || UpdateBusy) return;                       // a second click while busy does nothing
        string warn = Orch.IsActive || Orch.IsBusy ? " Your connection will be closed first and your network settings restored." : "";
        if (!Dialogs.Confirm("Install update", $"Download and install CiciPDA {_update.Manifest.Version}? The signed installer is verified before it runs.{warn}", "Install", false)) return;
        if (UpdateBusy) return;
        UpdateBusy = true;
        try
        {
            SetUpdatePhase("Downloading", "0%", 0, false);
            UpdateStatus = "Downloading...";
            var path = await _updater.DownloadAsync(_update, new Progress<double>(p =>
            {
                UpdateStatus = $"Downloading... {p:P0}";
                SetUpdatePhase("Downloading", $"{p:P0}", p, false);
            }), CancellationToken.None);
            SetUpdatePhase("Verifying", "Verifying", 1, true);          // the signed SHA-256 was just checked inside DownloadAsync
            UpdateStatus = "Verified. Closing CiciPDA and starting the installer...";
            SetUpdatePhase("Installing", "Installing", 1, true);
            await Orch.ShutdownAsync();
            AppUpdater.LaunchInstaller(path);
            Application.Current.Shutdown();                              // stays busy: the application is closing
        }
        catch
        {
            UpdateBusy = false; SetUpdatePhase("", "Update", 0, false);   // let the user retry after an error
            UpdateStatus = "The update failed. Try again.";
            throw;
        }
    }

    bool _demo;
    /// <summary>Development preview only (--preview ... --demo): static sample values for screenshots. Never used in normal runs.</summary>
    public void ApplyDemo()
    {
        _demo = true; _tick.Stop();
        StatusText = L["st.Connected"]; StatusBrush = Theme.Brush("Green"); Orb = OrbState.Connected; ButtonText = L["btn.disconnect"]; DetailText = "Internet verified";
        MethodText = "Phone VPN"; DeviceText = "Pixel 7"; DurationText = "12:48"; VpnText = "Active (encrypted)"; NetText = "Reachable (verified)"; IfaceText = "CiciPDA";
        DownText = "4.82 MB/s"; UpText = "612 KB/s"; TotDownText = "1.24 GB"; TotUpText = "88.3 MB"; TotText = "1.33 GB"; LatencyText = "62 ms"; LossText = "0%"; SignalText = "3/4 · LTE"; UptimeText = "12:48"; ConnsText = "37"; TrafficLive = true;
        var rnd = new Random(7); var d = new double[120]; var u = new double[120];
        for (int i = 0; i < 120; i++) { d[i] = 2e6 + 2.5e6 * Math.Abs(Math.Sin(i / 9.0)) + rnd.NextDouble() * 6e5; u[i] = 2e5 + 3e5 * Math.Abs(Math.Sin(i / 6.0 + 1)) + rnd.NextDouble() * 8e4; }
        DownHistory = d; UpHistory = u; ClockText = "14:32:07";
        foreach (var i in Indicators) i.On = true;
        PhoneNetworkText = "cellular, validated, metered";
        string[] det = { "Pixel 7 (USB)", "USB debugging authorized", "Installed 2.0.0 (UpToDate); Android 14", "Already up to date", "Companion service running", "Encrypted channel up; phone reached the internet in 48 ms", "Wintun adapter up", "Routes, DNS and MTU configured", "Verified" };
        Stages.Clear();
        var titles = new[] { "Detect phone", "Check USB debugging authorization", "Check Android companion", "Install / update companion", "Start companion service", "Establish secure tunnel", "Create virtual adapter", "Configure routes and DNS", "Verify internet access" };
        for (int i = 0; i < titles.Length; i++) Stages.Add(new StageRow { Title = titles[i], Detail = det[i], State = ItemState.Ok, Skipped = i == 3 });
        ShareStateText = "A connected device confirmed DNS and HTTPS. Sharing works."; ShareStateBrush = Theme.Brush("Green"); ShareClientsText = "1 device(s) connected"; ShowSharePassword = false;
        UpdateAvailable = true;
        if (Environment.GetCommandLineArgs().Contains("--demo-update")) { _update = new UpdateInfo(new UpdateManifest { Version = "2.2.0" }, new Version(2, 2, 0), new Uri("https://github.com/")); UpdateBusy = true; SetUpdatePhase("Downloading", "42%", 0.42, false); }
        Raise(nameof(UpdateBanner));
        ShowStages = true; ShowAdminBanner = false;
        Checklist.Clear();
        foreach (var (t, h) in new[] { ("ADB is available", "Bundled with CiciPDA"), ("Connect the phone (USB, or ADB over Wi-Fi)", "1 device(s) seen by ADB"), ("Enable USB debugging and allow this computer", "Authorized: Pixel 7"), ("Administrator rights", "Running elevated"), ("Tunnel components present", "Wintun, tun2socks and phone helper found") })
            Checklist.Add(new ChecklistItem { Title = t, Hint = h, State = ItemState.Ok });
        Raise(nameof(GlobalType)); Raise(nameof(GlobalVpn)); Raise(nameof(GlobalNet)); Raise(nameof(GlobalUsage));
        foreach (var m in Methods) { m.Selected = m.Method == ConnectionMethod.PhoneVpn; m.Availability = m.Method <= ConnectionMethod.Usb ? "Device ready" : "Not detected"; m.AvailState = m.Method <= ConnectionMethod.Usb ? ItemState.Ok : ItemState.Warn; }
    }

    void SyncStages()
    {
        var st = Orch.Stages;
        while (Stages.Count < st.Count) Stages.Add(new StageRow());
        for (int i = 0; i < st.Count; i++)
        {
            var r = Stages[i]; var x = st[i];
            r.Title = x.Title; r.Detail = x.Detail; r.Active = x.State == StageState.Active; r.Skipped = x.State == StageState.Skipped;
            r.State = x.State switch { StageState.Done => ItemState.Ok, StageState.Warn => ItemState.Warn, StageState.Failed => ItemState.Fail, _ => ItemState.Pending };
        }
        var ind = Orch.Indicators;
        Indicators[0].On = ind.DeviceConnected; Indicators[1].On = ind.CompanionInstalled; Indicators[2].On = ind.CompanionRunning;
        Indicators[3].On = ind.TunnelConnected; Indicators[4].On = ind.NetworkConfigured; Indicators[5].On = ind.InternetVerified;
        PhoneNetworkText = string.IsNullOrEmpty(Orch.PhoneNetwork) ? "—" : Orch.PhoneNetwork;
        var hs = Orch.Hotspot;
        Raise(nameof(ShareEnabled)); Raise(nameof(ShareSsid)); Raise(nameof(SharePasswordDisplay)); Raise(nameof(CanStartSharing)); Raise(nameof(CanStopSharing));
        var h = Orch.Hotspot;
        (ShareStateText, ShareStateBrush) = !Settings.Current.ShareWifi ? ("Off. Turn it on to let other devices use this connection through Wi-Fi.", Theme.Brush("Muted"))
            : h.State switch
            {
                HotspotState.Active => ($"Active. Join \"{Settings.Current.HotspotSsid}\" from the other device. Waiting for a device...", Theme.Brush("Green")),
                HotspotState.ClientConnected => ("A device is connected. Open the check page on it to confirm its internet.", Theme.Brush("Green")),
                HotspotState.ClientInternetVerified => ("A connected device confirmed DNS and HTTPS. Sharing works.", Theme.Brush("Green")),
                HotspotState.Starting => ("Starting the Windows hotspot...", Theme.Brush("Cyan")),
                HotspotState.Failed => ("Could not start: " + h.Detail, Theme.Brush("Red")),
                HotspotState.Unavailable when !string.IsNullOrEmpty(h.Detail) => ("Not available: " + h.Detail, Theme.Brush("Amber")),
                _ => (Orch.CanShareNow ? "On. Press Start sharing, or reconnect." : "On. Sharing starts automatically after the connection is verified.", Theme.Brush("Amber")),
            };
        ShareClientsText = h.State is HotspotState.ClientConnected or HotspotState.ClientInternetVerified ? $"{h.ClientCount} device(s) connected" : (h.ProbeUrl != null ? "Check page for a connected device: " + h.ProbeUrl : "");
        HotspotText = !Settings.Current.ShareWifi && hs.State == HotspotState.Unavailable ? "" : hs.State switch
        {
            HotspotState.Unavailable => "Wi-Fi sharing: not available — " + hs.Detail, HotspotState.Available => "Wi-Fi sharing: available",
            HotspotState.Starting => "Wi-Fi sharing: starting…", HotspotState.Active => "Wi-Fi sharing: hotspot active — " + hs.Detail,
            HotspotState.ClientConnected => "Wi-Fi sharing: client connected — " + hs.Detail,
            HotspotState.ClientInternetVerified => "Wi-Fi sharing: client internet verified — " + hs.Detail,
            _ => "Wi-Fi sharing failed: " + hs.Detail,
        };
        ShowStages = Orch.IsBusy || Orch.IsActive || Orch.State == ConnState.Error;
    }

    public bool IsActive => Orch.IsActive;
    public bool IsBusy => Orch.IsBusy;
    public string GlobalType => _demo ? "Phone VPN" : Orch.IsActive || Orch.IsBusy ? MethodName(Orch.Method) : "—";
    public string GlobalVpn => _demo ? "VPN: on" : Orch.Vpn == PathState.Up ? "VPN: on" : "VPN: off";
    public string GlobalNet => _demo ? "Network: online" : Orch.Internet switch { NetState.Reachable => "Network: online", NetState.Limited => "Network: limited", _ => "Network: —" };
    public string GlobalUsage => _demo ? "1.33 GB" : Orch.IsActive ? Fmt.Bytes(Orch.SessionBytes) : "—";

    static string MethodName(ConnectionMethod m) => m switch { ConnectionMethod.PhoneVpn => "Phone VPN", ConnectionMethod.Usb => "USB", ConnectionMethod.AndroidHotspot => "Android hotspot", _ => "iPhone hotspot" };

    void UpdateAvailability()
    {
        if (_demo) return;
        bool tools = File.Exists(AppPaths.Tun2Socks) && File.Exists(AppPaths.WintunDll) && File.Exists(AppPaths.AgentDex) && !Orch.AdbMissing;
        foreach (var m in Methods)
        {
            switch (m.Method)
            {
                case ConnectionMethod.PhoneVpn or ConnectionMethod.Usb:
                    if (!tools) { m.Availability = "Tools missing"; m.AvailState = ItemState.Fail; m.Note = "adb, Wintun, tun2socks or the helper is missing."; }
                    else if (!IsElevated) { m.Availability = "Needs administrator"; m.AvailState = ItemState.Warn; m.Note = "Will ask to restart elevated."; }
                    else if (Orch.PickDevice(m.Method, null) != null) { m.Availability = "Device ready"; m.AvailState = ItemState.Ok; m.Note = ""; }
                    else { m.Availability = "No device"; m.AvailState = ItemState.Warn; m.Note = ""; }
                    break;
                case ConnectionMethod.AndroidHotspot:
                    m.Availability = _ssid != null ? "Wi-Fi: " + _ssid : "Join hotspot"; m.AvailState = _ssid != null ? ItemState.Ok : ItemState.Warn; break;
                default:
                    var apple = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.Description.Contains("Apple Mobile Device", StringComparison.OrdinalIgnoreCase));
                    m.Availability = apple ? "USB adapter found" : _ssid != null ? "Wi-Fi: " + _ssid : "Not detected"; m.AvailState = apple || _ssid != null ? ItemState.Ok : ItemState.Warn; break;
            }
        }
    }

    // ---- dynamic checklist ----
    void RebuildChecklist()
    {
        if (_demo) return;
        var m = Settings.Current.LastMethod; var items = new List<ChecklistItem>();
        if (m is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb)
        {
            var devs = Orch.Devices;
            var auth = devs.FirstOrDefault(d => d.IsAuthorized); var unauth = devs.FirstOrDefault(d => d.State == "unauthorized");
            items.Add(new ChecklistItem { Title = "ADB is available", State = Orch.AdbMissing ? ItemState.Fail : ItemState.Ok, Hint = Orch.AdbMissing ? "adb.exe was not found. Set its path in Settings." : Adb.AdbPath ?? "" });
            items.Add(new ChecklistItem { Title = m == ConnectionMethod.Usb ? "Connect a data-capable USB cable" : "Connect the phone (USB, or ADB over Wi-Fi)", State = devs.Count > 0 ? ItemState.Ok : ItemState.Warn, Hint = devs.Count > 0 ? $"{devs.Count} device(s) seen by ADB" : "No device detected. Try another cable or port; some cables are charge-only." });
            items.Add(new ChecklistItem { Title = "Enable USB debugging and allow this computer", State = auth != null ? ItemState.Ok : unauth != null ? ItemState.Fail : ItemState.Pending,
                Hint = auth != null ? $"Authorized: {auth.DisplayName}" : unauth != null ? "The phone shows an Allow prompt — unlock it and tap Allow (tick Always allow)." : "Settings → Developer options → USB debugging. Tap the build number 7 times to reveal Developer options." });
            items.Add(new ChecklistItem { Title = "Administrator rights", State = IsElevated ? ItemState.Ok : ItemState.Warn, Hint = IsElevated ? "Running elevated" : "Needed for the virtual adapter. You will be asked to restart as administrator." });
            bool t = File.Exists(AppPaths.Tun2Socks) && File.Exists(AppPaths.WintunDll) && File.Exists(AppPaths.AgentDex);
            items.Add(new ChecklistItem { Title = "Tunnel components present", State = t ? ItemState.Ok : ItemState.Fail, Hint = t ? "Wintun, tun2socks and phone helper found" : "Run fetch-tools.ps1 to download wintun.dll and tun2socks.exe." });
        }
        else if (m == ConnectionMethod.AndroidHotspot)
        {
            items.Add(new ChecklistItem { Title = "Turn on the hotspot on the phone", State = ItemState.Pending, Hint = "Settings → Network → Hotspot & tethering → Wi-Fi hotspot." });
            items.Add(new ChecklistItem { Title = "Join it from the Windows Wi-Fi menu", State = _ssid != null ? ItemState.Ok : ItemState.Warn, Hint = _ssid != null ? "Connected to " + _ssid : "Windows is not connected to any Wi-Fi network." });
            items.Add(new ChecklistItem { Title = "Internet is verified on Connect", State = ItemState.Pending, Hint = "CiciPDA checks DNS, HTTP and HTTPS before reporting Connected." });
        }
        else
        {
            items.Add(new ChecklistItem { Title = "Turn on Personal Hotspot on the iPhone", State = ItemState.Pending, Hint = "Settings → Personal Hotspot → Allow Others to Join." });
            items.Add(new ChecklistItem { Title = "Join by Wi-Fi, or connect by USB and tap Trust", State = _ssid != null ? ItemState.Ok : ItemState.Warn, Hint = _ssid != null ? "Wi-Fi: " + _ssid : "USB needs Apple's drivers (Apple Devices / iTunes) installed." });
            items.Add(new ChecklistItem { Title = "Limitation", State = ItemState.Pending, Hint = "No tunnel is created for iPhone: CiciPDA verifies and monitors the connection Windows already has." });
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (i < Checklist.Count) { var c = Checklist[i]; c.Title = items[i].Title; c.State = items[i].State; c.Hint = items[i].Hint; }
            else Checklist.Add(items[i]);
        }
        while (Checklist.Count > items.Count) Checklist.RemoveAt(Checklist.Count - 1);
    }

    void RebuildDevices()
    {
        DeviceRows.Clear(); KnownRows.Clear();
        foreach (var d in Orch.Devices)
            DeviceRows.Add(new DeviceRow
            {
                Serial = d.Serial, Name = d.DisplayName, Transport = d.TransportName, Connected = true, Trusted = d.IsAuthorized,
                Trust = d.IsAuthorized ? "Authorized" : d.State == "unauthorized" ? "Not authorized" : d.State,
                Status = Orch.Device?.Serial == d.Serial && Orch.IsActive ? "In use" : "Connected", LastSeen = "now",
                Preferred = Settings.Current.PreferredDeviceSerial == d.Serial,
                Prefer = new RelayCommand(_ => { Settings.Current.PreferredDeviceSerial = d.Serial; Settings.Save(); RebuildDevices(); }),
                Unpair = new RelayCommand(async _ =>
                {
                    if (!Dialogs.Confirm("Unpair companion", $"Make {d.DisplayName} forget this PC? The companion stops and deletes its stored key. Any active connection is closed. The next Connect pairs again automatically.", "Unpair", true)) return;
                    await Orch.UnpairAsync(d.Serial);
                    Notifier.Show(Severity.Success, "Companion pairing removed.");
                }),
            });
        foreach (var k in Devices.Items.Where(k => Orch.Devices.All(d => d.Serial != k.Serial)))
            KnownRows.Add(new DeviceRow
            {
                Serial = k.Serial, Name = string.IsNullOrWhiteSpace(k.Name) ? k.Serial : k.Name, Transport = k.Transport, Trusted = k.Trusted,
                Trust = k.Trusted ? "Was authorized" : "Never authorized", Status = "Not connected", LastSeen = Fmt.Ago(k.LastSeen),
                Forget = new RelayCommand(_ => { if (Dialogs.Confirm("Forget device", $"Remove {k.Name} from the list of known devices? (This does not revoke USB debugging on the phone.)", "Forget", true)) Devices.Forget(k.Serial); }),
            });
        Raise(nameof(HasDevices)); Raise(nameof(HasKnown));
    }
    public bool HasDevices => DeviceRows.Count > 0;
    public bool HasKnown => KnownRows.Count > 0;

    void RebuildHistory()
    {
        Sessions.Clear();
        foreach (var s in History.Items)
            Sessions.Add(new SessionRow
            {
                Start = s.Start.ToString("yyyy-MM-dd HH:mm"), End = s.End.ToString("HH:mm"), Method = MethodName(s.Method), Device = s.Device,
                Duration = Fmt.Duration(s.Duration), Data = $"↓ {Fmt.Bytes(s.BytesDown)}  ↑ {Fmt.Bytes(s.BytesUp)}", Result = s.Result, Error = s.Error ?? "",
                ResultState = s.Result == "Completed" ? ItemState.Ok : s.Result == "Interrupted" ? ItemState.Warn : ItemState.Fail,
            });
        Raise(nameof(HasSessions));
    }
    public bool HasSessions => Sessions.Count > 0;
}

public static class Fmt
{
    public static string Bytes(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:0.0} KB" : b < 1073741824 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1073741824.0:0.00} GB";
    public static string Rate(double b) => b < 1024 ? $"{b:0} B/s" : b < 1048576 ? $"{b / 1024:0.0} KB/s" : $"{b / 1048576:0.00} MB/s";
    public static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    public static string Ago(DateTimeOffset t) { var d = DateTimeOffset.Now - t; return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : t.ToString("yyyy-MM-dd"); }
}
