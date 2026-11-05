using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;

namespace CiciPDA.App;

public partial class MainWindow : Window
{
    readonly ShellViewModel _vm;
    readonly Orchestrator _orch;
    bool _closing;

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
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        if (_orch.IsActive || _orch.IsBusy)
        {
            if (!Dialogs.Confirm("Quit CiciPDA?", "The connection will be closed and your network settings restored.", "Quit", false)) { e.Cancel = true; return; }
        }
        e.Cancel = true; _closing = true;
        Hide();
        try { await _orch.ShutdownAsync(); } catch (Exception ex) { Log.Error("shutdown", ex); }
        Log.Info("app", "CiciPDA closed cleanly.");
        Application.Current.Shutdown();
    }
}
