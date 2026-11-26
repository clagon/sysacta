# Phase 1C-2 進捗ログ（ディスプレイのモード・電源・入力切替）

指示書は [phase-1c-2.md](phase-1c-2.md)。ブランチ `phase-1c-2`（基点は `main` の `0c7b80e`）。

## 状態

| 項目 | 状態 | 主なファイル |
|---|---|---|
| 技術調査 | 完了 | 本ファイル §1 |
| `display_set_mode`（Core / Windows） | 実装・自動テスト済み。**実機でのモード変更は未実施**（利用者の承認なし）。CDS_TEST と読み取りは実機で確認 | `DisplayModeTools.cs`, `DisplayModeControl.cs` |
| ローカル復旧（`display_restore_mode`、トレイメニュー、デバイス画面） | 実装・自動テスト済み。**実機での復旧操作は未実施** | `DisplayModeTools.cs`, `TrayIcon.cs`, `DevicesViewModel.cs` |
| `display_set_power` / `display_set_input`（Core / Windows） | 実装・自動テスト済み。**実機での電源・入力の変更は未実施**（利用者の承認なし）。能力文字列と現在値の読み取りは実機で確認 | `DisplaySwitchTools.cs`, `Mccs.cs`, `DdcCiSwitchControl.cs` |
| `display_get_state` の `include_switching` | 実装・自動テスト済み。実機で読み取り確認 | `DisplayReadTools.cs` |
| 3 ツールの初期無効・個別許可 | 実装・自動テスト済み（新規 DB・アップグレード・既存クライアント） | `ToolCatalog.cs`, `ToolsViewModel.cs` |

未対応（理由は §8）: 唯一の画面・プライマリ画面のモード変更、回転・スケーリングの変更、`standby` / `suspend` / ハードオフ（VCP 0xD6 = 0x05）、MCCS 3.0 の入力切替、メーカー固有の入力値、復旧ホットキー、自動ロールバック、再起動後の復旧。

Phase 1D 以降には着手していない。

## 1. 技術調査と採否

公式文書（Microsoft Learn, 2026-11-26 確認）と、開発機の Windows 11（build 26300）での**読み取りのみ**の結果に基づく。外部 CLI、GUI 自動化、レジストリ直接変更、ドライバ追加、権限昇格、非公開 API は使っていない。依存の追加はない。すべて通常ユーザー権限。

| API | 用途 | 採否と根拠・制約 |
|---|---|---|
| [ChangeDisplaySettingsExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw) | モードの検証（`CDS_TEST`）と適用 | 採用。`lpszDeviceName` に対象の GDI ソース名を渡し、その表示ソースだけを対象にする（`NULL` = 既定デバイスは使わない）。`CDS_TEST` は「実際に変更せずに有効なモードかを判定する」と明記。適用は `dwflags = 0`（「動的に変更」。`CDS_UPDATEREGISTRY` を付けないのでレジストリに保存しない）。`CDS_GLOBAL` / `CDS_NORESET` / `CDS_RESET` / `CDS_SET_PRIMARY` / `DM_POSITION` は使わない。DEVMODE は「EnumDisplaySettings が返したもの」を使うよう文書が推奨しており、現在列挙される DEVMODE をそのまま使い、`dmFields` を文書記載のモード項目（`DM_BITSPERPEL / DM_PELSWIDTH / DM_PELSHEIGHT / DM_DISPLAYFLAGS / DM_DISPLAYFREQUENCY`）だけに上書きする（列挙値の `DM_POSITION` 等で他の配置を動かさないため）。戻り値は `DISP_CHANGE_SUCCESSFUL` 以外をすべて失敗扱い（`BADMODE` / `RESTART` → `NOT_SUPPORTED`、その他 → `OS_API_ERROR`）。数値（`CDS_TEST = 2`、`DISP_CHANGE_*`）は winuser.h の定義で、ページには載っていない。呼び出しが同期かどうかは文書に記載なし（実測は未実施） |
| [EnumDisplaySettingsExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsexw) | モード列挙・現在モード・読み戻し | 1C-1 と同じ（`EDS_RAWMODE` / `EDS_ROTATEDMODE` なし、整数 Hz） |
| [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig) | 変更前後の配置（位置・回転・複製・プライマリ・現在モード）の比較 | 採用（`QDC_ONLY_ACTIVE_PATHS`）。`SetDisplayConfig` は使わない（トポロジー全体を扱う API で、単一ソースの変更には過剰。`ChangeDisplaySettingsEx` で足りる） |
| [GetCapabilitiesStringLength](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getcapabilitiesstringlength) / [CapabilitiesRequestAndCapabilitiesReply](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-capabilitiesrequestandcapabilitiesreply) | MCCS 能力文字列 | 採用。長さは終端 NUL を含む文字数、文字列は常に ASCII（`LPSTR`、`byte[]` で受ける）。「通常はすぐ返るが数秒かかることがある」と明記（実測 3.6〜3.8 秒）。DDC/CI の Capabilities Request（読み取り）で、状態を変えない。長さ 0 または 8192 文字超は拒否 |
| [GetVCPFeatureAndVCPFeatureReply](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply) | 電源・入力の現在値 | 採用（VCP 0xD6 / 0x60 のみ）。`pvct` は NULL 可。非連続（NC）コードの最大値は未定義なので使わない。約 40 ms（文書）。値は SL バイト（下位 8 bit）として扱う |
| [SetVCPFeature](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature) | 電源・入力の変更 | 採用（VCP 0xD6 = 0x01/0x04、0x60 = MCCS 2.x の標準値のみ）。約 50 ms（文書）。成功は「モニターが受理した」ことしか示さない |
| [Using the Low-Level Monitor Configuration Functions](https://learn.microsoft.com/en-us/windows/win32/monitor/using-the-low-level-monitor-configuration-functions) | 手順 | 非連続コードの対応値は能力文字列から取る、という公式の手順に従う。各ページに「多くのモニターは MCCS を完全には実装しておらず、未定義の挙動になり得る。物理的に検証せずに任意のモニターで使うことは推奨しない」とある → 3 ツールとも `DefaultPolicy.Off`、実機未検証と明記 |

MCCS の値の意味は VESA MCCS（有償の規格書で原文は未入手）を、オープンソースの [ddcutil](https://github.com/rockowitz/ddcutil) の値表 `src/vcp/vcp_feature_codes.c`（commit `5085ae9bb1a8e5bd8db717d80dd80a279a0f05fb`、GPL-2.0-or-later。**値の意味を照合しただけでコードは転用していない**）で照合した。

| VCP | ddcutil の定義 | Sysacta の扱い |
|---|---|---|
| 0xD6 Power mode（MCCS 2.0: NC） | 01 `DPM: On, DPMS: Off` / 02 `DPM: Off, DPMS: Standby` / 03 `DPM: Off, DPMS: Suspend` / 04 `DPM: Off, DPMS: Off` / 05 `Write only value to turn off display` | `on` = 0x01、`off` = 0x04 だけを書く。0x05（ハードオフ、DDC/CI で復帰できないことが多い）・0x02/0x03 は書かない。読み取りは 01〜04 を `on/standby/suspend/off`、それ以外は null |
| 0x60 Input Source（MCCS 2.0/2.2: NC、**3.0: テーブル型**） | 01 VGA-1 … 0E Component 3（v2.0）、0F DisplayPort-1 / 10 DisplayPort-2 / 11 HDMI-1 / 12 HDMI-2（v2.2。「v2.1 でも有効と仮定」とのコメント） | 0x01〜0x12 だけを候補にする。`mccs_ver` が 2.x と宣言されたモニターだけ（3.0 では型が違うため）。ddcutil もこの機能は SL バイトだけで値を引いている |

P/Invoke: `DEVMODEW` に `dmDriverExtra`（offset 70）と `dmFields`（offset 72）を追加（サイズ 220 は不変、`WindowsDisplayLogicTests` で固定）。新しい関数は値型と `byte[]` だけで、構造体の追加はない。

## 2. 設計判断

### 識別と対象

- 1C-1 の正規 ID（`display-` + デバイスパスのハッシュ）、`DisplayTarget.Resolve`、`mode_id`（`DisplayListModesTool.ModeId`）をそのまま使う。`device` は 3 ツールとも**必須**（プライマリへの既定なし）。省略・`null`・`all`・`default`・未知 ID は拒否。
- モード変更の対象は内蔵・外部モニターだけ（仮想・Miracast・間接は `NOT_SUPPORTED`）。複製表示は `AMBIGUOUS_TARGET`。アダプターとツールの両方で確認する（`DisplayCatalog.BlockedForMode`）。
- 電源・入力は外部モニターだけ（`BlockedForSwitch`）。物理ハンドルは 1C-1 と同じく「1 つの `HMONITOR` にちょうど 1 台」のときだけ使い、開いた後に GDI ソース名が同じ対象のままか再確認する（`WindowsDisplays.Checked`）。
- `input_id` = `input-` + SHA-256(表示 ID | 60 | 値) の先頭 96 bit。表示 ID に結び付くので別の機器の ID は一致しない。書き込み前に、そのモニターが**いま**宣言している候補から再計算して照合する（消えた入力・別機器の ID は `INVALID_ARGUMENT`）。

### モード変更の手順（`DisplayModeChange.RunAsync`）

1. 解決（executor のロック前）→ ロック取得 → トポロジーで対象を再確認（複製・仮想・プライマリを拒否）。
2. `OpenMode`: アダプターのゲートを取り、対象を再解決。ゲート取得後の配置でもう一度プライマリ・複製を確認。
3. 現在のモード一覧を列挙し、`mode_id` を**いまの列挙**から再計算して照合（未列挙の値は合成しない。切り詰めに関係なく全列挙と照合）。現在モードが読めなければ復旧できないので拒否。
4. 現在モードと同じなら no-op（`verified`、書き込み・テストなし）。回転・スケーリング（fixed output）が現在と違うモードは拒否。
5. 変更前の配置を取得 → `CDS_TEST` → `PrepareApply`（ソースと対象の対応を再確認し、列挙中の DEVMODE を用意）。ここまでが開始点より前。
6. 復旧情報を登録 → `BeginSideEffect`（拒否されたら復旧情報を取り下げる）→ `ChangeDisplaySettingsExW(..., 0)` を 1 回だけ。
7. 戻り値に関係なく現在モードと配置を読み戻して判定する。

| 読み戻し | 結果 |
|---|---|
| 要求どおり・他の表示に変化なし（API が失敗を返しても読み戻しを正とする） | `success` / `verified` |
| 要求どおり・他の表示が変化 | `partial` / `verified` + `VERIFICATION_FAILED`、`other_displays_changed: true`。追加の変更はしない |
| 元のまま | `error`（API の失敗コード、API 成功なら `VERIFICATION_FAILED`）、`changed: false`。復旧情報は取り下げ（直前の記録に戻す） |
| 別のモード | `error` / `VERIFICATION_FAILED`、`changed: true`、復旧可能 |
| 読めない | `unknown`、復旧可能 |

「他の表示」の比較対象は、対象以外の表示の ID・プライマリ・複製・現在モード・位置・回転と、対象自身のプライマリ・複製・回転（対象の位置とモードは変わってよい）。観測された変化を Sysacta によるものと断定しない（説明文に明記）。

### 判断の補足（レビューで確認した点）

- **整数 Hz で区別できないモード**: 要求に使うのは `dmFields` の 5 項目だけで、その値は `mode_id` が表す値と同じ。Windows が同じ 5 項目の値を持つ DEVMODE を複数列挙しても、渡す要求は同一になる（Sysacta が別の信号モードを選んでいるわけではない）。59.94 Hz と 60 Hz を Windows が別の整数（59 / 60）で列挙する場合は別の `mode_id` になる。整数 Hz の API で表せない区別（分数 Hz の選択）はできず、`SetDisplayConfig` での分数指定は今回の範囲外。
- **復旧に必要なトポロジー**: 復旧が変えるのは対象のソースのモードだけなので、記録するのは表示 ID・元のモード・適用したモード。復旧時に、同じ表示 ID が単独で（複製でなく）存在し、現在モードが適用したモード（回転・スケーリングを含む）と一致することを確認する。他の表示の配置は復旧の対象にしない（他の画面が抜き差しされても、映らない画面を戻せるようにするため）。復旧後も他の表示の変化を比較して報告する。
- **復旧の競合**: 待機中に別の表示の変更（別のロックキー）で記録が置き換わった場合に古い記録を適用しないよう、アダプターのゲート取得後にも記録が最新か確認する（レビュー指摘で追加、テストあり）。
- **入力の現在値が宣言外のとき**: 値そのものは読めている（実機の P2314H のように宣言外の値を返す機種がある）。書く値は宣言済みの標準値に限るので、現在値の意味が分からなくても書き込みを許可する。電源は現在値が MCCS の定義外なら状態そのものが不明なので書かない。
- **能力の再評価**: 能力文字列も現在値もキャッシュせず、呼び出しのたびに取得し直す。切替後の読み戻しは同じハンドルで値だけを読む（能力文字列の再取得は数秒かかり、通信が切れた直後は意味がない）。次の呼び出しで再評価される。
- **読み戻しの証拠**: 読み戻しで一度でも目標以外の値が読めた場合は、その後に読めなくなっても `accepted` にせず `VERIFICATION_FAILED`（最後に読めた値を報告）。一度も読めないときだけ `accepted`（レビュー指摘で修正、テストあり）。

### 電源・入力の手順

1. 解決 → ロック → トポロジーで再確認（外部・非複製）→ `OpenSwitch`（ゲート、再解決、物理ハンドル、ソース名の再確認）。
2. 能力文字列を取得・解析（`Mccs.Parse`。長さ 8192、深さ 4、要素 2048 まで。印字可能 ASCII 以外、括弧の不整合、2 桁の 16 進以外、コード重複、`vcp(...)` の欠落・重複は不正）。`mccs_ver` が 2.x でない・コードがない・値の一覧がない・（電源は）01 と 04 の両方がない → `NOT_SUPPORTED`。解析不能・取得失敗 → `OS_API_ERROR` / 失敗コード（`unavailable`）。推測で補わない。
3. 現在値を読む。読めなければ書かない。電源の現在値が 01〜04 以外なら書かない。目標と同じなら no-op。
4. `PrepareWrite`（アダプターでも許可リストを再確認）→ 進捗保存 → `BeginSideEffect` → `SetVCPFeature` 1 回。
5. 読み戻し: 200 ms 間隔で最大 5 回（読み取りだけを再試行。書き込みは繰り返さない）。

| 結果 | 契約 |
|---|---|
| 読み戻して一致 | `success` / `verified`, `changed: true` |
| 読み戻せたが不一致（5 回目） | `error` / `VERIFICATION_FAILED`、`changed` は前後比較 |
| 書き込み受理後に一度も読めない（オフや入力切替で通信が切れた） | `success` / `accepted`, `current_*: null`, `changed: null`。表示状態は保証しない |
| 書き込み API が失敗 | `unknown`（DDC/CI の書き込みは失敗しても反映されている可能性がある）。再試行しない |
| 開始後のタイムアウト・キャンセル | `unknown`（executor。保存した進捗を返す） |

画面や通信が消えたこと自体を成功の根拠にしない。`accepted` は spec §9 の「API 受理のみ」の意味。

### 排他・副作用開始点

- executor のロックは 3 ツールとも `display:device:{id}`（輝度・コントラストと共有）。`display_restore_mode` も同じキー。
- アダプターの**プロセス全体で 1 つのゲート**（1C-1）をモード変更・電源・入力でも開いてから閉じるまで保持する。モード変更中は他のどのモニターの DDC/CI・WMI 読み書きも走らないので、モード変更で物理ハンドルが無効になる競合や、配置比較が Sysacta 自身の別変更を拾うことはない。ロック順は常に「executor のロック → スロット → ゲート」。
- 時間のかかる処理（能力文字列、列挙、`CDS_TEST`、再確認、ハンドル取得）はすべて `BeginSideEffect` より前。書き込みは各ツール 1 回だけなので `BeginNextSideEffect` は使わない。
- ネイティブ呼び出しが返らない場合は executor がロック・スロットを、コントロールがハンドルとゲートを終了まで保持する（フェイクで検証）。

### タイムアウト（spec §10 との差分あり）

| ツール | 秒 | 根拠 |
|---|---:|---|
| display_set_mode / display_restore_mode | 15 | **暫定値（実測なし）**。読み取り側の実測は列挙 1〜9 ms・CDS_TEST 1〜22 ms。適用の所要時間はモード変更を実施していないため未測定。短すぎると開始後のタイムアウトが `unknown` になり、利用者に確認を強いるため、ゲート待ちを含めて余裕を持たせた。実機確認（DM2）で見直す |
| display_set_power / display_set_input | 15 | 能力文字列の実測 3.6〜3.8 秒 + 読み取り（約 60〜120 ms）+ 書き込み + 読み戻し最大約 1.3 秒 |
| display_get_state | 10 → **15** | `include_switching: true` の能力文字列のため。既定（false）では処理は増えない（上限が延びるだけ） |

### 共通基盤の変更（最小限）

- `ToolDescriptor.LocalOnly` を追加。`ToolRegistry.All`（MCP の一覧・DB 同期・ツール/クライアント画面）から除外し、MCP クライアントからの呼び出しは executor が**未知のツール**として扱う（MCP ではプロトコルエラー）。全体許可・クライアント別許可は持たず、`Caller.LocalConsole` だけが実行できる。実行は通常どおり `ToolExecutor`（検証・ロック・pending 監査・開始点・タイムアウト・最終監査）。使うのは `display_restore_mode` だけ。
- ツール画面の「すべて有効」は `DefaultPolicy.Off` のツールを含めない（既存の Off ツールはなかったので既存の挙動は変わらない）。Off の 3 ツールに日本語の注意文を表示する。
- Core に `InternalsVisibleTo("Sysacta.App.Tests")` を追加（テストで復旧記録を作るため）。
- `ToolRegistry.AllNames`（ローカル専用ツールを含む名前一覧）を追加し、監査ログ画面のツールの絞り込みに使う。
- 認証・要求予算・監査容量・オーディオと 1C-1 の JSON 契約は変更していない。

### レビューと修正

独立した 2 系統のレビュー（規約・仕様）を行い、次を修正した: `display_set_mode` がモード変更の完了前にコントロール（ゲート）を解放し得る形（`await` なしの返却）、読み戻しの不一致の証拠を捨てて `accepted` にする経路、復旧ツールの記録の再確認がゲート取得前だけだった競合、監査ログ画面で `display_restore_mode` を絞り込めない点。モード変更と電源・入力の排他テストを追加した。重複コード（3 ツールの `ResolveTargetAsync` など）の共通化は、各ツールの読みやすさを優先して見送った。

## 3. JSON 契約

すべて spec §9 の封筒。

| ツール | 引数 | `data` |
|---|---|---|
| display_set_mode | `device`（必須）, `mode_id`（必須, 1〜64 文字） | `device_id`, `name`, `requested_mode_id`, `previous_mode`, `current_mode`?, `changed`?, `recovery_available`, `other_displays_changed`? |
| display_set_power | `device`（必須）, `state`（`on` / `off`、必須） | `device_id`, `name`, `requested_state`, `previous_state`?, `current_state`?, `changed`? |
| display_set_input | `device`（必須）, `input_id`（必須, 1〜64 文字） | `device_id`, `name`, `requested_input_id`, `previous_input_id`?, `current_input_id`?, `changed`? |
| display_get_state（加算） | `include_switching`（bool, 既定 false） | 既存項目 + `power` { `support`, `state`?, `error_code`? }, `input` { `support`, `current_input_id`?, `inputs[]` { `input_id`, `name`, `is_current` }, `error_code`? } |
| display_restore_mode（ローカルのみ） | なし | `display_set_mode` と同じ形 |

- モード値（`previous_mode` / `current_mode`）: `mode_id`, `width`, `height`, `refresh_hz`, `bits_per_pixel`, `interlaced`, `orientation`, `fixed_output`（`display_list_modes` の項目から `is_current` を除いたもの）。
- `power.state` / `previous_state` / `current_state`: `on` / `standby` / `suspend` / `off`、意味のない値・読めない値は null。
- `include_switching` 省略時（既定）: `power` = `{support: "unchecked", state: null, error_code: null}`、`input` = `{support: "unchecked", current_input_id: null, inputs: [], error_code: null}`。既存項目の意味・既定の処理は変わらない（モニターへの追加の問い合わせなし）。`support` の意味は 1C-1 と同じ（`supported` = いま読めた、`not_supported` = 明確な非対応、`unavailable` = 一時的な失敗・曖昧、`unchecked` = 未問い合わせ）。
- `inputs` は最大 18 件（MCCS 2.x の標準値の数）。名前は MCCS の定義（`VGA 1`〜`HDMI 2`）で、物理端子のラベルと一致する保証はない（実機で DELL P2314H が宣言外の 0x04 を現在値として返した例あり、§5）。
- 監査: `display_set_power` は `state` と `target`、他の 2 ツールと `display_restore_mode` は `target` だけ。`mode_id` / `input_id` の入力文字列、能力文字列、デバイスパス、結果は記録しない（Mcp.Tests で DB/WAL/CSV/JSON に含まれないことを確認）。`display_get_state` の監査引数は変えていない（`include_switching` は記録しない）。

## 4. 画面消失時の復旧手段

- **復旧情報**: `DisplayModeRecovery`（プロセス内に直近 1 件、永続化しない）。`{表示 ID, 名前, 元のモード, 適用したモード}`。`BeginSideEffect` の直前に登録するので、結果不明（タイムアウト・読み戻し不能）でも残る。変更が起きなかったと確認できた場合は取り下げて直前の記録に戻す。復旧に成功したら消す。現在モードが読めない場合はモード変更自体を拒否する。
- **復旧操作**: `display_restore_mode`（ローカル専用ツール）。トレイメニューの「直前の表示モード変更を元に戻す」（記録があるときだけ表示）と、デバイス画面の「元に戻す」ボタンから、`Caller.LocalConsole` で executor を通して実行する（ロック・監査・タイムアウトあり。確認ダイアログなし: 画面が見えない状況を想定）。結果は日本語の固定文言で通知する（ツールのメッセージは表示しない）。
- **限定**: 記録した元のモードだけを、同じ表示 ID にだけ適用する。元の表示が外れた・別の機器になった → `DEVICE_DISCONNECTED`。現在モードが Sysacta の適用したモードでない（外部操作や後続の変更）→ `TARGET_CHANGED` で適用せず記録を破棄。すでに元のモード → no-op。元のモードが列挙されなくなった → `NOT_SUPPORTED`。待機中に新しい記録ができた → `TARGET_CHANGED`（再試行可）。`CDS_TEST`・配置比較・読み戻しは通常の変更と同じ。
- **プライマリを対象外にした理由**: トレイの通知領域と管理コンソールを開く手段（Windows の既定ではタスクバーの通知領域はプライマリにある）が、変更対象の画面に依存しないようにするため。対象がプライマリになった場合（待機中の変化を含む）も拒否する。唯一の画面は必ずプライマリなので同時に対象外になる。復旧は、対象がプライマリになっていても実行できる。
- **非永続**: `CDS_UPDATEREGISTRY` を付けないので、文書上はレジストリの設定が変わらず、サインアウト・再起動で元に戻る想定。**Windows 11 の実挙動は未検証**（モード変更を実施していないため）。
- **限界**: Sysacta が終了すると記録は消える。復旧できるのは直近 1 件だけ（別の表示を続けて変更すると、前の表示は Sysacta からは戻せない。Windows の設定で戻す）。モニターが信号を表示できず Windows が表示を無効にした場合も Windows の設定（または Win+P）で戻す。自動タイマーでのロールバック、ホットキー、再起動後の復旧は実装していない（§8）。
- **電源・入力**: Sysacta からの復帰を保証しない。オフにしたモニターや信号のない入力に切り替えたモニターは DDC/CI に応答しなくなったり Windows から消えたりすることがある → モニターの電源ボタン・OSD で戻す。ツール説明とツール画面の注意文に記載。

## 5. 検証結果

2026-11-26。

| 環境 | 結果 |
|---|---|
| WSL2 (Linux) `dotnet build Sysacta.sln -c Release` | 警告 0・エラー 0 |
| WSL2 `dotnet test Sysacta.sln -c Release` | Core 428 / Mcp 91 / Infrastructure 50 成功、Windows.Tests 27 成功・15 スキップ（Windows 専用）。App.Tests は対象外 |
| 開発機 Windows 11 (build 26300) `dotnet.exe test`（`SYSACTA_HARDWARE_TESTS` 未設定、書き込みなし） | Core 428 / Mcp 91 / Infrastructure 50 / Windows 36 成功・6 スキップ（状態変更）。App 27 成功・1 スキップ・**1 失敗**: `Concurrent_start_and_settings_save_end_consistent` が起動中の別の Sysacta が使うポート 8765 に衝突（1C-1 と同じ環境要因。ポートが空いていた回は成功）。途中、新しいトレイのテストが既存のトレイのテストと並列に通知領域アイコンを作り testhost が異常終了したため、同じクラスに移して順次実行にした。新しい MCP テストの WAL 走査の競合（SQLite が `-wal` を削除）も修正（既存の `RejectionAuditTests` にも同じ断続的失敗があるが変更していない） |
| Windows CI (windows-latest) | PR 作成後に記録 |

追加した主な自動テスト: `DisplayModeToolTests`（35 件: 検証・no-op・プライマリ/複製/仮想/不明の拒否、待機中にプライマリになった場合、厳格な引数、他機器・未列挙の `mode_id`、回転・スケーリング、現在モード不明、CDS_TEST 失敗、適用失敗で不変・別モード・読めない、他の表示の変化、準備中の失効・無効化、ネイティブ呼び出しのハングとロック保持、復旧の成功・無許可で動くこと・MCP から呼べないこと・記録なし・古い記録・別機器・既に元のモード・元のモードが消えた・同じ表示との排他）、`MccsTests` / `DisplaySwitchToolTests`（69 件: 能力文字列の正常/不正/上限、MCCS 3.0・未宣言・部分宣言の拒否、許可リスト、`input_id`、`include_switching` の既定と各失敗理由、電源・入力の検証・no-op・accepted・unknown・不一致・証拠なしの拒否・厳格な引数・他機器/未宣言/消えた入力・メーカー固有値・準備中の失効/無効化/期限切れ・ハング）、`DisplaySwitchingServerTests`（5 件: 新規/アップグレード DB で無効・未付与、無効/未許可で表示を開かない、復旧ツールが MCP の一覧に出ず呼べない、出力スキーマと isError、合成秘密が DB/WAL/CSV/応答に残らない）、`DisplayRecoveryUiTests`（App: 復旧文言、「すべて有効」が Off ツールを含まない、トレイの復旧項目の表示追従）、Windows.Tests（定数・オフセット、読み取り専用の実機テスト）。

## 6. 機種別の確認状況（読み取りのみ。書き込みは一切していない）

環境: Windows 11 Home build 26300、NVIDIA GeForce RTX 5070 Ti、通常権限、拡張表示。**PowerToys の PowerDisplay（DDC/CI を使うモニター制御）が同時に動作していた**。

| モニター | 接続 | 能力文字列 | D6 宣言 / 現在値 | 60 宣言 / 現在値 | CDS_TEST（現在モード） | 書き込み |
|---|---|---|---|---|---|---|
| DELL S2422HG | DisplayPort | 3 回とも取得失敗（`OS_API_ERROR`、104〜3625 ms） | 未取得 | 未取得 | 成功（3〜4 ms） | 未実施 |
| DELL P2314H | HDMI | 3 回中 1 回取得（287 文字、`mccs_ver` 2.x、3783 ms）。2 回は失敗 | 01 04 05 / **0x04（off）** | 01 03 0F / **0x04（DVI 2、宣言外）** | 成功（1〜22 ms） | 未実施 |

- 同じ実行内でも、直後の `display_get_state` 相当の読み取りが成功・失敗の両方になった。1C-1 で成功したコントラストの読み取りもこの日は失敗した。別プロセスの DDC/CI 利用（PowerDisplay）との衝突が疑われる（Sysacta のゲートはプロセス内だけ）。Sysacta は推測せず `unavailable` を返しており、書き込み前の失敗では何も書かない。
- P2314H は Windows ではアクティブだが VCP 0xD6 は 0x04（DPM off）を返し、入力は能力文字列にない 0x04 を返した。**入力名と物理端子の対応は一般化できない**ことの実例として、宣言外の現在値は `current_input_id: null` になる（自動テストで同じ形を確認）。
- 未検証: 実際のモード・電源・入力の変更、変更後の通信消失・HPD 消失、非永続性（サインアウト後の復帰）、他の表示への影響、内蔵パネルのモード変更、複製・ドック・MST、MCCS 3.0 のモニター、PowerDisplay を止めた状態での能力文字列の安定性。2 台の結果は他の機種の対応を意味しない。

## 7. 仕様・指示書との差分

- **プライマリ画面のモード変更を拒否**（指示書の初期案「唯一の画面は拒否」より狭い）。理由は §4。復旧ホットキーなど、対象画面に依存しない復旧手段ができれば緩められる。
- `display_restore_mode` はローカル専用のツールとして追加（spec §7 の表にない。MCP には公開されず、許可設定もない）。`DefaultPolicy` は意味を持たない（DB に登録しない）。
- タイムアウト: 3 ツールとも 15 秒（指示書の初期案: 通常 5 秒、DDC/CI 10 秒）、`display_get_state` 10 → 15 秒。根拠は §2。
- `display_get_state` の監査引数は空のまま（`include_switching` を記録しない）。
- ツール画面の「すべて有効」から Off ツールを除外した。

## 8. 未対応とその理由

| 項目 | 理由 |
|---|---|
| プライマリ・唯一の画面のモード変更 | 画面が映らなくなったとき、トレイ・コンソールが同じ画面にあり、確実なローカル復旧手段がない |
| 回転・スケーリング（fixed output）の変更 | `ChangeDisplaySettingsEx` の文書が変更項目として挙げておらず、意味の検証ができない。`mode_id` の契約は維持し、現在と違う値のモードは拒否 |
| `standby` / `suspend` / ハードオフ（0x05） | 必要性の根拠がなく、0x05 は ddcutil で書き込み専用・DDC/CI で復帰できない機種が多い |
| MCCS 3.0 の入力切替 | 0x60 がテーブル型に変わり、値の意味が異なる |
| メーカー固有の入力値・`mccs_ver` 未宣言のモニター | 意味を確認できない（推測しない） |
| 復旧ホットキー | 割り当ての競合・登録失敗時の扱い・設定 UI が必要。プライマリを対象外にしたことでトレイから復旧できるため今回は不要と判断 |
| 自動ロールバック（タイマー） | 指示書どおり初期実装に含めない（承認が必要な別設計） |
| 再起動後の復旧 | 記録を永続化していない。非永続の変更なので再起動で戻る想定（未検証） |
| デバイス画面での電源・入力の表示 | 能力文字列の取得に数秒かかるため、選択のたびの問い合わせは避けた（`display_get_state` の `include_switching` で取得できる） |

## 9. 残存リスク

- **実機の書き込みが未検証**: `ChangeDisplaySettingsExW`・`SetVCPFeature` による変更、その後の読み戻し・通信消失・配置への影響は自動テスト（フェイク）だけで確認している。利用者の明示承認（対象機器・変更内容・復旧手段）を得た上で [manual-tests.md](manual-tests.md) の DM / DP / DI 項目を実施するまで、動作保証済みとは案内しない。
- 非永続性は文書上の挙動で、Windows 11 の表示設定データベース（CCD）での実挙動は未確認。
- 別プロセスの DDC/CI 利用（PowerToys PowerDisplay、メーカーのユーティリティ）とは排他できない。衝突すると読み取りは `unavailable`、書き込み API の失敗は `unknown`、読み戻しの失敗は `accepted` になり得る。
- DDC/CI の書き込みは失敗が返っても反映されている場合がある（`unknown` で報告するが、状態は確認できない）。
- `accepted` の後の実際の表示状態（オフになったか・入力に信号があるか）は Sysacta からは分からない。
- 入力値は SL バイトとして扱う。SH バイトに意味を持たせる機種では読み戻しが一致せず `VERIFICATION_FAILED` になり得る（書き込みは 1 回）。
- 整数 Hz の列挙のため、59.94 Hz と 60 Hz のように Windows が同じ整数で列挙するモードを区別できない場合、どちらが選ばれるかはドライバ次第（読み戻しは整数で比較）。
- 復旧は直近 1 件・プロセス稼働中だけ。Sysacta 終了後やホットプラグ後は Windows の設定で戻す必要がある。
- プライマリの判定は「座標 (0,0) のソース」（1C-1）。タスクバーの通知領域を別の画面に置く設定では、トレイが変更対象の画面にある可能性がある（デバイス画面からの復旧は可能）。
- 書き込み直前の再確認とネイティブ呼び出しの間のホットプラグは排他できない（1C-1 と同じ）。
- ネイティブ呼び出しが返らないと、以後のディスプレイ操作はプロセス終了まで使えない（1C-1 と同じ fail-closed）。
- 既存事項（変更していない）: MCP の結果に出力スキーマ未記載の `is_success` が含まれる。
