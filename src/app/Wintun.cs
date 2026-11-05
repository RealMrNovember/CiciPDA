using System;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Http;

namespace CiciPDA
{
    public sealed class WintunAdapter : IDisposable
    {
        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern IntPtr WintunCreateAdapter(string adapterName, string tunnelType, string requestedGUID);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void WintunCloseAdapter(IntPtr adapter);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int WintunStartSession(IntPtr adapter, ulong capacity);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void WintunEndSession(IntPtr adapter);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint size);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void WintunSendPacket(IntPtr session, IntPtr packet);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr WintunReceivePacket(IntPtr session, out uint size);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr WintunGetAdapterLUID(IntPtr adapter);

        [DllImport("iphlpapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint SetInterfaceMtu(IntPtr adapter, uint mtu);

        IntPtr _adapter;
        IntPtr _session;
        string _name;
        ulong _guid;
        NetworkInterface? _ni;

        public string Name => _name;
        public ulong GUID => _guid;
        public NetworkInterface? Interface => _ni;
        public bool IsRunning => _session != IntPtr.Zero;

        public WintunAdapter(string name = "CiciPDA")
        {
            _name = name;
            _adapter = WintunCreateAdapter(name, "CiciPDA_Tunnel", Guid.NewGuid().ToString("B"));
            if (_adapter == IntPtr.Zero)
                throw new InvalidOperationException("WintunCreateAdapter failed: " + Marshal.GetLastWin32Error());

            _guid = (ulong)Marshal.ReadInt64(WintunGetAdapterLUID(_adapter));
            _ni = FindInterface();
        }

        NetworkInterface? FindInterface()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var luid = (ulong)ni.Id.GetHashCode();
                        if (luid == _guid) return ni;
                    }
                }
            }
            return null;
        }

        public void Start(uint mtu = 1500)
        {
            if (_session != IntPtr.Zero) return;
            int rc = WintunStartSession(_adapter, (ulong)mtu * 4096);
            if (rc != 0) throw new InvalidOperationException("WintunStartSession failed: " + rc);
            SetMTU(mtu);
        }

        void SetMTU(uint mtu)
        {
            var iface = _ni ?? FindInterface();
            if (iface != null)
            {
                uint idx = (uint)iface.GetIPProperties().GetIPv4Properties().Index;
                SetInterfaceMtu(new IntPtr(idx), mtu);
            }
        }

        public void Stop()
        {
            if (_session == IntPtr.Zero) return;
            WintunEndSession(_adapter);
            _session = IntPtr.Zero;
        }

        public IntPtr AllocateSend(uint size) => WintunAllocateSendPacket(_session, size);
        public void Send(IntPtr packet) => WintunSendPacket(_session, packet);
        public IntPtr Receive(out uint size) => WintunReceivePacket(_session, out size);
        public void ReleaseReceive(IntPtr packet) => WintunReleaseReceivePacket(_session, packet);

        public void Dispose() { Stop(); if (_adapter != IntPtr.Zero) WintunCloseAdapter(_adapter); }
    }

    public sealed class RouteManager
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint CreateIpForwardEntry2(IntPtr row);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint DeleteIpForwardEntry2(IntPtr row);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint SetInterfaceMetric(IntPtr interfaceIndex, uint metric);

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_IPFORWARD_ROW2 {
            public IntPtr InterfaceLuid;
            public ulong InterfaceIndex;
            public byte AddressFamily;
            public byte PrefixLength;
            public IntPtr NextHop;
            public ulong Metric;
            public ulong Age;
            public ulong ValidLifetime;
            public ulong PreferredLifetime;
        }

        public static void AddDefaultRoute(string adapterName, string gateway = "10.0.0.1", uint metric = 10)
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => x.Name == adapterName);
            if (ni == null) throw new ArgumentException("Adapter not found: " + adapterName);

            uint idx = (uint)ni.GetIPProperties().GetIPv4Properties().Index;
            var row = new MIB_IPFORWARD_ROW2 {
                InterfaceLuid = IntPtr.Zero,
                InterfaceIndex = idx,
                AddressFamily = 2,
                PrefixLength = 0,
                NextHop = IntPtr.Zero,
                Metric = metric
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(row));
            Marshal.StructureToPtr(row, ptr, false);
            uint rc = CreateIpForwardEntry2(ptr);
            Marshal.FreeHGlobal(ptr);
            if (rc != 0) throw new System.ComponentModel.Win32Exception((int)rc, "Add route failed");

            SetInterfaceMetric(new IntPtr(idx), metric);
        }

        public static void RemoveDefaultRoute(string adapterName)
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => x.Name == adapterName);
            if (ni == null) return;
            uint idx = (uint)ni.GetIPProperties().GetIPv4Properties().Index;
            var row = new MIB_IPFORWARD_ROW2 {
                InterfaceIndex = idx,
                AddressFamily = 2,
                PrefixLength = 0
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(row));
            Marshal.StructureToPtr(row, ptr, false);
            DeleteIpForwardEntry2(ptr);
            Marshal.FreeHGlobal(ptr);
        }

        public static void SetDNS(string adapterName, string dns1, string? dns2 = null)
        {
            using var proc = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "netsh", Arguments = $"interface ip set dns \"{adapterName}\" static {dns1}",
                    Verb = "runas", UseShellExecute = true, CreateNoWindow = true
                }
            };
            proc.Start(); proc.WaitForExit();
            if (!string.IsNullOrEmpty(dns2)) {
                proc.StartInfo.Arguments = $"interface ip add dns \"{adapterName}\" {dns2} index=2";
                proc.Start(); proc.WaitForExit();
            }
        }
    }

    public sealed class NCSI
    {
        public static void EnableActiveProbing()
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet", true);
            if (key != null) {
                key.SetValue("EnableActiveProbing", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        public static async Task<bool> WaitForNCSI(string adapterName, int timeoutMs = 30000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs) {
                try {
                    var ni = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(x => x.Name == adapterName);
                    if (ni != null && ni.OperationalStatus == OperationalStatus.Up) {
                        var props = ni.GetIPProperties();
                        if (props.GatewayAddresses.Count > 0 && props.UnicastAddresses.Count > 0) {
                            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                            var resp = await client.GetAsync("http://www.msftconnecttest.com/connecttest.txt");
                            if (resp.IsSuccessStatusCode) {
                                var txt = await resp.Content.ReadAsStringAsync();
                                if (txt.Contains("Microsoft Connect Test")) return true;
                            }
                        }
                    }
                } catch { }
                await Task.Delay(1000);
            }
            return false;
        }
    }
}