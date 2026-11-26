# Phase 1B 進捗ログ(オーディオの通常機能)

指示書は [phase-1b.md](phase-1b.md)。ブランチ `phase-1b`(基点は main の `3c06947`)。

## 状態

| 項目 | 状態 | 主なファイル |
|---|---|---|
| `audio_list_devices` / `audio_set_mute` | 実装済み | `src/Sysacta.Core/Tools/Audio/` |
| `audio_list_sessions` / `audio_set_app_volume` / `audio_set_app_mute` | 実装済み | `AudioSessions.cs`, `IAudioSessions.cs`, `src/Sysacta.Windows/Audio/CoreAudioSessions.cs` |
| デバイス管理画面(列挙・別名の登録/変更/削除/再紐付け) | 実装済み | `src/Sysacta.App/Views/DevicesPage.xaml`, `ViewModels/DevicesViewModel.cs` |
| 実機での状態変更(ミュート・アプリ別音量/ミュート) | **未確認** | [manual-tests.md](manual-tests.md) B3, B5 |
| Phase 1B-EXP(`audio_set_default_device`) | **未着手** | — |

## JSON 契約

全ツール共通の外枠は spec §9(`status` / `verification` / `data` / `error` / `request_id`)。引数は未知の項目・明示的な `null`・型変換・公開した綴り以外の列挙値を拒否する。省略可能な引数は省略する。既存の 2 ツールの引数と結果は変えていない。

### audio_list_devices(読み取り、`not_applicable`)

引数: `direction?` = `all`(既定) / `output` / `input`

```json
{"devices":[{"id":"{0.0.0.00000000}.{guid}","name":"Speakers","direction":"output","state":"active",
  "is_default":true,"default_roles":["console","multimedia"],"volume":40,"muted":false,"aliases":["desk"],
  "operations":["audio_get_state","audio_set_volume","audio_set_mute","audio_list_sessions"],"error_code":null}],
 "total_count":1,"failed_count":0,"truncated":false}
```

- `state`: `active` / `disabled` / `unplugged` / `not_present`。active 以外の `volume` / `muted` は null。
- `is_default` は省略時対象(multimedia 役割)。役割ごとの区別は `default_roles`。
- 並び順は active が先。最大 100 件。超えた場合は `truncated: true`。
- 識別情報すら読めない機器は除外して `failed_count` に数える。一部の値だけ読めない機器は `error_code`(`OS_API_ERROR` / `DEVICE_DISCONNECTED`)を付けて返す。
- 列挙全体の失敗は `OS_API_ERROR`(空一覧にしない)。利用可否(tools/list)は変えない。
- `operations` は機器の状態から見た対応ツール。クライアントの権限は反映しない。

### audio_set_mute(変更)

引数: `enabled`(必須, bool)、`device?`(ID/別名)、`direction?` = `output`(既定) / `input`

```json
{"device":{"id":"...","name":"Speakers","direction":"output","state":"active","is_default":true},
 "previous_muted":false,"current_muted":true,"changed":true}
```

- 一致していれば `changed: false` / `verified`(書き込みなし)。音量は変えない。
- 非 active は `DEVICE_DISCONNECTED`。読み戻し不一致は `VERIFICATION_FAILED`、読み戻し不能は `status: unknown`。
- 監査: `enabled`, `direction`, `target`(解決済み ID)。

### audio_list_sessions(読み取り、`not_applicable`)

引数: `device?`(出力機器の ID/別名。省略は multimedia 既定出力)

```json
{"device_id":"...","sessions":[{"session_id":"as_<43文字>","device_id":"...","name":"player","process_id":1234,
  "state":"active","volume":60,"muted":false,"is_system_sounds":false,
  "operations":["audio_set_app_volume","audio_set_app_mute"]}],
 "total_count":1,"failed_count":0,"truncated":false}
```

- `state`: `active` / `inactive` / `expired`。expired は `operations` が空。
- `name` は表示名、なければプロセス名、どちらもなければ null。システム音は `"System sounds"`(`process_id` は null)。パス・リソース参照(`@...`)は返さず、最大 128 文字。
- 最大 100 件(active → inactive → expired の順で残す)。
- 非 active の機器は `DEVICE_DISCONNECTED`、見つからない機器・入力機器は `TARGET_NOT_FOUND`。

### audio_set_app_volume / audio_set_app_mute(変更)

引数: `session_id`(必須)と `level`(必須, 整数 0〜100) / `enabled`(必須, bool)

```json
{"session_id":"as_...","device_id":"...","previous_volume":60,"current_volume":30,"changed":true}
{"session_id":"as_...","device_id":"...","previous_muted":false,"current_muted":true,"changed":true}
```

| 状況 | 結果 |
|---|---|
| 未知・古い・終了済みの ID、解決後の終了や置き換わり | `TARGET_NOT_FOUND`(書き込みなし) |
| 同じインスタンス ID が複数 | `AMBIGUOUS_TARGET`(書き込みなし) |
| 見つからず、かつ一部の機器・セッションが読めなかった | `OS_API_ERROR` |
| 書き込み API の失敗 | `OS_API_ERROR` / `DEVICE_DISCONNECTED` |
| 読み戻し不一致 | `VERIFICATION_FAILED` |
| 書き込み後の読み戻し不能・セッション終了 | `status: unknown` |

監査: `level` または `enabled` と `target`(解決できた session_id)。解決できなかった入力は記録しない。

### session_id の有効期間

セッションが存在し、同じ出力機器にある間だけ有効。アプリの再起動、セッションの終了・再生成、出力先の変更で変わる。Sysacta の再起動では変わらない(決定的に生成)。

## 技術判断

1. **依存は追加していない。** NAudio.Wasapi 3.3.0(固定済み)の `EnumerateAudioEndPoints` / `AudioSessionManager.Sessions` / `AudioSessionControl` / `SimpleAudioVolume` を使う。所有関係は公開ソース(タグ commit `4f8889e`)で確認: コレクションから取り出したデバイスとセッションは呼び出し側が破棄、`SimpleAudioVolume` は借用、セッションマネージャーはデバイスの Dispose で解放。`CoreAudioException` は `COMException` 派生。
2. **session_id** = `as_` + Base64Url(SHA-256(エンドポイント ID + NUL + セッションインスタンス ID))。生の識別子はパスを含むため公開も監査もしない。ID の表やキャッシュは持たず、呼び出しごとに active な出力機器を列挙して照合する(上限 512)。鍵付きハッシュは再起動で ID が変わるため採用しなかった(残存リスク参照)。
3. **同じ実体への書き込み**: `IAudioSessions.Open` が返すハンドルで「確認 → `BeginSideEffect()` → 書き込み → 読み戻し」を同じ COM オブジェクトに対して行う。解決時とロック取得後の 2 回照合する。
4. **ロック**: アプリ音量とミュートは `audio:session:{session_id}` を共有。`audio_set_mute` は既存の `AudioTarget.Resolve`(`audio:device:{id}`、省略時は `audio:default:{direction}` も)。機器の変更とセッションの変更は並列(別の値を書く)。
5. **一覧の共通化**: 管理画面とツールは `AudioDeviceCatalog.List` を使う。画面の表示はツール実行ではないため `ToolExecutor` を通さない(全体許可で `audio_list_devices` を無効にしても画面は使え、再読み込みのたびに監査記録を増やさない)。画面に状態を変える操作はない(別名はローカル設定)。
6. **別名**: 既存の `device_aliases` を使い、マイグレーションはない。一意性と解決はどちらも列の `NOCASE`(ASCII の大小のみ同一視)。予約語・制御文字・65 文字以上・既知の ID(登録済みの切断機器を含む)を拒否。再紐付けは利用者が別名と機器を選び、確認ダイアログを承認したときだけ。権限は変えないので `AuthorizationGate` は使わない(待機中の呼び出しは解決済みの機器のまま)。
7. **一部失敗の扱い**: 列挙は機器・セッション単位で失敗を数え、全体の失敗と区別する。
8. **docs/tools.md** のサンプルを現行の `ToolExecution` に合わせて修正した。
9. **AGENTS.md** は変更していない(新しいルールは不要)。

## 仕様との差分

- 指示書の初期案からの追加項目: `audio_list_devices` の `error_code` / `total_count` / `failed_count` / `truncated`、セッション一覧の同様の件数。
- セッション一覧の表示名は 1 項目 `name`(表示名またはプロセス名)にまとめた。
- 読み戻しは常に可能なため、変更ツールは `accepted` を返さない。

## 検証結果(2026-11-26)

| 環境 | 結果 |
|---|---|
| Linux (WSL2) `dotnet build Sysacta.sln -c Release` | 警告 0 / エラー 0 |
| Linux `dotnet test` | Core 185 / Infrastructure 50 / Mcp 74 成功、Windows 5 成功・7 スキップ。App.Tests は対象外 |
| Windows 11 実機(`dotnet.exe <tests>.dll`) | Core 185 / Infrastructure 50 / Mcp 74 / App 17 成功、Windows 9 成功・3 スキップ(状態変更) |
| Windows CI | PR を参照 |

- Windows 実機の実行は、出力を Windows の一時フォルダーにコピーして行った(Mcp / App は WSL の UNC パスからだと出力なしで停止した。原因は未調査)。実行後にデバイス画面の余白を 1 行直した(以降の Windows 上の確認は CI)。
- 実デバイスの読み取り: 46 エンドポイント、既定出力のセッション 12 件、失敗 0。
- ミューテーション確認: `audio_set_mute` とアプリ別変更の開始点宣言を書き込みの後に移すと Core.Tests 18 件が失敗。セッションのロックキーを呼び出しごとに別にすると 1 件が失敗。確認後に元へ戻した。

完了条件との対応: 開始点・失効時の書き込みゼロ・排他・読み戻し・監査不能時の拒否は Core.Tests `AudioPhase1BToolTests.cs`、権限・スキーマ・監査の秘匿・容量上限・アップグレードは Mcp.Tests `AudioPhase1BServerTests.cs`。Phase 1A の予算・容量のテストは変更せず成功。

## 既知の制約・残存リスク

- 実機の状態変更は未確認([manual-tests.md](manual-tests.md) B3, B5)。切断・セッション再生成・複数役割の実機確認も未実施。
- `session_id` は鍵なしハッシュなので、パスを推測した照合は防げない。Windows が同じインスタンス ID を再利用した場合は区別できない([security.md](security.md))。
- セッションの照合は呼び出しごとに全 active 出力機器を列挙する(解決時と実行時の 2 回)。機器やセッションが非常に多い環境で 5 秒のタイムアウトに近づく可能性は未計測。
- 書き込み API が失敗を返した場合は未適用として `error` を返す(既存の `audio_set_volume` と同じ)。
- Windows の音量ミキサーなど外部からの変更は排他できない。
- デバイス画面は自動更新しない(再読み込みボタン、画面を開いたとき)。表示は最大 512 件。

## Phase 1B-EXP への引き継ぎ(未着手)

- 既定指定のロックは現状 `audio:default:{direction}`(multimedia のみ)。役割を指定して変更するツールを追加するときは、`AudioTarget.Resolve` と共通の役割別キーに広げ、`all` は固定順で複数取得する。
- `AudioRole` と `GetDefaultEndpointId(direction, role)` は読み取りに使える。変更用の API は何も追加していない。
- 公式 API の有無の調査、非公開 API の隔離(`Sysacta.Windows/Experimental`)、`DefaultPolicy.Experimental` は指示書 §8 のとおり別 PR。
