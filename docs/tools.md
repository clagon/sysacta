# MCP ツールの追加手順

新しいツールは、この手順と2つの参考実装をそのまま真似て追加します。認証・認可・入力検証・排他・タイムアウト・監査はすべて `ToolExecutor` が適用するので、ツールに書くのはドメインロジックだけです。

| 参考実装 | 種類 | ファイル |
|---|---|---|
| `audio_get_state` | 読み取り専用 | `src/Sysacta.Core/Tools/Audio/AudioGetStateTool.cs` |
| `audio_set_volume` | 状態変更(検証あり) | `src/Sysacta.Core/Tools/Audio/AudioSetVolumeTool.cs` |
| 対象デバイスの解決 | 共通処理 | `src/Sysacta.Core/Tools/Audio/AudioTarget.cs` |
| 実体が入れ替わり得る対象(音声セッション) | 不透明 ID・再照合・同じ実体への書き込み | `src/Sysacta.Core/Tools/Audio/AudioSessions.cs` |
| ツールと管理画面で同じ一覧を出す | 共通処理 | `src/Sysacta.Core/Tools/Audio/AudioDeviceCatalog.cs` |
| OS アダプター契約 | インターフェース | `src/Sysacta.Core/Tools/Audio/IAudioEndpoints.cs` |
| OS アダプター実装 | Windows (NAudio/COM) | `src/Sysacta.Windows/Audio/CoreAudioEndpoints.cs` |
| 単体テスト(フェイク) | Core.Tests | `tests/Sysacta.Core.Tests/AudioToolTests.cs` |
| 統合テスト(HTTP/MCP) | Mcp.Tests | `tests/Sysacta.Mcp.Tests/McpServerTests.cs` |
| 実機テスト | Windows.Tests | `tests/Sysacta.Windows.Tests/CoreAudioEndpointsTests.cs` |
| 複数対象への変更・部分成功、排他が必要な OS ハンドル | Core + Windows | `src/Sysacta.Core/Tools/Display/DisplaySetLevelTools.cs`, `src/Sysacta.Windows/Display/WindowsDisplays.cs` |
| 画面消失の恐れがある変更・ローカル専用の復旧ツール | Core + App | `src/Sysacta.Core/Tools/Display/DisplayModeTools.cs`（`DisplaySetModeTool` / `DisplayRestoreModeTool`） |
| 固定の許可リストに限った低レベル操作・通信消失時の `accepted` | Core + Windows | `src/Sysacta.Core/Tools/Display/DisplaySwitchTools.cs`, `Mccs.cs`, `src/Sysacta.Windows/Display/DdcCiSwitchControl.cs` |

| PC 全体の電源遷移（他の状態変更との排他、ローカル解除） | Core + Windows + App | `src/Sysacta.Core/Tools/Power/PowerTools.cs`, `src/Sysacta.Core/Tools/PowerTransitionGate.cs`, `src/Sysacta.Windows/Power/WindowsPower.cs` |
| 読み取りの欠損を値 null + 型付き理由で返す・OS 由来の数値の検証 | Core + Windows | `src/Sysacta.Core/Tools/SystemMonitoring/`, `src/Sysacta.Windows/SystemMonitoring/WindowsSystemMonitor.cs` |
| ローカルの許可リストに依存する状態変更・開始点での対象固有の最終確認・同じハンドルへの操作 | Core + Infrastructure + Windows + App | `src/Sysacta.Core/Tools/Apps/`, `src/Sysacta.Infrastructure/AppRegistryStore.cs`, `src/Sysacta.Windows/Apps/WindowsAppPlatform.cs`, `src/Sysacta.App/ViewModels/AppsViewModel.cs` |
| 1 要求内の 2 サンプルと単調時計による測定(`TimeProvider` で決定的にテスト)・接続断の恐れがある変更の fail-closed な安全判定・共有の排他キー | Core + Windows | `src/Sysacta.Core/Tools/Network/`(`NetworkTraffic` / `NetworkGuard` / `NetworkSetAdapterEnabledTool`), `src/Sysacta.Windows/Network/WindowsNetwork.cs` |

## 1 回の呼び出しで起きること

```
MCP tools/call ─▶ RequestGuards (Host/Origin → Bearer認証 → 監査:認証失敗)
              ─▶ レート制限 (クライアント単位)
              ─▶ McpServerHost の CallTool ハンドラー
              ─▶ ToolExecutor.ExecuteAsync
                   1. ツール検索 (未知 → プロトコルエラー)
                   2. 認可: 能力 ∩ サーバー全体の許可 ∩ クライアント別の許可
                   3. 引数の厳格な検証 (未知の項目・型変換・範囲外を拒否)
                   4. 待機キュー上限 (超過 → BUSY)
                   4b. 電源遷移ゲート (状態変更のみ。遷移中/保持中 → BUSY、遷移は先行変更があれば BUSY)
                   5. ResolveTargetAsync: ID/別名/既定 → 具体的なID とロックキー
                   6. リソースロック (状態変更ツールのみ) → 実行スロット
                   7. 監査 pending を永続化 (状態変更のみ。失敗 → 実行しない)
                   8. 再認可 (待機中・pending 書き込み中の失効・権限変更を反映)
                   9. ExecuteAsync (タイムアウト付き。ネイティブ処理が終わるまでロック保持)
                        └ 状態変更ツール: 読み取り → execution.BeginSideEffect() (最終の認可点・開始点) → OS 書き込み → 読み戻し
                  10. 監査の最終結果 (記録は固定値・登録名・解決済み target・数値/真偽値/列挙値の引数のみ)
```

## 手順

### 1. 名前・カテゴリ・既定の権限を決める

- 名前: `<category>_<verb>_<object>`。カテゴリは `ToolCategories` の定数(`apps` カテゴリだけは spec §7.6 に合わせて `app_` と `process_` の接頭辞を使う。`ToolCategories.NamePrefixes`)。spec §7 の表に名前があればそれを使う。
- 既定: 日常的で安全なものだけ `DefaultPolicy.On`。停止・切断・画面消失・データ消失の恐れがあれば `Off`。非公開API・未検証の方式は `Experimental`。
- 読み取り専用か: 状態を一切変えないときだけ `ReadOnly: true`(ロック・pending 監査が省略されるため、迷ったら false)。

### 2. OS アダプターの契約を Core に定義する

`src/Sysacta.Core/Tools/<Category>/I<Something>.cs` にインターフェースを置く。

- 同期メソッドで良い(executor がスレッドプール(MTA)で実行する)。
- 失敗は `ToolFailureException(ErrorCodes.XXX, "英語の短い説明")` で表す。メッセージに例外の原文・パス・秘密を入れない。
- 既存のインターフェースで足りるなら作らない。

### 3. Windows 実装を Sysacta.Windows に書く

`src/Sysacta.Windows/<Category>/<Name>.cs`。

- COM/WinRT オブジェクトは呼び出しごとに生成・破棄する(`using`)。UI スレッドで呼ばない前提で書く。
- COM の HRESULT を `ErrorCodes` に変換する(参考: `CoreAudioEndpoints.Guard`)。
- 公式 API を使う。非公開 API は `Experimental/` に隔離し、ツールを `DefaultPolicy.Experimental` にする(spec §12)。
- 管理者権限が必要/この環境で恒常的に使えない場合は、ツールの `GetAvailability()` で `ToolAvailability.Unavailable(ErrorCodes.NotSupported or AdminRequired, 理由)` を返す(tools/list から消える)。一時的な不可(切断など)は Unavailable にせず実行時エラーにする。

### 4. ツールクラスを Core に書く

`src/Sysacta.Core/Tools/<Category>/<Name>Tool.cs`。参考実装をコピーして変更する。

```csharp
public sealed class DisplaySetBrightnessArgs
{
    [Description("Brightness 0-100.")]
    [Range(0, 100)]
    public required int Level { get; init; }           // required = 必須

    [Description("Display id or alias. Omit for the primary display.")]
    [StringLength(512, MinimumLength = 1)]
    public string? Device { get; init; }                // 省略可能は nullable
}

public sealed record DisplaySetBrightnessData(string DeviceId, int PreviousLevel, int CurrentLevel, bool Changed);

public sealed class DisplaySetBrightnessTool(IDisplays displays, IDeviceAliases aliases)
    : SysactaTool<DisplaySetBrightnessArgs, DisplaySetBrightnessData>
{
    public override ToolDescriptor Descriptor { get; } = new(
        Name: "display_set_brightness",
        Category: ToolCategories.Display,
        Title: "Set display brightness",
        Description: "Sets the absolute brightness (0-100) ... and verifies it by reading it back.",
        DefaultPolicy: DefaultPolicy.On,
        ReadOnly: false,
        AuditedArguments: ["level"],               // 数値・真偽値・列挙値だけ。デバイスは解決済みの target として自動で記録される
        Timeout: TimeSpan.FromSeconds(10));        // 既定 5 秒。DDC/CI など遅いものだけ延ばす

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplaySetBrightnessArgs args, CancellationToken ct)
        => /* ID/別名/既定 → 具体的なID。ロックキーは "display:device:{id}" */;

    protected override Task<ToolOutcome<DisplaySetBrightnessData>> ExecuteAsync(DisplaySetBrightnessArgs args, ToolExecution execution, CancellationToken ct)
    {
        // 解決済みの対象は execution.TargetId
        // 1. 変更前の値を読む  2. 既に目標値なら no-op (Changed=false, Verified)
        // 3. ct.ThrowIfCancellationRequested() → execution.BeginSideEffect() → 変更
        // 4. 読み戻して一致すれば Verified、違えば VERIFICATION_FAILED
        // 変更後の処理(読み戻しなど)が失敗したら ToolOutcomeUnknownException を投げる(status=unknown になる)
    }
}
```

ルール:

- 引数クラスが入力スキーマと検証の唯一の定義。スキーマを手書きしない。使える属性: `Description` / `Range` / `StringLength` / `DefaultValue` / `required`。enum は snake_case 文字列になる。
- 省略可能な引数は「省略」で表す。明示的な `null` と、公開した綴り以外の列挙値(`"Output"`、`"output, input"` など)は検証で拒否される。
- OS に渡す識別子(デバイス ID・別名など)には `[NoControlCharacters]` を付ける(検証で拒否され、監査にも残らない)。
- 状態変更ツールの排他キーと実行対象には、入力の文字列ではなく OS が返した正規の ID を使う(参考: `AudioTarget.Resolve`)。`ToolTarget.TargetId` は監査ログに `target` として記録されるので、OS や登録情報から得た識別子だけを入れる。
- **状態変更ツールは、最初の OS 書き込みの直前に `execution.BeginSideEffect()` を必ず呼ぶ。** ここで最終の認可が行われ(直前に完了した失効や権限変更で拒否される)、以降のタイムアウトは `unknown`、それより前なら「何も変更していない」になる。読み取りや検証、no-op の判定はその前に済ませる。呼んだ後は OS への書き込み・読み戻しと、受理済み操作の結果をタイムアウト応答へ反映する `ReportAccepted` に限る。`ReportAccepted` はローカルな executor 状態の記録であり、追加の OS 呼び出しや外部副作用を開始しない。複数の OS 書き込みを順に行うツールは、2 回目以降の各書き込み直前に `execution.BeginNextSideEffect(cancellationToken)` を呼び、拒否やキャンセルが返ったら残りの書き込みを中止して、適用済みの結果を `ToolOutcome.Partial`（途中の進捗保存には `ReportProgress(execution, data)`）で返す（無断でロールバックしない）。対象別の結果を持ったまま失敗を返すときは `ToolOutcome.Error(data, code, message)`（何も適用していない場合）。
- `AuditedArguments` には数値・真偽値・列挙値の引数だけを書く(文字列はツール登録時にエラー)。
- null 許容(`string?` / `int?`)は配列形式の `type` にならないよう自動変換される: 引数では null を含めない単一の型(省略可能な引数は「省略」で表す)、結果では `anyOf: [{type}, {type: "null"}]`。一部の MCP クライアント(単一 `type` の方言)で拒否されないため。
- 値は絶対値で受け取る(相対値・トグルを避ける)。`all` などの一括指定は spec §8 で許可されたものだけ。
- 対象の解決は `ResolveTargetAsync` で行い、別名が解決できない・機器が見つからない場合に別の機器へフォールバックしない(`TARGET_NOT_FOUND`)。
- ロックキーは `"<category>:device:<id>"`。既定デバイスを対象にした場合は `"<category>:default:<direction>:<role>"`（例: `audio:default:output:multimedia`）も含める(参考: `AudioTarget.Resolve`)。複数の役割ロックを取る場合は `ResourceLocks` にまとめて渡し、辞書順取得でデッドロックを防ぐ。既定を対象にした変更ツールは、ロック取得後に `AudioTarget.EnsureStillDefault` 等で現在も既定であることを再確認する。
- 結果データに秘密・個人ファイル・コマンドライン・環境変数を含めない。
- 時間がかかる/応答しない可能性がある OS 呼び出しはキャンセル不能でもよい。executor がタイムアウト時に `status: unknown` を返し、処理が終わるまでロックを保持する。

### ローカル専用ツール（`LocalOnly`）

管理コンソール（トレイメニュー・画面）からだけ実行する復旧操作は `ToolDescriptor(LocalOnly: true)` にする。MCP クライアントからは一覧に出ず、呼び出しは未知のツールとして拒否される。全体・クライアント別の許可は持たず（DB に登録されない）、`Caller.LocalConsole` で `ToolExecutor` を通して実行する（検証・ロック・監査・タイムアウトは通常どおり）。任意の値を受け取る汎用操作には使わない（参考: `display_restore_mode` は Sysacta 自身の直前の変更を元に戻すだけ）。

### 5. 本番ツール一覧に登録する

`src/Sysacta.App/ToolCatalog.cs` の配列に追加する。ここ以外で登録しない。起動時に `ToolRegistry` が名前規則と重複を検証し、`AccessStore.SyncTools` が DB に登録する(既存環境では無効状態で追加される)。

### 6. テストを書く

| テスト | 場所 | 必須の内容 |
|---|---|---|
| 単体 | `tests/Sysacta.Core.Tests/` | フェイクアダプターで executor 経由で呼ぶ。成功・no-op・範囲外/未知引数・対象なし・別方向/別種の機器・切断・OSエラー・検証失敗 |
| 統合 | `tests/Sysacta.Mcp.Tests/` | 許可のないクライアントからの直接呼び出しが拒否されること。状態変更ツールなら監査に許可リストの引数だけが残ること |
| 実機 | `tests/Sysacta.Windows.Tests/` | 読み取りは機器がなければ `Assert.SkipWhen`。状態変更は `SYSACTA_HARDWARE_TESTS=1` のときだけ実行し、必ず元に戻す |
| 手動 | `docs/manual-tests.md` | 自動化できない確認項目と結果(未実施なら「未実施」) |

### 7. ドキュメント

- spec §7 の表と差分がある場合は spec を更新するのではなく、差分と理由を進捗ファイルに記録してユーザーに確認する。
- 新しいエラーコードを追加したら下の表を更新する。

## 結果の形式 (spec §9)

```json
{"status":"success","verification":"verified","data":{...},"request_id":"..."}
{"status":"partial","verification":"verified","data":{...},"error":{"code":"OS_API_ERROR","message":"...","retryable":false},"request_id":"..."}
{"status":"error","error":{"code":"DEVICE_DISCONNECTED","message":"...","retryable":false},"request_id":"..."}
{"status":"unknown","verification":"unknown","error":{"code":"TIMEOUT","message":"...","retryable":false},"request_id":"..."}
```

MCP では `structuredContent` にこの JSON、`content` に同じ JSON テキストを入れ、`status != success` なら `isError: true`。

| コード | 意味 | 誰が返すか |
|---|---|---|
| INVALID_ARGUMENT | 引数が不正・未知・範囲外 | executor (自動) / ツール |
| TOOL_DISABLED | サーバー全体で無効 | executor |
| PERMISSION_DENIED | クライアントに未許可、失効・期限切れ | executor |
| NOT_SUPPORTED / ADMIN_REQUIRED | この環境で恒常的に利用不可 | `GetAvailability()` |
| TARGET_NOT_FOUND / AMBIGUOUS_TARGET | 対象が解決できない | ツール (`ResolveTargetAsync`) |
| TARGET_PROTECTED | 安全規則が「対象が使用中かもしれない」として変更を拒否した(何も変更していない。`blocked_reason` に固定理由) | ツール(`NetworkGuard`) |
| TARGET_CHANGED | ロック待機中に既定デバイスが切り替わった、登録アプリの承認(世代)・実行ファイルが変わった、pid が別のプロセスに再利用された(いずれも副作用なし) | ツール (`EnsureStillDefault`、`AppTargets.Recheck`、アダプター) |
| DEVICE_DISCONNECTED / NO_ACTIVE_SESSION | 一時的に操作できない | ツール/アダプター |
| NOTIFICATIONS_DISABLED | Windows の設定・ポリシーで通知が無効(何も送っていない) | 通知アダプター |
| BUSY | 待機キューが満杯、通知のレート制限(再試行可) | executor / ツール |
| TIMEOUT / CANCELLED | 実行前なら retryable、実行中なら status=unknown | executor |
| VERIFICATION_FAILED | 変更後の読み戻しが一致しない | ツール |
| (任意のコード) + status=unknown | 変更を実行した後に失敗し、反映されたか分からない(読み戻し中の切断など) | ツールが `ToolOutcomeUnknownException` を投げる |
| OS_API_ERROR | OS API の失敗 | アダプター |
| AUDIT_UNAVAILABLE | 監査ログに書けないため状態変更を拒否 | executor |
| INTERNAL_ERROR | 想定外の例外(詳細は返さない) | executor |
| UNKNOWN_TOOL | 存在しないツール(MCP ではプロトコルエラー -32602) | executor |


### メディア（Phase 1D）

メディア操作は `Sysacta.Core/Tools/Media/` の共通フローを通す。対象解決後にセッション単位で排他し、一覧変更・現在セッション・操作可否・シーク範囲を再確認する。最終準備で必要な WinRT 読み取りを完了し、`BeginSideEffect` の直後に初めて書き込み API を開始する。操作の受理は状態変化の証明ではない。一覧の `app_label` は安全な選択表示であり、同一性や権限には使用しない。現在セッションを列挙結果と安全に照合できない場合、省略指定は拒否されるため、`media_list_sessions` の `app_label` と明示 `session_id` を使用する。実メディア制御は未検証。[進捗](phase-1d-progress.md)を参照する。

### 電源（Phase 1E）

- PC 全体に影響する遷移（ロック・スリープ・シャットダウン・再起動）は `ToolDescriptor(Transition: PowerTransition.X)` を付けるだけで、executor が `PowerTransitionGate` で排他的に受け付ける。ツール側で独自の排他やゲートを作らない。通常の状態変更ツールは何もしなくても共有側で参加する（遷移中は BUSY）。
- 特権が必要な OS 呼び出しは、準備（特権の有効化）を開始点より前に済ませ、`BeginSideEffect` → 偽装 → 要求 1 回 → 復元の順にする（`WindowsPower.WithShutdownPrivilege`）。プロセストークンを変更しない。
- 開始点後に OS が未実行を返した場合は例外ではなく `ToolOutcome.Error` を返す（executor はそれを「未実行確定」としてゲートを解放する。例外は未確定扱い）。
- 受理は完了ではない。遷移は常に `accepted`。詳細は [Phase 1E 進捗](phase-1e-progress.md)。

### システム監視（Phase 1F）

- 読み取りの一部が欠けても成功として返し、欠けた値は `null` と `UnavailableReason`（`not_supported` / `permission_denied` / `unavailable` / `invalid_data` / `interrupted` / `truncated`）で表す。0・空文字・推定値で埋めない。`partial` は状態変更専用で、読み取りの欠損には使わない。全体が読めないときだけ `ToolFailureException`。
- OS から得た数値の検証・単位変換・集計は Core（`SystemStatus` / `SystemInventory`）に置き、フェイクで単体テストする。Windows アダプターは生の値と OS の失敗理由だけを返す。
- 測定の時刻（UTC）と測定窓（開始・終了・実測 ms）を結果に含める。PDH のカウンター・WMI のクエリ・レジストリ値は定数に固定し、引数にしない。詳細は [Phase 1F 進捗](phase-1f-progress.md)。

### 通知(Phase 1G)

- `notification_send` は固定の送信元でプレーンテキストだけを出す。文字列は `[NotificationText]`(Core)で検証し、XML は固定テンプレートの DOM テキストノードで作る。テンプレート・画像・アクション・起動引数を引数にしない。
- レート制限はツールのインスタンス(プロセスに 1 つ)が持ち、`BeginSideEffect` の直後に枠を消費する。API の受理は常に `accepted` で、表示の証明ではない。詳細は [Phase 1G 進捗](phase-1g-progress.md)。

### アプリ・プロセス(Phase 2A)

- 対象がローカルの許可リスト(`IAppRegistry`)に依存する状態変更は、`execution.BeginSideEffect(targetCheck)` を使う。`targetCheck` は認可と同じ `AuthorizationGate` の読み取り側で評価され、登録の無効化・削除・再承認(世代変更)が開始点より前に確定していれば拒否される(監査は denied、OS 呼び出しなし)。中では Sysacta 自身の状態の短い読み取りだけを行い、OS 呼び出し・ハッシュ・await・ゲートの再取得をしない。許可リストへの書き込みは `AppRegistryStore` がゲートの書き込み側で行う。
- `app_id` は登録時に生成する不透明な ID(`app_` + 32 桁の 16 進)。名前・パス・PID・別名での解決はしない。解決時の登録(世代)をロック後に `AppTargets.Recheck` で照合し、新しい承認へ切り替えない。
- ネイティブハンドルは `ResolveTargetAsync` で保持しない。対象の固定に必要な値(pid・生成時刻)だけを解決し、実行ファイル・プロセスのハンドルはロック内の `ExecuteAsync` で開いて `using` で解放する。
- ロックキーは `app:{id}` と `process:{pid}:{生成時刻 FILETIME}`(`AppTarget`)。app_launch / app_close / process_terminate が共有する。
- `pid` は必ず正確な `start_time`(UTC、100 ns、`ProcessStartTime`)と組で受け取る。詳細は [Phase 2A 進捗](phase-2a-progress.md)。

### ネットワーク(Phase 2B)

- アダプターは `net-` + InterfaceGuid(32 桁小文字 16 進)で識別する。LUID・InterfaceIndex・名前・IP を ID の代わりにしない(Index は無効化→有効化で変わり得る)。対応付けは LUID で行う。
- OS の表(`GetIfTable2` など)は Windows アダプターが上限付きで読み、生の値と `NetTable<T>`(完全/切り詰め/失敗)を返す。種別・状態の解釈と安全判定は Core(`NetworkAdapters` / `NetworkGuard`)に置き、フェイクでテストする。
- 通信量は 1 要求内で 2 回読み、`TimeProvider` の単調時計で窓を測る。同一性・状態の変化、カウンターの減少、Sysacta 自身の変更(`NetworkChanges`)が窓に重なれば速度は null と理由。合算しない。
- 接続断の恐れがある変更は、経路の推定(接続元アドレス・転送ヘッダー・ループバック)に頼らず、「その対象を通る通信が存在し得ない」ことを完全な OS 情報から確かめたときだけ行う。情報の欠落・切り詰め・不一致は拒否(`TARGET_PROTECTED`)。全変更で `network:topology` を共有ロックし、ロック後に再判定、開始点の直前に変わりやすい値を再読する。詳細は [Phase 2B 進捗](phase-2b-progress.md)。
