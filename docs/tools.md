# MCP ツールの追加手順

新しいツールは、この手順と2つの参考実装をそのまま真似て追加します。認証・認可・入力検証・排他・タイムアウト・監査はすべて `ToolExecutor` が適用するので、ツールに書くのはドメインロジックだけです。

| 参考実装 | 種類 | ファイル |
|---|---|---|
| `audio_get_state` | 読み取り専用 | `src/Sysacta.Core/Tools/Audio/AudioGetStateTool.cs` |
| `audio_set_volume` | 状態変更(検証あり) | `src/Sysacta.Core/Tools/Audio/AudioSetVolumeTool.cs` |
| 対象デバイスの解決 | 共通処理 | `src/Sysacta.Core/Tools/Audio/AudioTarget.cs` |
| OS アダプター契約 | インターフェース | `src/Sysacta.Core/Tools/Audio/IAudioEndpoints.cs` |
| OS アダプター実装 | Windows (NAudio/COM) | `src/Sysacta.Windows/Audio/CoreAudioEndpoints.cs` |
| 単体テスト(フェイク) | Core.Tests | `tests/Sysacta.Core.Tests/AudioToolTests.cs` |
| 統合テスト(HTTP/MCP) | Mcp.Tests | `tests/Sysacta.Mcp.Tests/McpServerTests.cs` |
| 実機テスト | Windows.Tests | `tests/Sysacta.Windows.Tests/CoreAudioEndpointsTests.cs` |

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
                   5. ResolveTargetAsync: ID/別名/既定 → 具体的なID とロックキー
                   6. リソースロック (状態変更ツールのみ) → 実行スロット
                   7. 再認可 (待機中の失効・権限変更を反映)
                   8. 監査 pending を永続化 (状態変更のみ。失敗 → 実行しない)
                   9. ExecuteAsync (タイムアウト付き。ネイティブ処理が終わるまでロック保持)
                  10. 監査の最終結果
```

## 手順

### 1. 名前・カテゴリ・既定の権限を決める

- 名前: `<category>_<verb>_<object>`。カテゴリは `ToolCategories` の定数。spec §7 の表に名前があればそれを使う。
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
        AuditedArguments: ["level", "device"],     // 監査に残してよい引数だけ
        Timeout: TimeSpan.FromSeconds(10));        // 既定 5 秒。DDC/CI など遅いものだけ延ばす

    protected override ValueTask<ToolTarget> ResolveTargetAsync(DisplaySetBrightnessArgs args, CancellationToken ct)
        => /* ID/別名/既定 → 具体的なID。ロックキーは "display:device:{id}" */;

    protected override Task<ToolOutcome<DisplaySetBrightnessData>> ExecuteAsync(DisplaySetBrightnessArgs args, ToolTarget target, CancellationToken ct)
    {
        // 1. 変更前の値を読む  2. 既に目標値なら no-op (Changed=false, Verified)
        // 3. ct.ThrowIfCancellationRequested() → 変更  4. 読み戻して一致すれば Verified、違えば VERIFICATION_FAILED
        // 変更後の処理(読み戻しなど)が失敗したら ToolOutcomeUnknownException を投げる(status=unknown になる)
    }
}
```

ルール:

- 引数クラスが入力スキーマと検証の唯一の定義。スキーマを手書きしない。使える属性: `Description` / `Range` / `StringLength` / `DefaultValue` / `required`。enum は snake_case 文字列になる。
- null 許容(`string?` / `int?`)は配列形式の `type` にならないよう自動変換される: 引数では null を含めない単一の型(省略可能な引数は「省略」で表す)、結果では `anyOf: [{type}, {type: "null"}]`。一部の MCP クライアント(単一 `type` の方言)で拒否されないため。
- 値は絶対値で受け取る(相対値・トグルを避ける)。`all` などの一括指定は spec §8 で許可されたものだけ。
- 対象の解決は `ResolveTargetAsync` で行い、別名が解決できない・機器が見つからない場合に別の機器へフォールバックしない(`TARGET_NOT_FOUND`)。
- ロックキーは `"<category>:device:<id>"`。既定デバイスを対象にした場合は `"<category>:default:<role>"` も含める(参考: `AudioTarget.Resolve`)。
- 結果データに秘密・個人ファイル・コマンドライン・環境変数を含めない。
- 時間がかかる/応答しない可能性がある OS 呼び出しはキャンセル不能でもよい。executor がタイムアウト時に `status: unknown` を返し、処理が終わるまでロックを保持する。

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
| DEVICE_DISCONNECTED / NO_ACTIVE_SESSION | 一時的に操作できない | ツール/アダプター |
| BUSY | 待機キューが満杯(再試行可) | executor |
| TIMEOUT / CANCELLED | 実行前なら retryable、実行中なら status=unknown | executor |
| VERIFICATION_FAILED | 変更後の読み戻しが一致しない | ツール |
| (任意のコード) + status=unknown | 変更を実行した後に失敗し、反映されたか分からない(読み戻し中の切断など) | ツールが `ToolOutcomeUnknownException` を投げる |
| OS_API_ERROR | OS API の失敗 | アダプター |
| AUDIT_UNAVAILABLE | 監査ログに書けないため状態変更を拒否 | executor |
| INTERNAL_ERROR | 想定外の例外(詳細は返さない) | executor |
| UNKNOWN_TOOL | 存在しないツール(MCP ではプロトコルエラー -32602) | executor |
