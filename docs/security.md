# セキュリティ境界と実行経路

仕様の根拠は [spec.md](spec.md) §5, §6, §9〜§12。ここでは「どこで何が強制されるか」をコードの位置とともに示します。変更する場合は対応するテストを必ず更新してください。

## 信頼境界

| 境界 | 信頼しないもの | 強制する場所 |
|---|---|---|
| ネットワーク → HTTP | Host / Origin / Authorization ヘッダー、本文、転送ヘッダー | `src/Sysacta.Mcp/RequestGuards.cs`, `McpServerHost.Build` |
| MCP クライアント → ツール | ツール名、引数、tools/list のキャッシュ、annotations | `src/Sysacta.Core/Tools/ToolExecutor.cs`, `ToolJson.cs`, `Security/Access.cs` |
| ツール → OS | (OS の応答は検証して返す) | 各アダプター (`src/Sysacta.Windows/`) |
| ローカルユーザー → 管理コンソール | — 信頼する(同一ユーザー) | UI のみが設定・権限・クライアントを変更できる |

同じ Windows ユーザーで動作する他のプロセスや管理者は、設定DB・監査ログを改変できます(サンドボックスではない。spec §6, §11)。

## HTTP リクエストの経路

1. **Kestrel 制限** (`McpServerHost.Build`): 本文 256 KiB (超過 413)、同時接続 64、`Server` ヘッダーなし、ログ出力なし。待受は既定 `127.0.0.1:8765`。
2. **Host 検証** (`RequestGuards.IsHostAllowed`): `localhost`、ループバックIP、バインド先IP、設定済みホスト名のみ。`0.0.0.0` 待受時は IP リテラルの Host を許可(IP リテラルは DNS rebinding の対象にならない)。それ以外は 403。
3. **Origin 検証**: Origin ヘッダーがある場合のみ、設定済みの Origin と完全一致しなければ 403(`null` も拒否)。CORS ヘッダーは一切返さない。Origin のない非ブラウザークライアントは許可。
4. **Bearer 認証** (`TokenAuthenticator`): すべてのリクエスト(localhost でも、initialize/discover でも)に必須。失敗は 401 + `WWW-Authenticate: Bearer`、監査に `auth_failed`(クライアントIDは null、トークンは記録しない)。認証失敗は接続元アドレスごとに 30 回/分まで処理し、超過分は 429 で監査しない(監査ログの肥大化防止)。
5. **レート制限**: 認証済みクライアントごとに 60 リクエスト/分(超過 429)。
6. **MCP**: `/mcp` の Streamable HTTP(stateless)のみ。stdio・レガシー SSE (`/sse`, `/message`) はマップしない。その他のパスは 404。
7. **tools/list**: `ToolExecutor.ListTools(caller)` が実効権限で絞り込む。**これは表示上の絞り込みで、認可ではない。**
8. **tools/call**: `ToolExecutor.ExecuteAsync` が毎回独立に認可する(下記)。

## ツール実行の経路 (`ToolExecutor.ExecuteAsync`)

| 順序 | 処理 | 失敗時 |
|---|---|---|
| 1 | ツール検索 | 未知 → MCP プロトコルエラー (InvalidParams)、監査 `denied` |
| 2 | 実効権限 = 能力 (`GetAvailability`) ∩ サーバー全体 (`tools.enabled`) ∩ クライアント (`client_permissions`、明示許可のみ) ∩ クライアントが有効(未失効・期限内) | NOT_SUPPORTED / TOOL_DISABLED / PERMISSION_DENIED |
| 3 | 引数検証: 未知プロパティ・型変換・重複・範囲外・整数 enum を拒否 | INVALID_ARGUMENT(値はメッセージに含めない) |
| 4 | 待機キュー上限 (同時実行 8 + 待機 32) | BUSY (retryable) |
| 5 | 対象解決 → リソースロック(状態変更のみ、キー順に取得)→ 実行スロット | TIMEOUT/CANCELLED (retryable。何も実行していない) |
| 6 | **再認可**(待機中の失効・権限変更・全体無効化を反映) | PERMISSION_DENIED / TOOL_DISABLED |
| 7 | 状態変更ツールは監査 `pending` を永続化 | AUDIT_UNAVAILABLE(実行しない) |
| 8 | 実行(タイムアウト付き) | 実行中のタイムアウト/キャンセル → `status: unknown`。ネイティブ処理が終わるまでロックとスロットを保持 |
| 9 | 監査の最終結果 | 書けなくても結果は返す(pending が残る) |

管理コンソールの「動作確認」も同じ経路を `Caller.LocalConsole` で通ります(クライアント別許可の代わりにローカルユーザーとして扱うが、サーバー全体の許可・検証・監査は適用)。

## トークン

- 生成: `RandomNumberGenerator` 256 bit、`sya_` + base64url(43文字)。ユーザー指定のトークンは受け付けない。
- 保存: SHA-256(hex)のみ。原文は発行・再発行時に UI に一度だけ表示し、DB・ログ・設定に保存しない(テスト: DB ファイル全体に原文が含まれないこと)。
- 比較: ハッシュでの検索後、`CryptographicOperations.FixedTimeEquals`。
- 失効・期限・再発行(旧トークン即時無効)は次のリクエストから反映。待機中の呼び出しは実行直前の再認可で拒否。
- 最終利用日時は 1 分単位で更新。

## 監査ログ

- 記録: 成功・失敗・拒否・認証失敗・結果不明・pending。項目は spec §11 + 接続元(ソケットのアドレスのみ。`X-Forwarded-For` などは使わない)。
- 引数はツールごとの許可リスト (`ToolDescriptor.AuditedArguments`) にある項目だけ。未指定なら記録しない。
- 記録しないもの: トークン、Authorization ヘッダー、リクエスト/レスポンス本文、生の例外、スタックトレース。
- CSV 出力は全セルを引用符で囲み、`= + - @ タブ CR LF` で始まるセルに `'` を付ける(数式注入対策)。
- 保持期間は既定 30 日(1〜3650)。起動時と 6 時間ごとに削除。

## 設定と権限の変更経路

- 変更できるのは管理コンソール(WPF)だけ。MCP 層は `IAccessStore`(読み取り専用)しか受け取らず、設定変更用のツール・エンドポイントは存在しない。
- 新規 DB では `DefaultPolicy.On` のツールのみ全体で有効。後のバージョンで追加されたツールは無効のまま追加され、既存クライアントにも自動付与されない。
- 新規クライアントの初期権限はプリセット(現在有効な全ツール / 読み取り専用のみ / なし)から明示的な行として保存される。

## 権限レベル

- マニフェストは `asInvoker`。自動起動 (HKCU\...\Run) は昇格しない。MCP から昇格する手段はない。
- 管理者で起動した場合はタイトルとバナーで常時表示する。攻撃面が増えることは残存リスク。

## 禁止事項(実装してはいけないもの)

- 任意のシェル/PowerShell/スクリプト実行、任意のファイル読み書き、GUI 操作・スクリーンショット・クリップボード取得。
- 任意のパス・引数・URI・環境変数・DLL を受け取るツール。
- 認証の例外(localhost だから省略、など)、ワイルドカードの Origin/CORS、転送ヘッダーの信用。
- tools/list の絞り込みだけで認可したことにする実装。
- 監査ログ・エラー・UI への秘密や生の例外の出力。
- DLL インジェクション、カーネル改変、セキュリティ機構の回避(spec §12)。

## 既知の残存リスク

- 同一ユーザー権限のプロセスによる DB・設定の改変、監査ログの改ざん。
- 管理者として起動した場合、ツールの操作も管理者権限で実行される。
- `0.0.0.0` などで HTTP を非ループバック公開した場合、トークンは平文で流れる(UI で警告。HTTPS リバースプロキシを推奨)。
- 署名なしのバイナリは SmartScreen / Smart App Control によりブロックされ得る(このプロジェクトの開発機でも、未署名の `SQLitePCLRaw.batteries_v2.dll` がブロックされた)。コード署名は未決定(spec §15)。
- MCP SDK が JSON-RPC の `arguments` を辞書として受け取るため、ツール引数の重複キーは SDK 側で解消され、Sysacta の重複検出は MCP 経由では働かない。
