using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Xunit;

namespace CiciPDA.Tests;

public class NetshPlanTests
{
    static List<string> Cmds(AppSettings s, string? bypass = null, int origIf = 0) =>
        NetshPlan.Apply(s, 42, "10.66.0.2", bypass, origIf).Select(c => string.Join(" ", c.args)).ToList();

    [Fact]
    public void Plan_UsesSplitDefaultRoutes_NeverTouchesTheRealDefaultRoute()
    {
        var c = Cmds(new AppSettings());
        Assert.Contains(c, x => x.Contains("prefix=0.0.0.0/1") && x.Contains("interface=42") && x.Contains("store=active"));
        Assert.Contains(c, x => x.Contains("prefix=128.0.0.0/1") && x.Contains("interface=42") && x.Contains("store=active"));
        Assert.DoesNotContain(c, x => x.Contains("prefix=0.0.0.0/0"));
        Assert.DoesNotContain(c, x => x.Contains(" delete "));
    }

    [Fact]
    public void Plan_EverythingIsNonPersistent_SoRebootLeavesNothingBehind()
    {
        foreach (var c in Cmds(new AppSettings(), "192.168.1.50/32", 7).Where(x => x.Contains(" add route") || x.Contains("add route") || x.Contains("subinterface")))
            Assert.Contains("store=active", c);
    }

    [Fact]
    public void Plan_SetsDns_OnTheTunnelInterfaceOnly_WithoutRegistryEdits()
    {
        var c = Cmds(new AppSettings { DnsPrimary = "9.9.9.9", DnsSecondary = "149.112.112.112" });
        Assert.Contains(c, x => x.Contains("set dnsservers") && x.Contains("name=42") && x.Contains("address=9.9.9.9"));
        Assert.Contains(c, x => x.Contains("add dnsservers") && x.Contains("address=149.112.112.112") && x.Contains("index=2"));
        Assert.All(c, x => Assert.DoesNotContain("reg ", x)); Assert.All(c, x => Assert.DoesNotContain("ttl", x, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_OmitsSecondaryDns_WhenBlank() => Assert.DoesNotContain(Cmds(new AppSettings { DnsSecondary = " " }), x => x.Contains("add dnsservers"));

    [Theory]
    [InlineData(100, 576)] [InlineData(1400, 1400)] [InlineData(9000, 1500)]
    public void Mtu_IsClamped(int configured, int expected)
    {
        Assert.Equal(expected, NetshPlan.ClampMtu(configured));
        Assert.Contains(Cmds(new AppSettings { Mtu = configured }), x => x.Contains($"mtu={expected}"));
    }

    [Fact]
    public void BypassRoute_PinsTransportHostToOriginalInterface_AndHasMatchingDelete()
    {
        var c = Cmds(new AppSettings(), "192.168.1.50/32", 7);
        var add = c.Single(x => x.Contains("prefix=192.168.1.50/32"));
        Assert.Contains("interface=7", add);                      // original interface, not the tunnel
        Assert.DoesNotContain("interface=42", add);
        // bypass must be installed BEFORE the tunnel routes so the transport never loops
        Assert.True(c.IndexOf(add) < c.FindIndex(x => x.Contains("prefix=0.0.0.0/1")));
        var del = string.Join(" ", NetshPlan.DeleteBypass("192.168.1.50/32", 7));
        Assert.Contains("delete route", del); Assert.Contains("prefix=192.168.1.50/32", del); Assert.Contains("interface=7", del);
    }

    [Fact]
    public void NoBypass_ForUsbTransport() => Assert.DoesNotContain(Cmds(new AppSettings(), null, 0), x => x.Contains("192.168"));

    [Fact]
    public void Ipv6Plan_RoutesIpv6IntoTunnel()
    {
        Assert.Equal(new[] { "::/1", "8000::/1" }, NetshPlan.V6HalfRoutes);
        Assert.Contains("fd66:", string.Join(" ", NetshPlan.SetIpv6Address(42)));
        Assert.Contains("store=active", string.Join(" ", NetshPlan.AddIpv6Route("::/1", 42)));
    }
}

public class NativeLinkTests
{
    static IfaceInfo I(string name, string desc, NetworkInterfaceType t, bool up = true, bool ip = true) => new(name, desc, t, up, ip);

    [Fact]
    public void Iphone_UsbAdapter_IsPreferred_AndIsDistinctFromWifiMode()
    {
        var all = new[] { I("Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211), I("Ethernet 3", "Apple Mobile Device Ethernet", NetworkInterfaceType.Ethernet) };
        var (n, mode) = NativeLinks.SelectIphone(all, "Home");
        Assert.Equal("Ethernet 3", n!.Name); Assert.Equal(IphoneMode.UsbAppleEthernet, mode);
    }

    [Fact]
    public void Iphone_WifiHotspot_RequiresJoinedNetwork()
    {
        var all = new[] { I("Wi-Fi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211) };
        Assert.Equal(IphoneMode.WifiPersonalHotspot, NativeLinks.SelectIphone(all, "Mike's iPhone").mode);
        Assert.Equal(IphoneMode.None, NativeLinks.SelectIphone(all, null).mode);
    }

    [Fact]
    public void Iphone_AdapterWithoutAddress_IsReportedAsIdle_NotUsed()
    {
        var all = new[] { I("Ethernet 3", "Apple Mobile Device Ethernet", NetworkInterfaceType.Ethernet, up: true, ip: false) };
        Assert.Equal(IphoneMode.None, NativeLinks.SelectIphone(all, null).mode);
        Assert.True(NativeLinks.AppleAdapterPresentButIdle(all));
    }

    [Fact]
    public void Wifi_IgnoresLinkLocalOnlyOrDownAdapters()
    {
        var all = new[] { I("Wi-Fi", "x", NetworkInterfaceType.Wireless80211, up: false), I("Wi-Fi 2", "y", NetworkInterfaceType.Wireless80211, ip: false) };
        Assert.Null(NativeLinks.SelectWifi(all, "Net"));
    }

    [Fact]
    public void OtherAdapters_AreNeverMistakenForTheIphone()
    {
        var all = new[] { I("Ethernet", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet), I("Remote NDIS", "Remote NDIS based Internet Sharing Device", NetworkInterfaceType.Ethernet) };
        Assert.Equal(IphoneMode.None, NativeLinks.SelectIphone(all, null).mode);
    }
}

[Collection("AppPaths")]
public class RecoveryTests
{
    [Fact]
    public async Task Recovery_KillsOrphanTun2Socks_ButNeverAnUnrelatedProcess_AndClearsJournal()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-rec-" + Guid.NewGuid());
        var dir = Path.Combine(Path.GetTempPath(), "cici-fake-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "tun2socks.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), fake);
        using var orphan = Process.Start(new ProcessStartInfo(fake, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        using var innocent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var j = new RecoveryJournal(); j.State.Tun2SocksPid = orphan.Id; j.Save();
            await j.RecoverAsync(new AdbService(() => new AppSettings()));
            Assert.True(orphan.WaitForExit(5000), "orphan tun2socks must be stopped");
            Assert.False(innocent.HasExited, "an unrelated process must never be killed");
            Assert.True(new RecoveryJournal().State.IsEmpty);

            var j2 = new RecoveryJournal(); j2.State.Tun2SocksPid = innocent.Id; j2.Save();   // PID reused by another program
            await j2.RecoverAsync(new AdbService(() => new AppSettings()));
            Assert.False(innocent.HasExited);
        }
        finally { try { if (!orphan.HasExited) orphan.Kill(); } catch { } try { innocent.Kill(); } catch { } }
    }

    [Fact]
    public async Task Recovery_WithStaleBogusState_DoesNotThrow()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-rec-" + Guid.NewGuid());
        var j = new RecoveryJournal();
        j.State.Tun2SocksPid = 999999; j.State.ForwardPort = 1; j.State.AdbSerial = "ghost"; j.State.BypassRoutes.Add(new BypassRoute("203.0.113.9/32", 99999)); j.Save();
        await j.RecoverAsync(new AdbService(() => new AppSettings()));
        Assert.True(new RecoveryJournal().State.IsEmpty);
    }
}

public class Ipv6RelayTests : IClassFixture<AgentFixture>
{
    readonly AgentFixture _a;
    public Ipv6RelayTests(AgentFixture a) { _a = a; }

    [Fact]
    public async Task Tcp_ToIpv6Literal_IsRelayedThroughAgent()
    {
        if (!_a.Available || !Socket.OSSupportsIPv6) return;
        TcpListener l; try { l = new TcpListener(IPAddress.IPv6Loopback, 0); l.Start(); } catch (SocketException) { return; }
        using var stopper = new Stopper(l);
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient acc; try { acc = await l.AcceptTcpClientAsync(); } catch { return; } _ = Task.Run(async () => { using var cc = acc; var s2 = cc.GetStream(); var b2 = new byte[4096]; int n2; try { while ((n2 = await s2.ReadAsync(b2)) > 0) await s2.WriteAsync(b2.AsMemory(0, n2)); } catch { } }); } });
        using var relay = new SocksRelay(_a.Port, _a.Key); relay.Start();
        using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, relay.Port);
        var ns = c.GetStream();
        await ns.WriteAsync(new byte[] { 5, 1, 0 }); await ns.ReadExactlyAsync(new byte[2]);
        var req = new byte[] { 5, 1, 0, 4 }.Concat(IPAddress.IPv6Loopback.GetAddressBytes()).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray();
        await ns.WriteAsync(req); var rep = new byte[10]; await ns.ReadExactlyAsync(rep); Assert.Equal(0, rep[1]);
        var msg = System.Text.Encoding.ASCII.GetBytes("v6 hello"); await ns.WriteAsync(msg);
        var back = new byte[msg.Length]; await ns.ReadExactlyAsync(back); Assert.Equal(msg, back);
    }

    [Fact]
    public async Task Udp_Ipv6Datagram_IsRelayedThroughAgent()
    {
        if (!_a.Available || !Socket.OSSupportsIPv6) return;
        UdpClient echo; try { echo = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0)); } catch (SocketException) { return; }
        using var _e = echo; int eport = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () => { try { while (true) { var r = await echo.ReceiveAsync(); await echo.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint); } } catch { } });
        using var ch = await AgentChannel.ConnectUdpAsync(_a.Port, _a.Key, CancellationToken.None);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var f = new byte[] { 6 }.Concat(IPAddress.IPv6Loopback.GetAddressBytes()).Concat(new[] { (byte)(eport >> 8), (byte)eport }).Concat(payload).ToArray();
        await ch.WriteAsync(f);
        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await ch.ReadFrameAsync(to.Token)); var back = ch.Frame;
        Assert.Equal(6, back[0]); Assert.Equal(payload, back[19..]);
    }

    [Fact]
    public async Task Udp_LargeDatagram_NearLimit_RoundTrips()
    {
        if (!_a.Available) return;
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); int eport = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () => { try { while (true) { var r = await echo.ReceiveAsync(); await echo.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint); } } catch { } });
        using var ch = await AgentChannel.ConnectUdpAsync(_a.Port, _a.Key, CancellationToken.None);
        var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(14000);
        var f = new byte[] { 4, 127, 0, 0, 1, (byte)(eport >> 8), (byte)eport }.Concat(payload).ToArray();
        await ch.WriteAsync(f);
        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await ch.ReadFrameAsync(to.Token)); Assert.Equal(payload, ch.Frame[7..]);
    }

    [Fact]
    public async Task Service_RestartWithSameKey_AcceptsNewConnections()
    {
        // A restarted relay (system restart of the sticky service) resumes with the session key it stored; the same client must authenticate again.
        var key = _a.Key;
        using var fixture2 = new AgentFixtureWithKey(key);
        if (!fixture2.Available) return;
        var json = await AgentChannel.GetStatusAsync(fixture2.Port, key, CancellationToken.None);
        Assert.Contains("\"protocol\":3", json);
    }

    sealed class Stopper : IDisposable { readonly TcpListener _l; public Stopper(TcpListener l) { _l = l; } public void Dispose() => _l.Stop(); }
}

public sealed class AgentFixtureWithKey : IDisposable
{
    public int Port { get; } public bool Available { get; }
    readonly ManagedProcess? _p;
    public AgentFixtureWithKey(byte[] key)
    {
        string? java = new[] { @"C:\Program Files\Android\Android Studio\jbr\bin\java.exe" }.FirstOrDefault(File.Exists);
        string? cls = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "agent", "out", "classes", "app", "cicibyte", "cicipda", "TunnelMain.class"))) { cls = Path.Combine(d.FullName, "src", "agent", "out", "classes"); break; }
        if (java == null || cls == null) return;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _p = new ManagedProcess(java, new[] { "-cp", cls, "app.cicibyte.cicipda.TunnelMain", Port.ToString() }, redirectStdin: true);
        var ready = new TaskCompletionSource<bool>();
        _p.OutLine += x => { if (x.StartsWith("READY")) ready.TrySetResult(true); };
        _p.Start(); _p.WriteLineAsync(Convert.ToHexString(key).ToLowerInvariant()).Wait();
        Available = ready.Task.Wait(TimeSpan.FromSeconds(15));
    }
    public void Dispose() => _p?.Dispose();
}


public class Tun2SocksArgsTests
{
    [Fact]
    public void Arguments_UseDoubleDashLongOptions()
    {
        var a = TunnelNetwork.BuildTun2SocksArgs(1080);
        Assert.Equal(new[] { "--device", "tun://CiciPDA", "--proxy", "socks5://127.0.0.1:1080", "--loglevel", "info" }, a);
        Assert.DoesNotContain(a, x => x.StartsWith("-") && !x.StartsWith("--"));
    }

    [Fact]
    public async Task Arguments_AreAcceptedByTheRealBundledTun2Socks()
    {
        // Ask the real executable: every option we pass must exist in its own --help output (no phone or admin rights needed).
        string? exe = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && exe == null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "app", "tools", "tun2socks.exe");
            if (File.Exists(c)) exe = c;
        }
        if (exe == null) return;                                           // tools are fetched, not committed
        var help = await ProcessRunner.RunAsync(exe, new[] { "--help" }, TimeSpan.FromSeconds(15));
        foreach (var opt in TunnelNetwork.BuildTun2SocksArgs(1080).Where(a => a.StartsWith("--")))
            Assert.Contains(opt, help.Combined);
        // and the old, broken spelling really is rejected by this binary
        var bad = await ProcessRunner.RunAsync(exe, new[] { "-loglevel", "info", "--version" }, TimeSpan.FromSeconds(15));
        Assert.Contains("unknown shorthand flag", bad.Combined);
    }
}

public class NetshSyntaxTests
{
    [Fact]
    public void SetInterface_UsesTheParameterNameNetshExpects()
    {
        // `netsh interface ipv4 set interface` takes interface=, not name= (verified against the built-in netsh help).
        var cmd = string.Join(" ", NetshPlan.SetMetric(42));
        Assert.Contains("interface=42", cmd); Assert.DoesNotContain("name=42", cmd);
    }

    [Theory]
    [InlineData("set address", "[name=]")] [InlineData("set interface", "[interface=]")] [InlineData("set subinterface", "[interface=]")]
    [InlineData("set dnsservers", "[name=]")] [InlineData("add dnsservers", "[name=]")] [InlineData("add route", "[interface=]")] [InlineData("delete route", "[interface=]")]
    public async Task EveryCommandWeSend_MatchesTheNetshHelpOfThisWindows(string verb, string expectedParam)
    {
        var r = await ProcessRunner.RunAsync("netsh.exe", new[] { "interface", "ipv4" }.Concat(verb.Split(' ')).Concat(new[] { "?" }), TimeSpan.FromSeconds(15));
        Assert.Contains(expectedParam, r.Combined);
    }
}


public class Tun2SocksDriverTests
{
    static string? Exe()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "app", "tools", "tun2socks.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    [Fact]
    public async Task DeviceScheme_WeUse_IsASupportedDriver_AndWintunScheme_IsNot()
    {
        var exe = Exe();
        if (exe == null || WinNet.IsElevated()) return;           // never create a real adapter from a test; non-admin runs stop at "access denied"
        var scheme = TunnelNetwork.BuildTun2SocksArgs(1)[1];
        Assert.StartsWith("tun://", scheme);
        var ours = await ProcessRunner.RunAsync(exe, new[] { "--device", scheme, "--proxy", "socks5://127.0.0.1:1", "--loglevel", "info" }, TimeSpan.FromSeconds(6));
        Assert.DoesNotContain("unsupported driver", ours.Combined);
        var old = await ProcessRunner.RunAsync(exe, new[] { "--device", "wintun://CiciTest", "--proxy", "socks5://127.0.0.1:1", "--loglevel", "info" }, TimeSpan.FromSeconds(6));
        Assert.Contains("unsupported driver", old.Combined);      // proves the check is meaningful
    }

    [Fact]
    public void Summarize_ProducesReadableSentences()
    {
        const string fatal = "{\"level\":\"fatal\",\"msg\":\"[ENGINE] failed to start: unsupported driver: wintun\",\"stacktrace\":\"x\"}";
        Assert.Contains("update CiciPDA", Tun2SocksLog.Summarize(fatal));
        Assert.Contains("administrator", Tun2SocksLog.Summarize("2026/11/06 Failed to take device installation mutex: Access is denied. (Code 0x00000005)"));
        Assert.Equal("no details were reported.", Tun2SocksLog.Summarize(""));
    }
}


public class HeaderClickabilityTests
{
    [Fact]
    public void EveryHeaderButton_IsHitTestVisibleInChrome()
    {
        // The header is the window's drag area (WindowChrome caption). A control there receives mouse clicks ONLY if
        // WindowChrome.IsHitTestVisibleInChrome is true (directly or via the Chrome button styles); otherwise it looks fine but cannot be clicked.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "app", "MainWindow.xaml");
            if (!File.Exists(f)) continue;
            var xaml = File.ReadAllText(f);
            int a = xaml.IndexOf("<!-- ===== Header ===== -->"), b = xaml.IndexOf("<!-- ===== Body ===== -->");
            Assert.True(a > 0 && b > a, "header markers not found");
            var header = xaml[a..b];
            var buttons = System.Text.RegularExpressions.Regex.Matches(header, "<Button\\s[^>]*>");
            Assert.True(buttons.Count >= 5, "expected update, check-updates, notifications, min/max/close buttons");
            foreach (System.Text.RegularExpressions.Match m in buttons)
                Assert.True(m.Value.Contains("IsHitTestVisibleInChrome=\"True\"") || m.Value.Contains("StaticResource Chrome}") || m.Value.Contains("StaticResource ChromeClose}"), "not clickable in the caption area: " + m.Value[..Math.Min(90, m.Value.Length)]);
            Assert.Contains("Command=\"{Binding InstallUpdate}\"", header);
            Assert.Contains("Command=\"{Binding CheckUpdates}\"", header);
            return;
        }
        Assert.Fail("MainWindow.xaml not found");
    }
}


public class StaleAdapterTests
{
    [Fact]
    public async Task RemoveAdapter_ForNameThatDoesNotExist_ReturnsTrueImmediately()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await WinNet.RemoveAdapterAsync("definitely-not-an-adapter-" + Guid.NewGuid().ToString("N")[..6]));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Start_NoLongerFailsFastOnAStaleAdapter()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "core", "WinNet.cs");
            if (!File.Exists(f)) continue;
            var s = File.ReadAllText(f);
            Assert.Contains("reused = true", s.Replace("reusedAdapter = true", "reused = true"));   // leftover adapter is taken over, not an error
            Assert.DoesNotContain("could not be removed", s);
            Assert.DoesNotContain("Close it and retry.", s);
            return;
        }
        Assert.Fail("WinNet.cs not found");
    }
}


public class UpdateButtonStateTests
{
    static string Read(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    [Fact]
    public void HeaderUpdateButton_IsDisabledWhileBusy_AndShowsProgress()
    {
        var xaml = Read("src", "app", "MainWindow.xaml");
        int a = xaml.IndexOf("Command=\"{Binding InstallUpdate}\"");
        Assert.True(a > 0);
        var tag = xaml[(xaml.LastIndexOf("<Button", a))..(xaml.IndexOf('>', a) + 1)];
        Assert.Contains("IsEnabled=\"{Binding UpdateBusy", tag);                        // cannot be pressed again while an update runs
        Assert.Contains("<ProgressBar", xaml); Assert.Contains("Binding UpdateProgress", xaml); Assert.Contains("Binding UpdateChip", xaml);
    }

    [Fact]
    public void InstallUpdate_IgnoresSecondClicks_AndResetsAfterFailure()
    {
        var vm = Read("src", "app", "ShellViewModel.cs");
        int a = vm.IndexOf("async Task OnInstallUpdate");
        var body = vm[a..vm.IndexOf("bool _demo;", a)];
        Assert.Contains("if (_update == null || UpdateBusy) return;", body);
        Assert.Contains("UpdateBusy = true;", body);
        Assert.Contains("catch", body); Assert.Contains("UpdateBusy = false;", body);      // user can retry after an error
        foreach (var phase in new[] { "Downloading", "Verifying", "Installing" }) Assert.Contains(phase, body);
    }
}


public class WifiSharingCardTests
{
    static string Read(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    [Fact]
    public void ConnectPage_HasAWifiSharingCard_WithSwitchPasswordAndManualControls()
    {
        var xaml = Read("src", "app", "MainWindow.xaml");
        foreach (var binding in new[] { "ToggleShare", "ShareStateText", "ShareSsid", "SharePasswordDisplay", "CopySharePassword", "ToggleSharePasswordVisible", "StartShare", "StopShare", "ShareClientsText" })
            Assert.Contains("{Binding " + binding, xaml);
    }

    [Fact]
    public void Sharing_NeverStartsWithoutAnActiveConnection_AndTurnOnAsksForConfirmation()
    {
        var core = Read("src", "core", "Orchestrator.cs");
        Assert.Contains("Connect first: Wi-Fi sharing shares the verified CiciPDA connection.", core);
        Assert.Contains("public bool CanShareNow => IsActive && Method is ConnectionMethod.PhoneVpn or ConnectionMethod.Usb;", core);
        var vm = Read("src", "app", "ShellViewModel.cs");
        int a = vm.IndexOf("async Task OnToggleShare");
        var body = vm[a..(a + 1500)];
        Assert.Contains("Dialogs.Confirm", body);                    // an outward-facing network change is confirmed
        Assert.Contains("GeneratePassphrase", body);                  // a strong passphrase exists before the hotspot can start
    }
}


[Collection("AppPaths")]
public class DnsPolicyTests
{
    [Fact]
    public async Task NrptCmdlets_WeCall_ExistWithTheParametersWeUse()
    {
        var r = await ProcessRunner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
            "(Get-Command Add-DnsClientNrptRule -Syntax) + (Get-Command Remove-DnsClientNrptRule -Syntax) + (Get-Command Get-DnsClientNrptRule -Syntax)" }, TimeSpan.FromSeconds(30));
        foreach (var p in new[] { "-Namespace", "-NameServers", "-DisplayName", "-Comment", "-Force" }) Assert.Contains(p, r.Combined);
    }

    [Fact]
    public void Scripts_OnlyTouchTheRuleNamedCiciPDA_AndTakeServersFromTheEnvironment()
    {
        Assert.Contains("DisplayName -eq 'CiciPDA'", DnsPolicy.RemoveScript);                // never removes anybody else's rule
        Assert.Contains("-Namespace '.'", DnsPolicy.AddScript);
        Assert.Contains("$env:CICI_DNS", DnsPolicy.AddScript);                               // no server text is spliced into the script
        Assert.DoesNotContain("Remove-DnsClientNrptRule -Name *", DnsPolicy.RemoveScript);
    }

    [Fact]
    public async Task Add_RejectsInvalidServers_WithoutRunningAnything()
    {
        var (ok, detail) = await DnsPolicy.AddAsync(new[] { "1.1.1.1; Remove-Item C:\\ -Recurse" });
        Assert.False(ok); Assert.Contains("invalid", detail);
    }

    [Fact]
    public async Task RuleExists_CanBeQueriedWithoutAdmin()
    {
        // read-only query: must not throw; on this PC there is normally no CiciPDA rule
        _ = await DnsPolicy.RuleExistsAsync();
    }

    [Fact]
    public void Journal_RemembersTheRule_SoCrashRecoveryRemovesIt()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-nrpt-" + Guid.NewGuid());
        var j = new RecoveryJournal(); j.State.NrptActive = true; j.Save();
        Assert.True(new RecoveryJournal().State.NrptActive);
        Assert.False(new RecoveryJournal().State.IsEmpty);
        j.Reset();
    }

    [Fact]
    public void Disconnect_RemovesThePolicyFirst_AndRollbackChecksIt()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "core", "WinNet.cs");
            if (!File.Exists(f)) continue;
            var s = File.ReadAllText(f);
            int stop = s.IndexOf("public async Task StopAsync()");
            Assert.True(s.IndexOf("DnsPolicy.RemoveAsync()", stop) < s.IndexOf("StopGracefullyAsync", stop));
            Assert.Contains("the CiciPDA DNS policy rule is still present", s);
            return;
        }
        Assert.Fail("WinNet.cs not found");
    }
}


[Collection("AppPaths")]
public class WifiHandoverTests
{
    [Theory]
    [InlineData(false, false, false, false)]   // nothing asked: leave the PC's Wi-Fi alone
    [InlineData(true,  false, false, true)]    // user asked for it
    [InlineData(false, true,  false, true)]    // sharing needs the radio
    [InlineData(true,  true,  true,  false)]   // never when the phone itself is reached over Wi-Fi
    [InlineData(false, true,  true,  false)]
    public void ShouldRelease_TruthTable(bool drop, bool share, bool phoneOverWifi, bool expected) => Assert.Equal(expected, WifiHandover.ShouldRelease(drop, share, phoneOverWifi));

    [Fact]
    public async Task Current_ReadsTheRealWifiNetwork_LikeNetsh_WithoutChangingAnything()
    {
        var link = WifiHandover.Current();                                   // read-only Native Wifi query (language independent)
        var netsh = await ProcessRunner.RunAsync("netsh.exe", new[] { "wlan", "show", "interfaces" }, TimeSpan.FromSeconds(15));
        if (link == null) { Assert.DoesNotContain(" SSID ", netsh.Stdout.Replace("BSSID", "")); return; }     // not on Wi-Fi: netsh must agree
        Assert.False(string.IsNullOrWhiteSpace(link.Profile)); Assert.False(string.IsNullOrWhiteSpace(link.Ssid));
        Assert.Contains(link.Ssid, netsh.Stdout);                                                            // same network as Windows reports
        Assert.NotEqual(Guid.Empty, link.InterfaceGuid);
    }

    [Fact]
    public void Journal_RemembersTheReleasedWifi_SoCrashRecoveryCanRejoinIt()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-wifi-" + Guid.NewGuid());
        var j = new RecoveryJournal(); j.State.WifiProfile = "Home"; j.State.WifiInterfaceGuid = Guid.NewGuid().ToString(); j.State.WifiSsid = "Home"; j.Save();
        var j2 = new RecoveryJournal();
        Assert.Equal("Home", j2.State.WifiProfile); Assert.False(j2.State.IsEmpty);
        j2.Reset();
    }

    static string Src(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    [Fact]
    public void Orchestrator_ReleasesWifiBeforeTheTunnel_AndRestoresItLastOnEveryExitPath()
    {
        var s = Src("src", "core", "Orchestrator.cs");
        Assert.True(s.IndexOf("ReleaseWifiIfNeeded(dev);") < s.IndexOf("_net = new TunnelNetwork(_journal);"));
        int td = s.IndexOf("async Task TeardownAsync");
        Assert.True(s.IndexOf("RestoreWifi();", td) > s.IndexOf("_journal.Reset();", td));                 // after the tunnel and adapter are gone
        Assert.Contains("_journal.State.WifiProfile = link.Profile;", s);                                      // journaled BEFORE disconnecting
        Assert.True(s.IndexOf("_journal.State.WifiProfile = link.Profile;") < s.IndexOf("WifiHandover.Disconnect(link)"));
    }
}

public class AppBehaviourTests
{
    static string Src(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    [Fact]
    public void CtrlBreak_HasARealHandler_SoTheAppCannotKillItself()
    {
        // Regression: SetConsoleCtrlHandler(NULL, TRUE) only ignores Ctrl+C. Sending Ctrl+Break to tun2socks while attached to its console terminated CiciPDA itself
        // (the app closed on Disconnect/failed Connect in 2.1.7-2.2.0).
        var s = Src("src", "core", "ProcessRunner.cs");
        Assert.Contains("SetConsoleCtrlHandler(SwallowCtrl, true)", s);
        Assert.Contains("type is 0 or 1", s);
        Assert.DoesNotContain("SetConsoleCtrlHandler(IntPtr.Zero, true)", s);
    }

    [Fact]
    public void CloseButton_HidesToTrayWhileConnected_AndRealExitsAreExplicit()
    {
        var w = Src("src", "app", "MainWindow.xaml.cs");
        Assert.Contains("System.Windows.Forms.NotifyIcon", w);
        Assert.Contains("!_quitRequested && !App.Quitting && (_orch.IsActive || _orch.IsBusy)", w);
        Assert.Contains("HideToTray();", w);
        Assert.Contains("Quit CiciPDA", w); Assert.Contains("Disconnect", w); Assert.Contains("Open CiciPDA", w);
        var a = Src("src", "app", "App.xaml.cs");
        Assert.Contains("SessionEnding += (_, _) => Quitting = true;", a);
        var vm = Src("src", "app", "ShellViewModel.cs");
        Assert.Contains("App.Quitting = true;", vm);                                        // updater and restart-as-admin really exit
    }

    [Fact]
    public void SecondLaunch_AsksTheRunningInstanceToShowItself()
    {
        var a = Src("src", "app", "App.xaml.cs");
        Assert.Contains("EventWaitHandle.OpenExisting(ShowEventName)", a);
        Assert.Contains("win.ShowFromTray()", a);
    }
}

public class WindowPlacementTests
{
    static readonly Box Screen = new(0, 0, 1920, 1080);

    [Fact] public void NothingSaved_MeansCentre() => Assert.Null(WindowPlacement.Fit(null, null, null, null, Screen));

    [Fact]
    public void SavedPositionOnScreen_IsRestoredExactly()
    {
        var b = WindowPlacement.Fit(300, 120, 1200, 760, Screen);
        Assert.Equal(new Box(300, 120, 1200, 760), b);
    }

    [Theory]
    [InlineData(5000, 100)]      // monitor that no longer exists (far right)
    [InlineData(-4000, 100)]     // far left
    [InlineData(100, 3000)]      // far below
    public void SavedPositionOffScreen_FallsBackToCentre(double left, double top) => Assert.Null(WindowPlacement.Fit(left, top, 1200, 760, Screen));

    [Fact]
    public void WindowSlightlyOutside_IsPulledBackOnScreen()
    {
        var b = WindowPlacement.Fit(1000, 600, 1200, 760, Screen)!.Value;
        Assert.True(b.Right <= Screen.Right + 0.01 && b.Bottom <= Screen.Bottom + 0.01 && b.Left >= 0 && b.Top >= 0);
    }

    [Fact]
    public void TooSmallOrHugeSizes_AreClamped()
    {
        var small = WindowPlacement.Fit(100, 100, 300, 200, Screen)!.Value;
        Assert.True(small.Width >= WindowPlacement.MinWidth && small.Height >= WindowPlacement.MinHeight);
        var huge = WindowPlacement.Fit(0, 0, 9000, 9000, Screen)!.Value;
        Assert.True(huge.Width <= Screen.Width && huge.Height <= Screen.Height);
    }

    [Fact] public void NaNValues_AreIgnored() => Assert.Null(WindowPlacement.Fit(double.NaN, 10, 1200, 760, Screen));

    [Fact]
    public void WindowOnSecondaryMonitorWithNegativeCoordinates_IsKept()
    {
        var two = new Box(-1920, 0, 3840, 1080);                 // monitor to the left of the primary
        Assert.Equal(new Box(-1700, 80, 1200, 760), WindowPlacement.Fit(-1700, 80, 1200, 760, two));
    }

    [Fact]
    public void SettingsPage_DoesNotOverwriteSavedPosition()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "app", "ShellViewModel.cs");
            if (!File.Exists(f)) continue;
            Assert.Contains("e.WindowLeft = c.WindowLeft;", File.ReadAllText(f));
            return;
        }
        Assert.Fail("ShellViewModel.cs not found");
    }
}


[Collection("AppPaths")]
public class HotspotDevicesTests
{
    static HotspotDeviceStore NewStore() { AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-hsd-" + Guid.NewGuid()); return new HotspotDeviceStore(); }

    [Fact]
    public void Names_PreferAlias_ThenHostName_ThenUnknown()
    {
        var s = NewStore();
        var c = new HotspotClient("AA-BB-CC-DD-EE-FF", "DESKTOP-1", "192.168.137.5");
        Assert.Equal("DESKTOP-1", s.DisplayName(c));
        s.SetAlias(c.Mac, "Mike's laptop"); Assert.Equal("Mike's laptop", s.DisplayName(c));
        Assert.Equal("Unknown device", s.DisplayName(new HotspotClient("11-22-33-44-55-66", "", "")));
    }

    [Fact]
    public void Alias_SurvivesRestart_AndIsCaseInsensitiveOnMac()
    {
        var s = NewStore(); s.SetAlias("aa-bb-cc-dd-ee-ff", "Phone");
        var again = new HotspotDeviceStore();
        Assert.Equal("Phone", again.Get("AA-BB-CC-DD-EE-FF")!.Alias);
    }

    [Fact]
    public void Observe_TracksSessionStart_AndClearsItWhenTheDeviceLeaves()
    {
        var s = NewStore(); var c = new HotspotClient("AA-BB-CC-DD-EE-01", "x", "192.168.137.2");
        s.Observe(new[] { c });
        Assert.NotNull(s.Get(c.Mac)!.SessionStart); Assert.Equal("192.168.137.2", s.Get(c.Mac)!.LastIp);
        s.Observe(Array.Empty<HotspotClient>());
        Assert.Null(s.Get(c.Mac)!.SessionStart);                            // left: no longer "connected"
        Assert.NotNull(s.Get(c.Mac));                                       // but still known
    }

    [Fact]
    public void AliasLength_IsLimited() { var s = NewStore(); s.SetAlias("AA-00", new string('x', 200)); Assert.Equal(40, s.Get("AA-00")!.Alias.Length); }

    [Fact]
    public async Task StatusScript_RunsOnThisWindows_AndReturnsStateCountAndList_ReadOnly()
    {
        // Query a real Wi-Fi connection profile (read-only): proves GetTetheringClients/Get-NetNeighbor syntax and the JSON shape.
        var wifi = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
        if (wifi == null || !Guid.TryParse(wifi.Id, out var g)) return;
        var json = await HotspotService.RawStatusAsync(g.ToString());
        Assert.NotNull(json);
        using var d = System.Text.Json.JsonDocument.Parse(json!);
        var root = d.RootElement;
        var state = root.GetProperty("state").GetString();
        Assert.True(state is "On" or "Off" or "InTransition" or "NoProfile" or "Unknown", "unexpected state " + state);
        if (state != "NoProfile") { Assert.True(root.TryGetProperty("clients", out _)); Assert.True(root.TryGetProperty("list", out _)); }
    }

    [Fact]
    public void UiAndCommands_ForSharedDevices_Exist()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "app", "MainWindow.xaml");
            if (!File.Exists(f)) continue;
            var xaml = File.ReadAllText(f);
            foreach (var b in new[] { "SharedDevices", "ChangeSharePassword", "RefreshSharedDevices", "Rename", "CopyIp", "CopyMac" }) Assert.Contains("{Binding " + b, xaml);
            Assert.Contains("cannot disconnect or block a single device", xaml);          // the limitation is stated, not hidden
            var core = File.ReadAllText(Path.Combine(d.FullName, "src", "core", "Orchestrator.cs"));
            Assert.Contains("ChangeSharingPasswordAsync", core);
            return;
        }
        Assert.Fail("MainWindow.xaml not found");
    }
}
