using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysacta.Core.Tools.Apps;
using Sysacta.Infrastructure;
using Sysacta.Windows.Apps;
using Wpf.Ui.Controls;

namespace Sysacta.App.ViewModels;

/// <summary>
/// The local app allow-list (spec §4 "アプリ"): Start Menu discovery, manual registration with structured fixed arguments,
/// allowing, re-approval and deletion. Nothing found or registered is allowed until the user turns it on. Real paths and
/// arguments are shown here only (to the local user). Inspection, hashing and SQLite run off the UI thread.
/// </summary>
public sealed partial class AppsViewModel(SysactaRuntime runtime) : PageViewModel
{
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, RegisteredAppItem> _items = new(StringComparer.Ordinal);
    private int _loadGeneration;

    public ObservableCollection<RegisteredAppItem> Apps { get; } = [];

    public ObservableCollection<CandidateItem> Candidates { get; } = [];

    [ObservableProperty]
    private bool _hasApps;

    [ObservableProperty]
    private bool _hasCandidates;

    [ObservableProperty]
    private string _candidateSummary = "「スタートメニューから検出」で、スタートメニューのショートカットから候補を探します。";

    [ObservableProperty]
    private bool _isElevated;

    /// <summary>The registration / re-approval form. Never replaced by a refresh while open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraft), nameof(IsNewDraft), nameof(DraftTitle), nameof(DraftAction))]
    [NotifyCanExecuteChangedFor(nameof(SubmitDraftCommand))]
    private AppDraft? _draft;

    public bool HasDraft => Draft is not null;

    public bool IsNewDraft => Draft is { ReapproveId: null };

    public string DraftTitle => IsNewDraft ? "新しいアプリの登録" : "再承認";

    public string DraftAction => IsNewDraft ? "登録" : "再承認";

    public override async Task RefreshAsync()
    {
        var generation = ++_loadGeneration;
        await RunAsync(async () =>
        {
            var (stored, elevated) = await Task.Run(() => (runtime.Apps.ListAll(), runtime.AppPlatform.IsElevated));
            if (generation != _loadGeneration)
            {
                return;
            }

            IsElevated = elevated;
            Apps.Clear();
            foreach (var app in stored)
            {
                // An item is kept while its approval is the same, so a save still running for its switch keeps its
                // value; a re-approved, legacy or new row gets a new item. Removed rows are dropped from the cache.
                if (_items.TryGetValue(app.Id, out var item) && item.CanApply(app))
                {
                    item.ApplyStoredValue(app);
                }
                else
                {
                    item = new RegisteredAppItem(this, app);
                    _items[app.Id] = item;
                }

                Apps.Add(item);
            }

            foreach (var gone in _items.Keys.Except(stored.Select(a => a.Id)).ToList())
            {
                _items.Remove(gone);
            }

            HasApps = Apps.Count > 0;
            // Cheap check of each approved file (no hash), after the list is shown.
            var checks = await Task.Run(() => stored.Where(s => s.App is not null).ToDictionary(s => s.Id, s =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return AppListAvailableTool.StatusOf(runtime.AppPlatform, s.App!, timeout.Token);
            }));
            if (generation == _loadGeneration)
            {
                foreach (var item in Apps)
                {
                    item.ApplyStatus(checks.TryGetValue(item.Id, out var status) ? status : null);
                }
            }
        }, "アプリの一覧を読み込めませんでした");
    }

    [RelayCommand]
    private Task DiscoverAsync() => RunAsync(async () =>
    {
        ClearMessage();
        using var timeout = new CancellationTokenSource(InspectTimeout);
        DiscoveryResult result;
        try
        {
            result = await StartMenuDiscovery.ScanAsync(runtime.AppPlatform, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Notify(InfoBarSeverity.Warning, "検出を中断しました", "時間内に終わりませんでした。手動で登録してください。");
            return;
        }

        Candidates.Clear();
        foreach (var candidate in result.Candidates)
        {
            Candidates.Add(new CandidateItem(this, candidate));
        }

        HasCandidates = Candidates.Count > 0;
        CandidateSummary = $"{Candidates.Count} 件の候補"
            + (result.Truncated ? "（上限に達したため一部のみ）" : "")
            + (result.SkippedFolders > 0 ? $"。ローカルの固定ドライブ以外や読めないフォルダー {result.SkippedFolders} 件は対象外です" : "")
            + "。候補を登録しても、起動の許可は自動でオンになりません。";
    }, "検出できませんでした");

    [RelayCommand]
    private void PickExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "登録する実行ファイルを選択",
            Filter = "実行ファイル (*.exe)|*.exe",
            DereferenceLinks = false,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            StartDraft(dialog.FileName, System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
        }
    }

    internal void StartDraft(string path, string name) => Draft = new AppDraft(null, -1, path, name, "");

    internal void StartReapprove(RegisteredAppItem item)
    {
        if (item.App is { } app)
        {
            Draft = new AppDraft(app.Id, item.Generation, app.Path, app.DisplayName, AppDraft.FormatArguments(app.FixedArgs));
        }
    }

    [RelayCommand]
    private void CancelDraft() => Draft = null;

    private bool CanSubmitDraft() => Draft is not null;

    [RelayCommand(CanExecute = nameof(CanSubmitDraft))]
    private Task SubmitDraftAsync() => RunAsync(async () =>
    {
        if (Draft is not { } draft)
        {
            return;
        }

        ClearMessage();
        var args = AppDraft.ParseArguments(draft.Arguments);
        if (AppRules.ValidateFixedArgs(args) is { } argsError)
        {
            throw new ArgumentException(argsError);
        }

        if (draft.ReapproveId is null && AppRules.ValidateDisplayName(draft.Name.Trim()) is { } nameError)
        {
            throw new ArgumentException(nameError);
        }

        if (draft.ReapproveId is { } reapproveId && !await ConfirmAsync("再承認しますか?",
            $"現在のファイルと固定引数を承認し直します。\n\n{draft.Path}\n\n再承認すると起動・終了の許可はオフに戻ります。必要なら確認後にオンにしてください。", "再承認"))
        {
            return;
        }

        var check = await Task.Run(() =>
        {
            using var timeout = new CancellationTokenSource(InspectTimeout);
            return runtime.AppPlatform.Inspect(draft.Path, computeHash: true, timeout.Token);
        });
        if (!check.IsUsable)
        {
            throw new ArgumentException(RefusalText(check.Refusal));
        }

        if (draft.ReapproveId is { } id)
        {
            await Task.Run(() => runtime.Apps.Reapprove(id, draft.Generation, check.CanonicalPath!, args, check.Identity!));
            Notify(InfoBarSeverity.Success, $"「{draft.Name}」を再承認しました", "起動・終了の許可はオフです。必要ならオンにしてください。");
        }
        else
        {
            await Task.Run(() => runtime.Apps.Register(check.CanonicalPath!, draft.Name, args, check.Identity!));
            Notify(InfoBarSeverity.Success, $"「{draft.Name.Trim()}」を登録しました", "起動・終了の許可はオフです。MCP から使うには、一覧でオンにしてください。");
        }

        Draft = null;
        await RefreshAsync();
    }, "登録できませんでした");

    internal Task<bool> SetAllowedAsync(RegisteredAppItem item, bool allowed) => RunAsync(async () =>
    {
        item.Generation = await Task.Run(() => runtime.Apps.SetAllowed(item.Id, item.Generation, allowed));
        Notify(InfoBarSeverity.Success, allowed ? $"「{item.Name}」の起動・終了を許可しました" : $"「{item.Name}」の許可を取り消しました", "次のリクエストから即時に反映されます。");
    }, "許可を変更できませんでした");

    internal Task DeleteAsync(RegisteredAppItem item) => RunAsync(async () =>
    {
        if (!await ConfirmAsync("登録を削除しますか?", $"「{item.Name}」の登録を削除します。MCP から起動・終了できなくなります。アプリ自体は削除されません。", "削除"))
        {
            return;
        }

        await Task.Run(() => runtime.Apps.Delete(item.Id));
        _items.Remove(item.Id);

        Notify(InfoBarSeverity.Success, $"「{item.Name}」の登録を削除しました");
        await RefreshAsync();
    }, "削除できませんでした");

    /// <summary>Why an executable cannot be registered, for the local user.</summary>
    public static string RefusalText(ExecutableRefusal refusal) => refusal switch
    {
        ExecutableRefusal.InvalidPath => "通常の絶対パス（例: C:\\Program Files\\App\\app.exe）にある実行ファイルだけを登録できます。ネットワーク上の場所、相対パス、短い名前（~ を含む）、環境変数、259 文字を超えるパスは使えません。",
        ExecutableRefusal.NotExe => ".exe ファイルだけを登録できます。ショートカット・スクリプト・インストーラーは登録できません。",
        ExecutableRefusal.DeniedProgram => "シェル、スクリプトの実行環境、汎用ランタイム、インストーラー・更新ツール、管理ツールなど、任意のコマンドを実行できるプログラムは登録できません。",
        ExecutableRefusal.ProtectedLocation => "Windows フォルダー、ストアアプリのフォルダー、Sysacta 自身のフォルダーにあるプログラムは登録できません。",
        ExecutableRefusal.NotLocalFixedDrive => "ローカルの固定ドライブにあるプログラムだけを登録できます。ネットワークドライブやリムーバブルドライブは対象外です。",
        ExecutableRefusal.ReparsePoint => "ジャンクションやシンボリックリンクを経由する場所にあるプログラムは登録できません。実際の場所のファイルを選んでください。",
        ExecutableRefusal.PathMismatch => "パスが実際のファイルの場所と一致しません。実際の場所のファイルを選んでください。",
        ExecutableRefusal.NotFound => "ファイルが見つかりません。",
        ExecutableRefusal.NotPortableExecutable => "Windows の実行可能ファイル（x86 / x64 / ARM64 のアプリ）ではありません。",
        ExecutableRefusal.TooLarge => "512 MiB を超える実行ファイルは登録できません。",
        ExecutableRefusal.IdentityChanged => "承認したファイルから変更されています。",
        _ => "ファイルを読み取れませんでした。使用中・更新中でないか、アクセス権を確認してください。",
    };

    public static string StatusText(AppStatus? status) => status switch
    {
        AppStatus.Ready => "承認したファイルがあります",
        AppStatus.TargetChanged => "ファイルが変更されています。再承認するまで起動できません",
        AppStatus.TargetMissing => "ファイルが見つかりません",
        AppStatus.NotSupported => "安全性の条件を満たさなくなりました",
        AppStatus.CheckFailed => "状態を確認できませんでした",
        _ => "",
    };
}

/// <param name="ReapproveId">The app being re-approved; null for a new registration.</param>
public sealed partial class AppDraft(string? reapproveId, long generation, string path, string name, string arguments) : ObservableObject
{
    public string? ReapproveId { get; } = reapproveId;
    public long Generation { get; } = generation;
    public string Path { get; } = path;

    [ObservableProperty]
    private string _name = name;

    /// <summary>One argument per line, used exactly as written (no splitting on spaces, no quoting, no variables).</summary>
    [ObservableProperty]
    private string _arguments = arguments;

    /// <summary>Each line is one argument; a single trailing line break is ignored; empty text means no arguments.</summary>
    /// <summary>
    /// The inverse of <see cref="ParseArguments"/>: every argument on its own line, each line ended by a line break, so
    /// empty arguments (also a trailing or a single empty one) survive re-approval unchanged.
    /// </summary>
    public static string FormatArguments(IReadOnlyList<string> args) =>
        args.Count == 0 ? "" : string.Concat(args.Select(a => a + Environment.NewLine));

    public static IReadOnlyList<string> ParseArguments(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}

public sealed partial class RegisteredAppItem : ObservableObject
{
    private readonly LatestValueSaver<bool> _saver;
    private readonly AppsViewModel _owner;
    private bool _applyingStoredValue;

    public RegisteredAppItem(AppsViewModel owner, StoredApp stored)
    {
        _owner = owner;
        Id = stored.Id;
        Name = stored.DisplayName;
        App = stored.App;
        Generation = stored.App?.Generation ?? 0;
        _isAllowed = stored.Allowed && stored.App is not null;
        _saver = new LatestValueSaver<bool>(() => IsAllowed, v => owner.SetAllowedAsync(this, v), owner.RefreshAsync);
        _status = App is null ? "旧形式または読み取れない登録です。削除して登録し直してください。" : "";
    }

    public string Id { get; }
    public string Name { get; }
    public RegisteredApp? App { get; }

    /// <summary>The stored generation (disallowing starts a new one); passed with every change.</summary>
    public long Generation { get; internal set; }
    public bool IsValid => App is not null;
    public string Path => App?.Path ?? "";

    public string ArgumentsText => App is null ? ""
        : App.FixedArgs.Count == 0 ? "固定引数なし"
        : "固定引数: " + string.Join(" ", App.FixedArgs.Select(a => $"[{a}]"));

    [ObservableProperty]
    private bool _isAllowed;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private bool _needsAttention;

    internal void ApplyStatus(AppStatus? status)
    {
        if (App is null)
        {
            return;
        }

        Status = AppsViewModel.StatusText(status);
        NeedsAttention = status is not AppStatus.Ready;
    }

    /// <summary>Same approval (path, identity, arguments), so only the permission and generation may differ.</summary>
    internal bool CanApply(StoredApp stored) =>
        App is not null && stored.App is { } other && other.Path == App.Path && other.Identity == App.Identity && other.FixedArgs.SequenceEqual(App.FixedArgs);

    internal void ApplyStoredValue(StoredApp stored)
    {
        if (_saver.IsSaving)
        {
            return;
        }

        Generation = stored.App!.Generation;
        _applyingStoredValue = true;
        IsAllowed = stored.Allowed;
        _applyingStoredValue = false;
    }

    partial void OnIsAllowedChanged(bool value)
    {
        if (!_applyingStoredValue && App is not null)
        {
            _ = _saver.ChangedAsync();
        }
    }

    [RelayCommand]
    private void Reapprove() => _owner.StartReapprove(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}

public sealed partial class CandidateItem(AppsViewModel owner, AppCandidate candidate)
{
    public string Name { get; } = candidate.Name;
    public string Target { get; } = candidate.Target;
    public bool CanRegister { get; } = candidate.Check.IsUsable;

    public string Note { get; } = !candidate.Check.IsUsable ? AppsViewModel.RefusalText(candidate.Check.Refusal)
        : candidate.HasArguments ? "ショートカットの引数は取り込みません。必要な引数は登録時に 1 行に 1 つずつ入力してください。"
        : "";

    [RelayCommand]
    private void Register() => owner.StartDraft(candidate.Check.CanonicalPath ?? Target, Name);
}
