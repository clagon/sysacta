using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Sysacta.App.Views;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Mcp;
using Wpf.Ui.Controls;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace Sysacta.App;

/// <summary>Notification-area icon and menu. Works without the main window ever being shown.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly McpServerHost _host;
    private readonly Func<string> _endpointUrl;
    private readonly Func<Task<string?>> _startServer;
    private readonly Func<Task> _stopServer;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _status;
    private readonly MenuItem _toggleServer;
    private readonly MenuItem _restoreMode;
    private readonly DisplayModeRecovery? _recovery;
    private readonly MenuItem _releasePower;
    private readonly PowerTransitionGate? _powerGate;

    public TrayIcon(SysactaRuntime runtime, Action<Type?> show, Action exit)
        : this(runtime.Host, () => runtime.LocalEndpointUrl, runtime.StartServerAsync, runtime.StopServerAsync, show, exit,
            runtime.DisplayModeRecovery, runtime.RestoreDisplayModeAsync, runtime.PowerGate, runtime.ReleasePowerGateAsync)
    {
    }

    internal TrayIcon(
        McpServerHost host,
        Func<string> endpointUrl,
        Func<Task<string?>> startServer,
        Func<Task> stopServer,
        Action<Type?> show,
        Action exit,
        DisplayModeRecovery? recovery = null,
        Func<Task<ToolResult>>? restoreMode = null,
        PowerTransitionGate? powerGate = null,
        Func<Task<ToolResult>>? releasePower = null)
    {
        _host = host;
        _endpointUrl = endpointUrl;
        _startServer = startServer;
        _stopServer = stopServer;
        _status = new MenuItem { IsEnabled = false };
        _toggleServer = new MenuItem();
        _toggleServer.Click += async (_, _) => await ToggleServerAsync();

        // Reachable from the primary display's notification area, which a mode change never targets.
        _recovery = recovery;
        _restoreMode = new MenuItem { Header = DisplayModeRestoreText.MenuHeader, Icon = new SymbolIcon(SymbolRegular.ArrowUndo24), Visibility = Visibility.Collapsed };
        _restoreMode.Click += async (_, _) => await RestoreModeAsync(restoreMode);

        // Also on the tray menu: the overview page may be on a display that a mode change left blank.
        _powerGate = powerGate;
        _releasePower = new MenuItem { Header = PowerGateText.MenuHeader, Icon = new SymbolIcon(SymbolRegular.Play24), Visibility = Visibility.Collapsed };
        _releasePower.Click += async (_, _) => await ReleasePowerAsync(releasePower);

        var copy = new MenuItem { Header = "接続URLをコピー", Icon = new SymbolIcon(SymbolRegular.Copy24) };
        copy.Click += (_, _) => ClipboardHelper.TrySetText(_endpointUrl());
        var open = new MenuItem { Header = "管理コンソールを開く", Icon = new SymbolIcon(SymbolRegular.WindowNew24), FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => show(null);
        var logs = new MenuItem { Header = "監査ログ", Icon = new SymbolIcon(SymbolRegular.DocumentBulletList24) };
        logs.Click += (_, _) => show(typeof(LogsPage));
        var quit = new MenuItem { Header = "終了", Icon = new SymbolIcon(SymbolRegular.Power24) };
        quit.Click += (_, _) => exit();

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Sysacta;component/Assets/sysacta.ico")),
            ContextMenu = new ContextMenu { Items = { open, _restoreMode, _releasePower, new Separator(), _status, _toggleServer, copy, logs, new Separator(), quit } },
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => show(null);
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _host.StateChanged += OnStateChanged;
        if (_recovery is not null)
        {
            _recovery.Changed += OnRecoveryChanged;
        }

        if (_powerGate is not null)
        {
            _powerGate.Changed += OnPowerGateChanged;
        }

        UpdateState();
        UpdateRecovery();
        UpdatePowerGate();
    }

    internal bool CanReleasePower => _releasePower.Visibility == Visibility.Visible;

    internal bool CanRestoreMode => _restoreMode.Visibility == Visibility.Visible;

    /// <summary>For tests: the native notification icon exists.</summary>
    internal bool IsCreated => _icon.IsCreated;

    internal string? StatusText => _status.Header as string;

    public void ShowWarning(string title, string message) =>
        _icon.ShowNotification(title, message, NotificationIcon.Warning);

    public void Dispose()
    {
        _host.StateChanged -= OnStateChanged;
        if (_recovery is not null)
        {
            _recovery.Changed -= OnRecoveryChanged;
        }

        if (_powerGate is not null)
        {
            _powerGate.Changed -= OnPowerGateChanged;
        }

        _icon.Dispose();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _icon.Dispatcher.BeginInvoke(UpdateState);

    private void OnRecoveryChanged(object? sender, EventArgs e) => _icon.Dispatcher.BeginInvoke(UpdateRecovery);

    private void OnPowerGateChanged(object? sender, EventArgs e) => _icon.Dispatcher.BeginInvoke(UpdatePowerGate);

    private void UpdatePowerGate()
    {
        var state = _powerGate?.State;
        _releasePower.Visibility = state?.Phase == PowerGatePhase.Held ? Visibility.Visible : Visibility.Collapsed;
        _releasePower.ToolTip = state is null ? null : PowerGateText.Describe(state);
    }

    private async Task ReleasePowerAsync(Func<Task<ToolResult>>? release)
    {
        if (release is null || System.Windows.MessageBox.Show(PowerGateText.ConfirmMessage, PowerGateText.ConfirmTitle, System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Question, System.Windows.MessageBoxResult.Cancel) != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        _releasePower.IsEnabled = false;
        try
        {
            var (ok, title, message) = PowerGateText.DescribeRelease(await release());
            _icon.ShowNotification(title, message.Length == 0 ? " " : message, ok ? NotificationIcon.Info : NotificationIcon.Warning);
        }
        finally
        {
            _releasePower.IsEnabled = true;
            UpdatePowerGate();
        }
    }

    private void UpdateRecovery()
    {
        var pending = _recovery is null ? null : DisplayModeRestoreText.Pending(_recovery);
        _restoreMode.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        _restoreMode.ToolTip = pending;
    }

    private async Task RestoreModeAsync(Func<Task<ToolResult>>? restore)
    {
        if (restore is null)
        {
            return;
        }

        _restoreMode.IsEnabled = false;
        try
        {
            var (ok, title, message) = DisplayModeRestoreText.Describe(await restore());
            _icon.ShowNotification(title, message.Length == 0 ? " " : message, ok ? NotificationIcon.Info : NotificationIcon.Warning);
        }
        finally
        {
            _restoreMode.IsEnabled = true;
        }
    }

    private void UpdateState()
    {
        var running = _host.State == ServerState.Running;
        _status.Header = running ? $"実行中 — {_endpointUrl()}" : _host.State == ServerState.Faulted ? "停止(エラー)" : "停止中";
        _toggleServer.Header = running ? "サーバーを停止" : "サーバーを開始";
        _toggleServer.Icon = new SymbolIcon(running ? SymbolRegular.Stop24 : SymbolRegular.Play24);
        _icon.ToolTipText = running ? "Sysacta — 実行中" : "Sysacta — 停止中";
    }

    private async Task ToggleServerAsync()
    {
        _toggleServer.IsEnabled = false;
        try
        {
            if (_host.State == ServerState.Running)
            {
                await _stopServer();
            }
            else if (await _startServer() is { } error)
            {
                ShowWarning("MCPサーバーを起動できませんでした", error);
            }
        }
        finally
        {
            _toggleServer.IsEnabled = true;
        }
    }
}
