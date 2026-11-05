using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CiciPDA.App;

public partial class App : Application
{
    Mutex? _single;
    public static string[] Args { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = e.Args;
        bool preview = Args.Contains("--preview");

        if (!preview)
        {
            _single = new Mutex(true, "Global\\CiciPDA.SingleInstance", out bool first);
            if (!first) { MessageBox.Show("CiciPDA is already running.", "CiciPDA"); Shutdown(); return; }
        }

        Log.Init(AppPaths.DataFile("cicipda.log"));
        DispatcherUnhandledException += (_, a) => { Log.Error("ui", a.Exception, "Unhandled UI exception"); Log.Error("ui", a.Exception.ToString()); Notifier.Show(Severity.Error, a.Exception.Message); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("task", a.Exception.GetBaseException(), "Unobserved task exception"); a.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("fatal", (Exception)a.ExceptionObject, "Fatal");

        var settings = new SettingsStore();
        Theme.Apply(settings.Current.Theme);
        Log.Info("app", $"CiciPDA starting (elevated: {WinNet.IsElevated()}).");

        var adb = new AdbService(() => settings.Current);
        var journal = new RecoveryJournal();
        if (!preview) { try { journal.RecoverAsync(adb).Wait(TimeSpan.FromSeconds(20)); } catch (Exception ex) { Log.Error("recovery", ex); } }

        var devices = new DeviceStore(); var history = new HistoryStore();
        var orch = new Orchestrator(settings, adb, devices, history, journal);
        var vm = new ShellViewModel(orch, settings, history, devices, new DiagnosticsService(adb, orch), adb);
        var win = new MainWindow(vm, orch);
        MainWindow = win;

        if (preview) { RunPreview(win, vm); return; }
        win.Show();
        if (settings.Current.StartMinimized) win.WindowState = WindowState.Minimized;
        if (settings.Current.AutoConnect && (WinNet.IsElevated() || settings.Current.LastMethod is ConnectionMethod.AndroidHotspot or ConnectionMethod.IphoneHotspot))
            _ = Task.Run(async () => { await Task.Delay(2500); await orch.RefreshDevicesAsync(); await orch.ConnectAsync(settings.Current.LastMethod); });
    }

    /// <summary>Development-only: renders a page to PNG. "--demo" fills sample numbers; production never does.</summary>
    void RunPreview(MainWindow win, ShellViewModel vm)
    {
        int i = Array.IndexOf(Args, "--preview");
        string page = Args[i + 1], outFile = Args[i + 2];
        bool demo = Args.Contains("--demo");
        int w = 1280, h = 720;
        var ix = Array.IndexOf(Args, "--size"); if (ix >= 0) { w = int.Parse(Args[ix + 1]); h = int.Parse(Args[ix + 2]); }
        if (demo) vm.ApplyDemo();
        vm.Page = page;
        win.Width = w; win.Height = h; win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = -3000; win.Top = 0; win.ShowInTaskbar = false;
        win.Show();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            win.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(outFile)) enc.Save(f);
            Environment.Exit(0);
        };
        t.Start();
    }
}

public static class Dialogs
{
    public static bool Confirm(string title, string message, string okText, bool danger)
    {
        var owner = Application.Current.MainWindow;
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            Background = Theme.Brush("Panel"), AllowsTransparency = false, BorderBrush = Theme.Brush("BorderHi"), BorderThickness = new Thickness(1),
        };
        bool result = false;
        var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources[danger ? "Danger" : "Primary"], MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(danger ? "Amber" : "Text") });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Theme.Brush("Muted"), LineHeight = 19 });
        panel.Children.Add(buttons);
        dlg.Content = panel;
        dlg.Loaded += (_, _) => (danger ? cancel : ok).Focus();
        dlg.ShowDialog();
        return result;
    }
}
