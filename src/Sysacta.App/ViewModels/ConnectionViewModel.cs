using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.Mcp;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

public sealed partial class ConnectionViewModel(SysactaRuntime runtime) : PageViewModel
{
    public const int BindLoopback = 0;
    public const int BindAny = 1;
    public const int BindCustom = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomAddress), nameof(ShowExposureWarning))]
    private int _bindMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExposureWarning))]
    private string _customAddress = "";

    [ObservableProperty]
    private double? _port = 8765;

    [ObservableProperty]
    private string _allowedHosts = "";

    [ObservableProperty]
    private string _allowedOrigins = "";

    [ObservableProperty]
    private string _externalUrl = "";

    [ObservableProperty]
    private bool _startOnLaunch;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _configJson = "";

    public bool IsCustomAddress => BindMode == BindCustom;

    /// <summary>Plain HTTP on a non-loopback interface (spec §5 warning).</summary>
    public bool ShowExposureWarning => BindMode == BindAny
        || (BindMode == BindCustom && IPAddress.TryParse(CustomAddress, out var ip) && !IPAddress.IsLoopback(ip));

    public override Task RefreshAsync()
    {
        var s = runtime.Settings;
        BindMode = s.BindAddress switch
        {
            "127.0.0.1" => BindLoopback,
            "0.0.0.0" => BindAny,
            _ => BindCustom,
        };
        CustomAddress = BindMode == BindCustom ? s.BindAddress : "";
        Port = s.Port;
        AllowedHosts = string.Join(Environment.NewLine, s.AllowedHosts);
        AllowedOrigins = string.Join(Environment.NewLine, s.AllowedOrigins);
        ExternalUrl = s.ExternalUrl ?? "";
        StartOnLaunch = s.StartServerOnLaunch;
        UpdateRuntimeState();
        return Task.CompletedTask;
    }

    /// <summary>Client configuration snippet. The token is a placeholder: tokens are only shown when issued.</summary>
    public static string ConfigFor(string url, string token) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["sysacta"] = new JsonObject
                {
                    ["url"] = url,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                },
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static string PreferredUrl(SysactaRuntime runtime) =>
        string.IsNullOrWhiteSpace(runtime.Settings.ExternalUrl) ? runtime.LocalEndpointUrl : runtime.Settings.ExternalUrl!;

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        ClearMessage();
        var address = BindMode switch
        {
            BindLoopback => "127.0.0.1",
            BindAny => "0.0.0.0",
            _ => CustomAddress.Trim(),
        };
        var port = Port is { } p && p == Math.Floor(p) && p is >= 1 and <= 65535 ? (int)p : -1;
        var hosts = Lines(AllowedHosts);
        var origins = Lines(AllowedOrigins);
        var externalUrl = string.IsNullOrWhiteSpace(ExternalUrl) ? null : ExternalUrl.Trim();
        var startOnLaunch = StartOnLaunch;

        if (await runtime.SaveSettingsAsync(
                s => s with
                {
                    BindAddress = address,
                    Port = port,
                    AllowedHosts = hosts,
                    AllowedOrigins = origins,
                    ExternalUrl = externalUrl,
                    StartServerOnLaunch = startOnLaunch,
                },
                restartIfRunning: true) is { } error)
        {
            Notify(InfoBarSeverity.Error, "設定を適用できませんでした", error);
        }
        else
        {
            Notify(InfoBarSeverity.Success, "保存しました", runtime.Host.State == ServerState.Running ? "サーバーは新しい設定で実行中です。" : "次回のサーバー起動から適用されます。");
        }

        await RefreshAsync();
    });

    [RelayCommand]
    private Task StartAsync() => RunAsync(async () =>
    {
        ClearMessage();
        if (await runtime.StartServerAsync() is { } error)
        {
            Notify(InfoBarSeverity.Error, "MCPサーバーを起動できませんでした", error);
        }

        UpdateRuntimeState();
    });

    [RelayCommand]
    private Task StopAsync() => RunAsync(async () =>
    {
        ClearMessage();
        await runtime.Host.StopAsync();
        UpdateRuntimeState();
    });

    [RelayCommand]
    private void CopyConfig()
    {
        if (ClipboardHelper.TrySetText(ConfigJson))
        {
            Notify(InfoBarSeverity.Success, "接続設定をコピーしました", "YOUR_TOKEN をクライアント発行時に表示されたトークンに置き換えてください。");
        }
    }

    private void UpdateRuntimeState()
    {
        IsRunning = runtime.Host.State == ServerState.Running;
        ConfigJson = ConfigFor(PreferredUrl(runtime), "YOUR_TOKEN");
    }

    private static string[] Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
