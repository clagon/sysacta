# AGENTS.md — Sysacta 開発ルール

Sysacta は Windows 11 を MCP ツールで管理するトレイ常駐アプリです。仕様の正本は [docs/spec.md](docs/spec.md)。このファイルは「実装時に守る具体的なルール」だけを書きます。

## 最初に読むもの

| 目的 | 読むもの |
|---|---|
| 新しいMCPツールを追加する | [docs/tools.md](docs/tools.md)(手順・チェックリスト)、参考実装 `src/Sysacta.Core/Tools/Audio/` |
| 認証・認可・監査・HTTPに触る | [docs/security.md](docs/security.md) |
| 実機でしか確認できないこと | [docs/manual-tests.md](docs/manual-tests.md) |
| 現在の進捗・既知の制約 | [docs/phase-0-progress.md](docs/phase-0-progress.md) |

## ビルドとテスト

```bash
dotnet build Sysacta.sln -c Release      # 警告はエラー扱い (TreatWarningsAsErrors)
dotnet test Sysacta.sln -c Release
```

- .NET SDK 10 (`global.json`)。パッケージのバージョンは `Directory.Packages.props` だけで管理する。csproj に `Version=` を書かない。
- Linux/WSL でもビルドできる (`EnableWindowsTargeting`)。Linux では Core/Infrastructure/Mcp のテストが実行され、Windows.Tests はスキップ、App.Tests (WPF) は対象外になる。**Windows 専用の挙動は CI (windows-latest) か実機で確認する。**
- 実デバイスの状態を変えるテストは `SYSACTA_HARDWARE_TESTS=1` のときだけ動かし、必ず元に戻す。電源操作・ネットワーク切断・画面消失を起こす操作を実機テストとして自動実行しない。

## プロジェクトと依存方向

```
Sysacta.App ──▶ Sysacta.Mcp ─────────▶ Sysacta.Core
     │     ──▶ Sysacta.Windows ─────▶ Sysacta.Core
     └─────▶ Sysacta.Infrastructure ▶ Sysacta.Core
```

| プロジェクト | 置くもの | 置いてはいけないもの |
|---|---|---|
| Sysacta.Core | ツールのドメインロジック (`Tools/<Category>/`)、OSアダプターのインターフェース、実行パイプライン、認可、結果型 | WPF、ASP.NET Core、Windows API/COM/WinRT、SQLite |
| Sysacta.Windows | OS/デバイスアダプター (`<Category>/`)。Core のインターフェースを実装 | ツールの業務ロジック、HTTP、DB |
| Sysacta.Infrastructure | SQLite、マイグレーション、設定、監査ログ | Windows API、HTTP |
| Sysacta.Mcp | Kestrel、Host/Origin 検証、Bearer 認証、レート制限、MCP ハンドラー | ツール固有の処理、設定の変更 |
| Sysacta.App | WPF、トレイ、構成ルート (`SysactaRuntime`)、本番ツール一覧 (`ToolCatalog`) | ツール実行の独自経路 |

## 絶対に守ること

1. **ツールは `ToolExecutor` 以外から実行しない。** MCP も管理コンソールの「動作確認」も `ToolExecutor.ExecuteAsync` を通る。`[McpServerTool]` 属性や `WithTools*` で SDK にツールを直接登録しない。
2. **ツールは `SysactaTool<TArgs, TData>` を継承し、認証・認可・監査・ロック・タイムアウトを自分で実装しない。** 基底クラスのパイプライン用メンバーは internal で、迂回できない設計になっている。状態変更ツールは最初の OS 書き込みの直前に `execution.BeginSideEffect()` を呼ぶ(ここが最終の認可点と副作用の開始点。docs/tools.md)。権限を狭める書き込みは `AuthorizationGate.BeginUpdate()` の中で行う(`AccessStore` が実施済み)。
3. **本番ツールは `src/Sysacta.App/ToolCatalog.cs` にだけ登録する。** テスト用ツールはテストプロジェクト内に置く(本番ビルドに含まれない)。
4. **MCP から認証・権限・設定・ログを変更する手段を作らない。** MCP 層が受け取るのは読み取り専用の `IAccessStore` だけ。
5. **秘密や内部情報を記録・返却しない。** トークン原文、Authorization ヘッダー、リクエスト/レスポンス本文、スタックトレースを、監査ログ・MCP の応答・UI に出さない。例外メッセージ(内部のパスや SQL を含み得る)は MCP の応答・監査ログ・UI のエラー表示に出さず、固定文言にする(UI は `PageViewModel.SafeMessage`)。MCP の応答と監査ログにはファイルパスを含めない。一方、管理コンソールでローカルの利用者本人に見せる情報(仕様の「データ保存場所」、本人が選んだ書き出し先など)はパスを表示してよい。監査ログにはクライアントが入力した文字列を残さない: 記録するのは固定値・登録名・解決済み識別子と、許可リストにある数値・真偽値・列挙値の引数だけ。
6. **新しいツールの既定は spec §7 の表に従う。** 表の `ON` は日常的な操作(状態変更を含む。例: `audio_set_volume`)で、初回導入時に全体で有効になる。表にないツール、停止・切断・画面消失・データ消失の恐れがあるツールは `DefaultPolicy.Off`、非公開APIや未検証の方式は `Experimental`。迷ったら `Off` にして進捗ファイルに理由を書く。既存環境に後から追加したツールは自動で無効になり、既存クライアントにも自動付与されない(変更しないこと)。
7. **見かけだけの成功を返さない。** 実機で確認できない API を「実装済み」と報告しない。読み戻しで確認できたときだけ `Verification.Verified`。API受理のみなら `Accepted`。
8. **任意のシェル実行・ファイルアクセス・GUI操作・任意パス/引数/URLを受け付けるツールを作らない**(spec §1)。
9. **DB スキーマは `SysactaDatabase.Migrations` に追記するだけ。** 出荷済みのマイグレーションを書き換えない。
10. **ページは `ScrollViewer.CanContentScroll="False"` を付け、自分でスクロールする**(`PageScroll` スタイル、または高さいっぱいの Grid + 内側の ScrollViewer)。付けないと WPF UI 4.x の NavigationView(Page の既定値を true に変更している)が高さ無制限の DynamicScrollViewer で包み、内側のリストがスクロールできなくなる(App.Tests が検査)。リストは `ui:ListView` / `ui:DataGrid` を使い、素の WPF コントロールを使わない。破壊的操作のボタンは色で強調せず、確認ダイアログで止める。
11. **UI スレッドをブロックしない。** SQLite・OS API・サーバー起動は `Task.Run` / async で実行する(`PageViewModel.RunAsync` を使う)。

## コーディング規約

- C# latest、nullable 有効、file-scoped namespace、警告ゼロ。`.editorconfig` に従う。
- ツール名は `<category>_<verb>_<object>`(小文字・snake_case)。引数・結果の JSON は snake_case(`ToolJson.Options` が自動変換)。
- エラーコードは `ErrorCodes` の定数を使う。新しいコードが必要なら `ErrorCodes` に追加し、docs/tools.md の表も更新する。
- 抽象化は必要になってから。インターフェースは依存方向を保つため(Core ↔ Windows/Infrastructure)か、テストでOSを差し替えるためだけに作る。
- UI 文言は日本語。ツールの description とエラーメッセージ(エージェント向け)は英語。

## 変更時のチェックリスト

- [ ] `dotnet build Sysacta.sln -c Release` が警告ゼロ
- [ ] `dotnet test Sysacta.sln -c Release` が成功
- [ ] セキュリティ境界に触れたら Mcp.Tests に「拒否されること」のテストを追加した
- [ ] 実機でしか確認できない項目を docs/manual-tests.md に追記し、未確認なら「未確認」と書いた
- [ ] 進捗・判断を docs/phase-0-progress.md(またはフェーズの進捗ファイル)に記録した
