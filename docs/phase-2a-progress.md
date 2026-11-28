# Phase 2A アプリ・プロセス管理 — 進捗と判断

対象: [phase-2a.md](phase-2a.md)、spec §4 / §7.6 / §8〜§11。ツールは `app_list_available` / `app_list_running` / `app_launch` / `app_close` / `process_list_running` / `process_terminate` の 6 つと、管理画面の「アプリ」ページ。

## 状態

| 項目 | 状態 |
|---|---|
| 実装 | 完了((A) 登録・検出・起動と一覧、(B) 通常終了・プロセス一覧・強制終了を別コミット) |
| Linux/WSL の Release build / test | 成功(警告・エラー 0)。Windows 専用テストはスキップ、App.Tests は対象外 |
| Windows CI(build / test / smoke) | <!-- CI --> |
| 専用テストプロセスによる起動・検出・強制終了 | Windows CI で実行(テスト用の `HarmlessTarget` だけ) |
| 実在のアプリの起動・通常終了・強制終了 | 未実施(利用者の個別承認が必要。[manual-tests.md](manual-tests.md) A1〜A11) |
| MSIX/UWP・複数ユーザー・管理者起動 | 未検証(環境なし。パッケージアプリは対象外) |

## 技術調査と API の採否(2026-11-28)

| 用途 | 採用 | 不採用・理由 |
|---|---|---|
| 候補の検出 | `Environment.SpecialFolder.Programs` / `CommonPrograms` の列挙、`IShellLinkW::GetPath(SLGP_RAWPATH)` / `GetArguments` を `IPersistFile::Load(STGM_READ)` 後に読むだけ(既存の `ShellShortcut` を再利用、専用 STA スレッド) | `IShellLink::Resolve`(リンク追跡・探索・ネットワークアクセスが起こり得る)、`shell:AppsFolder` / パッケージアプリの列挙(起動権限・固定引数・同一性・終了対象を保証できないため Phase 2A では対象外) |
| 実行ファイルの検証 | `CreateFileW`(`FILE_FLAG_OPEN_REPARSE_POINT`、フォルダーは `FILE_FLAG_BACKUP_SEMANTICS`)、`GetFileInformationByHandle`(属性・サイズ)、`GetFileInformationByHandleEx(FileIdInfo)`(ボリューム + 128 ビットのファイル ID)、`GetFinalPathNameByHandleW`、`GetDriveTypeW`、PE ヘッダーの解析(Core)、`IncrementalHash`(SHA-256、`RandomAccess.Read` で同じハンドルから)、`FileVersionInfo`(元のファイル名、補助) | `WinVerifyTrust`(署名は承認済み実体との同一性を示さない。未署名の正規アプリも多い。将来の補助候補) |
| 起動 | `CreateProcessW`(`lpApplicationName` 明示、`bInheritHandles=FALSE`、`CREATE_SUSPENDED` → `QueryFullProcessImageNameW` で確認 → `ResumeThread`) | `Process.Start` / `ProcessStartInfo`(`UseShellExecute=false` でも `lpApplicationName` を渡さずコマンドラインから解釈し、ハンドル継承の制御も限られる)、`ShellExecute`・verb・`runas` |
| プロセス一覧 | `CreateToolhelp32Snapshot` / `Process32NextW`(pid・親 pid・イメージ名)、`ProcessIdToSessionId`、`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`、`GetProcessTimes`、`QueryFullProcessImageNameW` | `System.Diagnostics.Process`(プロセスごとのオブジェクト生成と例外が多く、生成時刻がローカル時刻の `DateTime`) |
| 保護判定 | `OpenProcessToken(TOKEN_QUERY)` + `GetTokenInformation(TokenUser / TokenIntegrityLevel / TokenElevation)`、`IsProcessCritical`、`GetProcessInformation(ProcessProtectionLevelInfo)` | プロセス名の denylist だけによる判定(補助にもしない) |
| 通常終了 | `EnumWindows` + `GetWindowThreadProcessId` / `IsWindowVisible` / `GetWindow(GW_OWNER)` / `GWL_EXSTYLE`、`PostMessageW(WM_CLOSE)` | `SendMessage(WM_CLOSE)`(保存確認やハングで無期限に待つ)、`SendMessageTimeout`(タイムアウトで受理不明になる)、`Process.CloseMainWindow`(メインウィンドウの推測) |
| 強制終了・確認 | 検証したハンドルへの `TerminateProcess`(固定の終了コード 1)、`WaitForSingleObject`(最大 2 秒) | pid を引き直す `Process.Kill`、プロセスツリーの終了 |

追加の NuGet 依存はない(すべて Windows SDK の文書化された API、DLL は `DefaultDllImportSearchPaths(System32)`)。新しいテスト専用プロジェクト `tests/HarmlessTarget`(60 秒待って終了するだけのコンソールアプリ)は本番配布に含まれない。

### 参照(2026-11-28 に確認)

- CreateProcessW(`lpApplicationName`、検索順序、`bInheritHandles`、`CREATE_SUSPENDED`): https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
- コマンドライン引数の解析規則: https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments 、CommandLineToArgvW: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw
- CreateFileW(共有モード、`FILE_FLAG_OPEN_REPARSE_POINT`、`FILE_FLAG_BACKUP_SEMANTICS`): https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
- 共有モードとアクセスの照合: https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files
- FILE_ID_INFO: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info 、GetFinalPathNameByHandleW: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
- PE 形式: https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
- OpenProcess / プロセスのアクセス権: https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights
- GetProcessTimes: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
- IsProcessCritical: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-isprocesscritical
- GetProcessInformation / PROCESS_PROTECTION_LEVEL_INFORMATION: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation
- 必須の integrity レベル: https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control
- TerminateProcess(同じハンドル、非同期、`WaitForSingleObject` での確認): https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess
- WM_CLOSE: https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close 、PostMessageW: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew
- Tool Help(CreateToolhelp32Snapshot / PROCESSENTRY32W): https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/
- IShellLinkW / IPersistFile::Load: https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw

## DB の設計(マイグレーション v3)

既存の `registered_apps (id, display_name, launch_target, fixed_args, allowed)` に列を追加した(出荷済みのマイグレーションは変更していない)。

| 列 | 内容 |
|---|---|
| `kind` | 現在の形式は `win32_exe`。NULL(v2 以前の行)は形式不明として扱い、解決しない |
| `generation` | 承認の世代。登録時 1、再承認ごとに +1 |
| `volume_serial` / `file_id` / `file_size` / `sha256` | 承認した実行ファイルの実体(16 進小文字) |
| `launch_target` | 承認時のハンドルの最終パス(ローカル表示用・起動時の照合用) |
| `fixed_args` | `kind = 'win32_exe'` のときは文字列の JSON 配列 |

- v3 は既存の全行を `allowed = 0` にする(旧形式の行を自動で有効化しない)。旧形式・破損した行(JSON・ID・パス・ハッシュの形式不正、世代 0、未知の種別)は `ListAllowed` にも開始点にも現れず、画面では「旧形式または読み取れない登録」として削除だけができる。
- 一意索引 `(volume_serial, file_id) WHERE kind IS NOT NULL` で、同じ実行ファイル(別表記・ハードリンクを含む)を複数の ID に登録してロックや許可を分割できないようにした。
- ID は `app_` + `Guid.NewGuid()`(122 ビットの乱数)。削除後の再利用は実質的に起こらない(墓標表は持たない)。登録数の上限は 256。

## 許可リストの承認・再承認・失効

- 登録(候補からでも手動でも)は**許可オフ**で作られる。許可は一覧のスイッチでだけオンにする。検出しただけでは DB に書かない。
- 再承認は同じパスの実行ファイルを再検査(ハッシュを含む)して実体と固定引数を更新し、世代を上げ、**許可をオフに戻す**。別のパスのファイルは新しい ID として登録する。表示名の変更機能は設けない(セキュリティ上の意味がなく、必要になってから)。
- **無効化は世代も上げる**(セキュリティレビューの指摘)。許可をオフにしてすぐオンに戻しても、オフにする前に解決して待機していた呼び出しは開始点で拒否される。オンにする操作は世代を変えない。
- 許可・無効化・再承認・削除はすべて `AuthorizationGate.BeginUpdate()` の中で DB にコミットする。画面の操作は読み取った世代を渡し、別の変更が先に入っていれば拒否する(楽観的な照合)。ハッシュ計算と OS の検査はゲートの外で先に行う。

## 許可リストと最終認可の原子的な照合

- `ToolExecution.BeginSideEffect(Func<ToolError?> targetCheck)` を追加した(既存の `BeginSideEffect()` はこれに `null` を渡す)。`targetCheck` は既存の認可評価の直後に、同じ `AuthorizationGate` の読み取り側で評価される。拒否なら `SideEffectRefusedException` として既存の経路で denied になり、開始済みにならない。全体・クライアント認可、放棄状態、電源遷移の開始通知の意味は変えていない。
- アプリ系の 3 つの状態変更ツールは `AppTargets.StillCurrent`(`IAppRegistry.IsCurrent(id, generation)`、DB の 1 回の読み取り)を渡す。開始点より前に確定した無効化・削除・再承認は必ず反映され、評価中に割り込めない。開始点の後の変更はその呼び出しに適用されない(spec §6)。
- ロック取得後にも `AppTargets.Recheck` で解決時と同じ世代を確認し(`TARGET_CHANGED`)、待機中に再承認されたアプリを新しい実体・固定引数で起動しない。

## 実体の固定と TOCTOU

起動直前の手順(`WindowsAppPlatform.PrepareLaunch` → 開始点 → `Start`):

1. 文字列の規則(`ExecutablePolicy.CheckPath`)とドライブ種別(`DRIVE_FIXED`)。
2. ルート直下から親までの各フォルダーを `FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES`、共有 `READ | WRITE`(**DELETE なし**)、`OPEN_REPARSE_POINT` で開き、ディレクトリであり再解析ポイントでないことを確認。データアクセス権を含めるのは、属性だけのオープンでは共有モードが記録されず名前変更を防げないため。
3. ファイルを `GENERIC_READ`、共有 `READ` だけ(**WRITE・DELETE なし**)で開き、再解析ポイント・ディレクトリでないこと、最終パスが登録時のパスと一致すること、保護された場所でないこと、サイズ上限、PE ヘッダー、自身の exe でないこと、バージョンリソースの名前を確認。
4. ボリューム + ファイル ID + サイズが承認と一致し、同じハンドルで読んだ全内容の SHA-256 が一致すること。
5. ハンドルを保持したまま開始点 → `CreateProcessW` → 中断状態のプロセスのイメージパスが検証済みのパスと一致することを確認 → 再開 → ハンドルを解放。

防げるもの: 検証後のファイルの書き換え(書き込み共有なし。ハードリンク経由でも同じファイルオブジェクト)、ファイルの名前変更・削除・同名ファイルへの差し替え(削除共有なし)、親フォルダーの名前変更・置換によるパスの付け替え(各フォルダーを削除共有なしで保持)、ジャンクション/シンボリックリンクの挿入(再解析ポイントを辿らず拒否し、名前変更もできない)。

防げないもの・限界:

- 同じユーザーのプロセスが自分のログオンセッションの DOS デバイス(`DefineDosDevice` によるドライブ文字)を再定義すること。同一ユーザー権限による改変は脅威モデルの範囲外(spec §6)だが、記録しておく。
- 起動後にアプリ自身が読み込む DLL・設定ファイル・子プロセスは固定しない(登録したアプリ自体の機能は制限できない)。
- 一覧(`app_list_available`)の状態はハッシュを計算しない安価な確認(ボリューム・ファイル ID・サイズ・最終パス・規則)で、内容の一致は起動時にだけ確かめる。
- 実行中プロセスとアプリの照合は、`QueryFullProcessImageNameW(PROCESS_NAME_NATIVE)` が返す NT パス(プロセス生成時の名前)を再解析ポイントを辿らずに開いたファイル ID と、承認したファイル ID の一致で行う(そのパスにリンクがあれば「不明」で照合しない)。プロセス生成後に元のファイルの名前を変え、同じパスに承認済みのファイルを移すと、別のプロセスが承認済みアプリに見える(同一ユーザーの操作が必要。終了されるのはそのユーザーのプロセス)。逆に、更新で元のファイルが置き換えられた実行中インスタンスは照合できず、起動・通常終了は「状態不明」で拒否される。名前の事前フィルター(イメージ名が登録ファイル名と同じプロセスだけ開く)のため、別名のハードリンクから起動されたプロセスは照合されない(安全側)。
- 起動時の照合はパスの大文字小文字を区別しない(NTFS の通常の設定)。フォルダー単位の大文字小文字区別を有効にした環境は想定していない。
- `~` と `%` を含むパスは一律に拒否する(8.3 の別名・環境変数との混同を避けるため。その文字を含む正規のフォルダー名も登録できない)。
- 実行ファイルは 512 MiB まで(起動ごとに全体をハッシュするため)。
- **ハッシュの対象は exe だけ**。同じフォルダーの DLL(既定の検索順序)・`app.exe.config`・`app.exe.manifest`・`.local` によるリダイレクトは固定しない。ユーザー単位でインストールされたアプリ(`%LOCALAPPDATA%` 配下など)はフォルダー自体が同じユーザーから書き込めるため、実際に実行されるコードが再承認なしに変わり得る(同一ユーザー権限の改変。脅威モデルの範囲外だが限界として記録)。
- 子プロセスのイメージの確認は、保持しているファイルハンドルの NT パス(`GetFinalPathNameByHandleW(VOLUME_NAME_NT)`)と、中断状態のプロセスの NT イメージ名(`QueryFullProcessImageNameW(PROCESS_NAME_NATIVE)`)の比較で行う(ドライブ文字の割り当てに依存しない。コードレビューの指摘で文字列の DOS パス比較から変更)。

## 固定引数

- 構造化した文字列の配列で保存する(最大 16 個、各 256 UTF-16 コード単位、合計 2048。制御文字・書式文字・不正なサロゲートを拒否。空文字は可)。画面では 1 行に 1 つ入力し、空白で分割・引用符や変数の解釈をしない。
- コマンドラインは `"<検証済みパス>"` に続けて、各引数を CommandLineToArgvW / MSVC の規則で引用して連結する(空・空白・タブ・引用符・引用符の前のバックスラッシュ・末尾のバックスラッシュを処理)。Core のテストで独自の参照パーサーと、Windows のテストで実際の `CommandLineToArgvW` と往復一致を確認する。
- MCP の入力・表示名・filter・パス・環境変数はコマンドラインに入らない。応答と監査に引数を出さない。アプリ独自の引数解釈(例: ファイル名を受け取る)は制限できないため、固定引数は利用者の信頼判断として承認する。

## 起動の安全性

- 検査は登録時と起動前の両方で同じ(`Inspect` と `PrepareLaunch` は同じ `Open` を使う)。
- 禁止: 相対パス・PATH 探索・環境変数展開・UNC/ネットワーク・デバイスパス・ADS・フォルダー・`.exe` 以外(`.lnk/.url/.bat/.cmd/.ps1/.vbs/.js/.msi/.dll` 等)、Windows フォルダー配下、`WindowsApps`(パッケージアプリと実行エイリアス)、Sysacta 自身のフォルダー・ファイル、シェル・スクリプトホスト・インタープリター・汎用ランタイム・インストーラー・更新ツール・管理ツール(名前とバージョンリソース。補助であり境界ではない)。
- Windows フォルダー配下を一律に拒否したことで、`cmd` / `powershell` / `wscript` / `mshta` / `rundll32` / `regsvr32` / `msiexec` / `explorer` などの OS 付属の起動手段は名前の一覧に頼らず登録できない。
- 昇格: Sysacta が昇格中は `app_launch` を非公開・直接呼び出しも拒否(通常権限の子プロセスを確実に作る方式は立証していないため採用しない)。昇格が必要な exe は CreateProcess が `ERROR_ELEVATION_REQUIRED` を返し、そのまま `NOT_SUPPORTED`(何も起動していない)。自動昇格しない。
- 既に起動している場合: 承認済みファイルをイメージとするプロセスがあれば `already_running=true` の no-op(verified、ウィンドウ操作なし)。同名のプロセスのイメージを確認できなければ起動しない(`OS_API_ERROR`、retryable)。
- 開始点の直後に `CreateProcessW` を 1 回だけ呼ぶ。自動リトライ・フォールバックなし。受理は accepted(アプリの準備完了・常駐は保証しない)。開始点前のキャンセル・タイムアウトでは何も起動しない(放棄された呼び出しは開始点で拒否される)。

## プロセス保護と終了

- セッションの確認は pid で `ProcessIdToSessionId` を呼んでからハンドルを開くが、続けて同じハンドルで生成時刻を照合するため、確認した pid は開いたプロセスと同じ(ハンドルを開いている間 pid は再利用されず、生成時刻が一致すればその間ずっと同じプロセスだった)。
- 一覧の範囲: 現在のセッションのプロセスのうち、トークンのユーザー SID が Sysacta と同じもの。他ユーザー(同じセッションの `dwm` 等を含む)は出さない。ユーザー・生成時刻を読めないプロセスは出さず `failed_count` に数える(SYSTEM のプロセスなど、通常いくつかある)。スナップショットの内部上限は 4096。
- 終了可否(`ProcessRules.Evaluate`、Core): 自己 → `self`、起動元(親 pid、かつ生成時刻が Sysacta より前)→ `sysacta_parent`、他ユーザー、保護レベルあり → `protected`、critical、integrity が high 以上 → `elevated`、medium 未満 → `low_integrity`、承認済みファイルでない → `not_registered`、読めない事実がある → `unverified`。
- 強制終了の手順: 列挙で pid + 生成時刻(+ 照合した app_id)を固定 → ロック(`app:{id}`、`process:{pid}:{FILETIME}`)→ 世代の再確認 → 最小権限でプロセスを開き生成時刻・終了状態・すべての事実を再検証 → 開始点(許可・世代の照合)→ 同じハンドルに `TerminateProcess` 1 回 → 同じハンドルで最大 2 秒待つ(verified / accepted)。pid が再利用されていれば `TARGET_CHANGED`、消えていれば `TARGET_NOT_FOUND`。子プロセス・他のインスタンスは終了しない。
- 通常終了: 解決時にプロセスが 1 件であること(0 件は was_running=false の no-op、複数は `AMBIGUOUS_TARGET`)。ロック後にハンドルを開いて再検証し、可視・所有者なし・ツールウィンドウでないトップレベルウィンドウがちょうど 1 つのときだけ、投函の直前にそのウィンドウがまだそのプロセスのものかを確認して `WM_CLOSE` を投函する(ハンドルを開いている間 pid は再利用されない)。保存確認・拒否・遅延はアプリの正常な挙動で、ダイアログを操作しない。投函後 2 秒以内に同じハンドルが終了を示したときだけ verified。強制終了へフォールバックしない。
- ウィンドウハンドルの限界: 最終確認から `PostMessageW` までの間にウィンドウが破棄され、同じハンドル値が同じプロセスの別のウィンドウに再割り当てされる可能性は排除できない(ハンドル値の再利用カウンターにより極めてまれ。送るのは同じプロセスへの通常の閉じる要求)。

## JSON 契約

| ツール | 引数 | 主な結果 |
|---|---|---|
| `app_list_available` | なし | `apps[] {app_id, display_name, kind:"win32_exe", status: ready/target_changed/target_missing/not_supported/check_failed, launchable}`, `count`, `truncated`, `launch_available`, `launch_blocked_reason: sysacta_elevated|null` |
| `app_list_running` | なし | `instances[] {app_id, display_name, pid, start_time, can_terminate, terminate_blocked_reason}`, `count`, `truncated`, `uncertain_app_ids[]`, `failed_count` |
| `app_launch` | `app_id`(`^app_[0-9a-f]{32}$`) | `{app_id, already_running, launched, pid, start_time}`。起動: accepted、既存: verified |
| `app_close` | `app_id` | `{app_id, was_running, pid, start_time, close_requested, exited}`。終了確認: verified、投函のみ: accepted |
| `process_list_running` | `limit?`(1〜256、既定 100)、`filter?`(1〜64 文字、制御文字不可、名前の大文字小文字を区別しない部分一致) | `processes[] {pid, start_time, name, app_id, can_terminate, terminate_blocked_reason}`, `total`, `truncated`, `failed_count` |
| `process_terminate` | `pid`(1〜2147483647)、`start_time`(`yyyy-MM-ddTHH:mm:ss.fffffffZ`、28 文字) | `{pid, start_time, app_id, terminated, exited}`。終了確認: verified、受理のみ: accepted |

- `start_time` は FILETIME(100 ns)をそのまま表す固定書式で、解析後に書式化し直して入力と完全一致する場合だけ受け付ける(精度違い・オフセット・全角数字などを拒否)。
- 明示 null・未知の引数・型違い・範囲外は executor の検証で `INVALID_ARGUMENT`(OS 呼び出しなし)。表示名とプロセス名は `UntrustedText` でパス・URI 形式を除き、出せなければ null。
- 既定: list 系 3 つと `app_launch` は On、`app_close` と `process_terminate` は Off(spec §7.6 の表どおり)。既存 DB への追加は 6 つとも無効、既存クライアントに付与しない。カテゴリは `apps`(「アプリ・プロセス」)。`ToolRegistry` の名前規則を、`apps` カテゴリだけ `app_` と `process_` の両方の接頭辞を許すように変更した(`ToolCategories.NamePrefixes`)。
- 監査: `target`(`app_id`、プロセスでは `app_id|pid|start_time`)、`process_terminate` の `pid`、`process_list_running` の `limit` だけ。
- タイムアウト: `app_launch` は 15 秒、ほかは既定の 5 秒。終了確認の待機は 2 秒。`app_launch` を延ばした根拠: 起動のたびに最大 512 MiB の exe 全体を SHA-256 で読む(ディスクとキャッシュの状態で 1 GB/s 未満になり得る)うえ、CreateProcess はウイルス対策ソフトのスキャンで数秒かかることがある。開始点の前のタイムアウトは「何も起動していない」として返る(放棄された呼び出しは開始点で拒否)ため、延長しても安全側。ハッシュはブロック(1 MiB)ごとにキャンセルを確認する。実在アプリでの実測は未実施。

## 管理画面(「アプリ」ページ)

- 「スタートメニューから検出」: ユーザーと全ユーザーのスタートメニューのプログラムフォルダー(ローカル固定ドライブのみ)。深さ 8、探索 2048 項目、候補 256 件、60 秒。再解析ポイントのフォルダーに入らない。ショートカットは読むだけ。`.exe` を指すものだけを候補にし、引数付きは引数を取り込まない(必要なら 1 行に 1 つ手で入力)。
- 「実行ファイルを選んで登録」: ファイル選択ダイアログ(`.exe`、ショートカットを辿らない)→ 表示名と固定引数の入力 → 検査(ハッシュ)→ 許可オフで登録。
- 一覧: 表示名・実際のパス・固定引数・ファイルの状態・許可スイッチ・再承認・削除(確認ダイアログ)。昇格中は警告を常時表示。パスと引数はこの画面のローカルの利用者にだけ表示する。
- 起動・終了の動作確認ボタンは設けない(実在のアプリを起動・終了させる UI を増やさないため。必要になれば executor 経由で追加する)。
- OS・DB・ハッシュは UI スレッドの外。入力中のフォームは再読み込みで上書きしない。許可スイッチは既存の `LatestValueSaver` で保存。

## テスト

| 場所 | 内容 |
|---|---|
| Core.Tests `AppPolicyTests` | パス規則(相対・UNC・デバイス・ADS・ドット・`~`・`%`・予約名・長さ)、拡張子、禁止プログラム(名前・バージョン名)、PE ヘッダー(偽 PE・DLL・ドライバー・未知の CPU/サブシステム・切り詰め)、argv の往復(空・空白・引用符・バックスラッシュ・日本語・注入)、開始時刻の可逆性と書式違いの拒否、固定引数・表示名・ID の制約、終了可否の規則 |
| Core.Tests `AppToolTests` / `ProcessToolTests` | 6 ツールの既定・厳格なスキーマ・追加引数の拒否(OS 呼び出し 0)、一覧の範囲・状態・秘匿、起動(accepted・既存・状態不明・未登録/無効・実体変更・各種失敗・開始後の例外)、準備中の無効化・削除・再承認・失効・再発行・全体無効・個別許可取り消しで起動/終了 0 回、待機中の再承認で新しい承認に切り替えない、準備中のキャンセル後に遅延起動しない、監査不能・電源遷移で OS 呼び出し 0、通常終了(1 件/複数/ウィンドウ 0・2/状態不明/保護/再利用/消失/ウィンドウ消失)、強制終了(pid 再利用の各時点・消失・保護/自己/親/critical/昇格/低 integrity/判定不能/未登録/他ユーザー/未許可・受理不明)、同一アプリのロック共有と別アプリの独立、ロックの解放 |
| Infrastructure.Tests `AppRegistryStoreTests` | v2 → v3 移行で旧形式の行を無効化・解決しない、登録は許可オフ、許可・世代・再承認で許可を引き継がない、古い世代の操作の拒否、同一ファイルの重複登録禁止、別パスの再承認拒否、不正な名前・パス・引数・識別情報、破損行、削除、登録数上限、書き込みがゲートを待つこと |
| Mcp.Tests `AppServerTests` | 新規/既存 DB の既定、MCP から登録・許可する手段がない、許可のないクライアント・全体無効・未許可アプリの直接呼び出しで OS 呼び出し 0、管理画面の無効化・再承認が準備中に確定した場合の拒否、パス・引数・ハッシュ・filter・入力 ID・例外文が応答・監査・CSV・JSON・DB/WAL に出ない |
| Windows.Tests `WindowsAppPlatformTests` | 構造体のサイズ、検査(最終パス・ファイル ID・ハッシュ)、偽 PE・DLL・Windows フォルダー・禁止プログラム・シンボリックリンクの拒否、準備中のファイル・親フォルダーのロックと解放、内容変更・差し替えの検出、`HarmlessTarget` の起動 → 照合 → 古い生成時刻の拒否 → 同じハンドルで強制終了 → 消失、自己の生成時刻、`CommandLineToArgvW` との往復、スタートメニュー検出(読み取りのみ) |
| App.Tests | 「アプリ」ページの読み込みとスクロール規則、スクリーンショット |

## レビュー(2026-11-28)

独立したコードレビュー(規約・不具合)と、仕様・セキュリティレビュー(任意実行・対象誤認・権限競合・情報漏洩)を行った。重大(high)な指摘はなかった。対応した指摘:

| 指摘 | 対応 |
|---|---|
| 許可をオフ→オンにすると、オフにする前に待機していた呼び出しが開始点を通る(中) | 無効化で世代を上げる。Infrastructure / Mcp のテストを追加 |
| 起動後のイメージ確認が DOS パスの文字列比較で、ドライブ文字の再定義を検出できない | NT パス同士の比較に変更 |
| プロセス照合でイメージのパスを再解析ポイントを辿って開いていた | NT パスを `FILE_FLAG_OPEN_REPARSE_POINT` で開き、リンクは「不明」に |
| 自プロセス情報の初期化失敗が `Lazy` にキャッシュされ、以後すべてのプロセス操作が失敗する / セッション取得の失敗を無視 | `LazyThreadSafetyMode.PublicationOnly`(次回再試行)、セッション取得の失敗は `OS_API_ERROR` |
| 管理画面で昇格判定(OS 呼び出し)を UI スレッドで行っていた / 項目キャッシュが再承認後に残る | 読み込みを `Task.Run` に移し、キャッシュを ID 単位にして消えた行を除く |
| 空白だけの filter を受け付ける | 拒否(`INVALID_ARGUMENT`) |
| 重複コード・古いコメント・意味のないフラグ(`CREATE_UNICODE_ENVIRONMENT`) | 整理 |

記録だけにしたもの(上の各節と残存リスクに記載): exe 以外の隣接ファイル、禁止一覧の不完全さ、ハードリンクを使った照合のすり替え(同一ユーザーのコードが必要)、タイムアウト 15 秒の根拠、`WinVerifyTrust` の不採用、SQLite の読み取りが開始点のゲート内で行われること(既存の認可評価と同じ。SQLite の待ちの間だけ権限変更が待たされる)。

## 実機確認

未実施。実在のアプリでの確認は [manual-tests.md](manual-tests.md) の A1〜A11 に従い、利用者が対象アプリと操作を個別に承認した場合だけ行う。

## 仕様との差分

- 名前規則: spec のツール名(`app_*` / `process_*`)を `apps` カテゴリのまま使うため、`ToolRegistry` の接頭辞の検査を広げた。
- パッケージアプリ(UWP/MSIX)は検出・登録とも対象外(起動・同一性・終了対象を保証できない。`WindowsApps` 配下を拒否)。
- `process_list_running` は CPU・メモリを返さない(必須ではなく、測定窓の設計が必要になるため)。
- 表示名の変更、登録アプリの動作確認ボタンは設けていない。

## 未対応事項と残存リスク

- 同一ユーザー権限のプロセスは DB を書き換えて許可リストを変えられる(既存と同じ。spec §6)。ドライブ文字の再定義も同様。
- 登録したアプリ自体の機能(ファイルを開く、URL を開く、他のプログラムを起動するなど)は制限できない。禁止プログラムの一覧は完全ではない(AutoHotkey・NirCmd などのスクリプト実行・ランチャー、Electron 系アプリやスクリプトエンジンを内蔵するアプリなど、汎用の実行手段になり得るアプリの判断は利用者の登録時の信頼判断と、承認した固定引数に依存する)。
- 実行中プロセスの照合はイメージパスのファイル ID による(上記の限界)。
- 通常終了のウィンドウハンドル再割り当ての競合(上記)。
- `can_terminate` は観測時点の参考情報で、強制終了時に独立して再検証する。
- 実在のアプリ・管理者起動・複数ユーザーでの挙動は未検証。
