using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.App.Platform;
using Sysacta.Infrastructure;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

public sealed partial class GeneralViewModel(SysactaRuntime runtime, Func<System.Windows.Window> window) : PageViewModel
{
    private bool _loading;

    public static IReadOnlyList<string> ThemeOptions { get; } = ["Windows の設定に従う", "ライト", "ダーク"];

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private int _themeIndex;

    public string DataDirectory => runtime.DataDirectory;

    public string Version => SysactaRuntime.Version;

    public string PrivilegeText => Privilege.IsElevated
        ? "管理者権限で実行中です。攻撃面が増えるため、通常は標準ユーザー権限での実行を推奨します。"
        : "標準ユーザー権限で実行中です(推奨)。";

    public override Task RefreshAsync()
    {
        _loading = true;
        try
        {
            AutoStart = Platform.AutoStart.IsEnabled;
            ThemeIndex = (int)runtime.Settings.Theme;
        }
        finally
        {
            _loading = false;
        }

        if (runtime.InvalidSettingKeys.Count > 0)
        {
            Notify(InfoBarSeverity.Warning, "読み取れない設定を既定値に戻しました", string.Join(", ", runtime.InvalidSettingKeys));
        }

        return Task.CompletedTask;
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            Platform.AutoStart.Set(value);
            Notify(InfoBarSeverity.Success, value ? "サインイン時に自動起動します" : "自動起動を無効にしました", value ? "タスクトレイに常駐した状態で起動します(管理者権限にはなりません)。" : "");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Notify(InfoBarSeverity.Error, "自動起動の設定を変更できませんでした", SafeMessage(ex));
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        var theme = (ThemePreference)value;
        ThemeController.Apply(theme, window());
        _ = RunAsync(() => runtime.SaveSettingsAsync(runtime.Settings with { Theme = theme }, restartIfRunning: false));
    }

    [RelayCommand]
    private void OpenDataDirectory() =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { DataDirectory }, UseShellExecute = false });
}
