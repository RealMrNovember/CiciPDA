using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CiciPDA;

public enum HotspotState { Unavailable, Available, Starting, Active, ClientConnected, ClientInternetVerified, Failed }

public sealed record HotspotCheck(bool Found, string Profile, string Capability, string Detail)
{
    public bool CanShare => Found && Capability == "Enabled";
}

/// <summary>
/// Wi-Fi sharing through Windows Mobile Hotspot, bound to ONE explicitly selected upstream interface.
/// The upstream is matched by adapter GUID against the connection profiles Windows reports; GetInternetConnectionProfile()
/// (whatever Windows currently prefers, possibly an unrelated Ethernet/Wi-Fi) is never used.
/// </summary>
public sealed class HotspotService
{
    // Shared preamble: loads WinRT and defines Find(profile by adapter GUID).
    const string Pre = @"
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
$null=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
$null=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
$null=[Windows.Networking.NetworkOperators.TetheringWiFiBand,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
$asTask=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0]
function Await($op,$t){$m=$asTask.MakeGenericMethod($t);$x=$m.Invoke($null,@($op));$x.Wait(-1)|Out-Null;$x.Result}
$asTaskA=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'})[0]
function AwaitA($op){$x=$asTaskA.Invoke($null,@($op));$x.Wait(-1)|Out-Null}
$want=''; if($env:CICI_ADAPTER){ $want=([guid]$env:CICI_ADAPTER).ToString().ToLower() }
$prof=$null
foreach($p in [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles()){
  if($want -ne '' -and $p.NetworkAdapter -ne $null -and $p.NetworkAdapter.NetworkAdapterId.ToString().ToLower() -eq $want){$prof=$p;break}
}
# Recovery only (CICI_ANY=1): the upstream adapter no longer exists, but the hotspot configuration is system-wide, so any profile can reach it.
# Ownership is still enforced by comparing the current SSID with the one CiciPDA configured.
if($null -eq $prof -and $env:CICI_ANY -eq '1'){ foreach($p in [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles()){ if([Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($p).ToString() -eq 'Enabled'){$prof=$p;break} } }
";

    const string CheckScript = Pre + @"
if($null -eq $prof){ '{""found"":false,""profile"":"""",""capability"":""NoProfile""}'; exit 0 }
$cap=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($prof).ToString()
$state='Unknown'
if($cap -eq 'Enabled'){ $m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof); $state=$m.TetheringOperationalState.ToString() }
(@{found=$true;profile=$prof.ProfileName;capability=$cap;state=$state}|ConvertTo-Json -Compress)
";

    // Read-only: the hotspot configuration as it is BEFORE CiciPDA touches it.
    const string ReadScript = Pre + @"
if($null -eq $prof){ throw 'NOPROFILE' }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
$cur=$m.GetCurrentAccessPointConfiguration()
$b='Auto'; try{ $b=$cur.Band.ToString() }catch{}
(@{ssid=$cur.Ssid;pass=$cur.Passphrase;band=$b;state=$m.TetheringOperationalState.ToString()}|ConvertTo-Json -Compress)
";

    const string StartScript = Pre + @"
if($null -eq $prof){ throw 'NOPROFILE' }
$cap=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($prof).ToString()
if($cap -ne 'Enabled'){ throw ('CAP:'+$cap) }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
if($m.TetheringOperationalState.ToString() -ne 'Off'){ throw 'ALREADYON' }
$cfg=New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
$cfg.Ssid=$env:CICI_SSID
$cfg.Passphrase=$env:CICI_PASS
if($env:CICI_BAND){ try{ $cfg.Band=[enum]::Parse([Windows.Networking.NetworkOperators.TetheringWiFiBand],$env:CICI_BAND) }catch{} }   # keep the user's band instead of resetting it to Auto
AwaitA ($m.ConfigureAccessPointAsync($cfg))
$r=Await ($m.StartTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
if($r.Status.ToString() -ne 'Success'){ throw ('START:'+$r.Status+' '+$r.AdditionalErrorMessage) }
'OK'
";

    const string StatusScript = Pre + @"
if($null -eq $prof){ '{""state"":""NoProfile"",""clients"":0}'; exit 0 }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
$n=0; try{ $n=$m.ClientCount }catch{}
# IPv4 neighbours by MAC (the hotspot hands out 192.168.137.x addresses)
$neigh=@{}
try{ Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.LinkLayerAddress -and $_.LinkLayerAddress -ne '00-00-00-00-00-00' -and $_.LinkLayerAddress -ne 'FF-FF-FF-FF-FF-FF' } | ForEach-Object { $neigh[($_.LinkLayerAddress -replace ':','-').ToUpper()] = $_.IPAddress } }catch{}
$list=@()
try{ foreach($c in $m.GetTetheringClients()){ $mac=($c.MacAddress -replace ':','-').ToUpper(); $name=''; try{ $name=[string]($c.HostNames | Select-Object -First 1).DisplayName }catch{}; $list += @{mac=$mac;name=$name;ip=[string]$neigh[$mac]} } }catch{}
(@{state=$m.TetheringOperationalState.ToString();clients=$n;max=$m.MaxClientCount;list=$list}|ConvertTo-Json -Compress -Depth 4)
";

    const string StopScript = Pre + @"
if($null -eq $prof){ 'NOPROFILE'; exit 0 }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
$cur=$m.GetCurrentAccessPointConfiguration()
# Ownership: only touch a hotspot that is still configured with the SSID CiciPDA set. Anything else belongs to the user.
if(-not $env:CICI_OWN_SSID -or $cur.Ssid -ne $env:CICI_OWN_SSID){ 'NOTOURS'; exit 0 }
if($m.TetheringOperationalState.ToString() -ne 'Off'){ $null=Await ($m.StopTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]) }
if($env:CICI_PREV_SSID){
  $cfg=New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
  $cfg.Ssid=$env:CICI_PREV_SSID; $cfg.Passphrase=$env:CICI_PREV_PASS
  if($env:CICI_PREV_BAND){ try{ $cfg.Band=[enum]::Parse([Windows.Networking.NetworkOperators.TetheringWiFiBand],$env:CICI_PREV_BAND) }catch{} }
  AwaitA ($m.ConfigureAccessPointAsync($cfg))
  'RESTORED'
} else { 'STOPPED' }
";

    readonly string _prevFile = AppPaths.DataFile("hotspot-prev.json");
    public HotspotState State { get; private set; } = HotspotState.Unavailable;
    public string Detail { get; private set; } = "";
    public int ClientCount { get; private set; }
    /// <summary>Devices currently joined to the hotspot (MAC, host name and IP as Windows reports them).</summary>
    public IReadOnlyList<HotspotClient> Clients { get; private set; } = Array.Empty<HotspotClient>();
    public HotspotDeviceStore Devices { get; } = new();
    public string? ProbeUrl { get; private set; }
    public string? UpstreamGuid { get; private set; }
    /// <summary>SSID CiciPDA configured; proves ownership during stop and crash recovery.</summary>
    public string? OwnSsid { get; private set; }
    /// <summary>Plain-language note about what could and could not be restored (shown in the log/UI).</summary>
    public string RestoreNote { get; private set; } = "";
    HotspotProbeServer? _probe;

    static Task<ProcResult> Ps(string script, IDictionary<string, string> env, int timeoutSec = 45) =>
        ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }, TimeSpan.FromSeconds(timeoutSec), env: env);

    /// <summary>Read-only status query (state, client count, client list) for the connection profile of an adapter; used by Refresh and by tests.</summary>
    public static async Task<string?> RawStatusAsync(string adapterGuid)
    {
        var r = await Ps(StatusScript, new Dictionary<string, string> { ["CICI_ADAPTER"] = adapterGuid }, 30);
        return r.Ok ? r.Stdout.Trim() : null;
    }

    /// <summary>GUID (Windows InterfaceGuid) of a network interface by name; used to select the upstream explicitly.</summary>
    public static string? AdapterGuid(string interfaceName)
    {
        var ni = WinNet.FindByName(interfaceName);
        return ni != null && Guid.TryParse(ni.Id, out var g) ? g.ToString() : null;
    }

    public static string Explain(string capability) => capability switch
    {
        "Enabled" => "Windows can share this connection.",
        "NoProfile" => "Windows has no connection profile for the CiciPDA adapter yet, so Mobile Hotspot cannot use it. Wait a few seconds after connecting, or share through Internet Connection Sharing manually (adapter Properties > Sharing).",
        "DisabledByGroupPolicy" => "Mobile Hotspot is disabled by group policy on this PC.",
        "DisabledByHardwareLimitation" => "This PC's Wi-Fi hardware or driver cannot host a hotspot (or it has no Wi-Fi adapter).",
        "DisabledByOperator" => "The mobile operator profile does not allow tethering.",
        "DisabledBySku" => "This edition of Windows does not support Mobile Hotspot.",
        "DisabledByRequiredAppNotInstalled" => "A required operator app is not installed.",
        "DisabledBySystemCapability" => "A Windows setting or capability blocks Mobile Hotspot.",
        _ => "Windows reports that Mobile Hotspot cannot share this connection (" + capability + ").",
    };

    /// <summary>Is sharing this specific upstream possible? Never starts anything.</summary>
    public async Task<HotspotCheck> CheckAsync(string adapterGuid)
    {
        var r = await Ps(CheckScript, new Dictionary<string, string> { ["CICI_ADAPTER"] = adapterGuid }, 30);
        if (!r.Ok) return new HotspotCheck(false, "", "Error", r.Combined);
        try
        {
            using var d = JsonDocument.Parse(r.Stdout.Trim());
            var root = d.RootElement; var cap = root.GetProperty("capability").GetString() ?? "Unknown";
            var found = root.GetProperty("found").GetBoolean();
            var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
            var res = new HotspotCheck(found, root.TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "", cap, Explain(cap) + (state is "On" or "InTransition" ? " (Mobile Hotspot is already on.)" : ""));
            State = res.CanShare ? HotspotState.Available : HotspotState.Unavailable; Detail = res.Detail;
            return res;
        }
        catch (JsonException) { return new HotspotCheck(false, "", "Error", "Unreadable hotspot capability response: " + r.Combined); }
    }

    /// <summary>Starts sharing the given upstream only. Call it after the upstream has passed internet verification.</summary>
    public async Task<bool> StartAsync(string adapterGuid, string ssid, string passphrase, string? hotspotGatewayIp = null, CancellationToken ct = default)
    {
        UpstreamGuid = adapterGuid;
        if (ssid.Length is < 1 or > 32) return Fail("SSID must be 1-32 characters.");
        if (passphrase.Length is < 8 or > 63) return Fail("Passphrase must be 8-63 characters.");
        Log.RegisterSecret(passphrase);
        State = HotspotState.Starting; Detail = "Starting Mobile Hotspot";

        // 1) Snapshot the user's current configuration BEFORE changing anything, and persist it (DPAPI) so a crash cannot lose it.
        var env = new Dictionary<string, string> { ["CICI_ADAPTER"] = adapterGuid, ["CICI_SSID"] = ssid, ["CICI_PASS"] = passphrase };
        var read = await Ps(ReadScript, new Dictionary<string, string> { ["CICI_ADAPTER"] = adapterGuid }, 30);
        if (read.Ok)
        {
            try
            {
                using var d = JsonDocument.Parse(read.Stdout.Trim());
                if (d.RootElement.GetProperty("state").GetString() != "Off") return Fail("Windows Mobile Hotspot is already on for a connection. Turn it off in Settings > Network & internet > Mobile hotspot, then connect again. CiciPDA will not take over a hotspot it did not start.");
                File.WriteAllText(_prevFile, Secret.Protect(read.Stdout.Trim()));
                var band = d.RootElement.TryGetProperty("band", out var b) ? b.GetString() : null;
                if (!string.IsNullOrEmpty(band)) env["CICI_BAND"] = band;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException) { return Fail("Could not read the current hotspot configuration, so nothing was changed: " + ex.Message); }
        }
        else if (read.Combined.Contains("NOPROFILE")) return Fail(Explain("NoProfile"));
        else return Fail("Could not read the current hotspot configuration, so nothing was changed: " + read.Combined);

        OwnSsid = ssid;
        var r = await Ps(StartScript, env, 60);
        if (!r.Ok || !r.Stdout.Contains("OK"))
        {
            await RestoreConfigurationAsync();                         // the new SSID may already have been written; put the old one back
            var e = r.Combined;
            if (e.Contains("NOPROFILE")) return Fail(Explain("NoProfile"));
            if (e.Contains("ALREADYON")) return Fail("Windows Mobile Hotspot is already on for a connection. Turn it off in Settings > Network & internet > Mobile hotspot, then connect again. CiciPDA will not take over a hotspot it did not start.");
            var cap = System.Text.RegularExpressions.Regex.Match(e, @"CAP:(\w+)");
            if (cap.Success) return Fail(Explain(cap.Groups[1].Value));
            var st = System.Text.RegularExpressions.Regex.Match(e, @"START:(\w+)");
            if (st.Success) return Fail($"Windows refused to start the hotspot ({st.Groups[1].Value}). Check that Wi-Fi is turned on and the adapter supports hosting.");
            return Fail("Hotspot start failed: " + (e.Length > 300 ? e[..300] : e));
        }
        State = HotspotState.Active; Detail = $"Hotspot \"{ssid}\" is on; waiting for a device";
        StartProbe(hotspotGatewayIp);
        return true;
    }

    bool Fail(string why) { State = HotspotState.Failed; Detail = why; Log.Warn("hotspot", why); return false; }

    void StartProbe(string? ip)
    {
        try
        {
            ip ??= FindHotspotGateway();
            if (ip == null) return;
            _probe = new HotspotProbeServer(IPAddress.Parse(ip));
            _probe.Start();
            ProbeUrl = $"http://{ip}:{_probe.Port}/check?t={_probe.Token}";
        }
        catch (Exception ex) { Log.Info("hotspot", "Client check page unavailable: " + ex.Message); }
    }

    /// <summary>The hotspot's own LAN address (ICS default 192.168.137.1) if present.</summary>
    public static string? FindHotspotGateway()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var a in n.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.Address.ToString().StartsWith("192.168.137.")) return a.Address.ToString();
        return null;
    }

    /// <summary>Refresh client count and promote the state: Active, ClientConnected, ClientInternetVerified.</summary>
    public async Task RefreshAsync()
    {
        if (State is not (HotspotState.Active or HotspotState.ClientConnected or HotspotState.ClientInternetVerified) || UpstreamGuid == null) return;
        var r = await Ps(StatusScript, new Dictionary<string, string> { ["CICI_ADAPTER"] = UpstreamGuid }, 20);
        if (!r.Ok) return;
        try
        {
            using var d = JsonDocument.Parse(r.Stdout.Trim());
            var st = d.RootElement.GetProperty("state").GetString();
            ClientCount = d.RootElement.TryGetProperty("clients", out var c) ? c.GetInt32() : 0;
            var found = new List<HotspotClient>();
            if (d.RootElement.TryGetProperty("list", out var arr))
            {
                IEnumerable<JsonElement> items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : arr.ValueKind == JsonValueKind.Object ? new[] { arr } : Array.Empty<JsonElement>();
                foreach (var it in items)
                {
                    var mac = it.TryGetProperty("mac", out var m1) ? m1.GetString() ?? "" : "";
                    if (mac.Length == 0) continue;
                    found.Add(new HotspotClient(mac, it.TryGetProperty("name", out var n1) ? n1.GetString() ?? "" : "", it.TryGetProperty("ip", out var i1) ? i1.GetString() ?? "" : ""));
                }
            }
            Clients = found; if (found.Count > 0) ClientCount = found.Count;
            Devices.Observe(found);
            if (st != "On") { State = HotspotState.Failed; Detail = "Windows turned the hotspot off (state: " + st + ")."; return; }
        }
        catch (JsonException) { return; }
        if (ClientCount == 0) { State = HotspotState.Active; Detail = "Hotspot is on; waiting for a device"; return; }
        if (_probe is { Verified: true }) { State = HotspotState.ClientInternetVerified; Detail = $"{ClientCount} device(s) connected; a device confirmed DNS and HTTPS"; }
        else { State = HotspotState.ClientConnected; Detail = $"{ClientCount} device(s) connected." + (ProbeUrl != null ? " Open " + ProbeUrl + " on a connected device to verify its internet." : " Internet on the device is not verified."); }
    }

    /// <summary>Stops the hotspot (only if it is still ours) and restores the configuration that existed before CiciPDA changed it.</summary>
    public async Task StopAsync()
    {
        try { _probe?.Dispose(); } catch { } _probe = null; ProbeUrl = null;
        await RestoreConfigurationAsync();
        State = HotspotState.Unavailable; Detail = ""; ClientCount = 0; UpstreamGuid = null;
    }

    /// <summary>
    /// Crash / restart recovery. The upstream adapter may no longer exist, so the system-wide hotspot configuration is reached through any
    /// capable profile; ownership is proven by the SSID that CiciPDA configured. A hotspot that does not carry that SSID is never touched.
    /// </summary>
    public async Task RecoverAsync(string? adapterGuid, string? ownSsid)
    {
        UpstreamGuid = adapterGuid; OwnSsid = ownSsid;
        if (string.IsNullOrEmpty(ownSsid)) { Log.Warn("hotspot", "Recovery found no record of the SSID CiciPDA used; the hotspot configuration is left unchanged."); try { File.Delete(_prevFile); } catch { } return; }
        await RestoreConfigurationAsync(anyProfile: true);
        UpstreamGuid = null;
    }

    async Task RestoreConfigurationAsync(bool anyProfile = false)
    {
        var env = new Dictionary<string, string> { ["CICI_ADAPTER"] = UpstreamGuid ?? "", ["CICI_OWN_SSID"] = OwnSsid ?? "" };
        if (anyProfile) env["CICI_ANY"] = "1";
        bool havePrev = false;
        if (File.Exists(_prevFile))
            try
            {
                using var d = JsonDocument.Parse(Secret.Unprotect(File.ReadAllText(_prevFile)));
                env["CICI_PREV_SSID"] = d.RootElement.GetProperty("ssid").GetString() ?? "";
                env["CICI_PREV_PASS"] = d.RootElement.GetProperty("pass").GetString() ?? "";
                if (d.RootElement.TryGetProperty("band", out var band)) env["CICI_PREV_BAND"] = band.GetString() ?? "";
                havePrev = true;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { Log.Warn("hotspot", "The saved previous hotspot configuration is unreadable."); }
        var r = await Ps(StopScript, env, 40);
        var o = r.Stdout.Trim();
        if (!r.Ok) { RestoreNote = "Stopping the hotspot reported: " + r.Combined; Log.Warn("hotspot", RestoreNote); return; }
        if (o.Contains("NOTOURS")) { RestoreNote = "The hotspot is not (or no longer) configured with the SSID CiciPDA used, so it was left untouched."; Log.Info("hotspot", RestoreNote); }
        else if (o.Contains("NOPROFILE")) { RestoreNote = "Windows has no connection profile that can reach the hotspot configuration; nothing was changed."; Log.Warn("hotspot", RestoreNote); }
        else if (o.Contains("RESTORED")) { RestoreNote = "Hotspot stopped. Restored the previous SSID, passphrase and band. Other Mobile Hotspot options were never modified by CiciPDA."; Log.Info("hotspot", RestoreNote); }
        else { RestoreNote = havePrev ? "Hotspot stopped." : "Hotspot stopped. No earlier configuration was recorded, so the SSID/passphrase CiciPDA set remain configured in Windows."; Log.Warn("hotspot", RestoreNote); }
        try { if (File.Exists(_prevFile)) File.Delete(_prevFile); } catch { }
    }
}

/// <summary>
/// Tiny HTTP page served ONLY on the hotspot's LAN address. A device on the hotspot opens it; the page performs a real DNS
/// lookup (by hostname) and a real HTTPS request from that device and reports the result back. Protected by a random token.
/// </summary>
public sealed class HotspotProbeServer : IDisposable
{
    readonly TcpListener _l;
    readonly CancellationTokenSource _cts = new();
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public int Port { get; private set; }
    public bool Verified { get; private set; }
    public bool? DnsOk { get; private set; }
    public bool? HttpsOk { get; private set; }

    public HotspotProbeServer(IPAddress bind, int port = 0) { _l = new TcpListener(bind, port); }

    public void Start() { _l.Start(8); Port = ((IPEndPoint)_l.LocalEndpoint).Port; _ = Task.Run(Loop); }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = c.SendTimeout = 5000;
                var ns = c.GetStream(); var buf = new byte[4096]; int n = await ns.ReadAsync(buf);
                var line = Encoding.ASCII.GetString(buf, 0, n).Split('\r', '\n')[0];          // "GET /path?query HTTP/1.1"
                var parts = line.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                string body, type = "text/html; charset=utf-8"; int code = 200;
                if (path.StartsWith("/check?t=" + Token)) body = Page();
                else if (path.StartsWith("/result?"))
                {
                    var q = System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?') + 1)..]);
                    if (q["t"] == Token) { DnsOk = q["dns"] == "1"; HttpsOk = q["https"] == "1"; Verified = DnsOk == true && HttpsOk == true; body = "ok"; type = "text/plain"; }
                    else { code = 403; body = "forbidden"; type = "text/plain"; }
                }
                else { code = 404; body = "not found"; type = "text/plain"; }
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {code} X\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await ns.WriteAsync(Encoding.ASCII.GetBytes(head)); await ns.WriteAsync(bytes);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    string Page() => @"<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'><title>CiciPDA check</title>
<body style='font-family:sans-serif;background:#07090c;color:#e7eef5;padding:24px'><h2>CiciPDA connection check</h2><p id=s>Testing DNS and HTTPS from this device...</p>
<script>
const nc={mode:'no-cors',cache:'no-store'};
async function t(u){try{await fetch(u,nc);return true}catch(e){return false}}
(async()=>{
 const dns=await t('https://one.one.one.one/');            // needs a DNS lookup of a hostname
 const https=await t('https://1.1.1.1/');                  // direct HTTPS, no DNS
 await fetch('/result?t=" + Token + @"&dns='+(dns?1:0)+'&https='+(https?1:0));
 document.getElementById('s').textContent='DNS: '+(dns?'OK':'FAILED')+'   HTTPS: '+(https?'OK':'FAILED');
})();
</script>";

    public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } _cts.Dispose(); }
}


public sealed record HotspotClient(string Mac, string HostName, string Ip);

public sealed class HotspotKnownDevice
{
    public string Mac { get; set; } = "";
    public string Alias { get; set; } = "";
    public string LastHostName { get; set; } = "";
    public string LastIp { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? SessionStart { get; set; }       // when it joined during the current sharing session
}

/// <summary>
/// Remembers the devices that joined the hotspot, with a name you choose for each. Windows offers no per-device block or kick for Mobile Hotspot,
/// so this store is about knowing who is connected, not forcing anyone off.
/// </summary>
public sealed class HotspotDeviceStore
{
    readonly string _path = AppPaths.DataFile("hotspot-devices.json");
    readonly Dictionary<string, HotspotKnownDevice> _items;
    public HotspotDeviceStore() { _items = (Json.LoadOrNew<List<HotspotKnownDevice>>(_path)).ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase); }

    public IReadOnlyCollection<HotspotKnownDevice> All => _items.Values.ToList();

    public HotspotKnownDevice? Get(string mac) => _items.TryGetValue(mac, out var d) ? d : null;

    public string DisplayName(HotspotClient c)
    {
        var k = Get(c.Mac);
        if (k != null && !string.IsNullOrWhiteSpace(k.Alias)) return k.Alias;
        return !string.IsNullOrWhiteSpace(c.HostName) ? c.HostName : "Unknown device";
    }

    public void Observe(IEnumerable<HotspotClient> current)
    {
        var now = DateTimeOffset.Now; var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in current)
        {
            macs.Add(c.Mac);
            if (!_items.TryGetValue(c.Mac, out var k)) { k = new HotspotKnownDevice { Mac = c.Mac, FirstSeen = now }; _items[c.Mac] = k; }
            k.LastSeen = now; if (k.SessionStart == null) k.SessionStart = now;
            if (!string.IsNullOrWhiteSpace(c.HostName)) k.LastHostName = c.HostName;
            if (!string.IsNullOrWhiteSpace(c.Ip)) k.LastIp = c.Ip;
        }
        foreach (var k in _items.Values) if (!macs.Contains(k.Mac)) k.SessionStart = null;   // left the hotspot
        Save();
    }

    public void SetAlias(string mac, string alias)
    {
        if (!_items.TryGetValue(mac, out var k)) { k = new HotspotKnownDevice { Mac = mac, FirstSeen = DateTimeOffset.Now, LastSeen = DateTimeOffset.Now }; _items[mac] = k; }
        k.Alias = alias.Trim().Length > 40 ? alias.Trim()[..40] : alias.Trim();
        Save();
    }

    public void Forget(string mac) { _items.Remove(mac); Save(); }
    void Save() { try { Json.Save(_path, _items.Values.ToList()); } catch (Exception ex) { Log.Debug("hotspot", "device list not saved: " + ex.Message); } }
}
