using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Linq;
using System.Net.NetworkInformation;

namespace CiciPDA
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, args) => {
                args.Handled = true;
                Log.Error("UI", args.Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, args) => {
                args.SetObserved();
                Log.Error("Task", args.Exception);
            };
            new Bootstrapper().Run();
        }
    }

    public sealed class Bootstrapper
    {
        MainWindow _win;
        Coordinator _coord;

        public void Run()
        {
            _coord = new Coordinator();
            _win = new MainWindow { DataContext = new MainViewModel(_coord) };
            _win.Closed += (s, e) => _coord.Dispose();
            _win.Show();
        }
    }

    public sealed class Coordinator : IDisposable
    {
        WintunAdapter _adapter;
        TunnelClient _tunnel;
        HotspotManager _hotspot;
        string _adbPath, _serial;
        byte[] _key;
        CancellationTokenSource _cts;
        bool _running;

        public event Action<string, string> StatusChanged;
        public event Action<long, long> TrafficUpdated;

        public async Task<bool> StartAsync(string adbPath, string serial, string ssid, string password, string shareAdapter)
        {
            if (_running) return false;
            _adbPath = adbPath; _serial = serial;
            _key = RandomNumberGenerator.GetBytes(32);
            _cts = new CancellationTokenSource();

            try
            {
                Status("adapter", "Creating Wintun adapter...");
                _adapter = new WintunAdapter("CiciPDA");
                _adapter.Start(1500);
                await Task.Delay(1000);

                Status("route", "Setting routes and DNS...");
                RouteManager.AddDefaultRoute(_adapter.Name, "10.0.0.1");
                RouteManager.SetDNS(_adapter.Name, "10.0.0.1", "1.1.1.1");
                NCSI.EnableActiveProbing();

                Status("tunnel", "Starting ADB tunnel...");
                _tunnel = new TunnelClient(_adbPath, _serial, 12345, 8899, _key);
                await _tunnel.StartAsync(_cts.Token);

                Status("agent", "Deploying agent to phone...");
                await DeployAgentAsync();

                Status("hotspot", "Starting Wi-Fi Hotspot...");
                _hotspot = new HotspotManager();
                await _hotspot.StartAsync(ssid, password, _adapter.Name, shareAdapter);

                Status("ics", "Enabling Internet Connection Sharing...");
                await ICSManager.EnableSharingAsync(_adapter.Name, shareAdapter);

                await NCSI.WaitForNCSI(_adapter.Name);

                _running = true;
                Status("done", "Connected - sharing via " + ssid);
                _ = MonitorTrafficAsync(_cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                Status("error", ex.Message);
                await StopAsync();
                return false;
            }
        }

        async Task DeployAgentAsync()
        {
            var dex = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.dex");
            if (!File.Exists(dex)) throw new FileNotFoundException("agent.dex not found");

            var psi = new ProcessStartInfo {
                FileName = _adbPath, Arguments = $"-s {_serial} push \"{dex}\" /data/local/tmp/agent.dex",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException("adb push failed");

            var keyHex = BitConverter.ToString(_key).Replace("-", "").ToLower();
            psi = new ProcessStartInfo {
                FileName = _adbPath,
                Arguments = $"-s {_serial} shell \"CLASSPATH=/data/local/tmp/agent.dex app_process / TunnelMain 8899 <<< '{keyHex}'\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            p = Process.Start(psi);
            await p.WaitForExitAsync();
            var output = await p.StandardOutput.ReadToEndAsync();
            if (!output.Contains("READY")) throw new InvalidOperationException("Agent start failed: " + output);
        }

        async Task MonitorTrafficAsync(CancellationToken ct)
        {
            long lastRx = 0, lastTx = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ni = _adapter.Interface;
                    if (ni != null)
                    {
                        var stats = ni.GetIPv4Statistics();
                        long rx = stats.BytesReceived, tx = stats.BytesSent;
                        TrafficUpdated?.Invoke(rx - lastRx, tx - lastTx);
                        lastRx = rx; lastTx = tx;
                    }
                }
                catch { }
                await Task.Delay(1000, ct);
            }
        }

        public async Task StopAsync()
        {
            if (!_running && _cts == null) return;
            _running = false;
            _cts?.Cancel();

            Status("stopping", "Stopping services...");

            if (_hotspot != null) { await _hotspot.StopAsync(); _hotspot.Dispose(); _hotspot = null; }
            if (_tunnel != null) { _tunnel.Dispose(); _tunnel = null; }
            if (_adapter != null)
            {
                RouteManager.RemoveDefaultRoute(_adapter.Name);
                _adapter.Dispose(); _adapter = null;
            }
            await ICSManager.DisableSharingAsync("CiciPDA", "");

            _cts?.Dispose(); _cts = null;
            Status("stopped", "Disconnected");
        }

        void Status(string stage, string msg) => StatusChanged?.Invoke(stage, msg);

        public void Dispose() => StopAsync().Wait(10000);
    }

    public class MainViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        readonly Coordinator _coord;
        string _status = "Ready";
        string _detail = "";
        bool _running;
        long _rx, _tx;

        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(); } }
        public bool Running { get => _running; set { _running = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotRunning)); } }
        public bool NotRunning => !_running;
        public long Rx { get => _rx; set { _rx = value; OnPropertyChanged(); OnPropertyChanged(nameof(RxStr)); } }
        public long Tx { get => _tx; set { _tx = value; OnPropertyChanged(); OnPropertyChanged(nameof(TxStr)); } }
        public string RxStr => FormatBytes(_rx) + "/s";
        public string TxStr => FormatBytes(_tx) + "/s";

        public System.Windows.Input.ICommand StartCmd { get; }
        public System.Windows.Input.ICommand StopCmd { get; }

        public MainViewModel(Coordinator coord)
        {
            _coord = coord;
            _coord.StatusChanged += (s, m) => { Status = s; Detail = m; };
            _coord.TrafficUpdated += (rx, tx) => { Rx = rx; Tx = tx; };

            StartCmd = new RelayCommand(async _ => {
                Running = true;
                var adb = FindAdb();
                var serial = (await AdbDevicesAsync(adb)).FirstOrDefault();
                if (serial == null) { Detail = "No device"; Running = false; return; }
                await _coord.StartAsync(adb, serial, "CiciPDA", "cicipda123", "Wi-Fi");
            }, _ => !Running);

            StopCmd = new RelayCommand(async _ => {
                await _coord.StopAsync();
                Running = false;
            }, _ => Running);
        }

        static string FindAdb()
        {
            var paths = new[] {
                @"C:\Users\Captain\Android\Sdk\platform-tools\adb.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
                "adb.exe"
            };
            return paths.FirstOrDefault(File.Exists) ?? "adb.exe";
        }

        static async Task<string[]> AdbDevicesAsync(string adb)
        {
            var psi = new ProcessStartInfo { FileName = adb, Arguments = "devices", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return outp.Split('\n').Skip(1).Where(l => l.Contains("\tdevice")).Select(l => l.Split('\t')[0]).ToArray();
        }

        static string FormatBytes(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b/1024.0:F1} KB" : $"{b/1048576.0:F1} MB";

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public class RelayCommand : System.Windows.Input.ICommand
    {
        readonly Func<object, Task> _exec; readonly Func<object, bool> _can;
        public RelayCommand(Func<object, Task> exec, Func<object, bool> can = null) { _exec = exec; _can = can; }
        public bool CanExecute(object p) => _can?.Invoke(p) ?? true;
        public event EventHandler CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public async void Execute(object p) => await _exec(p);
    }

    public static class Log
    {
        public static void Error(string tag, Exception ex) => Debug.WriteLine($"[{tag}] {ex}");
    }
}