using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Sysacta.App.Views;
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
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _status;
    private readonly MenuItem _toggleServer;

    public TrayIcon(SysactaRuntime runtime, Action<Type?> show, Action exit)
        : this(runtime.Host, () => runtime.LocalEndpointUrl, runtime.StartServerAsync, show, exit)
    {
    }

    internal TrayIcon(McpServerHost host, Func<string> endpointUrl, Func<Task<string?>> startServer, Action<Type?> show, Action exit)
    {
        _host = host;
        _endpointUrl = endpointUrl;
        _startServer = startServer;
        _status = new MenuItem { IsEnabled = false };
        _toggleServer = new MenuItem();
        _toggleServer.Click += async (_, _) => await ToggleServerAsync();

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
            ContextMenu = new ContextMenu { Items = { open, new Separator(), _status, _toggleServer, copy, logs, new Separator(), quit } },
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => show(null);
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _host.StateChanged += OnStateChanged;
        UpdateState();
    }

    /// <summary>For tests: the native notification icon exists.</summary>
    internal bool IsCreated => _icon.IsCreated;

    internal string? StatusText => _status.Header as string;

    public void ShowWarning(string title, string message) =>
        _icon.ShowNotification(title, message, NotificationIcon.Warning);

    public void Dispose()
    {
        _host.StateChanged -= OnStateChanged;
        _icon.Dispose();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _icon.Dispatcher.BeginInvoke(UpdateState);

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
                await _host.StopAsync();
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
