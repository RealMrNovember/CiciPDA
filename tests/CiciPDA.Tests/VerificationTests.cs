using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CiciPDA.Tests;

public class VerificationTests
{
    [Fact]
    public void DnsQuery_BuildsValidMessage_AndRecognisesOnlyMatchingResponses()
    {
        var q = DnsQuery.Build("example.com", 0x1234);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00 }, q[..4]);
        Assert.Equal(new byte[] { 7, (byte)'e' }, q[12..14]);
        var resp = (byte[])q.Clone(); resp[2] |= 0x80;                                // QR bit set
        Assert.True(DnsQuery.IsResponseFor(0x1234, resp));
        Assert.False(DnsQuery.IsResponseFor(0x1235, resp));                           // wrong transaction id
        Assert.False(DnsQuery.IsResponseFor(0x1234, q));                              // a query is not an answer
        Assert.False(DnsQuery.IsResponseFor(0x1234, new byte[5]));
    }

    [Fact]
    public async Task UdpDns_Passes_WhenServerAnswers_AndFails_WhenSilent()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () => { try { while (true) { var r = await server.ReceiveAsync(cts.Token); var a = r.Buffer.ToArray(); a[2] |= 0x80; await server.SendAsync(a, a.Length, r.RemoteEndPoint); } } catch { } });
        var ok = await Verifier.UdpDns("127.0.0.1", port);
        Assert.True(ok.Passed, ok.Detail); Assert.False(ok.Required);
        cts.Cancel();

        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); int sp = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var bad = await Verifier.UdpDns("127.0.0.1", sp);
        Assert.False(bad.Passed); Assert.NotNull(bad.Fix);
    }

    [Fact]
    public async Task AdapterReady_IsLocalScope_AndFailsForMissingAdapter()
    {
        var r = await Verifier.AdapterReady(int.MaxValue);
        Assert.False(r.Passed); Assert.Equal("Local", r.Scope);
        var real = NetworkInterface_Loopback();
        if (real > 0) { var ok = await Verifier.AdapterReady(real); Assert.Equal("Local", ok.Scope); }
    }

    static int NetworkInterface_Loopback()
    {
        foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback && n.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4)) return WinNet.IndexOf(n);
        return -1;
    }

    [Fact]
    public async Task Ipv6Report_DistinguishesProtectionFromConnectivity()
    {
        var off = await Verifier.Ipv6(false, false); Assert.Contains("OFF", off.Detail);
        var fail = await Verifier.Ipv6(true, false); Assert.Contains("could NOT be applied", fail.Detail);
        var on = await Verifier.Ipv6(true, true); Assert.Contains("ACTIVE", on.Detail);
        Assert.All(new[] { off, fail, on }, x => { Assert.False(x.Required); Assert.Contains("IPv6", x.Detail); });
    }

    [Fact]
    public void RouteListed_ParsesNetshOutput()
    {
        const string o = "Publish  Type      Met  Prefix                    Idx  Gateway/Interface Name\n" +
                         "No       Manual    1    192.168.1.50/32            7   Wi-Fi\n" +
                         "No       System    256  127.0.0.0/8                1   Loopback Pseudo-Interface 1\n";
        Assert.True(RollbackCheck.RouteListed(o, "192.168.1.50/32", 7));
        Assert.False(RollbackCheck.RouteListed(o, "192.168.1.50/32", 8));      // same prefix, different interface
        Assert.False(RollbackCheck.RouteListed(o, "10.0.0.0/8", 7));
    }

    [Fact]
    public async Task Rollback_WhenNothingWasInstalled_ReportsClean()
    {
        if (WinNet.FindByName(TunnelNetwork.AdapterName) != null) return;   // a real CiciPDA connection is active on this PC right now; not a rollback situation
        var (ok, detail) = await RollbackCheck.VerifyAsync(Array.Empty<BypassRoute>());
        Assert.True(ok, detail);
    }

    [Fact]
    public void Verification_RequiresInternetChecksNotJustLocalOnes()
    {
        // Guard against regressing to "adapter up == connected": the orchestrator must fail when DNS, HTTP and HTTPS all fail.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "core", "Orchestrator.cs");
            if (!File.Exists(f)) continue;
            var s = File.ReadAllText(f);
            Assert.Contains("r.Id is \"dns\" or \"http\" or \"https\"", s);
            Assert.Contains("Verifier.UdpDns", s); Assert.Contains("Verifier.AdapterReady", s); Assert.Contains("Verifier.WindowsConnectivity", s);
            return;
        }
        Assert.Fail("Orchestrator.cs not found");
    }
}
