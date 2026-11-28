# Phase 1G 通知 — 進捗と判断

対象: [phase-1g.md](phase-1g.md)、spec §7.9。ツールは `notification_send` の 1 つ。

## 状態

| 項目 | 状態 |
|---|---|
| 実装 | 完了 |
| Linux/WSL の Release build / test | 成功(警告・エラー 0)。Windows.Tests の Windows 専用テストはスキップ、App.Tests は対象外 |
| Windows CI(build / test / smoke) | 成功(2026-11-28、PR #19) |
| 実通知の表示 | 利用者の承認のうえ 1 件を送信し、表示を確認(下の「実機確認」) |
| 実際のスタートメニューへの登録 | 作成・再起動・移動時の付け替えを確認(自動テスト・CI は登録しない) |
| 配布形式別の実機確認 | ポータブル版は一部確認。インストーラーは未作成 |

## 技術調査と API の採否(2026-11-28)

| 候補 | 判断 | 理由 |
|---|---|---|
| `Windows.UI.Notifications.ToastNotificationManager` + AUMID 付きスタートメニューショートカット | **採用** | 追加依存なし(既存 TFM `net10.0-windows10.0.19041.0` の WinRT 投影に含まれる)。デスクトップアプリ向けの手順が公式文書にある |
| Windows App SDK `AppNotificationManager` | 不採用 | 大きな追加 NuGet。非パッケージではフレームワーク依存だとランタイムと Singleton パッケージが必要。自己完結型では `Register()` が失敗するという未解決の報告がある。win-x64 自己完結フォルダ配布と WSL クロスビルドへの影響が大きい |
| レジストリ `HKCU\Software\Classes\AppUserModelId` だけの登録 | 不採用 | CommunityToolkit は使っているが、今回確認した現行の Microsoft Learn に手順が見つからなかった |
| トレイバルーン / 独自ポップアップ | 禁止(指示書) | Windows 標準通知の成功として扱わない |

残る弱点: Microsoft Learn の通知概要は `Windows.UI.Notifications` を "Maintenance only" とし、新しい Windows App SDK アプリの推奨経路ではないと書いている。廃止の告知ではないが、将来 Windows App SDK へ移る可能性はある。

### 参照(2026-11-28 に確認)

- デスクトップアプリの通知にはスタートメニューのショートカットと AppUserModelID が必要: https://learn.microsoft.com/en-us/windows/win32/shell/enable-desktop-toast-with-appusermodelid
- 送信の手順(`CreateToastNotifier(appID)`): https://learn.microsoft.com/en-us/windows/win32/shell/quickstart-sending-desktop-toast
- デスクトップアプリはパラメーターなしの `CreateToastNotifier` を使わない: https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager.createtoastnotifier
- AUMID の形式(128 文字以内、空白なし): https://learn.microsoft.com/en-us/windows/win32/shell/appids
- 通知設定の読み取り: https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotifier.setting 、 https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.notificationsetting
- `Show`(戻り値なし。表示の保証は書かれていない): https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotifier.show
- 失敗イベント(非同期。事前に `Setting` を確認することが推奨): https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotification.failed
- API の位置づけ: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/
- Windows App SDK の非パッケージ配置: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps
- Windows App SDK 自己完結型の不具合報告(コミュニティ): https://github.com/microsoft/WindowsAppSDK/issues/6774
- CommunityToolkit の非パッケージ登録(MIT、参考のみ。コードは使っていない): https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/Microsoft.Toolkit.Uwp.Notifications/Toasts/Compat/ToastNotificationManagerCompat.cs

### 依存・ライセンス

追加の NuGet はない。使うのは既に配布物に含まれる `Microsoft.Windows.SDK.NET.dll` / `WinRT.Runtime.dll`(`Microsoft.Windows.SDK.NET.Ref` 10.0.19041 系、Microsoft の Windows SDK ライセンス条項)と、shell32 の COM(`IShellLinkW` / `IPersistFile` / `IPropertyStore`)。`Directory.Packages.props`・TFM・ビルド設定は変更していない。

## 登録方式とライフサイクル

| 項目 | 内容 |
|---|---|
| 送信元 ID | `Sysacta.Desktop`(固定。引数で指定できない) |
| 表示名・アイコン | ショートカット名 `Sysacta` と exe の埋め込みアイコン |
| 場所 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Sysacta.lnk`(現在のユーザーだけ。レジストリ・COM サーバーは登録しない) |
| タイミング | `App.OnStartup` から専用の STA スレッドで 1 回(単一起動制御の後)。起動は待たない。MCP の呼び出し・`SysactaRuntime` の生成では登録しない |
| なし | 作成する |
| 同じ ID で、リンク先が存在する | そのまま使う(別のコピーやインストーラーが所有していても書き換えない) |
| 同じ ID で、リンク先がない | 現在の exe に付け替える(ポータブル版の移動・更新) |
| ID が違う・ない・読めない | 変更しない。`notification_send` は `NOT_SUPPORTED`(一覧から消え、ツール画面に理由を表示) |
| 作成の失敗 | 同上。他のツールとアプリには影響しない |
| 登録が終わる前の呼び出し | `OS_API_ERROR`(再試行可)。ハングしてもスレッドは 1 本のままで、キューは増えない |
| 起動後に削除された | 送信前の確認で `NOT_SUPPORTED`。再起動で再作成 |
| 解放 | WinRT オブジェクトは呼び出しごと(agile)。保持するハンドルはなく、`Dispose` は不要 |

クリック: toast activator(COM)・`launch` 引数・イベントハンドラーを登録しない。本文はどの起動データにもならない。クリック時の OS の挙動(何もしない、またはショートカット先を起動して単一起動制御で既存ウィンドウを表示する、など)は、アプリ起動中は何も起きないことを確認した。終了後の挙動と通知センターに残るかは**未確認**。

プロセスの `SetCurrentProcessExplicitAppUserModelID` は呼ばない(通知の送信には不要で、既存のトレイ通知の挙動を変えないため)。ショートカットからピン留めした場合のタスクバーのグループ化は未確認。

## JSON 契約

引数(両方必須、未知の項目は拒否):

```json
{"title": "ビルド完了", "body": "テストが成功しました。\n次の手順を確認してください。"}
```

結果:

```json
{"status":"success","verification":"accepted","data":{"accepted":true,"retry_after_ms":null},"request_id":"..."}
{"status":"error","data":{"accepted":false,"retry_after_ms":1500},"error":{"code":"BUSY","message":"...","retryable":true},"request_id":"..."}
{"status":"unknown","verification":"unknown","data":{"accepted":null,"retry_after_ms":null},"error":{"code":"OS_API_ERROR","message":"...","retryable":false},"request_id":"..."}
```

| 状況 | 結果 |
|---|---|
| `Show` が例外なく戻った | success / accepted。**表示・閲覧の証明ではない**(集中モード、通知設定、ロック状態、OS の抑制、非同期の `Failed` は反映されない) |
| 開始後に `Show` が例外・タイムアウト | unknown、`retryable: false`。再送・別 API へのフォールバックはしない |
| 開始前: 引数不正 | `INVALID_ARGUMENT` |
| 開始前: 通知が無効(アプリ・ユーザー・ポリシー) | `NOTIFICATIONS_DISABLED`(新規コード) |
| 開始前: 登録の失敗・競合・消失 | `NOT_SUPPORTED` |
| 開始前: 準備中の API 失敗・登録中 | `OS_API_ERROR`(再試行可) |
| 開始前: レート超過 | `BUSY` + `retry_after_ms` |

`ToastNotifier.Setting` が例外になる場合(通知を一度も出していないアプリで起こり得ると知られているが未確認)は「無効」と断定せず送信に進む。その状態で OS が表示しなくても結果は accepted になる。

既定: `DefaultPolicy.On`、`ReadOnly: false`、`Transition` なし、`AuditedArguments` は空、タイムアウト 5 秒(実測による変更なし)。新規 DB は有効、既存 DB への追加は無効で、既存クライアントに付与しない。

## 文字列の契約

| 項目 | title | body |
|---|---|---|
| 長さ(UTF-16 コード単位、受け取ったまま) | 1〜64 | 1〜256(CRLF は 2) |
| 改行 | 不可 | LF / CRLF(LF に正規化)。単独の CR は不可 |

- 超過は拒否し、切り詰めない。JSON Schema の `maxLength` はコードポイント数なので、絵文字などではスキーマ上は通ってもサーバーが拒否することがある(説明文に UTF-16 と明記)。
- 拒否: 空・空白や不可視文字だけ、対にならないサロゲート、制御文字(タブ・NUL・DEL・C1 を含む)、U+2028 / U+2029、非文字(U+FFFE など)、書式文字(双方向制御、ゼロ幅スペース、BOM、ソフトハイフンなど。アラビア語の U+0600 系の前置記号も含まれる)。
- 許可: ZWNJ / ZWJ、異体字セレクター、結合文字、黒旗に続き終端された絵文字タグ列。
- 追加で拒否: `ms-resource:` / `ms-appx` で始まる値と、全体が `{...}` の値。Windows がリソース参照やデータバインドとして解釈し、利用者の文字列と違う表示になるおそれがあるため(デスクトップアプリでの実挙動は未確認で、安全側に倒した)。
- XML: 固定テンプレート(`ToastGeneric`、`text` 2 つ)を読み込み、値は DOM の `CreateTextNode` で入れる。文字列連結はしない。画像・音声・ボタン・リンク・`launch`・scenario・優先度は指定しない。
- URL: 文字列中の URL は拒否しない。toast のテキスト要素にリンクの記法はなく、起動先を指定する経路もない。Windows が本文中の URL を操作可能にしないかは**未確認**(N5)。操作可能になる場合は拒否規則を追加する。

## レート制限

| 項目 | 値 |
|---|---|
| 直近 60 秒の OS 送信開始 | 最大 5 回(ローリング) |
| 開始の最小間隔 | 2 秒 |
| 範囲 | プロセス全体(全クライアント・再発行・接続元・ローカル動作確認で共通) |

- 数値は指示書の初期案のまま。人が読む通知として十分で、通知センターを埋めない量。実運用での調整は未実施。
- `ToolCatalog` で 1 回だけ生成する `NotificationSendTool` が、開始時刻を最大 5 件だけ保持する(`TimeProvider.GetTimestamp` の単調時刻。内容・クライアントは保持しない。タイマー・キューなし)。壁時計の変更の影響を受けない。
- 順序: リソースロック `notification:send` → 上限確認(超過なら即 `BUSY`) → 準備(`Prepare`) → キャンセル確認 → `BeginSideEffect`(最終認可) → 記録 → `Show` 1 回。開始前の拒否は枠を消費せず、開始後は結果にかかわらず消費する。ロックで直列化するので確認と記録は競合しない。
- 先行する送信がハングしている間、後続はロックを待ってタイムアウトする(送信しない)。ロック待ちのあとに実行された要求は、その時点の上限で判定する。
- MCP ホストの再起動・権限や設定の変更ではリセットしない。**アプリの再起動でリセットされる**(残存制約)。HTTP の要求予算と監査の上限はそのまま適用される。

## プライバシー

- 監査に残るのはツール名・結果コード・所要時間などの既存項目だけ。title / body は引数にも対象にも記録しない。
- 応答と出力スキーマに本文・XML・通知 ID・内部パスはない。例外の本文は固定文言に置き換える。
- 通知は Windows の通知センター・ロック画面・画面共有に残る可能性があり、保持・同期・表示は Sysacta では制御できない。本文中の任意の秘密は検出できない(ツール説明・security.md・ツール画面に注意を記載)。

## テスト

自動テストはすべてフェイクを使い、実通知を送らない。

| 対象 | 内容 |
|---|---|
| Core.Tests `NotificationToolTests` | 既定・スキーマ、文字列契約(拒否と許可、長さの境界)、CRLF、非エコー、間隔・ローリング窓の境界・複数クライアント・再発行・ローカル・同時実行・壁時計変更、pending 監査の順序と監査不能、準備中の失効/再発行/期限/許可取消/全体無効/キャンセル/タイムアウト、失敗と unknown、ハング中のロックと電源ゲート |
| Mcp.Tests `NotificationServerTests` | HTTP/MCP 経由の accepted、ホスト再起動と別クライアントでの上限維持、未許可・全体無効・失効の拒否、不正入力の非エコー、合成の秘密が DB/WAL/CSV/JSON/応答に残らないこと、新規/既存 DB の既定 |
| Windows.Tests `WindowsDesktopNotificationsTests` | 一時フォルダーでのショートカット作成・冪等性・付け替え・競合の非上書き・未登録時の拒否、公式 XML DOM のペイロード(送信なし)。Windows CI で実行 |
| App.Tests | 動作確認ボタンの文言と固定引数、登録失敗の表示 |
| smoke | 起動前に Sysacta のものではない `Sysacta.lnk` を置き、起動・再起動が成功してファイルが変わらないことを確認(登録はしない) |

結果(Linux/WSL、2026-11-28): `dotnet build Sysacta.sln -c Release` 警告・エラー 0。`dotnet test` は Core 740 / Mcp 127 / Infrastructure 51 成功、Windows.Tests 68 成功・38 スキップ。`dotnet publish -r win-x64 --self-contained` は成功(起動は未確認)。

結果(Windows CI、2026-11-28、[run 46131083215](https://github.com/clagon/sysacta/actions/runs/46131083215)): Release build / test / smoke 成功。Core 740 / Mcp 127 / Infrastructure 51 / App 32 成功、Windows.Tests 95 成功・11 スキップ。`WindowsDesktopNotificationsTests`(ショートカットの作成・付け替え・非上書き、XML DOM のペイロード)と App.Tests の動作確認ボタンのテストは実行されて成功。smoke は他者の `Sysacta.lnk` が変わらないことを確認した。いずれも通知の表示と実際の登録は含まない。

## 実機確認(2026-11-28)

利用者が対象 PC(開発機の Windows 11、通常権限)・起動・固定文言 1 件の送信を明示承認して実施した。送信ボタンの操作・表示の目視・クリックは利用者。送信は 1 件だけで、通知設定・集中モードは変更していない。

| 項目 | 結果 |
|---|---|
| 対象 | `26ecaf2` の `win-x64` 自己完結型フォルダ |
| 登録 | 起動で `Sysacta.lnk`(ID `Sysacta.Desktop`)を 1 件作成。再起動でファイル不変。フォルダ移動後の起動でリンク先が付け替わった |
| 送信 | 管理画面の「テスト通知を送る」。accepted、送信元「Sysacta」とアイコン、日本語の title / body を表示 |
| クリック | アプリ起動中は何も起きなかった |
| 監査 | `local_console` / success、引数なし。DB と WAL に title / body の文字列なし |
| 既存データ | スキーマ変更なし。追加ツールは無効で追加され、クライアント権限は増えていない。実施前にバックアップを取得 |
| 後片付け | 作成した展開フォルダとショートカットを削除。利用者が有効化した notification_send と監査記録は既存データに残る |

注意: 最初の起動は、MSIX パッケージのデスクトップアプリから起動した開発ツール経由で、プロセスにパッケージ ID はないが AppData の新規ファイルがそのパッケージ内に振り分けられた(データベースの WAL が該当。トレイから終了してチェックポイント後に整合性を確認)。ショートカットは通常の場所に作られた。移動後の起動と再起動はエクスプローラーから行った。Sysacta の不具合ではないが、実機確認ではエクスプローラーから起動する。

## 配布形式別の対応状況

| 形式 | 状況 |
|---|---|
| ソースからのビルド | 起動時登録を実装。ビルド出力から直接の起動は未確認 |
| ポータブル(自己完結型フォルダ) | 開発機で起動・登録・移動時の付け替え・再起動・表示を確認。ZIP の配布物と別の PC は未確認。フォルダを削除してもショートカットは残る(手動で削除) |
| ユーザー単位 Inno Setup | **インストーラーが存在しないため未対応・未検証** |

Phase 3 のインストーラー要件:

1. `{userprograms}\Sysacta.lnk`(上の場所と同一名)を、`AppUserModelID: "Sysacta.Desktop"` を付けて作成する。サブフォルダーに置くと、起動時登録が 2 つ目を作る。
2. アンインストール時に自分が作ったショートカットを削除する。アプリが起動時に作った(インストーラーが作っていない)場合も、同じ ID であることを確認して削除する。ポータブル版が使っている場合の扱いを決める。
3. 更新でパスが変わらないことを確認する。manual-tests.md の N 項目をインストーラー構成でも実施する。

## 仕様との差分

- spec §7.9 の「ポータブル/インストーラー両構成で登録・表示を実機確認」のうち、インストーラー構成は未達(未作成。Phase 3)。
- spec の「Windows App SDK等」に対して、Windows App SDK ではなく `Windows.UI.Notifications` を採用。
- spec の「通知本文は既定マスク」に対して、title・body とも常に記録しない。
- エラーコード `NOTIFICATIONS_DISABLED` を追加。

## 未確認事項と残存リスク

- 実機で未確認: MCP 経由の送信、特殊文字と URL、アプリ終了後のクリック、通知センターへの残り方、通知無効時、集中モード、ロック画面、登録失敗の表示(manual-tests.md N4〜N8)。Phase 3 の手動確認に引き継ぐ。
- ショートカット作成直後は Windows がアプリを認識するまで通知が出ない可能性がある(accepted でも表示されない)。実機では作成の約 30 秒後の送信は表示された。
- 管理者として起動した場合の通知の表示は未確認。
- 同一ユーザーの他プロセスは同じ AUMID で通知を出せ、ショートカットを書き換えられる(サンドボックスではない)。
- 書き込み中の異常終了で壊れたショートカットが残ると「競合」になり、利用者が削除するまで使えない。
- 到達できないリンク先(未接続のドライブなど)は「存在しない」とみなして付け替える。
- 認可されたクライアントは任意の文章を Sysacta の名前で表示できる(フィッシングに使える)。付与は必要な範囲にする。
- アプリ再起動でレート制限がリセットされる。
- 見た目を紛らわせる文字(似た字形、結合文字の多用、Hangul filler などの不可視の文字)は完全には防げない。
