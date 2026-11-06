using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;

namespace CiciPDA.App;

public partial class MainWindow : Window
{
    readonly ShellViewModel _vm;
    readonly Orchestrator _orch;
    bool _closing, _quitRequested, _trayHintShown;
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _trayDisconnect;

    public MainWindow(ShellViewModel vm, Orchestrator orch)
    {
        InitializeComponent();
        DataContext = _vm = vm; _orch = orch;
        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxBtn.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseBtn.Click += (_, _) => Close();

        // passphrase box: PasswordBox cannot be data-bound, so keep it in sync by hand
        HotspotPass.PasswordChanged += (_, _) => { if (_vm.HotspotPassword != HotspotPass.Password) _vm.HotspotPassword = HotspotPass.Password; };
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.HotspotPassword) && HotspotPass.Password != _vm.HotspotPassword) HotspotPass.Password = _vm.HotspotPassword; };

        _vm.Events.CollectionChanged += (_, _) => { ActivityEmpty.Visibility = _vm.Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed; Dispatcher.BeginInvoke(() => ActivityScroll.ScrollToEnd()); };
        ActivityEmpty.Visibility = _vm.Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // keyboard: Ctrl+1..8 pages, Ctrl+Enter connect/disconnect, F5 refresh devices
        string[] pages = { "Connect", "Monitor", "Activity", "History", "Devices", "Diagnostics", "Settings", "Help" };
        for (int i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            InputBindings.Add(new KeyBinding(_vm.Navigate, Key.D1 + i, ModifierKeys.Control) { CommandParameter = p });
        }
        InputBindings.Add(new KeyBinding(_vm.ToggleConnect, Key.Enter, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.RefreshDevices, Key.F5, ModifierKeys.None));
        StateChanged += (_, _) => Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        if (!App.IsPreview) { RestorePlacement(); CreateTray(); }
    }

    // ---------- remember window position and size ----------
    System.Windows.Threading.DispatcherTimer? _saveTimer;

    void RestorePlacement()
    {
        var c = _vm.Settings.Current;
        var screen = new Box(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var fit = WindowPlacement.Fit(c.WindowLeft, c.WindowTop, c.WindowWidth, c.WindowHeight, screen);
        if (fit is { } b)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            if (c.WindowMaximized) WindowState = WindowState.Maximized;
        }
        // else: first run, or the saved spot is no longer on any screen -> the XAML default (centred) applies
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        StateChanged += (_, _) => ScheduleSave();
    }

    void ScheduleSave()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick -= SaveTick; _saveTimer.Tick += SaveTick;
        _saveTimer.Stop(); _saveTimer.Start();                       // debounce: write once the user stops dragging
    }

    void SaveTick(object? s, EventArgs e) { _saveTimer?.Stop(); SavePlacement(); }

    void SavePlacement()
    {
        if (App.IsPreview || !IsLoaded) return;
        var c = _vm.Settings.Current;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;   // normal bounds even when maximized/minimized
        if (r.IsEmpty || r.Width < 200 || r.Height < 200) return;
        c.WindowLeft = r.Left; c.WindowTop = r.Top; c.WindowWidth = r.Width; c.WindowHeight = r.Height;
        c.WindowMaximized = WindowState == WindowState.Maximized;
        try { _vm.Settings.SaveQuietly(); } catch (Exception ex) { Log.Debug("ui", "placement not saved: " + ex.Message); }
    }

    // ---------- system tray ----------
    void CreateTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem("Open CiciPDA");
        open.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _trayDisconnect = new System.Windows.Forms.ToolStripMenuItem("Disconnect") { Enabled = false };
        _trayDisconnect.Click += (_, _) => Dispatcher.Invoke(() => { _ = _orch.DisconnectAsync(); });
        var quit = new System.Windows.Forms.ToolStripMenuItem("Quit CiciPDA");
        quit.Click += (_, _) => Dispatcher.Invoke(QuitFromTray);
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { open, _trayDisconnect, new System.Windows.Forms.ToolStripSeparator(), quit });

        _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "CiciPDA", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(ShowFromTray); };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _orch.Changed += () => Dispatcher.BeginInvoke(UpdateTray);
        UpdateTray();
    }

    void UpdateTray()
    {
        if (_tray == null) return;
        var text = "CiciPDA - " + _vm.StatusText;
        _tray.Text = text.Length > 63 ? text[..63] : text;                       // NotifyIcon tooltips are limited to 63 characters
        if (_trayDisconnect != null) _trayDisconnect.Enabled = _orch.IsActive || _orch.IsBusy;
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Topmost = true; Topmost = false; Focus();
    }

    void HideToTray()
    {
        Hide();
        if (!_trayHintShown && _tray != null)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "CiciPDA is still connected", "It keeps running in the system tray. Click the icon to open it, or use Quit to disconnect and exit.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    void QuitFromTray()
    {
        ShowFromTray();
        if (_orch.IsActive || _orch.IsBusy)
            if (!Dialogs.Confirm("Quit CiciPDA?", "The connection will be closed and your network settings restored.", "Quit", false)) return;
        _quitRequested = true;
        Close();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        SavePlacement();                                              // also covers closing/hiding to the tray

        // The X button must not drop a working connection: while connected (or connecting) the window just goes to the system tray.
        // Real exits are the tray menu's Quit, Windows shutting down / signing out, and the updater (they set _quitRequested or App.Quitting).
        if (!_quitRequested && !App.Quitting && (_orch.IsActive || _orch.IsBusy))
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        e.Cancel = true; _closing = true;
        Hide();
        try { await _orch.ShutdownAsync(); } catch (Exception ex) { Log.Error("shutdown", ex); }
        Log.Info("app", "CiciPDA closed cleanly.");
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        App.Quitting = true;
        Application.Current.Shutdown();
    }
}
