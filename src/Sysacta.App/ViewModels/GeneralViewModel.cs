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
    private LatestValueSaver<int>? _themeSaver;

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

            // While a theme save is running, the selection shown is newer than the stored one: keep it, otherwise the
            // pending save would see this reset as a new choice and save the old theme again.
            var stored = (int)runtime.Settings.Theme;
            if (_themeSaver?.IsSaving != true && ThemeIndex != stored)
            {
                ThemeIndex = stored;
                ThemeController.Apply(runtime.Settings.Theme, window()); // keep the window in line with the selection
            }
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

        ThemeController.Apply((ThemePreference)value, window());
        _themeSaver ??= new LatestValueSaver<int>(() => ThemeIndex, SaveThemeAsync, RefreshAsync);
        _ = _themeSaver.ChangedAsync();
    }

    private Task<bool> SaveThemeAsync(int index) => RunAsync(async () =>
    {
        if (await runtime.SaveSettingsAsync(s => s with { Theme = (ThemePreference)index }, restartIfRunning: false) is { } error)
        {
            throw new InvalidOperationException(error);
        }
    }, "テーマを保存できませんでした");

    [RelayCommand]
    private void OpenDataDirectory() =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { DataDirectory }, UseShellExecute = false });
}
