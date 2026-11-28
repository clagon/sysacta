# Phase 1E 進捗（電源管理）

## 現在の結論（2026-11-28）

- 7 ツール（`power_get_state` / `power_list_plans` / `power_set_plan` / `power_lock` / `power_sleep` / `power_shutdown` / `power_restart`）と Windows アダプター `Sysacta.Windows/Power/WindowsPower.cs` を実装し、`ToolCatalog` に登録した。既定は spec §7.4 のとおり（On: get_state / list_plans / lock、Off: set_plan / sleep / shutdown / restart）。既存 DB への追加は全 7 ツール無効、既存クライアントへの自動付与なし。
- 電源遷移と全状態変更の排他は `ToolExecutor` に統合した 1 つの `PowerTransitionGate` で行う。ツールごとの排他機構は作っていない。
- 電源遷移が受理された後の解除は「ロック = 要求完了で解除」「スリープ = 開始点後の suspend→resume 通知、またはローカル解除」「シャットダウン/再起動 = ローカル解除のみ」。ローカル解除はローカル専用ツール `power_release_gate`（executor 経由・監査あり）と概要画面のボタン（確認ダイアログ付き）。
- `SeShutdownPrivilege` はプロセストークンを一切変更せず、複製した偽装トークンで有効化し、要求 1 回の間だけ呼び出しスレッドに設定する。
- 実機での電源操作（ロック・スリープ・シャットダウン・再起動・プラン変更）は一切行っていない。利用者の明示承認がないため。自動テスト・CI でも実行しない（フェイクのみ）。従って実機での受理・停止・復帰・通知の挙動は**未検証**。
- 検証結果は「検証結果」節を参照。

## 1. 技術調査（Microsoft Learn、2026-11-28 確認）

| API | 用途 | 採否 | 契約・制約（公式文書） |
|---|---|---|---|
| [GetSystemPowerStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus) / [SYSTEM_POWER_STATUS](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status) | AC・バッテリー | 採用 | `BOOL`。ACLineStatus 0/1/255、BatteryFlag ビット 1/2/4/8/128 と 255=不明、残量 0–100 と 255=不明、SystemStatusFlag（Win10 以降 battery saver）、秒数は DWORD で 0xFFFFFFFF(-1)=不明または AC 接続中。構造体 12 バイト |
| GetPwrCapabilities / [SYSTEM_POWER_CAPABILITIES](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_power_capabilities) | スリープ等の対応 | 採用 | 戻り値 `BOOLEAN`。読む項目は SystemS1–S4、HiberFilePresent、AoAc（S0 low power idle = Modern Standby）。先頭から BOOLEAN/BYTE が並ぶためオフセット S1=3, S2=4, S3=5, S4=6, HiberFilePresent=8, AoAc=20（winnt.h、構造体 76 バイト）。関数ページは learn で 404 だったため winnt.h と [IsPwrSuspendAllowed](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-ispwrsuspendallowed)（GetPwrCapabilities の使用を推奨）で確認。バッファは 128 バイト確保 |
| IsPwrSuspendAllowed | スリープ可否 | 不採用 | 非推奨と明記。GetPwrCapabilities で代替 |
| [PowerEnumerate](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerenumerate) (ACCESS_SCHEME=16) | プラン列挙 | 採用 | 戻り値 DWORD（0=成功）。GUID 16 バイトを受け取る。終端は ERROR_NO_MORE_ITEMS(259)（ページに記載なし、winerror.h の標準挙動）。最大 256 件まで、257 件目の存在で truncated |
| [PowerReadFriendlyName](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadfriendlyname) | プラン名 | 採用 | NULL バッファでサイズ取得、UTF-16。バッファは `PUCHAR` のためバイト単位と判断。2048 バイト超・失敗は null |
| [PowerGetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme) | 現在プラン | 採用 | 返された GUID は呼び出し側が `LocalFree` |
| [PowerSetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersetactivescheme) | プラン変更 | 採用 | 「現在のユーザーの」アクティブプランを設定。特権の記載なし。永続性の記載なし（Windows は電源設定として保持し再起動後も残る、と実運用上理解しているが実機未確認） |
| [LockWorkStation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-lockworkstation) | ロック | 採用 | 非同期。非ゼロは「開始した」だけでロック完了ではない。対話デスクトップのプロセスのみ。**ロック状態を判定する関数はない**と明記 → no-op 判定はしない |
| [SetSuspendState](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-setsuspendstate) | スリープ | 採用 | 3 引数とも `BOOLEAN`。`(FALSE, FALSE, FALSE)` = 休止状態にしない、bForce は「効果なし」、ウェイクイベントを無効にしない。「呼び出し**プロセス**が SE_SHUTDOWN_NAME を持つ必要」。戻るタイミング・Modern Standby の記載なし |
| [InitiateShutdownW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-initiateshutdownw) | シャットダウン・再起動 | 採用 | 「ローカル停止には呼び出し**スレッド**が SE_SHUTDOWN_NAME を持つ必要」。戻り値は Win32 エラーコードを直接返す。非同期で「API が戻った後かなり後に、または全く起きないことがある」「タイムアウト 0 でもアプリ・サービス・システムが中止できる」。SHUTDOWN_FORCE_SELF なしでは発行元セッションは対話的に終了し、成功でも停止は保証されない。SHUTDOWN_FORCE_OTHERS なしで他ユーザーがいれば ERROR_SHUTDOWN_USERS_LOGGED_ON。dwReason=0 は非推奨 |
| [ExitWindowsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-exitwindowsex) | シャットダウン・再起動 | 不採用 | 特権は「呼び出しプロセス」と記載されており、スレッド限定の特権スコープの根拠にならないため InitiateShutdownW を選んだ |
| [AdjustTokenPrivileges](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges) | 特権の有効化 | 採用 | 成功でも ERROR_NOT_ALL_ASSIGNED(1300) なら未保持（何も変更されない場合あり）。新しい特権は追加できない |
| [DuplicateTokenEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-duplicatetokenex) / [SetThreadToken](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadtoken) / [RevertToSelf](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-reverttoself) / [OpenThreadToken](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openthreadtoken) | スレッド限定の特権 | 採用 | TokenImpersonation + SecurityImpersonation の複製。RevertToSelf が失敗したら「プロセスを終了すべき」と明記 |
| [PBT_APMSUSPEND](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend) / [PBT_APMRESUMESUSPEND](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmresumesuspend) / [PBT_APMRESUMEAUTOMATIC](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmresumeautomatic) | 復帰検知 | 採用（.NET `SystemEvents.PowerModeChanged` 経由） | RESUMESUSPEND は「サスペンド前に APMSUSPEND を受け取った場合だけ」届く。RESUMEAUTOMATIC は毎回届くがユーザー不在を示さない。[SystemEvents.PowerModeChanged](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.powermodechanged) はメッセージポンプが必要（WPF アプリでは利用可） |
| [WM_ENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession) | 停止中止の検知 | 不採用 | wParam=FALSE は「セッションが終了しない」だが、Sysacta が QUERYENDSESSION を受ける前に中止された場合などに届く保証がない。中止の確実な証拠にならないため解除条件にしない |
| [Modern Standby](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby) | 制約 | — | S3 と Modern Standby は同時に有効にならず、切替は OS 再インストールが必要。SetSuspendState の Modern Standby での挙動は文書化されていない |

外部コマンド（shutdown.exe / powercfg / rundll32 / PowerShell）、非公開 API、強制フラグ（EWX_FORCE / SHUTDOWN_FORCE_*）、AbortSystemShutdown、ウェイクタイマー、WOL、休止状態の強制、プラン作成・編集は使っていない。追加 NuGet・追加権限なし。

## 2. JSON 契約

全ツールの入力は `additionalProperties: false`。`power_set_plan` 以外は引数なし（`force` / `delay` / `reason` 等は未知引数として INVALID_ARGUMENT）。今回 `RegularExpressionAttribute` を JSON Schema の `pattern` に出力するようにした（`media_*` の `session_id` も同様に厳密になる）。

| ツール | 入力 | 出力 `data` |
|---|---|---|
| power_get_state | なし | `ac_power`: online/offline/unknown、`battery`: present/absent/unknown、`battery_charging`、`battery_percent`(0–100)、`battery_seconds_remaining`、`battery_full_seconds`、`battery_saver`、`active_plan_id`、`sleep_supported`(S1–S3)、`hibernate_supported`(S4 かつ hiberfile)、`modern_standby`、`unavailable`: [power_status, active_plan, capabilities]。不明は null（0 や満充電に置換しない）。バッテリーなしは `absent` で異常扱いしない。全部取得不能なら OS_API_ERROR(retryable) |
| power_list_plans | なし | `plans[]`: `{plan_id, name, is_active}`（最大 64）、`active_plan_id`、`active_plan_read_failed`、`total_count`(列挙数、最大 256)、`truncated`、`failed_count`(名前取得失敗)。名前は信頼しない外部データ: `@`・`\`・`%` を含むものは null、制御・format 文字除去、64 文字まで |
| power_set_plan | `plan_id`: 36 文字、`^[0-9a-f]{8}-…-[0-9a-f]{12}$`（小文字 D 形式のみ） | `previous_plan_id`、`requested_plan_id`、`current_plan_id`（読み戻し、失敗時 null）、`changed`（不明 null） |
| power_lock / sleep / shutdown / restart | なし | `operation`: lock/sleep/shutdown/restart、`accepted`（不明 null） |

結果の対応（spec §9）:

| 状況 | status / verification | 備考 |
|---|---|---|
| プラン: 既に目標 | success / verified, changed=false | 書き込み 0 回 |
| プラン: 受理 + 読み戻し一致 | success / verified | |
| プラン: 受理 + 読み戻し失敗 | success / accepted, changed=null | `ReportAccepted` 済み |
| プラン: 受理 + 不一致 | error VERIFICATION_FAILED | 自動ロールバックなし |
| プラン: API 失敗 + 現在値=変更前 | error OS_API_ERROR, retryable=false | |
| プラン: API 失敗 + 現在値が変わった/読めない | unknown | |
| 遷移: OS が受理 | success / accepted, accepted=true | 完了・停止・ロックは観測しない |
| 遷移: OS が拒否（アクセス拒否・特権なし・他ユーザーあり） | error OS_API_ERROR, accepted=false | OS 未実行が確定 |
| 遷移: 既に停止処理中/予約済み | error BUSY, retryable=false | |
| 遷移: 準備失敗（特権未保持、S1–S3 非対応） | error、要求なし | 開始点より前 |
| 開始点後のタイムアウト/キャンセル | unknown（受理記録があれば accepted） | executor の既存処理 |

## 3. 特権管理

`WindowsPower.WithShutdownPrivilege`（スリープ・シャットダウン・再起動のみ。ロックは特権不要）:

1. `OpenThreadToken` で呼び出しスレッドが既に偽装中なら拒否（RevertToSelf で元の偽装を失わないため）。
2. `OpenProcessToken(TOKEN_DUPLICATE|TOKEN_QUERY)` → `DuplicateTokenEx(TokenImpersonation, SecurityImpersonation)`。
3. 複製トークンだけに `AdjustTokenPrivileges(SeShutdownPrivilege, ENABLED)`。失敗または `ERROR_NOT_ALL_ASSIGNED` なら OS_API_ERROR で拒否（要求なし）。P/Invoke の `SetLastError=true` はランタイムが呼び出し前に last error をクリアする。
4. `beginSideEffect()` = `ToolExecution.BeginSideEffect`（最終認可・開始点）。DB 読み取りを含むため偽装の前に行う。拒否・放棄なら例外で抜け、複製トークンを閉じる（偽装していない）。
5. `SetThreadToken(NULL, dup)` → OS 要求 1 回 → `finally` で `RevertToSelf`（失敗時は文書どおり `Environment.FailFast`）→ `using` で複製トークンを閉じる。SetThreadToken が失敗したら要求せず `Failed`（開始後だが未実行が確定）。

- プロセストークンは一度も変更しない。全処理が 1 スレッドで同期的に行われ、await を挟まないため、非同期切替による特権漏れはない。
- 特権を持てるのはアカウントが既に保持している場合だけ（新規付与・UAC 回避・昇格要求なし）。保持していなければ `GetAvailability` で sleep/shutdown/restart を非公開（NOT_SUPPORTED）。トークンの保持特権はプロセス存続中に変わらないため恒常的条件として扱う。照会失敗は非公開の根拠にしない。
- **未検証のリスク**: SetSuspendState の文書は「呼び出しプロセス」と記載。カーネルの特権確認は偽装中のスレッドトークンを使う設計だと理解しているが、文書上の保証はない。偽装トークンが使われない場合は ERROR_PRIVILEGE_NOT_HELD → `Refused`（未実行確定・ゲート解除）となり、安全側に失敗する。実機で要確認（manual-tests P5）。InitiateShutdownW は「呼び出しスレッド」と明記されている。
- Windows.Tests で、電源 API を呼ばずに特権スコープだけを検証する（要求の代わりにトークンを調べるデリゲートを渡す）: 要求中だけスレッドトークンに特権が有効、プロセストークンの属性は不変、終了後スレッドトークンなし、開始点での拒否・例外でも偽装が残らない。CI (windows-latest) で実行される。

## 4. 電源遷移ゲート（`PowerTransitionGate`）

### 状態遷移

```
            TryEnterTransition (変更 0 件かつ Idle)
   Idle ───────────────────────────────────────▶ Running
    ▲                                              │ 呼び出し(隔離中のネイティブ処理を含む)の終了 = ExitTransition
    │ 未開始 / 未実行確定のエラー / ロック /         │
    │ スリープで開始点後に suspend→resume 済み ◀────┤
    │                                              ▼
    │  NotifyResume (sleep, 開始点後に suspend 済み)  Held
    └──────────────── ReleaseHeld (ローカル解除) ◀───┘
```

- 通常の状態変更（ReadOnly=false かつ Transition なし）は Idle のときだけ共有 admission を取得（`_changes++`）。電源遷移は Idle かつ `_changes == 0` のときだけ排他 admission。どちらも 1 つのロック内で判定するため、両方が開始することはない（Core.Tests の 200 並列テスト）。
- 読み取り専用ツールはゲートを通らない。遷移中の取得は通常どおり（取得不能は各ツールのエラー）。
- 区別は信頼済みコードの `ToolDescriptor.Transition` だけ。`ToolRegistry` は ReadOnly・LocalOnly のツールに Transition を設定すると起動時に失敗する。
- ゲートはプロセスに 1 つ（`SysactaRuntime.PowerGate`）。MCP ホスト停止/再開・設定変更ではリセットされない（Mcp.Tests）。ローカルコンソールの変更（`display_restore_mode` など）も同じく BUSY。

### 取得順と解放責務

`ToolExecutor.ExecuteCoreAsync`: ツール検索 → 認可 → 引数検証 → 待機キュー上限 → **ゲート** → 対象解決 → リソースロック → 実行スロット → pending 監査 → 再認可 → 実行（開始点で `MarkStarted`）→ 最終監査 → 解放。

- 未認可・不正引数・キュー満杯の要求はゲートを取らない。ゲート拒否は既存のキュー満杯と同じく `Denied` + BUSY(retryable) で監査（既存の監査上限を通る。新しい監査経路なし）。
- 解放は既存の `HeldResources.Release` に統合。通常は `finally`、ネイティブ処理が隔離された場合（解決中のハング、実行中のタイムアウト/キャンセル）は処理の実終了時に 1 回だけ。従って HTTP タイムアウト後も実処理中の変更・遷移はゲートを保持し続ける。
- 遷移の「未実行確定」は、ツールが**返した** `status: error`（例外ではない）だけ。開始点後の例外は安全側に未確定（Held）として扱う。
- `MarkStarted` は `BeginSideEffect` 内（`AuthorizationGate` の読み取りロック中）で呼ばれ、ゲート内部のロックだけを短時間取る。逆順の取得はないためデッドロックしない。開始点以前の電源通知は無視する。

### 解除条件

| 遷移 | 自動解除 | ローカル解除 |
|---|---|---|
| 未開始 / 開始前の拒否・キャンセル / 準備失敗 | 即時 | — |
| OS が未実行を返した（拒否・BUSY・失敗） | 要求の実終了後 | — |
| ロック | 要求の実終了後（ロックは停止ではない） | — |
| スリープ受理または不明 | 要求の実終了 **かつ** 開始点後に suspend→resume を観測 | 要求の実終了後に可 |
| シャットダウン/再起動の受理または不明 | なし（OS の中止通知は確実な証拠にならない） | 要求の実終了後に可 |

- 経過時間・HTTP 切断・ホスト再開・単独の resume 通知はいずれも解除条件にしない。
- ローカル解除: 概要画面の「状態変更を再開」またはトレイメニューの「電源操作の後の状態変更を再開」（表示モード変更で概要画面のある画面が映らない場合のため。トレイはプライマリ画面の通知領域）→ 確認ダイアログ → `power_release_gate`（LocalOnly、MCP には存在しない、executor 経由・最終監査あり）。実行中（Running）は拒否（BUSY）。OS に送った停止要求は取り消さない（AbortSystemShutdown は使わない）。
- `power_release_gate` は OS 状態を変えないため pipeline 上は ReadOnly（ゲートを通らず、ロックも pending もない）。最終監査は記録される。これは「ローカル利用者の明示的な解除経路」で、MCP から到達できない（AccessPolicy の LocalOnly 判定）。
- アプリを再起動するとゲートは Idle に戻る。OS に残っている停止要求まで制御できないのは限界（アプリ終了 = 停止処理中の可能性が高い）。

## 5. 監査・タイムアウト

- pending は既存の `IAuditLog.Write`（SQLite `BEGIN IMMEDIATE` トランザクションのコミット）で、OS 要求より前に同期的に書く。今回 `SysactaDatabase.Open` で `PRAGMA synchronous = FULL` を明示した（WAL + FULL はコミットごとに WAL を fsync）。書けなければ AUDIT_UNAVAILABLE で実行しない（容量上限・予約枠・DB 障害は既存どおり）。
- OS がシャットダウンを先に進め最終監査を書けない場合、pending が残り、次回起動時の `RecoverInterrupted` で `unknown` / `INTERRUPTED` になる（Mcp.Tests）。応答のための遅延、停止の延期、再試行はしない。
- 受理を得たら `ReportAccepted` で記録してから返す（メモリ内であり永続監査ではない）。最終監査は応答の前に書かれる（`Call.Finish`）。
- 監査に残るのは登録済みツール名と、`power_set_plan` の解決済み `target`（OS が列挙した GUID）だけ。プラン名・生の入力・トークン情報・例外本文は残さない（DB/WAL/CSV/JSON をバイト検索する Mcp.Tests）。
- タイムアウトは既定 5 秒。SetSuspendState は一般にシステムが復帰するまで戻らないため、MCP の `power_sleep` は通常 5 秒で `unknown`(TIMEOUT、retryable=false)を返すのが想定どおりの応答で、ゲートは実終了まで Running。実機で戻るタイミングは未確認(P5)。
- `Changed` イベントはハンドラーの例外を握りつぶし、executor の解放経路を壊さない（概要画面は 3 秒ごとのポーリングで状態を表示）。

## 6. 設計判断

- **ゲートを executor 内部に置き、ツールは Descriptor の 1 項目で参加**: 既存の admission/HeldResources に数行足すだけで、全カテゴリ（将来のツールを含む）が例外なく対象になる。ツールごとの排他は作らない。
- **drain ではなく即時 BUSY**: 先行変更があれば遷移を副作用前に拒否する（計画の初期案どおり）。
- **シャットダウン/再起動はローカル解除のみ**: WM_ENDSESSION(FALSE) 等は Sysacta が問い合わせを受ける前の中止で届かない可能性があり、「確実な中止情報」にならない。計画の「より単純で安全な方式」を採用。
- **スリープは suspend→resume の両方を要求**: RESUMEAUTOMATIC だけの復帰（遠隔ウェイク）では .NET の PowerModes.Resume が届かない可能性がある（PowerModeChanged の実装は RESUMESUSPEND/RESUMECRITICAL を Resume とする想定。実機未確認）。届かなければローカル解除で戻せる。
- **Modern Standby だけの PC ではスリープ非公開**: SetSuspendState の挙動が文書化されていないため、S1–S3 がなければ NOT_SUPPORTED（電源モデルは再インストールなしに変わらないので恒常条件）。実行時にも再確認する。
- **ロックの no-op なし**: ロック状態を判定する公式関数がないと明記されているため推測しない。
- **管理画面の「動作確認」**: 既存仕様で読み取り専用・引数なしのツールだけ実行可能。電源の状態変更は実行できない（App.Tests）。
- **「すべて有効」**: 既存仕様で Off のツールは対象外。電源では get_state / list_plans / lock だけが有効になる（App.Tests）。Off の 4 ツールとロックに個別の注意文を表示。
- **AGENTS.md**: 新しい必須ルール「電源遷移ツールは `ToolDescriptor.Transition` を付け、独自の排他を作らない」「状態変更をゲートの外で実行しない」を最小限追記した。

## 7. 仕様との差分

- spec §7.4 の表どおり。差分なし。
- 追加: ローカル専用の `power_release_gate`（MCP 非公開、許可・DB 登録なし）。計画 §5 の「ローカル利用者の明示的な解除経路」。

## 8. 検証結果

（レビュー対応後の最終結果で更新する）

## 9. 実機検証の範囲

- 実機では何も実行していない（開発環境は WSL2。Windows 実機での読み取り確認も未実施）。Windows CI で `WindowsPowerTests`（読み取り・構造体サイズ・特権スコープ）が実行される。
- 未検証: 各電源 API の実際の受理と結果、SetSuspendState の戻るタイミングと偽装トークンでの特権、PowerModeChanged の通知順序と欠落、Modern Standby・ノート PC・バッテリーでの読み取り、リモートデスクトップでの挙動、プラン変更の永続性。manual-tests.md の Phase 1E 表を参照。

## 10. 残存リスク

- スリープの特権が偽装トークンで有効にならない場合、power_sleep は常に OS 拒否になる（安全側の失敗。未実行・ゲート解除）。
- スリープ後に resume 通知が届かない環境では、復帰後もローカル解除までゲートが残る（MCP の状態変更は BUSY）。
- シャットダウン/再起動が取り消された場合、ローカル利用者が解除するまで状態変更は BUSY。リモートだけで使っている場合は PC の前に行く必要がある（設計上の選択）。
- OS が先に停止した場合、最終監査と応答は失われ、pending → 次回起動で unknown。
- 他者(利用者・Windows Update 等)の停止処理が既に進行中/予約済みの場合、Sysacta の要求は `BUSY` で未実行のため、ゲートは Idle に戻り状態変更を受け付け続ける(Sysacta 自身の遷移ではないため。停止処理中の変更は通常時と同じ扱い)。
- 対象解決がネイティブ処理で戻らない状態変更は、戻るまで共有 admission を保持し、その間の電源遷移は BUSY になる(安全側。遷移を遅らせる既知の方法)。
- 同一ユーザーのプロセスが偽の WM_POWERBROADCAST を SystemEvents のウィンドウへ送れば保持中のスリープを解除できるが、そのプロセスは電源 API を直接呼べるため脅威モデルの範囲外。
- `LockWorkStation` などの受理は完了を意味しない。結果は常に `accepted` で、実際の状態は観測しない。
- 偽装中の RevertToSelf 失敗時はプロセスを即時終了する（文書の推奨。監査は次回起動で unknown）。
- プラン名のサニタイズは既知のパス・間接文字列を除外するだけで、名前に含まれ得る任意の情報を完全には検出できない。
