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
$asTask=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0]
function Await($op,$t){$m=$asTask.MakeGenericMethod($t);$x=$m.Invoke($null,@($op));$x.Wait(-1)|Out-Null;$x.Result}
$asTaskA=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'})[0]
function AwaitA($op){$x=$asTaskA.Invoke($null,@($op));$x.Wait(-1)|Out-Null}
$want=([guid]$env:CICI_ADAPTER).ToString().ToLower()
$prof=$null
foreach($p in [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles()){
  if($p.NetworkAdapter -ne $null -and $p.NetworkAdapter.NetworkAdapterId.ToString().ToLower() -eq $want){$prof=$p;break}
}
";

    const string CheckScript = Pre + @"
if($null -eq $prof){ '{""found"":false,""profile"":"""",""capability"":""NoProfile""}'; exit 0 }
$cap=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($prof).ToString()
$state='Unknown'
if($cap -eq 'Enabled'){ $m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof); $state=$m.TetheringOperationalState.ToString() }
(@{found=$true;profile=$prof.ProfileName;capability=$cap;state=$state}|ConvertTo-Json -Compress)
";

    const string StartScript = Pre + @"
if($null -eq $prof){ throw 'NOPROFILE' }
$cap=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($prof).ToString()
if($cap -ne 'Enabled'){ throw ('CAP:'+$cap) }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
if($m.TetheringOperationalState.ToString() -ne 'Off'){ throw 'ALREADYON' }
$cur=$m.GetCurrentAccessPointConfiguration()
$prev=@{ssid=$cur.Ssid;pass=$cur.Passphrase;band=$cur.Band.ToString()}
[Console]::Out.WriteLine('PREV:'+($prev|ConvertTo-Json -Compress))
$cfg=New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
$cfg.Ssid=$env:CICI_SSID
$cfg.Passphrase=$env:CICI_PASS
AwaitA ($m.ConfigureAccessPointAsync($cfg))
$r=Await ($m.StartTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
if($r.Status.ToString() -ne 'Success'){ throw ('START:'+$r.Status+' '+$r.AdditionalErrorMessage) }
'OK'
";

    const string StatusScript = Pre + @"
if($null -eq $prof){ '{""state"":""NoProfile"",""clients"":0}'; exit 0 }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
$n=0; try{ $n=$m.ClientCount }catch{}
(@{state=$m.TetheringOperationalState.ToString();clients=$n;max=$m.MaxClientCount}|ConvertTo-Json -Compress)
";

    const string StopScript = Pre + @"
if($null -eq $prof){ 'OK'; exit 0 }
$m=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
if($m.TetheringOperationalState.ToString() -ne 'Off'){ $null=Await ($m.StopTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]) }
if($env:CICI_PREV_SSID){
  $cfg=New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
  $cfg.Ssid=$env:CICI_PREV_SSID; $cfg.Passphrase=$env:CICI_PREV_PASS
  AwaitA ($m.ConfigureAccessPointAsync($cfg))
}
'OK'
";

    readonly string _prevFile = AppPaths.DataFile("hotspot-prev.json");
    public HotspotState State { get; private set; } = HotspotState.Unavailable;
    public string Detail { get; private set; } = "";
    public int ClientCount { get; private set; }
    public string? ProbeUrl { get; private set; }
    public string? UpstreamGuid { get; private set; }
    HotspotProbeServer? _probe;

    static Task<ProcResult> Ps(string script, IDictionary<string, string> env, int timeoutSec = 45) =>
        ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }, TimeSpan.FromSeconds(timeoutSec), env: env);

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
        var r = await Ps(StartScript, new Dictionary<string, string> { ["CICI_ADAPTER"] = adapterGuid, ["CICI_SSID"] = ssid, ["CICI_PASS"] = passphrase }, 60);
        // The script prints the previous configuration first so it can be restored; keep it protected at rest.
        foreach (var line in r.Stdout.Split('\n'))
            if (line.StartsWith("PREV:")) { try { File.WriteAllText(_prevFile, Secret.Protect(line[5..].Trim())); } catch (Exception ex) { Log.Warn("hotspot", "Could not save previous hotspot configuration: " + ex.Message); } }
        if (!r.Ok || !r.Stdout.Contains("OK"))
        {
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
            if (st != "On") { State = HotspotState.Failed; Detail = "Windows turned the hotspot off (state: " + st + ")."; return; }
        }
        catch (JsonException) { return; }
        if (ClientCount == 0) { State = HotspotState.Active; Detail = "Hotspot is on; waiting for a device"; return; }
        if (_probe is { Verified: true }) { State = HotspotState.ClientInternetVerified; Detail = $"{ClientCount} device(s) connected; a device confirmed DNS and HTTPS"; }
        else { State = HotspotState.ClientConnected; Detail = $"{ClientCount} device(s) connected." + (ProbeUrl != null ? " Open " + ProbeUrl + " on a connected device to verify its internet." : " Internet on the device is not verified."); }
    }

    /// <summary>Stops the hotspot and restores the configuration that existed before CiciPDA changed it.</summary>
    public async Task StopAsync()
    {
        try { _probe?.Dispose(); } catch { } _probe = null; ProbeUrl = null;
        var env = new Dictionary<string, string> { ["CICI_ADAPTER"] = UpstreamGuid ?? Guid.Empty.ToString() };
        if (UpstreamGuid == null) { await StopFromJournalAsync(env); return; }
        if (File.Exists(_prevFile))
            try
            {
                using var d = JsonDocument.Parse(Secret.Unprotect(File.ReadAllText(_prevFile)));
                env["CICI_PREV_SSID"] = d.RootElement.GetProperty("ssid").GetString() ?? "";
                env["CICI_PREV_PASS"] = d.RootElement.GetProperty("pass").GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { Log.Warn("hotspot", "Previous hotspot configuration unreadable; leaving the current one."); }
        var r = await Ps(StopScript, env, 40);
        if (!r.Ok) Log.Warn("hotspot", "Stop reported: " + r.Combined); else Log.Info("hotspot", "Hotspot stopped; previous configuration restored where available.");
        try { if (File.Exists(_prevFile)) File.Delete(_prevFile); } catch { }
        State = HotspotState.Unavailable; Detail = ""; ClientCount = 0; UpstreamGuid = null;
    }

    async Task StopFromJournalAsync(Dictionary<string, string> env)
    {
        // Crash recovery: the adapter is gone, so find any hotspot we started via the journal-recorded GUID.
        var guid = new RecoveryJournal().State.HotspotAdapter;
        if (guid != null) { env["CICI_ADAPTER"] = guid; UpstreamGuid = guid; await StopAsync(); }
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
