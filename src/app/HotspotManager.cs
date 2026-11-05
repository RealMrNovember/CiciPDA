using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;

namespace CiciPDA
{
    [ComImport, Guid("BA126AE5-2166-11D1-B1D0-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INetSharingManager
    {
        void get_SharingInstalled([Out, MarshalAs(UnmanagedType.Bool)] out bool installed);
        void get_SharingEnabled([Out, MarshalAs(UnmanagedType.Bool)] out bool enabled);
        void EnumEveryConnection([Out] out IEnumNetConnection enumConn);
        void get_INetConnectionProps(IntPtr connection, [Out] out INetConnectionProps props);
        void get_INetSharingConfigurationForINetConnection(IntPtr connection, [Out] out INetSharingConfiguration config);
        void get_INetSharingPortMappingCollection(IntPtr connection, [Out] out IntPtr collection);
        void get_INetSharingProtocolMappingCollection(IntPtr connection, [Out] out IntPtr collection);
        void get_NetConnectionProps(IntPtr connection, [Out] out INetConnectionProps props);
    }

    [ComImport, Guid("BA126AE6-2166-11D1-B1D0-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INetConnectionProps
    {
        void get_Guid([Out] out Guid guid);
        void get_Name([Out, MarshalAs(UnmanagedType.BStr)] out string name);
        void get_DeviceName([Out, MarshalAs(UnmanagedType.BStr)] out string deviceName);
        void get_Status([Out] out NETCON_STATUS status);
        void get_Characteristics([Out] out uint characteristics);
        void get_IconPath([Out, MarshalAs(UnmanagedType.BStr)] out string iconPath);
    }

    [ComImport, Guid("BA126AE7-2166-11D1-B1D0-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INetSharingConfiguration
    {
        void get_SharingConnectionType([Out] out SHARINGCONNECTIONTYPE type);
        void EnableSharing(SHARINGCONNECTIONTYPE type);
        void DisableSharing();
        void get_InternetFirewallEnabled([Out, MarshalAs(UnmanagedType.Bool)] out bool enabled);
        void put_InternetFirewallEnabled([In, MarshalAs(UnmanagedType.Bool)] bool enabled);
    }

    [ComImport, Guid("BA126AE8-2166-11D1-B1D0-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumNetConnection
    {
        void Next(int celt, [Out] IntPtr[] rgelt, [Out] out int pceltFetched);
        void Skip(int celt);
        void Reset();
        void Clone([Out] out IEnumNetConnection ppenum);
    }

    enum NETCON_STATUS { Disconnected = 0, Connecting = 1, Connected = 2, Disconnecting = 3, HardwareNotPresent = 4, HardwareDisabled = 5, HardwareMalfunction = 6, MediaDisconnected = 7, Authenticating = 8, AuthenticationSucceeded = 9, AuthenticationFailed = 10, InvalidAddress = 11, CredentialsRequired = 12 }
    enum SHARINGCONNECTIONTYPE { Public = 0, Private = 1 }

    public sealed class HotspotManager : IDisposable
    {
        INetSharingManager? _mgr;
        INetSharingConfiguration? _publicCfg, _privateCfg;
        string _ssid = "";
        string _pass = "";
        bool _running;

        public bool IsRunning => _running;
        public string SSID => _ssid;
        public string Password => _pass;

        public async Task<bool> StartAsync(string ssid, string password, string sourceAdapterName, string shareAdapterName)
        {
            if (_running) return true;
            _ssid = ssid; _pass = password;

            var type = Type.GetTypeFromCLSID(new Guid("BA126AE5-2166-11D1-B1D0-00805FC1270E"));
            if (type == null) throw new InvalidOperationException("ICS type not found");
            
            _mgr = (INetSharingManager?)Activator.CreateInstance(type);
            if (_mgr == null) throw new InvalidOperationException("Failed to create ICS Manager");

            bool installed, enabled;
            _mgr.get_SharingInstalled(out installed);
            _mgr.get_SharingEnabled(out enabled);
            if (!installed) throw new InvalidOperationException("ICS not installed");

            IEnumNetConnection enumConn;
            _mgr.EnumEveryConnection(out enumConn);

            IntPtr[] conns = new IntPtr[32];
            int fetched;
            enumConn.Next(conns.Length, conns, out fetched);

            string? publicGuid = null, privateGuid = null;

            for (int i = 0; i < fetched; i++)
            {
                if (conns[i] == IntPtr.Zero) continue;
                INetConnectionProps props;
                _mgr.get_INetConnectionProps(conns[i], out props);
                props.get_Name(out string name);
                props.get_Guid(out Guid guid);

                if (name.Equals(sourceAdapterName, StringComparison.OrdinalIgnoreCase))
                    publicGuid = guid.ToString("B");
                else if (name.Equals(shareAdapterName, StringComparison.OrdinalIgnoreCase))
                    privateGuid = guid.ToString("B");

                Marshal.ReleaseComObject(props);
            }
            Marshal.ReleaseComObject(enumConn);

            if (publicGuid == null) throw new ArgumentException("Source adapter not found: " + sourceAdapterName);
            if (privateGuid == null) throw new ArgumentException("Share adapter not found: " + shareAdapterName);

            _mgr.get_INetSharingConfigurationForINetConnection(
                new IntPtr(0), out INetSharingConfiguration cfg);
            cfg.EnableSharing(SHARINGCONNECTIONTYPE.Public);
            _mgr.get_INetSharingConfigurationForINetConnection(
                new IntPtr(0), out INetSharingConfiguration cfg2);
            cfg2.EnableSharing(SHARINGCONNECTIONTYPE.Private);

            await ConfigureHotspotAsync(ssid, password);
            _running = true;
            return true;
        }

        async Task ConfigureHotspotAsync(string ssid, string password)
        {
            var psi = new ProcessStartInfo {
                FileName = "powershell", UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"-Command \"Set-NetConnectionProfile -InterfaceAlias '{_ssid}' -NetworkCategory Private; " +
                    $"New-NetFirewallRule -DisplayName 'CiciPDA Hotspot' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 80,443,53; " +
                    $"netsh wlan set hostednetwork mode=allow ssid={ssid} key={password} keyUsage=persistent; " +
                    $"netsh wlan start hostednetwork\"",
                Verb = "runas"
            };
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }

        public async Task StopAsync()
        {
            if (!_running) return;
            _running = false;

            var psi = new ProcessStartInfo {
                FileName = "netsh", Arguments = "wlan stop hostednetwork",
                Verb = "runas", UseShellExecute = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi); 
            if (p != null) await p.WaitForExitAsync();

            try {
                _publicCfg?.DisableSharing(); _privateCfg?.DisableSharing();
            } catch { }
        }

        public void Dispose() { if (_running) StopAsync().Wait(5000); }
    }

    public sealed class ICSManager
    {
        public static async Task EnableSharingAsync(string sourceAdapter, string targetAdapter)
        {
            var type = Type.GetTypeFromCLSID(new Guid("BA126AE5-2166-11D1-B1D0-00805FC1270E"));
            if (type == null) return;
            
            var mgr = (INetSharingManager?)Activator.CreateInstance(type);
            if (mgr == null) return;

            mgr.get_SharingInstalled(out bool installed);
            if (!installed) throw new InvalidOperationException("ICS not installed");

            mgr.EnumEveryConnection(out IEnumNetConnection enumConn);
            IntPtr[] conns = new IntPtr[32];
            enumConn.Next(conns.Length, conns, out int fetched);

            IntPtr sourceConn = IntPtr.Zero, targetConn = IntPtr.Zero;
            for (int i = 0; i < fetched; i++)
            {
                mgr.get_INetConnectionProps(conns[i], out INetConnectionProps props);
                props.get_Name(out string name);
                if (name == sourceAdapter) sourceConn = conns[i];
                if (name == targetAdapter) targetConn = conns[i];
                Marshal.ReleaseComObject(props);
            }
            Marshal.ReleaseComObject(enumConn);

            if (sourceConn == IntPtr.Zero) throw new ArgumentException("Source adapter not found");
            if (targetConn == IntPtr.Zero) throw new ArgumentException("Target adapter not found");

            mgr.get_INetSharingConfigurationForINetConnection(sourceConn, out INetSharingConfiguration cfg);
            cfg.EnableSharing(SHARINGCONNECTIONTYPE.Public);

            mgr.get_INetSharingConfigurationForINetConnection(targetConn, out INetSharingConfiguration cfg2);
            cfg2.EnableSharing(SHARINGCONNECTIONTYPE.Private);
            
            await Task.CompletedTask; // Async uyarıları bastırmak için
        }

        public static async Task DisableSharingAsync(string sourceAdapter, string targetAdapter)
        {
            var type = Type.GetTypeFromCLSID(new Guid("BA126AE5-2166-11D1-B1D0-00805FC1270E"));
            if (type == null) return;
            
            var mgr = (INetSharingManager?)Activator.CreateInstance(type);
            if (mgr == null) return;
            
            mgr.EnumEveryConnection(out IEnumNetConnection enumConn);
            IntPtr[] conns = new IntPtr[32];
            enumConn.Next(conns.Length, conns, out int fetched);

            for (int i = 0; i < fetched; i++)
            {
                mgr.get_INetConnectionProps(conns[i], out INetConnectionProps props);
                props.get_Name(out string name);
                if (name == sourceAdapter || name == targetAdapter)
                {
                    mgr.get_INetSharingConfigurationForINetConnection(conns[i], out INetSharingConfiguration cfg);
                    cfg.DisableSharing();
                }
                Marshal.ReleaseComObject(props);
            }
            Marshal.ReleaseComObject(enumConn);
            
            await Task.CompletedTask;
        }
    }
}