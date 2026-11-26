# Phase 1B-EXP 進捗ログ（既定オーディオデバイス変更）

指示書は [phase-1b-exp.md](phase-1b-exp.md)。ブランチ `phase-1b-exp`（基点は `origin/main` の `6b7eed4`）。

## 状態

| 項目 | 状態 | 主なファイル |
|---|---|---|
| 技術調査・採否判定 | 完了（条件付き実装可） | 本ファイル |
| 共通基盤（`partial` / `unknown`・複数副作用の開始点・進捗スナップショット） | 実装・自動テスト済み | `src/Sysacta.Core/Tools/ToolResult.cs`, `ToolExecution.cs`, `SysactaTool.cs`, `ToolExecutor.cs` |
| 既定対象ロックの役割別共有と再確認 | 実装・自動テスト済み | `src/Sysacta.Core/Tools/Audio/AudioTarget.cs`, `AudioSetVolumeTool.cs`, `AudioSetMuteTool.cs` |
| `audio_set_default_device`（Core） | 実装・自動テスト済み | `src/Sysacta.Core/Tools/Audio/IAudioDefaultEndpoints.cs`, `AudioSetDefaultDeviceTool.cs` |
| 非公開 COM アダプター（Windows 隔離） | 実装・ビルド済み（**実機未検証**） | `src/Sysacta.Windows/Experimental/Audio/PolicyConfigDefaultEndpoints.cs` |
| 管理コンソール UI（Experimental 注意表示） | 実装済み（Windows CI で XAML/描画確認） | `src/Sysacta.App/Views/ToolsPage.xaml`, `ToolCatalog.cs` |
| Windows 実機での既定デバイス変更確認 | **未検証** | [manual-tests.md](manual-tests.md) E1〜E4 |

## 1. 技術調査

### 公式 API の有無

- Microsoft 公式の Core Audio API（`IMMDeviceEnumerator`）には `GetDefaultAudioEndpoint`（読み取り）しかなく、ユーザーセッション全体の既定オーディオエンドポイントを変更する公式公開 API は存在しない。Microsoft の設計方針として、既定デバイスの選択はシステム UI（設定 / サウンドコントロールパネル）を通じたユーザー操作に委ねられている。
- 検討して却下した代替案:
  - `IAudioPolicyConfigFactory::SetPersistedDefaultAudioEndpoint`（`Windows.Media.Internal.AudioPolicyConfig`、EarTrumpet が現在使用）: アプリ個別（プロセス単位）のルーティング変更であり、システム全体の既定デバイス変更ではない。さらに非公開 WinRT API であり用途外。
  - レジストリ（`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\...`）の直接書き換え: 指示書 §1 により禁止。
  - GUI 自動化（`mmsys.cpl` / 設定アプリ操作）や外部 CLI 呼び出し: 指示書 §1 により禁止。

### 非公開 API 候補の調査

一次ソースから以下を確認した:

| 項目 | 内容 |
|---|---|
| 参照元 1 | [frgnca/AudioDeviceCmdlets](https://github.com/frgnca/AudioDeviceCmdlets)（commit `26d08d30e9d804c016c994117f9b038b7d50da95`、**MIT License**）。`SOURCE/IPolicyConfig.cs`, `SOURCE/PolicyConfigClient.cs` で定義・利用。コードは転用せず契約確認のみに使用。 |
| 参照元 2 | [File-New-Project/EarTrumpet](https://github.com/File-New-Project/EarTrumpet)（commit `17bbd57a71dbea2e287a83ebe34a6d67be0040c3`、固有の除外条項付き MIT 系ライセンス）。過去の履歴（commit `00770d32c757799fc91a7ff768891db09b2020e4` 等）で `IPolicyConfig` を使用。現在はアプリ別ルーティング用の `IAudioPolicyConfigFactory` に移行。コード転用なし。 |
| 参照元 3 | SoundSwitch（GPL 系ライセンスのため参照のみ、コード転用禁止）。 |
| COM クラス | `CPolicyConfigClient` / CLSID `{870AF99C-171D-4F9E-AF0D-E63DF40C2BC9}` |
| COM インターフェース | `IPolicyConfig` / IID `{F8679F50-850A-41CF-9C72-430F290290C8}`（`IUnknown` 派生、Win7 以降の形式） |
| vtable 構成 | `IUnknown`（3 スロット）に続き、10 個の先行メソッド（`GetMixFormat`〜`SetPropertyValue`）を挟んだ **11 番目（vtable インデックス 13）** が `SetDefaultEndpoint([MarshalAs(LPWStr)] string deviceId, ERole role)`。`ERole` は公開定義（`mmdeviceapi.h`）の `eConsole=0`, `eMultimedia=1`, `eCommunications=2`。 |
| 対応環境 | Windows 11 x64（24H2 を含む既存 OSS 実績）。本タスクでは Windows 11（build >= 22000）かつ x64 に限定し、実行時に `CoCreateInstance` + `QueryInterface` プローブで存在確認する。 |
| 必要権限 | 通常ユーザー権限（管理者権限不要）。マニフェストは `asInvoker` を維持。 |
| アパートメント | 実行基盤の `ToolExecutor` はスレッドプール（MTA）上で呼び出す。UI スレッド（STA）から `GetAvailability()` が呼ばれた場合はスレッドプール（MTA）へ逃がしてプローブする。 |
| 失敗時の挙動 | `HRESULT` を返す（`E_NOTFOUND`/`E_INVALIDARG` → `TARGET_NOT_FOUND`、`AUDCLNT_E_DEVICE_INVALIDATED` → `DEVICE_DISCONNECTED`、その他 → `OS_API_ERROR`）。生の例外メッセージやパスは返却しない。 |

### 採否判定

- **条件付きで実装可** と判断する。
- 根拠: 公式 API は存在しないものの、`IPolicyConfig::SetDefaultEndpoint` は複数の長寿 OSS で CLSID/IID/vtable 契約が一致して確認されており、通常権限で動作し、既存の公開 API `IAudioEndpoints.GetDefaultEndpointId(direction, role)` による変更前後の読み戻し検証が可能である。
- 安全策:
  1. `DefaultPolicy.Experimental`（新規・アップグレードともに初期無効、既存クライアントへ自動付与しない）。
  2. `Sysacta.Windows/Experimental/Audio/` に隔離し、独自の最小限の COM インターフェース宣言のみを記述（外部パッケージ追加なし、他方式への自動フォールバックなし）。
  3. 能力判定（Windows 11 x64 かつ QI プローブ成功）を満たさない環境では `tools/list` から隠し直接呼び出しも拒否。
  4. 役割ごとの独立変更を前提とせず、実行前後に両方向×3役割（計 6 項目）を読み戻し、意図しない役割の変化も検出して `partial` として報告・停止する。
  5. **Windows 実機での既定デバイス変更テスト（`SYSACTA_HARDWARE_TESTS=1`）は利用者の明示承認なしに実行しない。未検証の間は「実機未検証」と明記し、動作確認済みとして公開しない。**

## 2. 設計判断

- **役割別共有ロックと再検証**: `AudioTarget.DefaultRoleKey` で `audio:default:{direction}:{role}` を導入した。既定指定の `audio_set_volume` / `audio_set_mute` は `audio:default:{direction}:multimedia` を取得し、ロック取得後に `AudioTarget.EnsureStillDefault` で現在も既定であることを再確認する（切り替わっていたら副作用なしで `TARGET_CHANGED`、`retryable: true`）。`audio_set_default_device` は対象デバイスのキーに加え、指定方向の 3 役割すべてのキーを `ResourceLocks` にまとめて渡して辞書順取得する（非公開 API が役割を連動変更する可能性に備え、デッドロックを避けつつ全役割を直列化する）。
- **複数副作用の開始点・進捗スナップショット**: `ToolExecution.BeginSideEffect()`（初回書き込み直前）に加え、2 回目以降の書き込み直前に呼ぶ `BeginNextSideEffect(CancellationToken)` を追加した。`ToolExecutor` 側のタイムアウト/キャンセル/例外による切り離し（`Detach()`）、`CancellationToken`、および `AuthorizationGate` 下の再認可を検査し、拒否された場合は残りの書き込みを中止して `partial` を返す（ロールバックはしない）。途中の進捗は `ReportProgress` で `TData` として記録し、開始後にタイムアウトや例外が起きた場合も `status: unknown` の `data` に反映する。
- **結果ステータス (`ToolOutcome.Partial` / `Unknown`)**: `ToolExecutor` は `HasStarted` が真のときのみ `Partial` / `Unknown` を許可し、未開始なら `INTERNAL_ERROR` としてフェイルセーフにする。MCP 応答では `status != success` に対して `isError=true` を設定する。

## 3. JSON 契約 (`audio_set_default_device`)

引数: `device`（必須, ID または登録済み別名, 制御文字不可）, `direction`（必須, `output` / `input`）, `role`（必須, `console` / `multimedia` / `communications` / `all`）。監査は `direction`, `role`, `target`（解決済み正規 ID）のみ。

成功 (`status: "success"`, `verification: "verified"`):
```json
{
  "status": "success",
  "verification": "verified",
  "data": {
    "device_id": "spk2",
    "direction": "output",
    "requested_role": "console",
    "roles": [
      {"role": "console", "previous_device_id": "spk", "current_device_id": "spk2", "changed": true, "status": "changed", "error_code": null}
    ],
    "unrequested_changes": []
  },
  "request_id": "..."
}
```

部分成功 (`status: "partial"`, `verification: "verified"`, MCP `isError: true`):
```json
{
  "status": "partial",
  "verification": "verified",
  "data": {
    "device_id": "spk2",
    "direction": "output",
    "requested_role": "all",
    "roles": [
      {"role": "console", "previous_device_id": "spk", "current_device_id": "spk2", "changed": true, "status": "changed", "error_code": null},
      {"role": "multimedia", "previous_device_id": "spk", "current_device_id": "spk", "changed": false, "status": "failed", "error_code": "OS_API_ERROR"},
      {"role": "communications", "previous_device_id": "spk", "current_device_id": null, "changed": null, "status": "not_attempted", "error_code": null}
    ],
    "unrequested_changes": []
  },
  "error": {"code": "OS_API_ERROR", "message": "The Windows audio policy interface returned an error.", "retryable": false},
  "request_id": "..."
}
```

結果不明 (`status: "unknown"`, `verification: "unknown"`, MCP `isError: true`):
```json
{
  "status": "unknown",
  "verification": "unknown",
  "data": {
    "device_id": "spk2",
    "direction": "output",
    "requested_role": "all",
    "roles": [
      {"role": "console", "previous_device_id": "spk", "current_device_id": "spk2", "changed": true, "status": "changed", "error_code": null},
      {"role": "multimedia", "previous_device_id": "spk", "current_device_id": null, "changed": null, "status": "unknown", "error_code": null},
      {"role": "communications", "previous_device_id": "spk", "current_device_id": null, "changed": null, "status": "not_attempted", "error_code": null}
    ],
    "unrequested_changes": []
  },
  "error": {"code": "TIMEOUT", "message": "The operation did not complete within the time limit. Whether the change was applied is unknown.", "retryable": false},
  "request_id": "..."
}
```

## 4. 仕様・指示書との差分

- **ロック範囲の拡大**: 指示書 §4 では「対象方向・役割のキー」とあるが、非公開 API の役割間連動による競合を防ぐため、単一役割指定時でも指定方向の 3 役割すべてのロックキーを取得する設計とした（取得順序は `ResourceLocks` で固定しデッドロックしない）。
- **`AudioDeviceCatalog` の `operations`**: `audio_list_devices` の `operations` には `audio_set_default_device` を含めていない（Experimental で初期無効、かつ通常機能と分離するため）。
- **タイムアウト 5 秒**: 既存の既定値 5 秒を初期値として維持（Windows 実機での実測は未実施）。
- **`partial` の `verification`**: 変更済みの役割がすべて読み戻しで確認できている場合の `partial` は `verification: "verified"` を返し、確認不能な役割が 1 つでもあれば外枠を `status: "unknown"`, `verification: "unknown"` とする。
- **キャンセル競合時の `status: "unknown"`**: 呼び出しの途中でクライアントキャンセルが起きた場合、`ToolExecutor` の `WaitAsync(token)` が即座に `OperationCanceledException` を受けると `status: "unknown"`（`data` には進捗スナップショットを保持）になり、`BeginNextSideEffect` が後続の書き込みを阻止する。

## 5. 検証結果

| 環境 | 結果 |
|---|---|
| Linux (WSL2) `dotnet build Sysacta.sln -c Release --no-restore` | 警告 0 / エラー 0 |
| Linux `dotnet test Sysacta.sln -c Release --no-build` | Core 218 / Infrastructure 50 / Mcp 78 成功、Windows 5 成功・9 スキップ。App.Tests は対象外 |
| ミューテーション確認（実施後に復元済み） | ① `BeginSideEffect()` 除去 → `MultiSideEffectTests` 7 件失敗、② `BeginNextSideEffect` 無効化 → `MultiSideEffectTests` 7 件失敗、③ ロックキー変更 → `Default_targeted_volume_queued_behind_default_change` 失敗 |
| Windows 実機での既定デバイス変更 | **未検証**（利用者の明示承認なしに変更テストを実行しない方針のため） |
| Windows CI (`windows-latest`, [PR #7](https://github.com/clagon/sysacta/pull/7), run `45754482058`, commit `5e08d0e`) | ビルド・スモークテスト成功。Core 218 / Infrastructure 50 / Mcp 78 / App 17 成功、Windows 7 成功・7 スキップ（`PolicyConfigDefaultEndpointsTests.Availability_and_open_dispose_without_writes_leave_defaults_unchanged` を含む読み取り・能力判定テストが成功、実機変更テストと音声デバイス依存テストはスキップ） |

## 6. 残存リスク

- **非公開 API の破壊・プロセス異常終了**: `IPolicyConfig` は非公開 COM インターフェースであり、将来の Windows 更新で廃止・挙動変更される可能性がある。特に vtable レイアウトが変化した場合、P/Invoke / COM 呼び出しでプロセスが異常終了（クラッシュ）するリスクが残る。
- **役割連動の未検証**: Windows 11 実機で `console` / `multimedia` が連動するか独立かは未確認。連動が起きた場合は `unrequested_changes` と `partial` として報告される。
- **外部操作・キャンセル不能呼び出し**: Windows 設定画面など外部アプリによる変更は排他できない。またネイティブ COM 呼び出し自体はキャンセル不能であり、タイムアウト後も完了までロックと実行スロットを保持する。
- **実機未検証**: COM クラスの実行時プローブおよび実デバイスの切替・復元は実機で確認していない。

## 7. 公開可否の判断

- **実験的機能（初期無効・実機未検証）としてのみマージ可能**。新規・既存環境ともに無効（`DefaultPolicy.Experimental`）で出荷され、管理コンソールに「実機未検証・非公開 API」の警告を表示している。実機での動作確認（[manual-tests.md](manual-tests.md) E1〜E4）が完了するまで、「動作確認済み」として案内・既定有効化しないこと。
