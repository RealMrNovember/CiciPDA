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
            Assert.Contains("RemoveAdapterAsync(AdapterName)", s);
            Assert.DoesNotContain("Close it and retry.", s);
            return;
        }
        Assert.Fail("WinNet.cs not found");
    }
}
