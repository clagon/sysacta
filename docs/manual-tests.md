# 実機テスト手順と確認状況

自動テストで確認できない項目の手順と結果を記録します。**未実施の項目を成功扱いしない**でください。結果を記録するときは日付・環境(Windows のビルド、機器)・実施者を書きます。

危険な操作(シャットダウン・スリープ・ネットワーク切断・画面消失を起こす設定)は、隔離された環境で、手動で、復旧手段を用意して行います。

## 環境の準備

```powershell
dotnet build Sysacta.sln -c Release
dotnet test Sysacta.sln -c Release
# 実デバイスの状態を変えるテストも含める場合(音量を一時的に変更し、元に戻す)
$env:SYSACTA_HARDWARE_TESTS = "1"; dotnet test tests/Sysacta.Windows.Tests -c Release
```

アプリの起動: `src/Sysacta.App/bin/Release/net10.0-windows/Sysacta.exe`(`--minimized` でトレイのみ)。データは `%LOCALAPPDATA%\Sysacta\`。

## Phase 0 チェックリスト

| # | 項目 | 手順 | 期待結果 | 状況 |
|---|---|---|---|---|
| M1 | 起動とトレイ | Sysacta.exe を起動 | 管理コンソールが開き、通知領域にアイコン。トレイメニューに状態とURL | 一部確認: 通知領域アイコンの生成と状態追従は App.Tests で実機確認 (2026-11-25)。exe からの起動は未実施(下記制約) |
| M2 | ウィンドウを閉じる | ×で閉じる → トレイアイコンをクリック | 閉じてもプロセスが残り、再表示される | 未実施 |
| M3 | 単一インスタンス | 起動中にもう一度起動 | 2つ目は終了し、既存のウィンドウが前面に出る | 未実施 |
| M4 | 終了とリソース解放 | トレイ → 終了 | プロセス終了、ポート 8765 が解放される(`netstat -ano \| findstr 8765`) | 未実施(停止時のポート解放は Mcp.Tests で自動確認済み) |
| M5 | 自動起動 | 一般 → 自動起動をオン → サインアウト/サインイン | トレイのみで起動、標準権限。`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Sysacta` が作成される | 未実施 |
| M6 | 管理者表示 | 管理者として実行 | タイトルとバナーに「管理者」 | 未実施 |
| M7 | テーマ | 一般 → ライト/ダーク/OS追従、Windows のテーマ切替 | 即時に反映、OS追従時は追従 | 未実施 |
| M8 | DPI | 125%/150%/175% のモニター、モニター間移動 | 文字・アイコンがぼやけず、切れない | 未実施 |
| M9 | トークン発行と接続 | クライアント → 発行 → 接続設定をコピー → MCP クライアント(Claude Desktop / Codex 等)に設定 | tools/list に許可したツールだけが表示され、audio_get_state が成功 | 未実施(SDK クライアントでの接続は Mcp.Tests で自動確認済み) |
| M10 | 失効の即時反映 | 接続中のクライアントを失効 | 次のリクエストが 401 | 未実施(Mcp.Tests で自動確認済み) |
| M11 | 権限変更の即時反映 | ツールを全体で無効化 → クライアントから呼び出し | TOOL_DISABLED、tools/list から消える | 未実施(Mcp.Tests で自動確認済み) |
| M12 | 監査ログ | ログ画面で検索・絞り込み・CSV/JSON 出力 | 出力ファイルにトークン・ヘッダーが含まれない。CSV を Excel で開いても数式が実行されない | 未実施 |
| M13 | バインド変更 | 接続 → ポートを使用中のポートに変更して保存 | エラー表示、以前の設定で実行継続 | 未実施(Mcp.Tests で自動確認済み) |
| M14 | 非ループバック警告 | 0.0.0.0 を選択 | 警告が表示される。FW は変更されない | 未実施 |
| M15 | 設定破損 | 終了 → `sysacta.db` の settings の port を `abc` に書き換え → 起動 | 既定値で起動し、一般画面に警告 | 未実施(Infrastructure.Tests で自動確認済み) |

## リファレンスツール (audio)

| # | 項目 | 手順 | 期待結果 | 状況 |
|---|---|---|---|---|
| A1 | 既定出力の取得 | `audio_get_state` (引数なし) | 既定の出力デバイス、音量 0-100、ミュート | 確認済み: アダプターを Windows 11 実機の既定出力で実行(Windows.Tests, 2026-11-25) |
| A2 | 音量変更と検証 | `audio_set_volume` level=現在値±2 → 元に戻す | `verified`、前後の値が返り、実際の音量が変わる | 確認済み: アダプターで変更→読み戻し→復元(`SYSACTA_HARDWARE_TESTS=1`, 2026-11-25)。MCP 経由の実機操作は未実施 |
| A3 | 未知の ID | device に存在しない ID | TARGET_NOT_FOUND | 確認済み(Windows.Tests, 2026-11-25) |
| A4 | 入力デバイス | direction=input | マイクの状態(録音はしない) | 未実施 |
| A5 | 切断中の機器 | USB/Bluetooth ヘッドセットを外した状態で、その ID を指定 | get_state は state=unplugged/not_present と volume=null、set_volume は DEVICE_DISCONNECTED | 未実施 |
| A6 | 既定デバイスなし | すべての出力を無効化 | TARGET_NOT_FOUND | 未実施 |
| A7 | 同時呼び出し | 同じデバイスへの set_volume を並列に 20 回 | すべて verified、値が交錯しない | 未実施(フェイクで自動確認済み) |
| A8 | 外部からの変更 | set 直後に Windows の音量ミキサーで変更 | VERIFICATION_FAILED になり得る(外部変更は排他できない) | 未実施 |

## 2026-11-25 の開発環境での制約

開発機 (Windows 11 + WSL2) では以下の理由でアプリ本体と DB を使うテストを Windows 上で実行できませんでした。CI (GitHub Actions `windows-latest`) で自動テストを実行してください。

- Windows 側に .NET SDK と ASP.NET Core ランタイムがない(.NET 10.0.14 ランタイムと WindowsDesktop ランタイムのみ)。SDK のインストールは権限上行わなかった。
- Device Guard / Smart App Control が、未署名の apphost (`*.exe`) と、新しくコピーした未署名の `SQLitePCLRaw.batteries_v2.dll`、さらに後半では新しくビルドした Sysacta 自身のテスト DLL もブロックした(`0x800711C7`)。ポリシーの回避は行っていない。
- そのため開発機の Windows 上で実行できたのは: Core.Tests (69)、Infrastructure.Tests (37, ブロックされる前の初回のみ)、Windows.Tests (3, 実デバイス)、App.Tests (2, XAML 読み込みと通知領域アイコン)。
- 最終コードの全自動テストは GitHub Actions (windows-latest) で成功: Core 71 / Infrastructure 37 / Mcp 38 / App 2、Windows.Tests 3 はランナーに音声デバイスがないためスキップ (PR #1, 2026-11-25)。
- M1〜M4 と M9 はリポジトリ所有者が手動で確認する(2026-11-25 時点で未実施)。
