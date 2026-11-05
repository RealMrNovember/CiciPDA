using System.Net.NetworkInformation;

namespace CiciPDA;

/// <summary>On-demand diagnostic tests. Each returns a plain-language result and a suggested fix; none changes system state.</summary>
public sealed class DiagnosticsService
{
    readonly AdbService _adb;
    readonly Orchestrator _orch;
    public DiagnosticsService(AdbService adb, Orchestrator orch) { _adb = adb; _orch = orch; }

    public static readonly (string id, string title)[] Tests =
    {
        ("admin", "Administrator rights"),
        ("tools", "Bundled tools (adb, Wintun, tun2socks, helper)"),
        ("device", "Device detection"),
        ("adb", "USB / ADB authorization"),
        ("handshake", "Phone helper handshake"),
        ("adapter", "Virtual adapter"),
        ("route", "Default route"),
        ("dns", "DNS resolution"),
        ("http", "HTTP connectivity"),
        ("https", "HTTPS connectivity"),
        ("ncsi", "Windows internet detection"),
        ("hotspot", "Wi-Fi hotspot availability"),
        ("firewall", "Firewall and permissions"),
    };

    public async Task<CheckResult> RunAsync(string id, CancellationToken ct = default)
    {
        var title = Tests.First(t => t.id == id).title;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await RunCore(id, title, ct);
            return r with { Ms = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new CheckResult(id, title, false, false, ex.Message, "Run the test again; if it keeps failing, check the Activity log.", sw.ElapsedMilliseconds); }
    }

    CheckResult Ok(string id, string t, string d) => new(id, t, true, false, d);
    CheckResult Bad(string id, string t, string d, string fix) => new(id, t, false, false, d, fix);

    async Task<CheckResult> RunCore(string id, string t, CancellationToken ct)
    {
        switch (id)
        {
            case "admin":
                return WinNet.IsElevated() ? Ok(id, t, "Running elevated.") : Bad(id, t, "Not running as administrator.", "Use \"Restart as administrator\" before connecting; the virtual adapter, routes and DNS need it.");
            case "tools":
            {
                var missing = new List<string>();
                if (_adb.AdbPath == null && _adb.Locate() == null) missing.Add("adb.exe");
                if (!File.Exists(AppPaths.WintunDll)) missing.Add("wintun.dll");
                if (!File.Exists(AppPaths.Tun2Socks)) missing.Add("tun2socks.exe");
                if (!File.Exists(AppPaths.AgentDex)) missing.Add("agent.dex");
                return missing.Count == 0 ? Ok(id, t, $"adb: {_adb.AdbPath}") : Bad(id, t, "Missing: " + string.Join(", ", missing), "Run src\\app\\fetch-tools.ps1, or set the adb path in Settings.");
            }
            case "device":
            {
                var l = await _orch.RefreshDevicesAsync(ct);
                return l.Count > 0 ? Ok(id, t, string.Join("; ", l.Select(d => $"{d.DisplayName} [{d.TransportName}, {d.State}]")))
                    : Bad(id, t, "No devices reported by adb.", "Use a data-capable USB cable, enable Developer options → USB debugging, then restart ADB from the Devices page.");
            }
            case "adb":
            {
                var l = _orch.Devices;
                if (l.Count == 0) return Bad(id, t, "No device to check.", "Connect the phone first.");
                var bad = l.FirstOrDefault(d => !d.IsAuthorized);
                return bad == null ? Ok(id, t, "All detected devices are authorized.")
                    : Bad(id, t, $"{bad.Serial} is {bad.State}.", bad.State == "unauthorized" ? "Unlock the phone and tap Allow on the USB debugging prompt (tick Always allow)." : "Reconnect the cable or restart ADB from the Devices page.");
            }
            case "handshake":
            {
                var dev = _orch.Devices.FirstOrDefault(d => d.IsAuthorized);
                if (dev == null) return Bad(id, t, "No authorized device.", "Connect and authorize the phone first.");
                using var agent = await _adb.StartAgentAsync(dev.Serial, ct);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (await AgentChannel.ConnectAsync(agent.LocalPort, agent.Key, "1.1.1.1", 443, ct, TimeSpan.FromSeconds(12))) { }
                return Ok(id, t, $"Encrypted channel works; the phone reached 1.1.1.1 in {sw.ElapsedMilliseconds} ms.");
            }
            case "adapter":
            {
                var ni = WinNet.FindByName(TunnelNetwork.AdapterName);
                return ni != null && ni.OperationalStatus == OperationalStatus.Up ? Ok(id, t, $"{ni.Name} is up (interface {WinNet.IndexOf(ni)}).")
                    : _orch.IsActive ? Bad(id, t, "The virtual adapter is not present although a connection is active.", "Disconnect and connect again.")
                    : Ok(id, t, "No adapter present, which is expected while disconnected.");
            }
            case "route":
            {
                int best = WinNet.BestInterfaceFor(System.Net.IPAddress.Parse("1.1.1.1"));
                var ni = WinNet.FindByIndex(best);
                bool viaTunnel = ni?.Name == TunnelNetwork.AdapterName;
                if (_orch.IsActive && _orch.Method is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb && !viaTunnel)
                    return Bad(id, t, $"Internet traffic leaves via {ni?.Name ?? "interface " + best}, not the tunnel.", "Another VPN or adapter has priority. Disconnect other VPNs.");
                return Ok(id, t, $"Internet traffic uses {ni?.Name ?? "interface " + best}.");
            }
            case "dns": return await Verifier.Dns().ContinueWith(x => Wrap(x.Result, id, t));
            case "http": return Wrap(await Verifier.Http(), id, t);
            case "https": return Wrap(await Verifier.Https(), id, t);
            case "ncsi":
            {
                bool avail = NetworkInterface.GetIsNetworkAvailable();
                return avail ? Ok(id, t, "Windows reports a network is available. Per-profile status is verified on connect.") : Bad(id, t, "Windows reports no network.", "Check adapters in Network Connections.");
            }
            case "hotspot":
            {
                var r = await ProcessRunner.RunAsync("netsh.exe", new[] { "wlan", "show", "drivers" }, TimeSpan.FromSeconds(10), ct);
                if (!r.Ok) return Bad(id, t, "No WLAN service or Wi-Fi adapter found.", "Install/enable a Wi-Fi adapter to share the connection over Wi-Fi.");
                bool hosted = r.Stdout.Contains("Hosted network supported  : Yes", StringComparison.OrdinalIgnoreCase) || r.Stdout.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase);
                return hosted ? Ok(id, t, "The Wi-Fi driver reports hotspot-capable features.") : Bad(id, t, "The Wi-Fi driver does not report hotspot support.", "Update the Wi-Fi driver or use a different adapter.");
            }
            case "firewall":
            {
                var r = await ProcessRunner.RunAsync("netsh.exe", new[] { "advfirewall", "show", "currentprofile", "state" }, TimeSpan.FromSeconds(10), ct);
                string state = r.Stdout.Contains("ON", StringComparison.OrdinalIgnoreCase) ? "on" : "off";
                return Ok(id, t, $"Windows Firewall is {state}. CiciPDA opens no inbound ports; its relay listens on loopback only.");
            }
        }
        return Bad(id, t, "Unknown test.", "");
    }

    static CheckResult Wrap(CheckResult r, string id, string t) => r with { Id = id, Title = t, Required = false };
}
