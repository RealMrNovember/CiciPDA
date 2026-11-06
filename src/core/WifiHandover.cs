using System.Runtime.InteropServices;
using System.Text;

namespace CiciPDA;

public sealed record WifiLink(Guid InterfaceGuid, string Interface, string Profile, string Ssid);

/// <summary>
/// Frees the PC's Wi-Fi radio while the phone connection is in use: remembers the Wi-Fi network the PC is on, disconnects it, and joins it again
/// afterwards. Uses the Native Wifi API (wlanapi.dll), which - unlike parsing `netsh wlan` text - works the same on every Windows language.
/// </summary>
public static class WifiHandover
{
    const uint ClientVersion = 2;
    const int OpcodeCurrentConnection = 7;       // wlan_intf_opcode_current_connection
    const int StateConnected = 1;                // wlan_interface_state_connected
    const int ConnectionModeProfile = 0;         // wlan_connection_mode_profile
    const int BssTypeAny = 3;                    // dot11_BSS_type_any

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WlanConnectionParameters
    {
        public int ConnectionMode;
        public string Profile;                   // LPCWSTR
        public IntPtr Ssid;                      // PDOT11_SSID (null: use the profile)
        public IntPtr DesiredBssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] static extern uint WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern uint WlanConnect(IntPtr handle, ref Guid iface, ref WlanConnectionParameters p, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    /// <summary>The Wi-Fi network this PC is currently connected to (first connected WLAN interface), or null. Read-only.</summary>
    public static WifiLink? Current()
    {
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var h) != 0) return null;
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero) return null;
            try
            {
                int count = Marshal.ReadInt32(list, 0);
                int size = Marshal.SizeOf<WlanInterfaceInfo>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(list + 8 + i * size);
                    if (info.State != StateConnected) continue;
                    var g = info.InterfaceGuid;
                    if (WlanQueryInterface(h, ref g, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0 || data == IntPtr.Zero) continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: isState(4) mode(4) strProfileName[256 WCHAR] then WLAN_ASSOCIATION_ATTRIBUTES { DOT11_SSID {len(4), bytes[32]} ... }
                        var profile = Marshal.PtrToStringUni(data + 8, 256)?.TrimEnd('\0') ?? "";
                        int ssidLen = Marshal.ReadInt32(data, 8 + 512);
                        var ssidBytes = new byte[Math.Clamp(ssidLen, 0, 32)];
                        Marshal.Copy(data + 8 + 512 + 4, ssidBytes, 0, ssidBytes.Length);
                        if (profile.Length == 0) continue;
                        return new WifiLink(info.InterfaceGuid, info.Description, profile, Encoding.UTF8.GetString(ssidBytes));
                    }
                    finally { WlanFreeMemory(data); }
                }
                return null;
            }
            finally { WlanFreeMemory(list); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException) { return null; }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>Disconnects from the Wi-Fi network (the saved profile is kept). Returns true on success.</summary>
    public static bool Disconnect(WifiLink link)
    {
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var h) != 0) return false;
        try { var g = link.InterfaceGuid; return WlanDisconnect(h, ref g, IntPtr.Zero) == 0; }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>Joins the saved profile again. Returns true when Windows accepted the request (the join itself completes asynchronously).</summary>
    public static bool Reconnect(WifiLink link)
    {
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var h) != 0) return false;
        try
        {
            var g = link.InterfaceGuid;
            var p = new WlanConnectionParameters { ConnectionMode = ConnectionModeProfile, Profile = link.Profile, Ssid = IntPtr.Zero, DesiredBssidList = IntPtr.Zero, BssType = BssTypeAny, Flags = 0 };
            return WlanConnect(h, ref g, ref p, IntPtr.Zero) == 0;
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>
    /// Decide whether the PC's Wi-Fi must be released for this connection: when the user asked for it, or when Wi-Fi sharing is on (the radio is then needed to
    /// host the hotspot). Never when the phone itself is reached over Wi-Fi (ADB over Wi-Fi), because that would cut the tunnel's own transport.
    /// </summary>
    public static bool ShouldRelease(bool dropWifiSetting, bool shareWifiSetting, bool phoneReachedOverWifi) => !phoneReachedOverWifi && (dropWifiSetting || shareWifiSetting);
}
