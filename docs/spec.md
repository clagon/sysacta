# Sysacta — 実装仕様書

- 版: 0.1（設計合意版）
- 日付: 2026-11-25
- 状態: Grill-me Q1〜Q25 合意済み。個別ツールの詳細・暫定数値は実装案であり、実機検証前。
- プロジェクト名: Sysacta。リポジトリ: `clagon/sysacta`。

## 1. 目的とスコープ

Windows 11 PCをローカルまたはリモートのパーソナルエージェントから、構造化されたMCPツールで安全かつ確実に管理する。PC設定を操作する専用APIであり、汎用コンピューター操作エージェントではない。

対象: オーディオ、ディスプレイ、メディア、電源、システム監視、アプリ・プロセス、ネットワーク、Bluetooth、通知。
対象外: 任意シェル・PowerShell実行、任意ファイル読み書き、マウス・キーボード・GUI自動化、スクリーンショット、クリップボード、外観設定、汎用Windowsサービス・デバイス管理、他OS対応、クラウド連携、テレメトリー、自動更新。アプリの登録・認証・権限・ログ削除をMCP経由で提供しない。

## 2. 合意事項（Q1〜Q25）

| Q | 項目 | 決定 |
|---|---|---|
| 1 | 責務 | PC総合管理型、汎用シェル等は対象外 |
| 2 | OS | Windows専用 |
| 3 | 言語 | C# / .NET、公式MCP SDK |
| 4 | 常駐 | タスクトレイ、ユーザーセッション、ログイン時自動起動可 |
| 5 | 接続 | Streamable HTTP専用 |
| 6 | 認証 | Bearerトークン |
| 7 | 機能 | 9カテゴリ |
| 8 | ツール粒度 | 状態取得の集約＋状態変更の独立、カテゴリ_動詞_対象 |
| 9 | 権限 | ツール単位、カテゴリ一括、即時反映、実行時確認なし |
| 10 | 依存 | ネイティブAPI＋NuGet、外部CLI原則なし、外部通信なし |
| 11 | 特権 | 通常権限が既定、任意管理者起動、MCPによる昇格なし |
| 12 | UI | WPF + WPF UI、MVVM、モダンなFluent管理コンソール |
| 13 | ログ | SQLite監査ログ、30日標準、検索・CSV/JSON出力、秘匿 |
| 14 | クライアント | 複数トークン＋個別権限、ハッシュのみ保存、失効即時 |
| 15 | 実装順 | Phase 0〜3 |
| 16 | 公開可否 | 恒常非対応は非公開、一時不可は構造化エラー |
| 17 | 初期許可 | 日常操作は許可、危険・追加ツールは無効 |
| 18 | アプリ | 自動検出＋許可リスト、任意パス・引数禁止 |
| 19 | 結果 | 状態検証可能なら検証、他はAPI受理を明示 |
| 20 | ネットワーク | localhost既定、バインド先選択、TLS終端外部委譲 |
| 21 | 配布 | x64自己完結型、ZIP＋Inno Setup、手動更新 |
| 22 | 識別 | ID・別名・既定対象、一括操作は個別結果 |
| 23 | 同時実行 | リソース単位排他、独立操作並列 |
| 24 | テスト | 単体＋統合＋実機、セキュリティ重点 |
| 25 | 非公開API | 条件付き許容、隔離、実験的扱い、既定無効 |

## 3. 技術スタックとプロジェクト構成

.NET 10 LTS、Windows 11 x64、WPF、WPF UI (lepoco)、CommunityToolkit.Mvvm、ASP.NET Core/Kestrel、公式 ModelContextProtocol.AspNetCore SDK、Microsoft.Data.Sqlite を候補とする。パッケージの安定版・ライセンス・互換性を実装開始時に確認し、バージョンを固定する。

```text
Sysacta.sln
Directory.Build.props
Directory.Packages.props
src/
  Sysacta.App/           # WPF、トレイ、管理画面、Composition Root
  Sysacta.Core/          # 契約、ユースケース、権限、結果型、排他制御
  Sysacta.Mcp/           # HTTP、認証、MCPツール、スキーマ、フィルタ
  Sysacta.Windows/       # OS/デバイスの実装
    Audio/
    Display/
    Media/
    Power/
    SystemInfo/
    Apps/
    Network/
    Bluetooth/
    Notifications/
    Experimental/         # 非公開APIアダプター
  Sysacta.Infrastructure/ # SQLite、設定、監査、マイグレーション
tests/
  Sysacta.Core.Tests/
  Sysacta.Mcp.Tests/
  Sysacta.Infrastructure.Tests/
  Sysacta.Windows.Tests/
installer/
  setup.iss
docs/
  spec.md
  tools.md
  security.md
  manual-tests.md
.github/workflows/
```

依存方向: App → MCP / Windows / Infrastructure / Core。MCP・Windows・Infrastructure → Core。CoreはWPF、HTTP、Windows API、SQLiteに依存しない。ツール実行は共通ユースケース経由とし、ローカルの動作確認UIも監査・入力検証・全体権限を迂回しない。Windows API呼び出しは機能ごとのアダプターに分離する。COM/WinRTのアパートメント要件、WPF Dispatcher、ハンドルの解放を明示する。UIスレッドをブロックしない。

## 4. 起動・常駐・設定UI

- ユーザーセッション内で単一インスタンスを起動。通常権限が既定。WPFウィンドウを閉じてもトレイに残る。明示的な終了でホストとデバイス資源を解放。
- サーバー起動/停止、接続情報コピー、設定、ログ、終了をトレイから操作。Windowsログイン時の自動起動を設定可能。自動起動は昇格しない。
- 管理者起動はユーザー自身の操作に限る。管理者として動作中はUIに常時表示。MCPから昇格不可。昇格中に通常権限でのアプリ起動を保証できない場合、アプリ起動ツールを非公開とする（権限の意図しない継承を禁止）。
- UIはWPF UIによるFluent Design。ライト・ダーク・OS追従、DPI対応、キーボード操作、適切な余白、空状態/失敗状態、過剰なカードの羅列を避ける。監視用途のUIでありWindows設定アプリの代替ではない。

| 画面 | 内容 |
|---|---|
| 概要 | 稼働状態、権限レベル、接続情報、公開可能ツール数、最近の操作 |
| 接続 | バインド先、ポート、外部URL設定、開始/停止、設定コピー |
| ツール | カテゴリ・ツール別許可、利用可否理由、実験的機能、動作確認 |
| クライアント | 発行/失効/再発行、有効期限、権限、最終利用、プリセット |
| デバイス | ID・名称・状態・機能・別名管理 |
| アプリ | 自動検出、手動登録、固定引数、起動許可 |
| ログ | 検索、フィルタ、詳細、CSV/JSON出力、保持期間 |
| 一般 | 自動起動、テーマ、データ保存場所、バージョン |

接続数の表示は実際に計測できる指標に限る。stateless MCPでは「接続中のエージェント数」ではなく処理中リクエスト数などを表示する。

## 5. MCP・ネットワーク

- `/mcp` のStreamable HTTPのみ。stdio・レガシーSSEエンドポイントは非対応。
- 初期バインド: `127.0.0.1:8765`（ポートは暫定）。特定IP、全インターフェースを選択可能。変更時はホストを安全に再起動し、失敗時は以前の設定に復帰するか停止を明示する。
- リモート利用の推奨: localhost待受 + Tailscale Serve等のHTTPS終端。Tailscale依存なし。HTTPの非loopback公開には警告。公衆インターネットへの直接公開を推奨しない。FW自動変更、UPnP、トンネルの自動作成なし。
- Host/Origin検証（DNS rebinding対策）。許可Host/Originは明示的な設定、Originが存在する場合は検証し不許可なら403。Originなしの非ブラウザクライアントを許容する。任意のCORS許可禁止。リバースプロキシで使用する外部ホストは明示的に登録し、信頼済みプロキシ以外の転送ヘッダーを信用しない。
- MCP SDK v2の仕様では2026-07-28以降のプロトコルはstateless。旧クライアント対応を含めSDK標準のバージョン互換機能を優先し、まずstatelessを既定とする。対象クライアントで互換性が必要な場合に限り旧リビジョンのstateful互換を検証する。
- `tools/list` はリクエストの認証主体ごとにフィルタ。`tools/call` でも独立に検証。ツール一覧変更通知は対応するプロトコル・トランスポートで配信できる場合のみ実装し、statelessで即時プッシュを前提にしない。クライアントのキャッシュや通知対応に依存せず、常に実行時の認可を適用する。
- レート制限、リクエストサイズ制限、同時実行制限、タイムアウトを設ける。不要なHTTPエンドポイントは公開しない。認証失敗は適切なHTTP 401、権限違反はMCP/HTTP層に応じて構造化する。

接続例（クライアントの設定形式は個別に確認）:
```json
{
  "mcpServers": {
    "sysacta": {
      "url": "http://127.0.0.1:8765/mcp",
      "headers": { "Authorization": "Bearer YOUR_TOKEN" }
    }
  }
}
```

## 6. 認証・認可

- 暗号学的乱数256bit以上のBearerトークンを生成。トークン原文は発行時/再発行時のみ表示。ハッシュ（例: SHA-256）を保存し、比較は固定時間。十分なエントロピーを持つランダムトークンが前提。ユーザー任意の弱いパスワードをトークンにしない。
- 名前、内部ID、作成日時、期限（無期限可）、失効状態、最終利用、権限を保持。再発行は旧トークンを失効させる。原文をDB/ログに保存しない。発行後の接続設定コピーは新規発行時に可能、以降はトークン差し替えが必要。
- localhostでも全MCPリクエストに認証を要求。セッションID（互換モードのみ）は認証情報の代替にしない。セッションとトークンIDの紐付けを検証する。
- 実効権限 = サーバー全体の許可 ∩ クライアント別許可 ∩ 実行環境の能力。新規トークンは発行時の全体許可を基に初期設定し、読み取り専用等のプリセットを用意する。将来の許可追加を既存トークンへ自動付与しない（安全側の実装案）。
- 設定変更・失効は次回認可から即時有効。キュー待機中の操作は副作用発生直前に再検証。すでに実行された副作用は巻き戻せない。
- 初期許可は日常操作と情報取得、危険な操作・実験的API・アップデート追加ツールは無効。許可設定はUIのみで変更可能。
- ローカル設定・DBはWindowsユーザー単位のアクセス制御で保護。ユーザー自身や同等以上の権限を持つローカルプロセスからの改変を防ぐサンドボックスではない。
- アプリ全体が管理者権限で起動した場合の攻撃面増加は残存リスク。通常権限を推奨。未登録の実行ファイル、任意コマンド、任意URI、スクリプト、DLL、環境変数の注入を受け付けない。

## 7. MCPツール一覧（実装案）

以下は合意した9カテゴリを具体化した候補。公開可否は能力・権限・検証結果に従う。名称、引数、既定許可の細部は未承認の実装案。`ON`=初回導入時に全体許可、`OFF`=明示的な許可が必要、`EXP`=実験的・初期無効。新バージョンで追加するツールはこの表より新規無効ルールを優先する。

### 7.1 オーディオ（Phase 1）

実装: NAudio / Core Audio（MMDevice, EndpointVolume, AudioSession）。マイク録音は対象外。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| audio_get_state | device?, direction=output | ON |
| audio_list_devices | direction=all | ON |
| audio_set_volume | level, device?, direction=output | ON |
| audio_set_mute | enabled, device?, direction=output | ON |
| audio_list_sessions | device? | ON |
| audio_set_app_volume | session_id, level | ON |
| audio_set_app_mute | session_id, enabled | ON |
| audio_set_default_device | device, direction, role | EXP |

音量は0〜100整数。direction=input/output。role=console/multimedia/communications/all。既定デバイス変更は非公開API利用の可能性があるため、日常操作の許可より実験的無効を優先。アプリ別音量はセッションIDを利用し、名前重複を勝手に解決しない。

### 7.2 ディスプレイ（Phase 1）

実装: Win32 DisplayConfig/Monitor Configuration API、DDC/CI、内蔵パネル用WMIを必要に応じ検証。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| display_list_devices | なし | ON |
| display_get_state | device? | ON |
| display_set_brightness | level, device? | ON |
| display_set_contrast | level, device? | ON |
| display_list_modes | device | ON |
| display_set_mode | device, mode_id | OFF |
| display_set_power | device, state | OFF |
| display_set_input | device, input_id | OFF |

輝度・コントラストは0〜100に正規化し、機器固有の値域に変換。mode_idは列挙済みの解像度/リフレッシュレート組み合わせ。対応VCP値のみ許可し、汎用VCP書き込みは提供しない。画面消失リスクがある設定には事前検証と、実現可能な場合にローカル復旧手段を設ける。ソフトウェアによる疑似輝度変更は黙って代替しない。DDC/CIによる電源・入力切替は機種依存。

### 7.3 メディア（Phase 1）

実装: WinRT GlobalSystemMediaTransportControlsSessionManager (GSMTC)。アプリが公開するセッションと対応操作に限る。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| media_get_state | session_id? | ON |
| media_list_sessions | なし | ON |
| media_play | session_id? | ON |
| media_pause | session_id? | ON |
| media_stop | session_id? | ON |
| media_next_track | session_id? | ON |
| media_previous_track | session_id? | ON |
| media_seek | position_ms, session_id? | ON |

状態にはタイトル、アーティスト、再生状態、位置、対応操作を含める（取得できる場合）。アルバム画像やメディアファイルは取得しない。未再生・操作非対応は明示する。seekは対応セッションかつ範囲検証できる場合に限る。

### 7.4 電源（Phase 1）

実装: Win32電源API、電源プランAPI。OSの権限・ポリシーに従う。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| power_get_state | なし | ON |
| power_list_plans | なし | ON |
| power_set_plan | plan_id | OFF |
| power_lock | なし | ON |
| power_sleep | なし | OFF |
| power_shutdown | なし | OFF |
| power_restart | なし | OFF |

強制終了フラグ・任意コマンド・任意スケジュールは提供しない。電源操作の成功はOS受理を意味し、実際の停止を保証しない。接続断前の応答もベストエフォート。実行前に監査記録を永続化する。Wake-on-LANやログイン前操作は対象外。

### 7.5 システム監視（Phase 1）

実装: System.Diagnostics、Win32、LibreHardwareMonitor（候補。ドライバ/権限/ライセンス検証必須）。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| system_get_status | なし | ON |
| system_get_hardware | なし | ON |
| system_get_temperatures | なし | ON |
| system_get_storage | なし | ON |

CPU/GPU使用率、RAM、稼働時間、OS、機器、センサー、容量等。取得できない項目はnullと理由を返す。測定時刻と必要に応じサンプリング時間を付ける。センサー値を捏造しない。特権ドライバを勝手に導入しない。ファイル列挙・個人ファイル取得なし。

### 7.6 アプリ・プロセス（Phase 2）

実装: スタートメニュー、登録済みアプリ情報、System.Diagnostics、Win32。パッケージアプリの起動方法は別途検証。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| app_list_available | なし | ON |
| app_list_running | なし | ON |
| app_launch | app_id | ON |
| app_close | app_id | OFF |
| process_list_running | limit?, filter? | ON |
| process_terminate | pid, start_time | OFF |

自動検出と手動登録はUIから。起動は許可リストの安定IDと登録済み固定引数のみ。登録時・起動時に実行対象を検証し、スクリプトホスト/シェル/任意引数を受け付けるランチャー等を除外または明示的に安全性審査する。リンク・実行対象差し替え・PATH探索・シェル解釈を避ける。ユーザーが登録したアプリ自体の機能まで制限できるわけではない。終了は通常終了を優先し、強制終了は独立ツール。PID再利用対策として開始時刻を確認。自己プロセス・保護プロセス・重要OSプロセスの終了は禁止し、通常ユーザーの管理対象に限定する。コマンドライン・環境変数を返さない。

### 7.7 ネットワーク（Phase 2）

実装: System.Net.NetworkInformation、IP Helper API、必要に応じ正式なアダプター管理API。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| network_get_status | なし | ON |
| network_list_adapters | なし | ON |
| network_get_traffic | adapter? | ON |
| network_set_adapter_enabled | adapter, enabled | OFF |

状態、IP、接続速度、送受信カウンタ等。通信速度は差分と経過時間を用いる。状態変更時は明示ID必須。ネットワーク設定・FW・DNS・ルーティングの汎用変更は対象外。現在のMCP接続に使われるアダプターの無効化は可能なら拒否し、判定不能なら安全側に拒否する案。リモート接続断を完全には防げないため危険操作扱い。

### 7.8 Bluetooth（Phase 2）

実装: WinRT DeviceInformation / Windows.Devices.Bluetooth。接続/切断はプロファイル別の技術検証が必須。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| bluetooth_list_devices | なし | ON |
| bluetooth_get_state | device | ON |
| bluetooth_connect | device | ON* |
| bluetooth_disconnect | device | OFF |

*公式・検証済みの方式で対応できるペアリング済み機器のみON。非公開APIに依存する場合はEXP。汎用接続/切断を保証しない。対応プロファイルに限定し、未ペアリング機器のペアリング・PIN入力、全体の無線無効化は対象外。利用中の入力機器切断なども危険操作扱い。

### 7.9 通知（Phase 1）

実装: Windows App SDK等のデスクトップ通知API。ポータブル/インストーラー両構成で登録・表示を実機確認。

| Tool | 引数の概要 | 既定 |
|---|---|---|
| notification_send | title, body | ON |

アプリ固有の送信元でテキスト通知を出す。任意URL・外部画像・起動アクション・XML入力は禁止。文字数制限とレート制限を設ける。API受理とユーザー閲覧を区別する。通知本文は監査ログでは既定マスク。

## 8. デバイス識別・公開可否

- 各カテゴリにデバイスID、表示名、別名、接続状態、対応機能を提供。可能な限りOSの安定IDを利用。別名はカテゴリ内で一意、予約語（例: all）やIDとの衝突禁止。
- ID/別名で指定。音声は方向・役割に応じた既定デバイス、ディスプレイはプライマリ、メディアは現在のセッションを省略時の対象とする。Bluetoothおよびネットワークの変更は対象指定必須。
- 別名が解決不能、重複、デバイスが切断された場合、別デバイスへフォールバックしない。IDが変わる可能性のある機器は再紐付けのUIを用意し、曖昧な自動紐付けは禁止。
- `all` は明示的に対応する低リスク操作のみ（例: 輝度）。一括結果は機器ごとの成功/失敗を返す。危険な操作や接続切替に一括指定を認めない。
- 権限不足、明確な恒常非対応、ユーザー無効は非公開。デバイス一時切断、メディア未再生、一時APIエラーは公開のまま構造化エラー。能力検出の失敗を恒常非対応と断定しない。状態変化を再評価し、UIには理由を表示。

## 9. 実行結果・エラー契約

すべてのツールに厳密なJSON Schema、明確な説明、列挙値・値域・文字列長制約を付ける。未知引数を拒否し、型の暗黙変換を避ける。MCP annotations（readOnlyHint等）はヒントであり認可に使わない。対応SDKでは構造化出力とoutputSchemaを使用する。

```json
{
  "status": "success",
  "verification": "verified",
  "data": {"device_id":"example", "previous_volume":70, "current_volume":30},
  "request_id": "example-id"
}
```

```json
{
  "status": "error",
  "error": {"code":"DEVICE_DISCONNECTED", "message":"The specified audio device is disconnected.", "retryable":false},
  "request_id": "example-id"
}
```

- status: success / error / partial（明示的一括操作）/ unknown（副作用の有無が不明）。verification: verified / accepted / not_applicable / unknown。acceptedはAPI受理のみで実行完了ではない。変更前後の値を可能な限り返す。
- MCPのツール実行エラーでは `isError=true` を適切に設定。プロトコル違反はプロトコルエラー。partial/unknownはクライアントが成功と誤解しないよう明示する。
- 共通コード例: INVALID_ARGUMENT, TOOL_DISABLED, PERMISSION_DENIED, ADMIN_REQUIRED, NOT_SUPPORTED, TARGET_NOT_FOUND, AMBIGUOUS_TARGET, DEVICE_DISCONNECTED, NO_ACTIVE_SESSION, BUSY, TIMEOUT, VERIFICATION_FAILED, OS_API_ERROR, INTERNAL_ERROR。
- エラーは内部スタックトレースや秘密を返さない。再試行可能性は副作用が未発生と判断できる場合に限りtrueとする。すでに目標状態の場合はno-opを返す。相対変更やトグルより絶対値操作を優先。

## 10. 並列実行・タイムアウト

独立操作を並列化し、変更操作はリソース単位（カテゴリ＋解決済みID）で直列化。既定デバイス変更と既定デバイス指定操作は共通の競合制御を持たせる。物理機器の共用バス等も考慮。一括操作で複数ロックを取る場合は固定順序。状態変更後の検証までロックを保持する。外部アプリによる変更は排他できない。

待機キュー・同時実行数・リクエストサイズ・レートに上限を設定する。暫定初期値: 同時実行8、全待機32、本文256KiB、トークン単位60件/分（実測で調整）。通常操作5秒、DDC/CI 10秒、Bluetooth 30秒程度を暫定上限とする。ネイティブAPIがキャンセル不能な場合、HTTPタイムアウトでロックを早期解放しない。結果不明を明示し、実処理完了まで隔離する。副作用後の無条件リトライ禁止。電源等の全体操作は専用ゲートとし、開始後の新規変更を拒否する設計を検討。

## 11. データ保存・監査

保存先候補: `%LOCALAPPDATA%\Sysacta\`。実行ファイルと分離し、ポータブル版でも共通。SQLiteのトランザクション、スキーマバージョン管理、マイグレーション、必要時のバックアップを実装する。

| テーブル（案） | 主な項目 |
|---|---|
| settings | key, value, updated_at |
| tools | tool_name, enabled, first_seen_version |
| clients | id, name, token_hash, created_at, expires_at, revoked_at, last_used_at |
| client_permissions | client_id, tool_name, allowed |
| device_aliases | category, device_id, alias |
| registered_apps | id, display_name, launch_target, fixed_args, allowed |
| audit_events | request_id, timestamp_utc, client_id, tool_name, source, sanitized_args, result, error_code, duration_ms |

監査ログは実行・拒否・認証失敗を記録。危険な副作用の前に実行意図を永続化し、終了後に結果を追記/更新。途中終了で未確定記録が残ることを許容する。認証失敗では不明なクライアントIDをnullとしトークン原文を絶対に記録しない。ログ書き込み失敗時は安全側に状態変更を拒否する方針を推奨。機密引数はフィールド単位の許可リストで記録し、本文・認証ヘッダー・レスポンス全文・生例外は保存しない。接続元は信頼できる範囲の情報のみ。30日標準、設定可能、定期削除、検索、CSV/JSON出力。CSV出力時は数式注入対策。監査ログは同一ユーザー権限のローカル改変に対する耐改ざん保証はない。

## 12. 非公開APIの扱い

公式API優先。非公開APIは既存OSSの利用実績・ライセンス・動作環境を確認し、Experimentalアダプターに隔離。初期状態無効、UIに注意を表示、機能単位で停止可能。Windowsアップデート後の互換性を確認する。DLLインジェクション、カーネル改変、セキュリティ機構の回避は禁止。実現できない機能は未対応として扱い、見かけだけの成功を返さない。

## 13. 実装フェーズと完了条件

| フェーズ | 対象 | 完了条件 |
|---|---|---|
| 0 | ソリューション、UI骨格、ホスト、MCP認証、ツール登録・認可、設定、監査、CI | モックツールを認証済みクライアントから呼び出せ、失効・権限変更・拒否・ログがテストされる |
| 1 | Audio, Display, Media, Power, System, Notification | 各カテゴリの正式APIで実現可能な中核操作を実機確認し、非対応・失敗が正しく返る |
| 2 | Apps, Network, Bluetooth、実験的機能 | 許可リスト、対象識別、危険操作制限、対応機器の実機確認。未対応は明記 |
| 3 | UI仕上げ、セキュリティ監査、復帰/更新試験、配布、文書 | 自動テスト合格、実機チェック結果、ZIP/インストーラー生成、チェックサム、既知の制約公開 |

段階ごとに動作可能な状態を維持する。Phase 0より前または並行して、非公開API・DDC/CI・Bluetooth・通知登録の小規模技術検証を行い、実装可否を早期に把握する。未検証機能を完了扱いしない。

## 14. テスト・品質保証

- 単体テスト: 入力制約、権限の積集合、別名解決、PID再利用、結果検証、排他、タイムアウト、マスキング、マイグレーション。
- HTTP/MCP統合テスト: 認証なし・不正・期限切れ・失効、主体ごとのtools/list、直接tools/call拒否、キュー待ち中の失効、Host/Origin、レート・サイズ制限、プロトコル互換、監査ログの秘匿。
- 実機: 音量/ミュート/アプリ別、外部モニターの明るさ、DDC/CI非対応、機器切断、メディアなし、通常/管理者権限、アプリ起動/終了、Bluetoothプロファイル、通知、スリープ/復帰、複数同時呼び出し、ログイン自動起動、設定破損、更新/アンインストール。
- 危険操作の自動テストはモックまたは隔離環境で実施。CIホストや利用中PCのシャットダウン等を行わない。Windows GitHub Actionsでビルドと自動テスト。物理機器のテストは docs/manual-tests.md に実施条件と結果を記録。
- 固定カバレッジ数値は設けない。認証・認可・入力検証などのセキュリティ境界を重点的に検証する。

## 15. 配布・更新

GitHub ActionsでWindows x64の.NET自己完結型をビルド。単一exe化は必須とせず、WPF/SQLite/ネイティブ依存が正常動作するフォルダ配布を優先。ZIPとInno SetupインストーラーをGitHub Releasesへ公開し、SHA-256チェックサムを添付。ユーザー単位インストール、通常権限。設定・トークン・監査ログはアップデート時に維持。アンインストール時のデータ削除はユーザーに選択させる案。アプリ自身の更新チェック・自動ダウンロードなし。依存関係のライセンス表記を同梱する。コード署名は未決定。署名なしではSmartScreen/Smart App Controlで制限され得る。チェックサムは信頼できる配布元が前提であり署名の代替ではない。

## 16. 実装前の確認事項・既知の限界

1. 公式MCP SDK v2および利用予定のCodex/Claude等の互換性。旧版通知と新stateless仕様の差異。
2. 既定オーディオデバイス変更の実装可否と非公開APIの安定性。
3. 外部ディスプレイのDDC/CI、対応VCP、内蔵パネルの輝度方式、モード変更・復旧。
4. Bluetooth機器の種類・プロファイル別の接続/切断可否。汎用機能は保証しない。
5. LibreHardwareMonitorのセンサー・ドライバ・権限・ライセンス。
6. 通知APIの登録要件とポータブル配布での動作。
7. 昇格時のアプリ起動権限、ログイン/ロック状態の制約。
8. コード署名、ライセンス、詳細なツールスキーマ、暫定上限値は未確定。

本仕様の時点でコード実装、ビルド、実機動作、依存関係の全面監査は実施していない。

## 17. 実装エージェントへの指示

本仕様を判断の基準として利用し、Phase 0から着手する。過剰な仕様管理を増やさず、実装とテストを小さい単位で進める。未決事項は安全側の暫定値を採用して理由を記録し、重大な責務変更・権限拡大はユーザーに確認する。特に認証/認可を後付けしない。対応不明なAPIを実装済みとして報告しない。各フェーズの完了条件とテスト結果をREADMEまたはdocsに残す。

## 18. 参考資料（2026-11-25確認）

- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- SDK v2 transports: https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html
- SDK v2 stateless: https://csharp.sdk.modelcontextprotocol.io/v2/concepts/stateless/stateless.html
- HTTP session mode: https://csharp.sdk.modelcontextprotocol.io/v2/api/ModelContextProtocol.AspNetCore.HttpServerSessionMode.html
- Monitor Configuration API: https://learn.microsoft.com/en-us/windows/win32/api/_monitor/
- GetMonitorCapabilities: https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-getmonitorcapabilities
- WPF UI（採用候補）: https://github.com/lepoco/wpfui
- NAudio（採用候補）: https://github.com/naudio/NAudio
- LibreHardwareMonitor（採用候補）: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
