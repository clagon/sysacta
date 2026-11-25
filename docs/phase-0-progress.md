# Phase 0 進捗ログ

作業の再開用メモ。コンテキスト圧縮後や次のフェーズの開始時は、まずこのファイルと [AGENTS.md](../AGENTS.md) を読むこと。

## 状態: Phase 0 実装完了・Windows CI 全件成功。exe の対話確認 (M1〜M4) と実 MCP クライアント接続 (M9) はユーザーによる手動確認待ち

| 項目 | 状態 | 主なファイル |
|---|---|---|
| ソリューション・中央パッケージ管理・CI | 完了 | `Sysacta.sln`, `Directory.*.props`, `global.json`, `.github/workflows/ci.yml` |
| 共通実行基盤(登録・スキーマ・検証・能力判定・認可・対象解決・排他・キュー・タイムアウト・結果・監査) | 完了 | `src/Sysacta.Core/Tools/` |
| 認証・認可(トークン、ハッシュ、固定時間比較、失効・再発行・期限、プリセット、即時反映、再認可) | 完了 | `src/Sysacta.Core/Security/`, `src/Sysacta.Infrastructure/AccessStore.cs` |
| MCP サーバー(Kestrel, /mcp stateless, Host/Origin, Bearer, レート・サイズ・同時接続制限, 開始/停止/再起動) | 完了 | `src/Sysacta.Mcp/` |
| 永続化(SQLite, マイグレーション, 設定, ツール, クライアント, 権限, 別名, 登録アプリ, 監査, 保持, 検索, CSV/JSON) | 完了 | `src/Sysacta.Infrastructure/` |
| リファレンスツール `audio_get_state` / `audio_set_volume` | 完了 | `src/Sysacta.Core/Tools/Audio/`, `src/Sysacta.Windows/Audio/` |
| WPF 管理コンソール 6画面・トレイ・単一インスタンス・自動起動・権限表示・テーマ | 完了 | `src/Sysacta.App/` |
| 後続向けドキュメント | 完了 | `AGENTS.md`, `docs/tools.md`, `docs/security.md`, `docs/manual-tests.md`, `README.md` |

デバイス画面・アプリ画面は該当機能(Phase 1/2)がないため作っていない(ダミーUI禁止)。`MainWindow.xaml` のナビゲーションに項目を追加するだけで拡張できる。`device_aliases` / `registered_apps` テーブルと別名解決 (`IDeviceAliases`) は実装済み。

## 検証結果

### Linux (WSL2, .NET SDK 10.0.404) — 最終コード

- `dotnet build Sysacta.sln -c Release`: 警告 0 / エラー 0(`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`)。
- `dotnet test Sysacta.sln -c Release`: **Core 71 / Infrastructure 37 / Mcp 38 成功**、Windows.Tests 3 スキップ(非Windows)、App.Tests は非Windowsでは対象外。

### Windows CI (GitHub Actions windows-latest, PR #1, run 45622045931, 2026-11-25) — 最終コード

- Restore / `dotnet build Sysacta.sln -c Release` / `dotnet test` すべて成功。
- **Core 71 / Infrastructure 37 / Mcp 38 / App 2 成功**、Windows.Tests 3 スキップ(ランナーに既定の音声出力デバイスがない。実機では下記のとおり成功)。
- これにより「Windows 向けビルド」「Windows での自動テスト(MCP 統合テストと最後のコード変更を含む)」を確認済み。

### Windows 11 実機(このPC。.NET ランタイム 10.0.14 のみ、`dotnet.exe <tests>.dll` で実行)

| テスト | 結果 | 備考 |
|---|---|---|
| Core.Tests | 69 件成功 | 最終版より前のビルド(その後の追加2件は Linux のみ) |
| Infrastructure.Tests | 37 件成功 | 1 回目のみ。その後 Smart App Control が SQLite ラッパーをブロック |
| Windows.Tests | 3 件成功 | 実デバイスの既定出力の読み取り、未知ID、`SYSACTA_HARDWARE_TESTS=1` で音量を2%変更→読み戻し→復元 |
| App.Tests | 2 件成功 | 全6ページ + MainWindow の XAML 読み込み/レイアウト、実際の通知領域アイコン生成と Kestrel ホストの起動/停止への追従 |
| Mcp.Tests | 未実行 | ASP.NET Core ランタイムがなく、さらに Smart App Control が `SQLitePCLRaw.batteries_v2.dll` をブロック (0x800711C7) |
| アプリ本体 (Sysacta.exe) の対話起動 | 未実施 | 未署名 apphost が Device Guard でブロック。SQLite も同上 |

最後のコード変更の後は、Smart App Control が新しくビルドした未署名 DLL 自体をブロックするようになったため、このPCでは再実行していない(ポリシーの回避はしていない)。最終コードの Windows 上の検証は上記 CI で行った。

## 完了条件の対応

| 完了条件 (docs/phase-0.md) | 根拠 |
|---|---|
| Windows 向けに正常にビルドできる | 確認済み: Windows CI でビルド成功(Linux でも成功) |
| 実装した自動テストが成功する | 確認済み: Windows CI で全件成功(音声デバイスが必要な3件は実機で成功)、Linux で全件成功 |
| 管理コンソールとタスクトレイの基盤が動作する | App.Tests が Windows CI と実機で成功(全画面の XAML、通知領域アイコンの生成と状態追従)。exe の対話操作 (M1〜M4) はユーザーの手動確認待ち |
| Bearer トークンの発行と MCP 接続が可能 | Mcp.Tests(SDK クライアントで 2025-06-18 / 2025-11-25 / 最新の各プロトコルで接続)。実クライアント (Claude/Codex) は未実施 → M9 |
| クライアントごとの認証・認可が機能する | Mcp.Tests / Core.Tests(主体別 tools/list、直接呼び出し拒否、失効・期限・再発行、待機中の失効) |
| リファレンスツールが共通実行基盤を経由する | ツールは `SysactaTool<,>` のみ、SDK には2つのハンドラーだけを登録、`ToolCatalog` 経由。Mcp.Tests で監査記録まで確認 |
| 操作履歴が適切に記録される | Core/Infrastructure/Mcp.Tests(成功・失敗・拒否・認証失敗・pending→最終、許可リスト外の引数とトークンが DB に存在しないこと) |
| 新規ツール追加のパターンとドキュメント | `docs/tools.md`, `AGENTS.md`, 参考実装2つ |
| 実行できなかった実機テストの明記 | `docs/manual-tests.md`、本ファイル |

## 重要な技術判断

1. **単一実行経路**: MCP SDK の属性ツール (`[McpServerTool]`) を使わず、`WithListToolsHandler` / `WithCallToolHandler` だけを登録し、全呼び出しを `ToolExecutor.ExecuteAsync` に通す。ツールは `SysactaTool<TArgs,TData>` 継承でしか作れない(非ジェネリック基底のパイプラインメンバーは internal)。
2. **スキーマと検証の単一ソース**: 引数クラスの属性から `JsonSchemaExporter` で入力スキーマを生成し、同じクラスへ厳格な `JsonSerializerOptions`(未知プロパティ・型変換・重複・整数 enum を拒否)でデシリアライズ後、`Validator` で範囲検証。
3. **再認可**: ロックと実行スロットの取得後、実行直前に `AccessPolicy.Evaluate` を再実行。
4. **キャンセル不能処理の隔離**: タイムアウト/キャンセル時は実行中タスクが終わるまでロック・スロットを保持(継続で解放)。状態変更ツールは `status=unknown`。ホスト停止でも解放されない(executor はホストより長生き)。
5. **監査の事前永続化**: 状態変更ツールは実行前に `pending` を書き、失敗したら実行しない。引数はツールごとの許可リストのみ。
6. **MCP から設定変更不可を構造で保証**: MCP 層は読み取り専用の `IAccessStore` だけを受け取る。
7. **ツールの配置**: ドメインロジックは Core (`Tools/<Category>/`)、OS 呼び出しは Windows (`<Category>/`) のアダプター。Core のテストは Linux でも動く。spec §3 の構成(Windows に機能フォルダー)と矛盾しない範囲での判断。
8. **stateless**: `HttpServerSessionMode.Stateless`(SDK 2.4.0 の既定)。旧クライアントの initialize ハンドシェイクも同じエンドポイントで動作(テスト済み)。stateful 互換は未対応(必要になったら検証)。ツール一覧変更通知は stateless では配信できないため未実装。
9. **新ツールの既定**: 新規 DB では `DefaultPolicy.On` のみ有効。既存 DB に後から追加されたツールは無効。クライアント権限は明示行のみで、将来ツールは自動付与されない。
10. **Host 検証**: `localhost`、ループバックIP、バインドIP、設定済みホスト名を許可。`0.0.0.0` 待受時は IP リテラル Host を許可。Origin は存在時のみ検証、不一致は 403。
11. **認証失敗の監査は接続元ごとに 30 件/分まで**(超過は 429 で監査しない)。監査 DB の肥大化 DoS 対策。spec にない暫定値。
12. **トレイ**: WPF-UI.Tray はウィンドウのビジュアルツリーに依存するため、ウィンドウなしで動く H.NotifyIcon.Wpf (MIT) を採用。`TrayIcon` は `McpServerHost` とデリゲートだけに依存し、DB なしで実機テスト可能。
13. **UI テストプロジェクト追加**: spec の一覧に `Sysacta.App.Tests` を追加(XAML のエラーは実行時にしか出ないため)。非Windowsでは `IsTestProject=false`。
14. **pack URI** はアセンブリ名を明示 (`/Sysacta;component/...`)。
15. **UI 文言は日本語**、ツールの description / エラーメッセージ(エージェント向け)は英語。
16. **NAudio.Wasapi 3.3.0**(22.0.0 は非掲載の誤版のため不使用)。Core Audio は既定デバイスの役割に `Multimedia` を使う。

## 仕様書との差分・未決事項

- テストプロジェクト `Sysacta.App.Tests` を追加(上記 13)。
- `ErrorCodes` に `CANCELLED`, `AUDIT_UNAVAILABLE` を追加。未知ツールは `UNKNOWN_TOOL`(MCP ではプロトコルエラー)。
- 暫定値: 同時実行 8 / 待機 32 / 本文 256 KiB / 60 リクエスト/分/クライアント / 同時接続 64 / 認証失敗 30 件/分/アドレス / 既定タイムアウト 5 秒 / 監査保持 30 日。spec §10 の値を踏襲し、実測での調整は未実施。
- クライアントの有効期限は発行時のみ選択可能(発行後の変更 UI はない。`AccessStore.SetClientExpiry` はある)。
- `installer/`、配布、コード署名は Phase 3。

## 残存するセキュリティリスク

[docs/security.md](security.md) の「既知の残存リスク」を参照(同一ユーザーによる DB 改変、管理者起動時の権限、HTTP 非ループバック公開時の平文、未署名バイナリのブロック、MCP 経由の重複キー)。

## 後続フェーズへの引き継ぎ

1. PR #1 (https://github.com/clagon/sysacta/pull/1, draft) の CI は成功済み。マージはユーザーの判断。
2. 実機チェックリスト `docs/manual-tests.md` の M1〜M15 と A4〜A8 を実施して結果を記録する。
3. Phase 1 のツールは `docs/tools.md` の手順で追加する。WinRT (GSMTC, 通知) を使う場合は `Sysacta.Windows` の TFM を `net10.0-windows10.0.xxxxx` に上げる必要がある。
4. デバイス別名の UI(デバイス画面)は、最初に別名を必要とするカテゴリと一緒に追加する。
5. このPC固有の Windows 実行手順(SDK なし・Smart App Control あり): ASP.NET Core ランタイムパック (NuGet `microsoft.aspnetcore.app.runtime.win-x64`) の DLL を出力に同梱し、`*.deps.json` を削除、`runtimeconfig.json` のフレームワークを `Microsoft.NETCore.App` / `Microsoft.WindowsDesktop.App` だけにして `dotnet.exe` で実行。ただし Smart App Control が未署名 DLL を順次ブロックするため、恒久的な手段ではない。

## パッケージ(固定版)

ModelContextProtocol(.AspNetCore) 2.4.0 / Microsoft.Data.Sqlite 10.0.15 / NAudio.Wasapi 3.3.0 / WPF-UI 4.3.0 / H.NotifyIcon.Wpf 2.4.1 / CommunityToolkit.Mvvm 8.4.2 / xunit.v3 3.2.2 / xunit.runner.visualstudio 3.1.5 / Microsoft.NET.Test.Sdk 18.12.0
