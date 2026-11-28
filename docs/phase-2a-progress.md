# Phase 2A アプリ・プロセス管理 — 進捗と判断

対象: [phase-2a.md](phase-2a.md)、spec §4 / §7.6 / §8〜§11。ツールは `app_list_available` / `app_list_running` / `app_launch` / `app_close` / `process_list_running` / `process_terminate` の 6 つと、管理画面の「アプリ」ページ。

## 状態

| 項目 | 状態 |
|---|---|
| 実装 | 完了((A) 登録・検出・起動と一覧、(B) 通常終了・プロセス一覧・強制終了を別コミット) |
| Linux/WSL の Release build / test | 成功(警告・エラー 0)。Windows 専用テストはスキップ、App.Tests は対象外 |
| Windows CI(build / test / smoke) | 成功(2026-11-28、PR #21)。初回は Windows テストの前提誤り 1 件(.NET の「exe」アセンブリを DLL として扱った)で失敗し、テストを修正 |
| 専用テストプロセスによる起動・検出・強制終了 | Windows CI で成功(テスト用の `HarmlessTarget` だけ): 読み取り共有のみで保持したファイルからの `CreateProcessW`、保持中のファイル書き込み・名前変更・親フォルダー移動の拒否、内容変更・差し替えの検出、`FileVersionInfo` の読み取り、照合、古い生成時刻の拒否、同じハンドルでの強制終了と終了確認、シンボリックリンクの拒否、`CommandLineToArgvW` との往復 |
| 実機でのアダプター確認(専用テストプロセスのみ) | 成功(2026-11-28、開発 PC。manual-tests A0) |
| 実在のアプリの起動・通常終了・強制終了 | 確認(2026-11-28、利用者承認、`ImagingDevices.exe` で各 1 回、MCP HTTP 経由)。固定引数は専用テストプロセスで確認。保存確認の出るアプリは未確認 |
| 管理画面「アプリ」・外部 MCP クライアント・既存 DB の移行 | 確認(2026-11-28、利用者の操作と報告、manual-tests A1・A2・A10・A11・A12) |
| MSIX/UWP・複数ユーザー・管理者起動 | 未検証(環境なし。パッケージアプリは対象外) |
| 終了対象を Sysacta が起動したプロセスに限定(2026-11-29) | Core・MCP のテストと CI で確認。変更後の実在アプリでの再確認は未実施(上の実在アプリ確認は、起動 → 通常終了・強制終了とも Sysacta が起動したプロセスに対するもので、新しい方針の範囲内) |

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
5. 副作用のない準備(保持中のファイルの NT パス、コマンドライン)を開始点より前に済ませる。
6. ハンドルを保持したまま開始点 → `CreateProcessW(CREATE_SUSPENDED)` → 中断状態のプロセスのイメージが保持中のファイルであることを NT パスで確認 → **再開の直前の確認**(`BeginNextSideEffect(targetCheck)`: 取消・executor の切り離し(タイムアウト)・最終認可・登録の許可と世代)→ 再開 → ハンドルを解放。
7. イメージ不一致・再開前の拒否・再開失敗・例外のときは再開せず、**この呼び出しが生成したプロセスだけ**を同じハンドルで `TerminateProcess` し、最大 2 秒待って終了を確認する(回収。既存プロセスの終了とは別で、失効後でも行う)。回収を確認できれば `status: error`(何も実行されていない)、確認できなければ `status: unknown`(retryable=false)。

防げるもの: 検証後のファイルの書き換え(書き込み共有なし。ハードリンク経由でも同じファイルオブジェクト)、ファイルの名前変更・削除・同名ファイルへの差し替え(削除共有なし)、親フォルダーの名前変更・置換によるパスの付け替え(各フォルダーを削除共有なしで保持)、ジャンクション/シンボリックリンクの挿入(再解析ポイントを辿らず拒否し、名前変更もできない)。

防げないもの・限界:

- 同じユーザーのプロセスが自分のログオンセッションの DOS デバイス(`DefineDosDevice` によるドライブ文字)を再定義すること。同一ユーザー権限による改変は脅威モデルの範囲外(spec §6)だが、記録しておく。
- 起動後にアプリ自身が読み込む DLL・設定ファイル・子プロセスは固定しない(登録したアプリ自体の機能は制限できない)。
- 一覧(`app_list_available`)の状態はハッシュを計算しない安価な確認(ボリューム・ファイル ID・サイズ・最終パス・規則)で、内容の一致は起動時にだけ確かめる。
- **実行中プロセスとアプリの照合(PR レビュー P1 で変更)**: 制限付きのプロセスハンドルから、ロード済みイメージのファイル(ファイル ID)を特定する文書化された API はない。`QueryFullProcessImageNameW` が返すのはプロセス生成時のパス名で、実行中の exe は名前を変えられ、同じパスに別のファイルを置ける(通常のインプレース更新がまさにそれ)。そこで、そのパスにあるファイルを照合に使うのは、**そのファイルの ChangeTime がプロセスの生成時刻以前の場合だけ**とした。名前変更・移動・ハードリンク作成・書き込みはファイルの ChangeTime を更新するので、生成時刻以前から変わっていないファイルは、イメージがロードされた時点でそのパスのファイルだったことになる。条件を満たさなければ「不明」(`uncertain_app_ids`、app_id なし → 終了不可、起動は状態不明で拒否)。パスは NT パスで、再解析ポイントを辿らずに開く。旧イメージが動いたまま exe を置換・再承認した場合(新しいコピー、古いファイルの名前変更の両方)に照合しないことを Windows のテストで確認した(開発 PC・CI)。
  - 限界: 親フォルダーの名前変更はファイル自身の ChangeTime を変えないため、プロセス生成後にフォルダーごと入れ替えてパスを成立させると照合を欺ける(同一ユーザーのコードが必要。終了されるのはそのユーザーのプロセス)。親フォルダーの ChangeTime も条件にすると、アプリが自分のフォルダーに書き込むだけで照合できなくなるため採用しない。ChangeTime の更新はファイルシステム(NTFS)の挙動に依存する(名前変更での更新は開発 PC と CI で確認)。同じユーザーのコードは `SetFileInformationByHandle(FileBasicInfo)` で置き換えたファイルの ChangeTime を古い値に書き換えられる(同じ脅威)。システム時計がプロセス生成後に巻き戻ると、その後の置換でも条件を満たし得る。比較は厳密な「より前」(同じ時刻の刻み内の名前変更は不明)。
  - 可用性: アプリの実行中にファイルのメタデータが変わると(ブロック解除による Zone.Identifier の削除、ACL・属性の変更、ハードリンクの作成など)、そのインスタンスは終了するまで「不明」になり、起動と通常終了・強制終了は拒否される(安全側)。ChangeTime を持たないファイルシステムでは照合できない(固定ドライブの NTFS/ReFS 以外は想定しない)。名前の事前フィルター(イメージ名が登録ファイル名と同じプロセスだけ開く)のため、別名のハードリンクから起動されたプロセスは照合されない(安全側)。
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
- 開始点の直後に `CreateProcessW` を 1 回だけ呼ぶ。自動リトライ・フォールバックなし。受理は accepted(アプリの準備完了・常駐は保証しない)。開始点前のキャンセル・タイムアウトでは何も起動しない(放棄された呼び出しは開始点で拒否される)。開始点の後でも、再開前に取消・タイムアウト・失効・無効化・再承認が確定していれば、作成したプロセスは一度も実行されずに回収される(PR レビューで追加)。

## プロセス保護と終了

- セッションの確認は pid で `ProcessIdToSessionId` を呼んでからハンドルを開くが、続けて同じハンドルで生成時刻を照合するため、確認した pid は開いたプロセスと同じ(ハンドルを開いている間 pid は再利用されず、生成時刻が一致すればその間ずっと同じプロセスだった)。
- 一覧の範囲: 現在のセッションのプロセスのうち、トークンのユーザー SID が Sysacta と同じもの。他ユーザー(同じセッションの `dwm` 等を含む)は出さない。ユーザー・生成時刻を読めないプロセスは出さず `failed_count` に数える(SYSTEM のプロセスなど、通常いくつかある)。スナップショットの内部上限は 4096。上限で切り詰めた場合は全アプリを「不明」にし、起動と通常終了は拒否する。`Process32NextW` の失敗は正常な終端(`ERROR_NO_MORE_FILES`)と区別し、途中の失敗は列挙全体の失敗(`OS_API_ERROR`)にする(PR レビュー P2)。
- 終了可否(`ProcessRules.Evaluate`、Core): 自己 → `self`、起動元(親 pid、かつ生成時刻が Sysacta より前)→ `sysacta_parent`、他ユーザー、保護レベルあり → `protected`、critical、integrity が high 以上 → `elevated`、medium 未満 → `low_integrity`、アプリと関係ない → `not_registered`、アプリらしいが Sysacta が現在の承認で起動していない → `not_managed`、読めない事実がある → `unverified`。終了・通常終了できるのは管理対象(`managed`)だけ(上の「終了対象を Sysacta が起動したプロセスに限定」)。
- 強制終了の手順: 列挙で pid + 生成時刻(+ 照合した app_id)を固定 → ロック(`app:{id}`、`process:{pid}:{FILETIME}`)→ 世代の再確認 → 最小権限でプロセスを開き生成時刻・終了状態・すべての事実を再検証 → 開始点(許可・世代の照合)→ 同じハンドルに `TerminateProcess` 1 回 → 同じハンドルで最大 2 秒待つ(verified / accepted)。pid が再利用されていれば `TARGET_CHANGED`、消えていれば `TARGET_NOT_FOUND`。子プロセス・他のインスタンスは終了しない。
- 通常終了: 解決時にプロセスが 1 件であること(0 件は was_running=false の no-op、複数は `AMBIGUOUS_TARGET`)。**ロック取得後に再列挙し**、解決時と同じ pid・生成時刻のインスタンスが唯一であることを確かめる(追加起動は `AMBIGUOUS_TARGET`、置き換わりは `TARGET_CHANGED`、消失は `TARGET_NOT_FOUND`、不明・切り詰めは `OS_API_ERROR`。PR レビューで追加)。そのうえでハンドルを開いて再検証し、可視・所有者なし・ツールウィンドウでないトップレベルウィンドウがちょうど 1 つのときだけ、投函の直前にそのウィンドウがまだそのプロセスのものかを確認して `WM_CLOSE` を投函する(ハンドルを開いている間 pid は再利用されない)。保存確認・拒否・遅延はアプリの正常な挙動で、ダイアログを操作しない。投函後 2 秒以内に同じハンドルが終了を示したときだけ verified。強制終了へフォールバックしない。
- ウィンドウハンドルの限界: 最終確認から `PostMessageW` までの間にウィンドウが破棄され、同じハンドル値が同じプロセスの別のウィンドウに再割り当てされる可能性は排除できない(ハンドル値の再利用カウンターにより極めてまれ。送るのは同じプロセスへの通常の閉じる要求)。

## JSON 契約

| ツール | 引数 | 主な結果 |
|---|---|---|
| `app_list_available` | なし | `apps[] {app_id, display_name, kind:"win32_exe", status: ready/target_changed/target_missing/not_supported/check_failed, launchable}`, `count`, `truncated`, `launch_available`, `launch_blocked_reason: sysacta_elevated|null` |
| `app_list_running` | なし | `instances[] {app_id, display_name, app_match: managed/estimated, pid, start_time, can_terminate, terminate_blocked_reason}`, `count`, `truncated`, `uncertain_app_ids[]`, `failed_count` |
| `app_launch` | `app_id`(`^app_[0-9a-f]{32}$`) | `{app_id, already_running, launched, pid, start_time}`。起動: accepted(生成したプロセスを管理対象に記録)、管理対象が稼働中: verified の no-op、推定のインスタンスだけ: `AMBIGUOUS_TARGET` |
| `app_close` | `app_id` | `{app_id, was_running, pid, start_time, close_requested, exited}`。終了確認: verified、投函のみ: accepted |
| `process_list_running` | `limit?`(1〜256、既定 100)、`filter?`(1〜64 文字、制御文字不可、名前の大文字小文字を区別しない部分一致) | `processes[] {pid, start_time, name, app_id, app_match, can_terminate, terminate_blocked_reason}`, `total`, `truncated`, `failed_count` |
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

修正後の再レビューでは不具合はなく、低リスクの 2 点を追加で対応した: 自身の実行ファイルの識別に失敗した状態をキャッシュしない(次回再試行)、アダプターの `ToolFailureException` を管理画面で固定の日本語文言にする。残した低リスク: 管理画面で、許可の保存と同時に走った一覧の再読み込みが古い値を表示し得る(次の操作が「変更されました」で拒否されて再読み込みされるだけで、誤った許可にはならない)。

記録だけにしたもの(上の各節と残存リスクに記載): exe 以外の隣接ファイル、禁止一覧の不完全さ、ハードリンクを使った照合のすり替え(同一ユーザーのコードが必要)、タイムアウト 15 秒の根拠、`WinVerifyTrust` の不採用、SQLite の読み取りが開始点のゲート内で行われること(既存の認可評価と同じ。SQLite の待ちの間だけ権限変更が待たされる)。

## 終了対象を Sysacta が起動したプロセスに限定(PR 再レビュー、2026-11-28)

再レビューで、ChangeTime による照合は通常のファイル置換は検出できても、親フォルダーごとの置換・時計の補正など通常の操作でも外れ得るため、破壊的操作の許可境界にしないよう求められた。制限付きのプロセスハンドルからロード済みイメージのファイルを証明する文書化された API は見つからないため、次の方式に変更した。

- **管理対象(`managed`)**: Sysacta が `app_launch` で承認済みの exe から `CREATE_SUSPENDED` で生成し、再開前に保持中のファイルと同じイメージであることを確認したプロセスだけを、プロセス内の有限な表(`ManagedProcesses`、最大 256 件、古いものから外す)に pid + 正確な生成時刻 + 登録 ID + 承認の世代で記録する。終了済みの項目は一覧のたびに除く(完全な列挙のときだけ。列挙を始める前に追加された項目だけが対象で、列挙中に起動したプロセスは除かない)。列挙で一度でも検査できなかった管理対象は表から外れ、以後は推定扱いになる(安全側)。永続化しない。
- `app_close` / `process_terminate` は管理対象で、そのアプリが今も同じ世代で許可されているものだけを対象にする。終了直前の同一ハンドルによる生成時刻・保護・integrity の検証、最終認可、ロックはそのまま。
- **推定(`estimated`)**: イメージのパスにある承認済みファイル(ChangeTime の条件付き)との一致は、一覧の表示(`app_match: "estimated"`、`terminate_blocked_reason: "not_managed"`)と、`app_launch` が「起動済みかもしれない」と判断して起動しない側にだけ使い、終了の許可には使わない。
- `app_launch`: 管理対象のインスタンスが動いていれば no-op(verified)。推定のインスタンスしかなければ `AMBIGUOUS_TARGET`(起動もしないし verified ともしない)。状態不明・切り詰めは `OS_API_ERROR`。
- 管理対象にならないもの: 利用者・他のプログラムが起動したプロセス、Sysacta の起動前(再起動を含む)に起動したプロセス、再承認・無効化(世代が変わる)より前に起動したプロセス、表があふれて外れたプロセス。いずれも一覧には出るが終了できない。

**仕様との差分(利便性の低下)**: spec §7.6 / phase-2a.md は登録アプリの通常終了・強制終了を想定しているが、本実装では自分で起動したアプリや Sysacta 再起動前のインスタンスを MCP から閉じられない。安全性を優先した判断として記録する(公式 API でロード済みイメージの同一性を証明できる方法が見つかれば拡張を検討する)。

テスト: 追跡外(利用者が起動)・再承認後・Sysacta 再起動相当(表が空)のプロセスは一覧のみで終了・通常終了が拒否されること、pid 再利用、管理対象の通常終了・強制終了、表の上限と除去(Core・Mcp)。Windows の専用テストプロセスでの起動・終了は従来どおり。

## PR レビュー(2026-11-28)への対応

| 指摘 | 対応 |
|---|---|
| P1: 現在のパスのファイル ID はロード済みイメージの証明ではない | ChangeTime による条件付きの照合に変更し、満たさなければ不明(上記)。旧イメージ稼働中の置換・再承認の回帰テスト(新しいコピー/古いファイルの名前変更) |
| P2: 切り詰めたスナップショット | 切り詰め時は全アプリを不明にし、起動・通常終了は Core でも拒否。`Process32NextW` の失敗と終端を区別 |
| 通常終了の一意性をロック後に再確認 | 再列挙して同じ pid・生成時刻の唯一のインスタンスであることを確認。フェイクで追加起動・置き換わり・消失・不明・切り詰めを検査 |
| 中断状態での生成と再開の間 | 再開前に `BeginNextSideEffect(targetCheck)`、拒否・不一致・再開失敗・例外では自分が生成したプロセスだけを回収し、回収の確認結果で error / unknown を分ける。フェイクと専用の無害プロセスで検査(再開前に拒否したプロセスのコードが一度も実行されないことを含む) |
| 起動準備を開始点の前へ | NT パスの取得とコマンドラインの組み立てを `PrepareLaunch` へ移動 |

`ToolExecution.BeginNextSideEffect` に `targetCheck` を受け取るオーバーロードを追加した(`BeginSideEffect(targetCheck)` と同じく、認可と同じゲート内で評価)。既存の呼び出しの意味は変えていない。

## 実機確認

- 2026-11-28、開発 PC(Windows 11): `WindowsAppPlatformTests` 9 件が成功(manual-tests A0)。起動・終了したのはテスト用に複製した `HarmlessTarget` だけで、実在のアプリ・既存のプロセスには読み取り(スタートメニューの検出、セッションのプロセス列挙)しか行っていない。
- 2026-11-28、利用者の承認のもと `C:\Program Files\Windows Photo Viewer\ImagingDevices.exe` だけを対象に、本番の部品を一時 DB で組み立てた検証プログラムから MCP HTTP で確認した(通常権限): 登録(ハッシュあり)→ `ready` → 起動 accepted(pid / start_time が一覧と一致)→ 2 回目は `already_running` → `app_close` verified / exited → 再起動 → 古い start_time で `TARGET_CHANGED` → 強制終了 verified / exited → 再実行で `TARGET_NOT_FOUND` → 自身は `self`。監査は `target` と `pid` だけ。`failed_count` は 0 だった。検証プログラムと一時 DB は削除済み。
- 同日、テスト専用 `HarmlessTarget` の複製で A5(書き換え → `target_changed` → `TARGET_CHANGED` → 再承認で許可オフ → 許可して起動)と、固定引数がそのまま届くこと(Windows テストにも追加)を MCP HTTP 経由で確認した。
- 同日、利用者が自己完結版を通常権限で起動し、管理画面(検出・登録・許可・再承認・テーマ・スクロール)と、自分の MCP クライアントからの `app_list_available` → `app_launch` → `app_close` を確認して「すべて問題なし」と報告した。DB の写しの読み取りで、schema v3 への移行、`sysacta.db.v2.bak` の作成、6 ツールが無効で追加され利用者が有効化・付与した 3 ツール以外は無効・未付与のままであること、監査に MCP 経由の成功が残ったことを確認した。
- 未確認: 管理者として起動した場合(A9、任意)、複数ユーザー(環境なし)、保存確認の出るアプリの通常終了、DPI の変更。その後、保存確認待ちの通常終了はテスト専用のウィンドウ(WM_CLOSE を無視/受け入れ)で開発 PC と CI で確認し、DPI は 150% 相当の描画を確認して見つかったレイアウトの不具合(登録ボタン・一覧が画面外)を修正した。実在のアプリの保存ダイアログと実機での DPI 変更、管理者起動・複数ユーザーは、利用者の判断(2026-11-28)により未確認のまま Phase 2A を完了とする(複数ユーザー・管理者起動は phase-2a.md §10 で「環境がなければ未検証」を許容)。
- 残りの実在のアプリでの確認は [manual-tests.md](manual-tests.md) の A1〜A11 に従い、利用者が対象アプリと操作を個別に承認した場合だけ行う。

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
