# Phase 1C-1 進捗ログ（ディスプレイ基本操作）

指示書は [phase-1c-1.md](phase-1c-1.md)。ブランチ `phase-1c-1`（基点は `main` の `762f842`）。

## 状態

| 項目 | 状態 | 主なファイル |
|---|---|---|
| 技術調査 | 完了 | 本ファイル §1 |
| 5 ツール（Core） | 実装・自動テスト済み | `src/Sysacta.Core/Tools/Display/` |
| Windows アダプター（DisplayConfig / Dxva2 / WMI） | 実装済み。**読み取りは外部モニター 2 台で実機確認。書き込みと内蔵パネルは実機未検証** | `src/Sysacta.Windows/Display/` |
| 一括輝度の部分成功・未着手・結果不明 | 実装・自動テスト済み（フェイク） | `DisplaySetLevelTools.cs`, `DisplaySetLevelToolTests.cs` |
| デバイス管理画面（ディスプレイ） | 実装・App.Tests と画面画像（ライト/ダーク, 100%）で確認 | `DevicesViewModel.cs`, `DevicesPage.xaml` |
| 実機の輝度・コントラスト変更 | **未実施**（利用者の明示承認がないため） | [manual-tests.md](manual-tests.md) D5 |

Phase 1C-2（解像度・リフレッシュレート変更、電源、入力切替）には着手していない。

## 1. 技術調査と採否

公式文書（Microsoft Learn, 2026-11-26 確認）と、開発機の Windows 11（build 26300）での読み取り結果に基づく。OSS のコードは参照・転用していない。依存の追加は `System.Management` 10.0.15（.NET の公式パッケージ, MIT）だけ。すべて通常ユーザー権限で呼び、昇格しない。

| API | 用途 | 採否と根拠・制約 |
|---|---|---|
| [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig) / `GetDisplayConfigBufferSizes` | アクティブなトポロジー | 採用。`QDC_ONLY_ACTIVE_PATHS` のみ（`QDC_ALL_PATHS` は高コストと明記）。`ERROR_INSUFFICIENT_BUFFER` は 3 回まで取り直す。`ERROR_ACCESS_DENIED`（リモート・非コンソール）は `NO_ACTIVE_SESSION`。Windows 7 以降 |
| [DisplayConfigGetDeviceInfo（target name）](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name) / source name | 対象の識別名・GDI デバイス名・接続方式 | 採用。`monitorDevicePath` を正規 ID の元にする |
| `EnumDisplayMonitors` / `GetMonitorInfoW` | GDI 名 → `HMONITOR` | 採用（DDC/CI を開くときだけ） |
| [EnumDisplaySettingsExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsexw) | モード列挙・現在モード | 採用。`EDS_RAWMODE` は使わない（モニターに適合するモードだけ）。回転は現在の向きのみ。リフレッシュは整数 Hz で、59.94 Hz は OS が返す整数（通常 59）のまま扱う |
| [GetPhysicalMonitorsFromHMONITOR](https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor) / [DestroyPhysicalMonitor](https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-destroyphysicalmonitor) | 物理モニターハンドル | 採用。1 つの `HMONITOR` に複数の物理モニターがあり得るため、**ちょうど 1 台のときだけ**使う。ハンドルは呼び出しごとに開閉し保存しない。値 0 も有効なハンドルとして扱う（成否は戻り値） |
| [GetMonitorBrightness](https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-getmonitorbrightness) / [SetMonitorBrightness](https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-setmonitorbrightness) / [Contrast](https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-getmonitorcontrast) | 外部モニターの値 | 採用（high-level のみ。任意 VCP の low-level API は使わない）。公式の目安は読み取り約 40 ms・書き込み約 50 ms。多くのモニターが MCCS を完全には実装していないと明記されている |
| [GetMonitorCapabilities](https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-getmonitorcapabilities) | 非対応の判別 | 補助として採用。**最初の読み取りに失敗したときだけ**呼び、能力フラグがなければ `NOT_SUPPORTED`。照会自体の失敗は非対応と断定しない |
| [WmiMonitorBrightness](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorbrightness) / [WmiSetBrightness](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmisetbrightness-method-in-class-wmimonitorbrightnessmethods) | 内蔵パネルの輝度 | 採用（内蔵パネルのみ、`System.Management`）。値は百分率で `Level[]` にある値だけ。コントラストはない。**実機未検証**（開発機に内蔵パネルがない） |
| DDC/CI のエラーコード | エラー分類 | 開発機の `FormatMessageW` で意味を確認: `0xC0262580`（I2C バスなし）・`0xC0262584`（VCP 非対応）→ `NOT_SUPPORTED`、`0xC026258D` / `0xC026258C`（モニター消失・無効ハンドル）→ `DEVICE_DISCONNECTED`、その他（`0xC0262581` 応答なしを含む）→ `OS_API_ERROR` |

不採用: 非公開 API、low-level VCP、外部 CLI、GUI 自動化、レジストリ操作、ガンマ変更や暗色オーバーレイによる疑似輝度。

P/Invoke の x64 構造体サイズ（PATH_INFO 72 / MODE_INFO 64 / TARGET_DEVICE_NAME 420 / DEVMODEW 220 / MONITORINFOEXW 104 / PHYSICAL_MONITOR 264）は `WindowsDisplayLogicTests` で固定し、値の妥当性は実機の読み取りで確認した。

## 2. 設計判断

### 識別

- 正規 ID は `display-` + SHA-256(大文字化した `monitorDevicePath`) の先頭 128 bit。名前・列挙順・`HMONITOR`・`adapterId`（再起動で変わる）は使わない。キャッシュは持たず毎回列挙して照合する。同じ ID が複数あれば `AMBIGUOUS_TARGET`。
- デバイスパスは接続先（ポートの UID）を含むため、**ポートやドックを変えると ID が変わり得る**。別名は自動で付け替えない（画面から明示的に再紐付け）。
- デバイスパスを返さない対象は ID を付けず `unidentified_count` に数える。`all` はそれがあると成功にしない。
- 列挙するのはアクティブな表示だけ（接続済みでもデスクトップに含まれないモニターは出ない）。
- 複製表示（1 つの source に複数 target）は、物理ハンドルと target を確実に対応付けられないため DDC/CI の操作とモード列挙を `AMBIGUOUS_TARGET` で拒否する。複製中の内蔵パネルは WMI をデバイスパスで特定できるので輝度操作を許可する。拡張でも 1 つの `HMONITOR` に物理モニターが複数あれば拒否する。
- プライマリは座標 (0,0) の source。省略指定はロック取得後に再確認し、変わっていれば `TARGET_CHANGED`。
- 方式は種類で決める（内蔵 = WMI、それ以外 = DDC/CI、仮想 = 非対応）。失敗しても別方式へ切り替えない。

### 排他・リソース

- 変更ツールのロックキーは `display:device:{id}`。輝度とコントラストで共有し、`all` は全対象のキーを `ResourceLocks` の辞書順で取る。
- 読み取り（get_state と管理画面）と書き込みの調停は、`WindowsDisplays` の**プロセス全体で 1 つのゲート**（開く → 読む → 書く → 読み戻す → 閉じる、まで保持）。共用バスを確実に識別できないため保守的に直列化する。
- ロックの順序は常に「executor のリソースロック → 実行スロット → アダプターのゲート」。ゲートを持つ側がリソースロックを取ることはないのでデッドロックしない。ゲート待ちは呼び出しのキャンセル/タイムアウトで打ち切る。
- ゲートを開いた後に同じ ID を再解決し、ハンドル取得後にも source 名が同じ target のままか確認する。
- `BeginSideEffect` / `BeginNextSideEffect` はゲート取得と読み取りの後、ネイティブ書き込みの直前に呼ぶ（ゲート待ちの間の失効も反映される）。
- ネイティブ呼び出しが返らない場合、executor がロック・スロット・ハンドル・ゲートを終了まで保持する。その間の他の DDC/CI 呼び出しはタイムアウトする（何も変更しない）。サーバーの停止・再起動では解放されず、解放されるのは呼び出しの終了かプロセス終了。

### 値と検証

- 公開値 `level` (0〜100) → ネイティブ値は `min + round(level × (max − min) / 100)`。離散値（WMI）は最も近い対応値（同距離は小さい方）。結果の `effective_level` が実効目標。
- `max == min` は `NOT_SUPPORTED`、`max < min`・現在値が範囲外・空の離散一覧は `OS_API_ERROR`。いずれも書き込まない。
- no-op と検証はネイティブ値で比較する。読み戻しは最大 5 回・100 ms 間隔（読み取りだけを再試行。書き込みは 1 回）。
- 書き込み API の失敗、読み戻し不能、キャンセル、想定外の例外は `unknown`。読み戻せて不一致なら `VERIFICATION_FAILED`。

### 一括（`device: "all"`）と結果

- 対象は解決時の全アクティブ表示（ID で重複排除、最大 8。超過は開始前に `INVALID_ARGUMENT`、0 件は `TARGET_NOT_FOUND`）。後から接続した機器は加えない。
- 単一指定と同じ実行経路。順に 1 台ずつ処理する。
- **続行するのは対象の状態が分かっている失敗だけ**: 書き込み前の失敗（非対応・曖昧・切断・読み取り失敗・範囲不正）と、読み戻せた上での不一致。**中止するのは**書き込み後の結果不明、後続書き込みの認可拒否、キャンセル、タイムアウト。
- 集計: 結果不明が 1 つでもあれば `unknown`。全対象が `changed`/`unchanged`（かつ未識別 0）なら `success`/`verified`。`changed` が 1 つ以上あり未達があれば `partial`。それ以外は `error`（不一致で値だけ動いた場合は `devices[].changed = true` で分かる）。
- 初回書き込みの認可拒否は `denied`（何も書いていないので `data` なし）。
- タイムアウト・キャンセルは、開始後なら executor が `unknown` にし、直前に保存した進捗（書き込み中の対象は `unknown`、以降は `not_attempted`）を返す。開始後の読み取り中に切れた場合も全体は `unknown` になる（Phase 1B-EXP の共通契約）。

### タイムアウト

| ツール | 秒 | 根拠 |
|---|---:|---|
| display_list_devices / display_list_modes | 5 | モニターに問い合わせない。実測 10 ms 未満 |
| display_get_state | 10 | DDC/CI 読み取り 2 回。実測各 52〜62 ms |
| display_set_contrast | 10 | 単一対象（spec §10 の DDC/CI 暫定値） |
| display_set_brightness | 20 | 最大 8 台。1 台あたり読み取り・書き込み・読み戻し（実測と公式値で 0.2 秒程度、遅延反映や能力照会で数秒）を見込んだ全体予算。単一指定も同じ値になる（ツール単位の設定のため。spec の 10 秒からの差分） |

### 共通基盤の変更（最小限）

- `ToolOutcome.Error<TData>(data, code, message)` を追加し、executor は対象別の結果を持つ `error` をそのまま返す（監査は `error`）。`partial` / `unknown` が開始済みを要求する規則は変えていない。
- 管理コンソールの「動作確認」は、必須引数のあるツール（`display_list_modes`）では無効にする（引数なしで実行するため）。
- 認証・認可・監査・要求予算・オーディオの JSON 契約は変更していない。

## 3. JSON 契約

すべて spec §9 の封筒。`device` は正規 ID か別名（省略時はプライマリ）。null・空文字・制御文字・未知の引数・`default` は拒否し、`all` は `display_set_brightness` だけ（綴りは小文字の `all` のみ）。

| ツール | 引数 | `data` |
|---|---|---|
| display_list_devices | なし | `devices[]` { `id`, `name`, `state`("active"), `kind`(internal/external/virtual/unknown), `connection`, `is_primary`, `is_cloned`, `current_mode`?, `aliases[]`, `brightness`{`support`,`error_code`}, `contrast`{同}, `operations[]`, `error_code` }, `total_count`, `unidentified_count`, `truncated`（最大 32 件） |
| display_get_state | `device?` | `device_id`, `name`, `state`, `kind`, `connection`, `is_primary`, `is_cloned`, `current_mode`, `brightness` / `contrast` { `support`, `level`, `native_range`{`min`,`max`}, `method`(ddc_ci/wmi), `error_code` } |
| display_list_modes | `device`（必須） | `device_id`, `name`, `modes[]` { `mode_id`, `width`, `height`, `refresh_hz`, `bits_per_pixel`, `interlaced`, `orientation`, `fixed_output`, `is_current` }, `total_count`, `truncated`（最大 512 件。現在モードは必ず含める） |
| display_set_brightness / display_set_contrast | `level`(0〜100, 必須), `device?` | `requested_level`, `devices[]` { `device_id`, `name`, `status`(changed/unchanged/failed/unknown/not_attempted), `effective_level`, `previous_level`, `current_level`, `changed`, `native_range`, `error_code` }, `unidentified_displays` |

- `support`: `supported`（いま読めた）/ `not_supported`（明確な非対応）/ `unavailable`（一時的な失敗・曖昧）/ `unchecked`（未問い合わせ）。読めない値は null（0 にしない）。
- `current_mode`: `width`, `height`（デスクトップ解像度。パネルのネイティブ解像度ではない）, `refresh_numerator`, `refresh_denominator`。
- `operations` は読み取りツールだけ。変更の対応可否は問い合わせるまで分からないため含めない。
- `mode_id` = `mode-` + SHA-256(表示 ID と全属性) の先頭 96 bit。表示 ID が変わる・属性が変わると無効。列挙されたモードは適用できる保証ではなく、Phase 1C-2 では適用前に再列挙して検証する。
- 監査に残るのは `level` と `target`（正規 ID か `all`）だけ。

## 4. 検証結果

2026-11-26。

| 環境 | 結果 |
|---|---|
| WSL2 (Linux) `dotnet build -c Release` | 警告 0・エラー 0 |
| WSL2 `dotnet test -c Release` | Core 304 / Mcp 86 / Infrastructure 50 成功、Windows.Tests 21 成功・14 スキップ（Windows 専用）。App.Tests は対象外 |
| 開発機 Windows 11 (build 26300) `dotnet.exe test`（`SYSACTA_HARDWARE_TESTS` 未設定） | Core 304 / Mcp 86 / Infrastructure 50 / Windows 29 成功・6 スキップ（状態変更）。App 19 成功・1 スキップ・**1 失敗**: `Concurrent_start_and_settings_save_end_consistent` が、起動中の別の Sysacta が使うポート 8765 に衝突した（本変更と無関係の環境要因） |
| 画面画像（`SYSACTA_SCREENSHOT_DIR`） | `devices-display-light/dark.png` を目視確認（100% 相当）。切れ・重なりなし |
| Windows CI（build / test / smoke） | PR のチェックを参照 |

## 5. 機種別の確認状況（読み取りのみ）

環境: Windows 11 Home build 26300、NVIDIA GeForce RTX 5070 Ti、通常権限。拡張表示（複製なし）。モニター側の DDC/CI 設定値は OSD で未確認（読み取りは成功）。

| モニター | 接続 | 識別 | モード列挙 | 輝度読み取り | コントラスト読み取り | 書き込み |
|---|---|---|---|---|---|---|
| DELL S2422HG | DisplayPort | 一意・プライマリ | 166 件, 約 9 ms（現在 1920×1080 165 Hz、164.997 Hz） | DDC/CI, 0〜100, 約 53 ms | DDC/CI, 0〜100, 約 52 ms | 未検証 |
| DELL P2314H | HDMI | 一意 | 27 件, 約 1 ms（現在 1920×1080 60 Hz） | DDC/CI, 0〜100, 約 60 ms | DDC/CI, 0〜100, 約 62 ms | 未検証 |
| 内蔵パネル | — | **未検証**（機器なし）。一致しない WMI インスタンスを指定すると `NOT_SUPPORTED` になることだけ確認 | | | | 未検証 |

未検証: 複製表示、ドック/MST、仮想ディスプレイ、DDC/CI 無効・非対応のモニター、ホットプラグ、リモートセッション、WMI の読み書き、実機での書き込みと反映遅延、実 MCP クライアント経由の呼び出し。2 台の結果は他の機器の対応を意味しない。

## 6. 残存リスク

- 書き込み直前のトポロジー再確認とネイティブ書き込みの間のホットプラグは排他できない（ハンドルは開いた物理モニターに固定され、消失時は失敗する想定。実機未検証）。
- 物理ハンドルは GDI source 名経由で取得する。OS が同じ source 名を別のモニターへ付け替える瞬間は再確認で大半を検出するが、完全ではない。
- ハードウェア/ドライバ呼び出しが返らないと、以後の DDC/CI 操作はプロセス終了まで使えない。管理画面の詳細読み取りも 1 件ずつのため「応答していません」のままになる。
- 反映が 400 ms より遅いモニターや値を丸めるモニターは `VERIFICATION_FAILED` になり得る（書き込みは繰り返さない）。
- 外部アプリや OSD、自動輝度による同時変更は排他できない。
- ポート変更で ID が変わり、別名の再紐付けが必要。
- 8 台を超える環境では `all` が使えない。遅いモニターが多いと 20 秒の予算で `unknown` になり得る。
- 既存事項（本 PR では変更していない）: MCP の結果に出力スキーマ未記載の `is_success` が含まれる（`ToolResult.IsSuccess` が直列化される）。オーディオの既定役割ロックの残課題も別タスク。

## 7. Phase 1C-2 への引き継ぎ

- `display:device:{id}` のロックと `WindowsDisplays` のゲートを電源・入力切替でも共有する。
- モード適用前に `ListModes` を再実行し、`mode_id` を現在の列挙と照合する。適用の試行（`CDS_TEST`）と復旧手段は未設計。
- 複製表示と 1 source 複数物理モニターの扱いは現状「拒否」。緩める場合は確実な対応付けの根拠が必要。
