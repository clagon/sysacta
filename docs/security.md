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

1. **ホスト設定の固定** (`McpServerHost.Build`): appsettings・環境変数・コマンドラインを読まず、環境は Production 固定。`Kestrel:Endpoints`・`ASPNETCORE_URLS`・`ASPNETCORE_FORWARDEDHEADERS_ENABLED` などで待受の追加や接続元の書き換えが起きない(Mcp.Tests で検査)。
1. **Kestrel 制限** (`McpServerHost.Build`): 本文 256 KiB (超過 413)、同時接続 64、`Server` ヘッダーなし、ログ出力なし。待受は既定 `127.0.0.1:8765`。
2. **Host 検証** (`RequestGuards.IsHostAllowed`): `localhost`、ループバックIP、バインド先IP、設定済みホスト名のみ。`0.0.0.0` 待受時は IP リテラルの Host を許可(IP リテラルは DNS rebinding の対象にならない)。それ以外は 403(固定文言の理由を本文に返す)。監査に `denied` / `HostNotAllowed` とメソッド・パス・Host を記録。
3. **Origin 検証**: Origin ヘッダーがある場合のみ、設定済みの Origin と完全一致しなければ 403(`null` も拒否)。監査に `OriginNotAllowed`。CORS ヘッダーは一切返さない。Origin のない非ブラウザークライアントは許可。
4. **Bearer 認証** (`TokenAuthenticator`): すべてのリクエスト(localhost でも、initialize/discover でも)に必須。失敗は 401 + `WWW-Authenticate: Bearer`、監査に `auth_failed`(クライアントIDは null、メソッドとパスのみ記録。クエリ文字列・ヘッダー・トークンは記録しない)。拒否(認証・Host・Origin)の監査は接続元アドレスごとに 30 件/分まで。超過分の認証失敗は 429、Host/Origin の拒否は 403 のまま記録しない(監査ログの肥大化防止)。
5. **レート制限**: 認証済みクライアントごとに 60 リクエスト/分(超過 429)。
6. **MCP**: `/mcp` の Streamable HTTP(stateless)のみ。stdio・レガシー SSE (`/sse`, `/message`) はマップしない。その他のパスは 404。
7. **tools/list**: `ToolExecutor.ListTools(caller)` が実効権限で絞り込む。**これは表示上の絞り込みで、認可ではない。**
8. **tools/call**: `ToolExecutor.ExecuteAsync` が毎回独立に認可する(下記)。

## ツール実行の経路 (`ToolExecutor.ExecuteAsync`)

| 順序 | 処理 | 失敗時 |
|---|---|---|
| 1 | ツール検索 | 未知 → MCP プロトコルエラー (InvalidParams)、監査 `denied` |
| 2 | 実効権限 = 能力 (`GetAvailability`) ∩ サーバー全体 (`tools.enabled`) ∩ クライアント (`client_permissions`、明示許可のみ) ∩ クライアントが有効(未失効・期限内) | NOT_SUPPORTED / TOOL_DISABLED / PERMISSION_DENIED |
| 3 | 引数検証: 未知プロパティ・型変換・重複・範囲外・`null`(省略で表す)・公開した綴り以外の列挙値・制御文字(`[NoControlCharacters]`)を拒否 | INVALID_ARGUMENT(メッセージには既知の引数名だけを含め、値や未知の名前は返さない) |
| 4 | 待機キュー上限 (同時実行 8 + 待機 32) | BUSY (retryable) |
| 5 | 対象解決 → リソースロック(状態変更のみ、キー順に取得)→ 実行スロット | TIMEOUT/CANCELLED (retryable。何も実行していない) |
| 6 | 状態変更ツールは監査 `pending` を永続化 | AUDIT_UNAVAILABLE(実行しない) |
| 7 | 再認可(実行の直前。待機中や pending 書き込み中の変更を早めに検出する) | PERMISSION_DENIED / TOOL_DISABLED(pending を拒否で置き換える) |
| 8 | 実行(タイムアウト付き)。状態変更ツールは最初の OS 書き込みの直前に `ToolExecution.BeginSideEffect()` を呼ぶ。ここが**最終の認可点かつ副作用の開始点**: `AuthorizationGate` の読み取り側で認可を評価し、許可なら「開始済み」と記録する | 開始点での拒否 → PERMISSION_DENIED / TOOL_DISABLED(監査は denied、書き込みなし)。開始前のタイムアウト/キャンセル → 呼び出しを「放棄」にして、後から開始点に来ても書き込ませず、`error`(再試行可、Nothing was changed)。開始後 → `status: unknown`。どちらもネイティブ処理が終わるまでロックとスロットを保持 |
| 9 | 監査の最終結果 | 書けなくても結果は返す(pending が残る) |

### 権限変更と操作開始の順序(SEC-01)

- 権限を狭める書き込み(失効・再発行・個別の許可取り消し・期限変更・ツールの全体無効化。実装上は `AccessStore` の権限関連の書き込みすべて)は、`AuthorizationGate` の書き込み側で行う。
- 開始点の評価は同じゲートの読み取り側で行う。そのため、開始点より前に完了した変更は必ず反映され、評価中に変更が割り込まない(各条件の読み取りが一貫する)。
- ゲートを持つのは評価(DB の数回の読み取り)の間だけで、ネイティブ処理の間は持たない。長い OS 呼び出しが権限変更を止めることはない(Core.Tests で検査)。
- 開始点の後に完了した変更は、その呼び出しには適用されない(spec §6「すでに実行された副作用は巻き戻せない」)。

管理コンソールの「動作確認」も同じ経路を `Caller.LocalConsole` で通ります(クライアント別許可の代わりにローカルユーザーとして扱うが、サーバー全体の許可・検証・監査は適用)。

## トークン

- 生成: `RandomNumberGenerator` 256 bit、`sya_` + base64url(43文字)。ユーザー指定のトークンは受け付けない。
- 保存: SHA-256(hex)のみ。原文は発行・再発行時に UI に一度だけ表示し、DB・ログ・設定に保存しない(テスト: DB ファイル全体に原文が含まれないこと)。
- 比較: ハッシュでの検索後、`CryptographicOperations.FixedTimeEquals`。
- 失効・期限・再発行(旧トークン即時無効)は次のリクエストから反映。待機中の呼び出しも、実行直前に「認証に使ったトークンのハッシュ」を照合して拒否する(`Caller.CredentialHash`。ログや応答には出さない)。
- 最終利用日時は 1 分単位で更新。

## 監査ログ

- 記録: 成功・失敗・拒否・認証失敗・結果不明・pending。項目は spec §11 + 接続元(ソケットのアドレスのみ。`X-Forwarded-For` などは使わない)。
- 記録するのは固定値・登録名・解決済み識別子だけ(SEC-03)。クライアントが入力した文字列はどの経路でも記録しない(形式の検査では機密が含まれないことを保証できないため)。
  - ツール名: 登録済みの名前。未登録の名前は `(unknown tool)`。
  - 引数: `ToolDescriptor.AuditedArguments` の許可リストのうち、数値・真偽値・列挙値だけ(文字列の引数はツール登録時にエラーになる)。検証に失敗した呼び出しの引数は記録しない。
  - 対象: 解決に成功したときの `ToolTarget.TargetId`(OS や Sysacta の登録情報から得た識別子)を `target` として記録する。入力されたデバイス文字列そのものは記録しない。
  - HTTP での拒否: 理由コード(`HostNotAllowed` / `OriginNotAllowed` / 認証失敗の種類)、標準メソッドか `other`、パスが `/mcp` か `other` だけ。Host・Origin・パス・クエリ・ヘッダーの値は記録しない。
  - 回帰テスト: 合成の機密文字列を未登録のツール名・デバイス入力・パス・Host・Origin・Authorization に入れ、DB・WAL・CSV・JSON のどこにも残らないことを Mcp.Tests で検査。
- 記録しないもの: トークン、Authorization ヘッダー、リクエスト/レスポンス本文、生の例外、スタックトレース。管理コンソールのエラー表示も、OS・ファイル・DB の例外メッセージ(パスや SQL を含み得る)は出さず固定文言+例外の型名にする(`PageViewModel.SafeMessage`)。
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
- 開始点(`BeginSideEffect`)の後に完了した権限変更は、すでに始まった OS 操作には適用されない(spec §6)。開始点から実際のネイティブ呼び出しまでの間は、ツールのコードで数命令のみ。
- 開始点の宣言はツールの責務。状態変更ツールが `BeginSideEffect()` を呼ばずに書き込むと、開始点での再認可と「開始前なら何も変更していない」の判定が働かない(executor の早めの再認可だけになる)。docs/tools.md と AGENTS.md で必須とし、参考実装と Core.Tests の境界テストで示している。
- 権限変更の書き込みは、開始点の評価中(DB の読み取りが混雑で待つ場合は最大で SQLite の待ち時間)だけ待たされる。
- 未認証のリクエストも、トークンの形式が正しければ拒否の回数制限より前に DB を照会する。また認証済みリクエストの最終利用日時の更新は、クライアント別の制限より前に行う(SEC-05、Phase 1 かリモート運用の開始までに対応予定)。
- 監査ログは日数でだけ削除し、行数・容量の上限がない。拒否の記録は接続元ごとに 30 件/分だが、長期間続くと DB が大きくなる(SEC-06、同上)。
- 同じユーザーの環境変数による ASP.NET の hosting startup の注入などは、同一ユーザー権限のプロセスによる改変として脅威モデルの範囲外。
- リバースプロキシ(Tailscale Serve など)経由では、すべての接続元が `127.0.0.1` になる。監査ログの接続元で遠隔のクライアントを区別できず、拒否の記録件数の上限(アドレス単位)も共有される。転送ヘッダーは信用しない方針のため、これは仕様。
- MCP SDK が JSON-RPC の `arguments` を辞書として受け取るため、ツール引数の重複キーは SDK 側で解消され、Sysacta の重複検出は MCP 経由では働かない。
