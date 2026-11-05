using System.Net.NetworkInformation;

namespace CiciPDA;

public enum IphoneMode { None, UsbAppleEthernet, WifiPersonalHotspot }

public sealed record IfaceInfo(string Name, string Description, NetworkInterfaceType Type, bool Up, bool HasUsableIPv4);

/// <summary>
/// Interface selection for the connection methods that use a link Windows already has (no CiciPDA tunnel, no Android companion):
///   * iPhone over USB: Apple's driver exposes an "Apple Mobile Device Ethernet" adapter once Personal Hotspot is on and the PC is trusted.
///   * iPhone Personal Hotspot over Wi-Fi: the PC has joined the phone's Wi-Fi network.
///   * Android Wi-Fi hotspot: the PC has joined the phone's Wi-Fi network.
/// CiciPDA only verifies and monitors these links; the traffic is not tunnelled or encrypted by CiciPDA.
/// </summary>
public static class NativeLinks
{
    public static List<IfaceInfo> Snapshot() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(n => new IfaceInfo(n.Name, n.Description, n.NetworkInterfaceType, n.OperationalStatus == OperationalStatus.Up,
            n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254")))).ToList();

    static bool Usable(IfaceInfo i) => i.Up && i.HasUsableIPv4;

    public static IfaceInfo? SelectWifi(IEnumerable<IfaceInfo> all, string? connectedSsid) =>
        connectedSsid == null ? null : all.FirstOrDefault(i => Usable(i) && i.Type == NetworkInterfaceType.Wireless80211);

    public static (IfaceInfo? iface, IphoneMode mode) SelectIphone(IEnumerable<IfaceInfo> all, string? connectedSsid)
    {
        var list = all.ToList();
        var usb = list.FirstOrDefault(i => Usable(i) && i.Description.Contains("Apple Mobile Device", StringComparison.OrdinalIgnoreCase));
        if (usb != null) return (usb, IphoneMode.UsbAppleEthernet);
        var wifi = SelectWifi(list, connectedSsid);
        return wifi != null ? (wifi, IphoneMode.WifiPersonalHotspot) : (null, IphoneMode.None);
    }

    /// <summary>True when an Apple USB Ethernet adapter exists but is not usable yet (driver present, hotspot off or phone not trusted).</summary>
    public static bool AppleAdapterPresentButIdle(IEnumerable<IfaceInfo> all) =>
        all.Any(i => i.Description.Contains("Apple Mobile Device", StringComparison.OrdinalIgnoreCase) && !Usable(i));
}
