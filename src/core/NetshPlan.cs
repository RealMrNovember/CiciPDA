namespace CiciPDA;

/// <summary>
/// The exact netsh commands CiciPDA runs, built in one place so they can be reviewed and unit-tested without touching the machine.
/// Every change is non-persistent (store=active) and every added route has a matching delete, so a reboot or rollback never leaves stale state.
/// </summary>
public static class NetshPlan
{
    public const string TunNetmask = "255.255.255.0";
    public const string TunIpv6 = "fd66:6c1c:6f00::2/64";
    public static readonly string[] V4HalfRoutes = { "0.0.0.0/1", "128.0.0.0/1" };
    public static readonly string[] V6HalfRoutes = { "::/1", "8000::/1" };

    public static int ClampMtu(int mtu) => Math.Clamp(mtu, 576, 1500);

    public static string[] SetAddress(int idx, string address) => new[] { "interface", "ipv4", "set", "address", $"name={idx}", "source=static", $"address={address}", $"mask={TunNetmask}" };
    public static string[] SetMetric(int idx) => new[] { "interface", "ipv4", "set", "interface", $"name={idx}", "metric=1" };
    public static string[] SetMtu(int idx, int mtu) => new[] { "interface", "ipv4", "set", "subinterface", $"interface={idx}", $"mtu={ClampMtu(mtu)}", "store=active" };
    public static string[] SetDns(int idx, string primary) => new[] { "interface", "ipv4", "set", "dnsservers", $"name={idx}", "source=static", $"address={primary}", "validate=no" };
    public static string[] AddDns(int idx, string secondary) => new[] { "interface", "ipv4", "add", "dnsservers", $"name={idx}", $"address={secondary}", "index=2", "validate=no" };

    /// <summary>On-link route that pins the transport host (phone over ADB-over-Wi-Fi) to its original interface so it cannot loop into the tunnel.</summary>
    public static string[] AddBypass(string prefix, int originalIf) => new[] { "interface", "ipv4", "add", "route", $"prefix={prefix}", $"interface={originalIf}", "nexthop=0.0.0.0", "metric=1", "store=active" };
    public static string[] DeleteBypass(string prefix, int originalIf) => new[] { "interface", "ipv4", "delete", "route", $"prefix={prefix}", $"interface={originalIf}", "nexthop=0.0.0.0" };

    public static string[] AddTunnelRoute(string prefix, int idx) => new[] { "interface", "ipv4", "add", "route", $"prefix={prefix}", $"interface={idx}", "nexthop=0.0.0.0", "metric=1", "store=active" };
    public static string[] SetIpv6Address(int idx) => new[] { "interface", "ipv6", "set", "address", $"interface={idx}", $"address={TunIpv6}", "store=active" };
    public static string[] AddIpv6Route(string prefix, int idx) => new[] { "interface", "ipv6", "add", "route", $"prefix={prefix}", $"interface={idx}", "nexthop=::", "metric=1", "store=active" };

    /// <summary>Everything applied for a tunnel, in order, as (description, args, required). Used by TunnelNetwork and by tests.</summary>
    public static List<(string what, string[] args, bool required)> Apply(AppSettings s, int idx, string tunAddress, string? bypassPrefix, int originalIf)
    {
        var l = new List<(string, string[], bool)>
        {
            ("set address", SetAddress(idx, tunAddress), true),
            ("set metric", SetMetric(idx), false),
            ("set mtu", SetMtu(idx, s.Mtu), false),
            ("set dns", SetDns(idx, s.DnsPrimary), true),
        };
        if (!string.IsNullOrWhiteSpace(s.DnsSecondary)) l.Add(("add secondary dns", AddDns(idx, s.DnsSecondary), false));
        if (bypassPrefix != null && originalIf > 0) l.Add(("bypass route", AddBypass(bypassPrefix, originalIf), true));
        foreach (var p in V4HalfRoutes) l.Add(("add route " + p, AddTunnelRoute(p, idx), true));
        return l;
    }
}
