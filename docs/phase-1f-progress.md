# Phase 1F 進捗: システム監視

計画は [phase-1f.md](phase-1f.md)。ブランチ `phase-1f`（main `b557f99` から作成）。

## 1. 実装範囲

| ツール | 内容 | 既定 | ReadOnly |
|---|---|---|---|
| `system_get_status` | CPU 使用率（約 1 秒の測定窓）、物理メモリ、稼働時間、GPU ごとの利用率（最も忙しいエンジン・エンジン別）と GPU メモリ使用量 | On | true |
| `system_get_hardware` | Windows の製品名・バージョン・ビルド・UBR・機能更新名・アーキテクチャ、CPU（ソケットごと＋トポロジー合計）、搭載 RAM と OS 利用可能 RAM、GPU（名前・PCI ID・DXGI メモリ容量）、PC のメーカー・モデル | On | true |
| `system_get_temperatures` | GPU のドライバー報告温度（WDDM）、ACPI サーマルゾーン。CPU パッケージ/コア、GPU ホットスポット/メモリ、ドライブ温度は `not_collected` に理由付きで列挙 | On | true |
| `system_get_storage` | ドライブ文字を持つローカル固定ディスクの論理ボリューム容量 | On | true |

- 4 ツールとも引数なし（`additionalProperties: false`、プロパティなし）、`AuditedArguments` は空、タイムアウトは既定の 5 秒、`Transition` なし。`ToolCatalog` にだけ登録した。
- 新規 DB では spec §7 のとおり On。既存 DB への追加は無効・既存クライアントに付与なし（既存の `AccessStore.SyncTools` の動作。Infrastructure テストで確認）。
- 構成: Core `Tools/SystemMonitoring/`（読み取り型・アダプター契約 `ISystemMonitor`・解釈と検証・ツール）、Windows `SystemMonitoring/`（`WindowsSystemMonitor`・`NativeMethods`）。名前空間は `System` との衝突を避けて `SystemMonitoring`。アダプターは 1 つ（4 ツールが共有。状態を持たない）。新しい抽象化・共通基盤の変更はない。
- 新規 NuGet・ネイティブ DLL の同梱はない。既存の `System.Management` を WMI に再利用した。

## 2. 技術調査と採否

参照日 2026-11-28。すべて通常権限の非パッケージ Win32 デスクトップアプリで利用でき、Windows 11 に標準で入っている DLL だけを System32 から読み込む（`DefaultDllImportSearchPaths(System32)`）。

| 用途 | API | 採否 | 根拠・制約 |
|---|---|---|---|
| CPU 使用率 | [PDH](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw) `\Processor Information(_Total)\% Processor Time` | **採用** | 全プロセッサグループの合計。`PdhAddEnglishCounterW` で英語名のまま追加でき、表示言語に依存しない（Vista 以降）。2 回収集の差分（率カウンター）。`PDH_FMT_NOCAP100` で 100 の切り詰めを無効にし、範囲検査は Core で行う |
| CPU 使用率 | [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes) | 不採用 | 「64 プロセッサを超える環境では呼び出しスレッドのプライマリグループの合計」と文書化されている。全グループを扱える PDH を選んだ（GPU にも PDH が必要で、1 つのクエリで同じ測定窓を共有できる） |
| CPU 使用率 | `% Processor Utility` | 不採用 | 周波数補正値（100 を超え得る）。タスクマネージャーはこちらを表示するため差が出ることを description に明記 |
| GPU 利用率 | PDH `\GPU Engine(*)\Utilization Percentage` | **採用** | WDDM 2.0 以降のドライバーで提供。インスタンス名 `pid_<pid>_luid_0x<hi>_0x<lo>_phys_<n>_eng_<n>_engtype_<type>`（実機で確認）。PID は内部集計だけに使い返さない |
| GPU メモリ使用量 | PDH `\GPU Adapter Memory(*)\Dedicated Usage` / `Shared Usage` | **採用** | アダプター単位・全プロセス合計（インスタンス `luid_..._phys_<n>`、実機で確認）。DXGI のプロセス別予算（QueryVideoMemoryInfo）は使わない |
| GPU 構成・識別 | [DXGI](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_adapter_desc1) `CreateDXGIFactory1` → `EnumAdapters1` → `GetDesc1` | **採用** | デバイスは作成しない。`DedicatedVideoMemory` 等は SIZE_T（64 ビット）で 4 GiB 超も桁落ちしない。`AdapterLuid` で PDH・D3DKMT と照合。`DXGI_ADAPTER_FLAG_SOFTWARE`=2 で Basic Render Driver を区別。COM はすべて呼び出し内で解放 |
| GPU 容量 | WMI `Win32_VideoController.AdapterRAM` | 不採用 | 32 ビット値で 4 GiB 超を表せない |
| GPU 温度 | [D3DKMTOpenAdapterFromLuid](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/nf-d3dkmthk-d3dkmtopenadapterfromluid) / [D3DKMTQueryAdapterInfo](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/nf-d3dkmthk-d3dkmtqueryadapterinfo)(`KMTQAITYPE_ADAPTERPERFDATA`) / D3DKMTCloseAdapter（gdi32） | **採用** | [D3DKMT_ADAPTER_PERFDATA](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_adapter_perfdata).Temperature は「main temperature sensors reading in decimal Celsius. 1 = 0.1C」。ベンダー非依存で LUID 照合。照会のみで、ファン・クロック・電力の設定 API は呼ばない。列挙値は文書に数値がなく、値付きヘッダー（Wine `include/ddk/d3dkmthk.h`: `KMTQAITYPE_DRIVERVERSION = 13, KMTQAITYPE_ADAPTERTYPE = 15`、14 は欠番）から 62 と確認し、実機で正しい温度が返ることを確認した。構造体は 64 バイト（Temperature はオフセット 56、Windows.Tests で検査） |
| GPU 温度 | NVAPI / NVML / AMD ADLX / Intel IGCL | 不採用（調査のみ） | ベンダー DLL の読み込み元（ドライバーストア）・ABI・版・再配布条件をベンダーごとに検証する必要がある。D3DKMT で GPU 温度は共通に読めるため今回は追加しない。ホットスポット・メモリジャンクションはこれらでしか読めない可能性があり、`not_collected` に残す |
| ACPI 温度 | PDH `\Thermal Zone Information(*)\High Precision Temperature`（0.1 K）/ `Temperature`（K） | **採用** | 通常権限で読める。ACPI サーマルゾーンは CPU 温度ではないため `acpi_thermal_zone` として別種で返す |
| ACPI 温度 | WMI `root\wmi MSAcpi_ThermalZoneTemperature` | 不採用 | 一般に管理者権限が必要 |
| CPU 温度 | MSR / SMBus / EC / PCI 構成空間 | 不採用 | カーネルドライバーが必要。通常権限の公開 API はない → `not_collected: cpu_package / cpu_core`（`not_supported`） |
| メモリ | [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex) | **採用** | `ullTotalPhys`（OS が使える物理メモリ）・`ullAvailPhys` |
| 搭載 RAM | [GetPhysicallyInstalledSystemMemory](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getphysicallyinstalledsystemmemory) | **採用** | SMBIOS 由来（KB）。OS 利用可能量より小さければ OS 側が失敗する（文書） |
| 稼働時間 | GetTickCount64（`Environment.TickCount64`） | **採用** | 起動からのミリ秒。文書は「作業状態の時間は QueryUnbiasedInterruptTime を使う」と対比しており、スリープ・休止を含む値と解釈。Fast Startup の「シャットダウン」はカーネルの休止なので継続する |
| 測定窓の検査 | [QueryUnbiasedInterruptTime](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime) | **採用** | スリープ・休止を含まない（文書に明記、100 ns 単位）。GetTickCount64 の差と比べてスリープを検出 |
| CPU 構成 | WMI `SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor`、`GetLogicalProcessorInformationEx(RelationAll)`、`GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)` | **採用** | 物理コアは `RelationProcessorCore` の数え上げで、論理数から推定しない。`Environment.ProcessorCount` はアフィニティ・ジョブ制限の影響を受けるため使わない |
| OS | WMI `SELECT Caption FROM Win32_OperatingSystem`、`Environment.OSVersion.Version`、レジストリ固定値 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` の `UBR`・`DisplayVersion`、`RuntimeInformation.OSArchitecture` | **採用** | レジストリの `ProductName` は Windows 11 でも "Windows 10" を返すため使わない。`OSArchitecture` は WMI では表示言語に依存するため使わない |
| PC | WMI `SELECT Manufacturer, Model FROM Win32_ComputerSystem` | **採用** | 2 プロパティのみ。同クラスの Name・UserName・Domain は取得しない |
| ストレージ | `GetLogicalDrives` → `GetDriveTypeW` → `QueryDosDeviceW` → [GetDiskSpaceInformationW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskspaceinformationw) | **採用** | 種別判定を最初に行い、固定ディスク以外（ネットワーク・リムーバブル・光学・RAM ディスク・不明）には以後アクセスしない。`\??\` を指す SUBST はフォルダーなので除外。`DISK_SPACE_INFORMATION` の Actual*（クォータを考慮しないボリューム全体）と Caller*（クォータ適用）を区別できる（Windows 10 1809 以降） |
| ストレージ | DriveInfo / GetDiskFreeSpaceEx | 不採用 | `lpTotalNumberOfBytes` は「呼び出しユーザーが使える総量」でクォータ時にボリューム総量と基準が異なり、使用率を同じ基準で出せない |
| 外部 CLI | PowerShell / wmic / typeperf / nvidia-smi | 不採用 | 実装では使わない（実機の比較確認でだけ nvidia-smi の照会を手動で使用） |

### LibreHardwareMonitor（不採用）

- ライセンス MPL-2.0（一部は別条件、THIRD-PARTY-NOTICES）。README は「一部のセンサーは管理者権限が必要」と記載。
- CPU 温度等はカーネルドライバー経由: 従来は WinRing0（任意の物理メモリ・I/O ポート・MSR アクセスを許す古いドライバー）、v0.9.5（リリースノート "Swap WinRing0 to PawnIO" #1857）以降は PawnIO（汎用スクリプト型カーネルドライバー、GPL-2.0、インストーラーで導入）。最新は v0.9.6（PawnIO モジュール 2.2）。
- ドライバーの導入・ロードには管理者権限が必要で、計画の禁止事項（特権ドライバーの導入・ロード、汎用メモリ/IO/MSR アクセス）に当たる。ドライバーを使わない部分（NVAPI/ADL ラッパー等）だけを安全に切り離して検証するコストに対し、D3DKMT と PDH で同等の範囲が公式 API で読めるため、依存を追加しない。
- 参照: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor 、https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.5 、https://pawnio.eu/ 、https://github.com/namazso/PawnIO 。WinRing0 の CVE-2020-14979 は NVD のページを取得できず、本文は未確認。

## 3. 安全境界

- クエリ・カウンターパス・レジストリ値・フィールドはすべて実装の定数。MCP の引数はなく、任意の WMI/PDH/レジストリ/ファイル/パスは受け付けない。
- 負荷生成、プロセス起動・停止、GPU 設定、ファン制御、ストレージへの書き込み、サービス起動、権限昇格、ドライバーの導入・ロード、セキュリティ設定の変更は行わない。D3DKMT は照会（Query）だけで、エスケープ・設定系の関数は宣言もしていない。
- ストレージは `SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX)` を呼び出しスレッドだけに設定し、終了時に戻す（ディスク挿入ダイアログで止まらない）。ファイル・ディレクトリの列挙、生読み取り、SMART、ボリュームラベル・シリアル・GUID パスの取得はしない（`GetVolumeInformation` は呼ばない）。
- WMI はローカル固定 namespace `\\.\root\cimv2`、必要プロパティだけの `SELECT`、タイムアウト 3 秒、結果オブジェクトは `using` で解放。
- 返さないもの: 製品キー、シリアル番号、MachineGuid、SMBIOS UUID、ユーザー名、コンピューター名、ドメイン、SID、MAC、デバイスインスタンスパス、PID、ボリュームシリアル・ラベル・GUID パス、マウント先・SUBST の参照先。

## 4. JSON 契約

共通: 時刻は UTC の ISO 8601（`+00:00`）。サイズは bytes（64 ビット整数）、利用率は percent（0〜100、0.1 刻み）、温度は °C、時間はフィールド名の単位（`_seconds`、`_ms`）。欠損は値を `null` にし、理由 `not_supported` / `permission_denied` / `unavailable` / `invalid_data` / `interrupted` / `truncated` を付ける。NaN・Infinity・範囲外は出さない（検証で `invalid_data`）。0 は測定された値。結果は常に `status: success`, `verification: not_applicable`。

### system_get_status

```json
{
  "measured_at": "2026-11-28T05:04:27.27+00:00",
  "sampling_started_at": "2026-11-28T05:04:26.27+00:00",
  "sampling_ended_at": "2026-11-28T05:04:27.27+00:00",
  "sampling_ms": 1000,
  "cpu": {"usage_percent": 17.1, "reason": null, "logical_processors": 20},
  "memory": {"total_bytes": 33985626112, "available_bytes": 4764577792, "used_bytes": 29221048320, "usage_percent": 86, "reason": null},
  "uptime_seconds": 580535, "uptime_reason": null,
  "gpus": [{
    "gpu_id": "luid_0x00000000_0x00014bc6", "name": "NVIDIA GeForce RTX …", "adapter_type": "hardware",
    "utilization_percent": 10.3, "utilization_reason": null, "busiest_engine_type": "3D",
    "engines": [{"physical_index": 0, "engine_index": 0, "engine_type": "3D", "utilization_percent": 10.3, "reason": null}, "…"],
    "engines_truncated": false,
    "dedicated_memory_used_bytes": 2406813696, "shared_memory_used_bytes": 163459072, "memory_usage_reason": null,
    "dedicated_memory_total_bytes": 16771973120, "shared_memory_limit_bytes": 16992813056
  }],
  "gpu_total_count": 3, "gpus_truncated": false, "gpus_reason": null
}
```

- CPU/GPU 利用率は測定窓の平均、メモリ・GPU メモリ・稼働時間は窓の終了直後の瞬間値（`measured_at`）。両者を 1 つの原子的なスナップショットとは扱わない。
- `adapter_type`: `hardware` / `software`（DXGI のソフトウェアフラグ）/ `unknown`（カウンターにだけ現れ DXGI に無い LUID。名前・容量は推測しない）。
- 全体が読めない（CPU・メモリ・稼働時間・GPU のすべてが欠損）場合だけ `OS_API_ERROR`（retryable）。

### system_get_hardware

`measured_at`, `os{product_name, version, build_number, update_build_revision, display_version, architecture}`, `processors[{name, manufacturer, physical_cores, logical_processors}]`, `processors_truncated`, `processor_topology{packages, physical_cores, logical_processors}`, `memory{installed_bytes, os_usable_bytes}`, `gpus[{gpu_id, name, adapter_type, vendor_id, device_id, dedicated_video_memory_bytes, dedicated_system_memory_bytes, shared_system_memory_bytes}]`, `gpus_truncated`, `machine{manufacturer, model}`, `unavailable[{item, reason}]`。

項目ごとに失敗を区別し、`unavailable` に列挙する（例: WMI がアクセス拒否 → `os_product_name: permission_denied`）。`processors` / `gpus` が空のとき、`unavailable` に載っていれば取得失敗、載っていなければ正常に 0 件。

### system_get_temperatures

`measured_at`, `sensors[{sensor_id, device_type, device_id, sensor_type, label, temperature_c, source, measured_at, reason}]`, `sensors_truncated`, `source_failures[{source, reason}]`, `not_collected[{sensor_type, reason, detail}]`。

- GPU: `sensor_id = <gpu_id>:temperature`, `device_type: gpu`, `device_id = gpu_id`（status / hardware と同じ LUID）, `sensor_type: gpu_temperature`（ドライバーの主温度。コア/ホットスポット/メモリのどれかは仕様上明示されないため断定しない）, `source: d3dkmt_adapter_perfdata`。ソフトウェアアダプターは対象外。
- ACPI: `sensor_id = acpi_thermal_zone:<ACPI パス>`（例 `_TZ.TZ00`）, `device_type` / `sensor_type: acpi_thermal_zone`, `device_id: null`, `source: pdh_thermal_zone`。パス形式でない名前は `sensor_id` / `label` を null にして値だけ返す。
- `not_collected`（常に固定）: `cpu_package`, `cpu_core`, `gpu_hotspot`, `gpu_memory_junction`, `storage_drive`（すべて `not_supported`）。
- 全ソースが読めなくても成功として返し、`source_failures` と `not_collected` で診断する。

### system_get_storage

`measured_at`, `volumes[{volume_id, drive_letter, drive_type, total_bytes, free_bytes, available_bytes, used_bytes, usage_percent, reason}]`, `failed_count`, `excluded_count`。

- `total_bytes` = ActualTotal × クラスタサイズ（クォータを考慮しない論理ボリューム容量。物理ディスク容量ではない）、`free_bytes` = ActualAvailable、`available_bytes` = CallerAvailable（クォータ適用後に現在のユーザーが使える量）、`used_bytes = total - free`、`usage_percent = used / total`（同じ基準）。
- `volume_id` = `drive:<文字>`。ドライブ文字は選択用ラベルで、物理ディスクの永続 ID ではない。複数ボリュームの合計（「PC の総容量」）は返さない。

## 5. 計測と集計

### CPU
- 1 つの PDH クエリに CPU と GPU のカウンターを追加し、収集 → 1000 ms 待機 → 収集。率カウンターの最初の値は使わない。待機は `CancellationToken.WaitHandle` で、キャンセル時は 2 回目の収集をせずに戻る。
- 測定窓の検査（`SystemStatus.CheckWindow`）: GetTickCount64 の差 − 非バイアス割り込み時間の差 > 250 ms → `interrupted`（スリープ・休止をまたいだ）。差が 0 以下、逆転（非バイアス側が 250 ms 超大きい）、要求の半分未満 → `invalid_data`。この場合 CPU と GPU 利用率は欠損、瞬間値は返す。
- 値は 0〜100 から ±1.0 ポイントまでを丸め誤差として 0.1 刻みで 0〜100 に収め、それを超える・非有限は `invalid_data`（切り詰めて成功にしない）。

### GPU
- インスタンス名を `pid / luid / phys / eng / engtype` に分解（正規表現、256 文字以下）。1 件でも分解できなければ全アダプターの利用率を `invalid_data`（どのアダプターの値か分からないため）。
- エンジン単位 `(luid, phys, eng)` で、プロセス別インスタンスの値を合計する。異なるエンジンは加算しない。アダプターの代表値は最も忙しいエンジンの値（タスクマネージャーと同じ考え方。https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/ ）。
- 1 エンジンに 2 種類の engtype → `invalid_data`。同じ PID の同名（または `#n` 付き）インスタンスが複数ある場合は、有効な値がすべて 0 のときだけ受け入れ（合計しても 1 件を選んでも結果が同じ）、1 件でも非 0 があれば `invalid_data`。PDH と GPU Engine カウンターの文書は同名インスタンスの独立性を定めていないため、値が異なることを独立の根拠にしない（PR レビュー対応）。実機では Hyper-V の VM ワーカー `vmwp`＝WSL2 の GPU 準仮想化が全エンジンに同名 2 件（値 0）を持つ。
- 1 回目の収集直後に `PdhGetRawCounterArrayW` で GPU Engine のインスタンス名を読み、2 回目の無効値（CStatus）を照合する。1 回目に無かった名前（窓内で新規出現、レート未算出）だけを除外し、`excluded_new_instances` に数える。1 回目にもあった名前の無効値、または 1 回目の名前を読めなかった場合の無効値は、そのエンジンを `unavailable`（null）にし、代表値も null（過小評価を正常値として返さない）。窓内に終了したプロセスは 2 回目に現れないため数えない（description に明記）。インスタンスが 1 件もないアダプター（Basic Render Driver 等）も `unavailable` で、0 にしない。実機で 1,114 件すべてが 1 回目の名前と照合できた。
- エンジン合計が 101 % を超えたら `invalid_data`。負値・非有限も `invalid_data`。
- 上限: 内部で読む GPU Engine インスタンス 8192（超過は全アダプター `truncated`）、返す GPU 64、GPU あたりエンジン 64（`engines_truncated`、代表値は全エンジンで計算）、GPU Adapter Memory インスタンス 256、PDH バッファ 16 MiB、サイズ変化時の再試行 3 回。
- GPU メモリ: 同じ LUID の物理アダプター（リンク構成）は合計（checked、オーバーフローは `invalid_data`）。同じ phys が重複・負値は `invalid_data`。容量は DXGI（`DedicatedVideoMemory`・`SharedSystemMemory`）。iGPU の Dedicated は小さな予約領域で、実際は共有メモリを使う。

### メモリ・稼働時間
- `used = total − available`。total が 0、available > total、long を超える値は `invalid_data`。コミット・ページファイル・搭載量とは別概念で、タスクマネージャーの「使用中」（圧縮・予約の扱い）と一致しないことがある。
- 稼働時間は秒（切り捨て）。アプリの起動時間・壁時計の差では代替しない。

### 温度・ストレージ
- D3DKMT: 0.1 °C 単位の ULONG。0 はドライバーが報告していない（タスクマネージャーも表示しない）として `not_supported`。真の 0.0 °C とは区別できない（残存リスク）。2^31 以上は `invalid_data`。それ以外の値域で補正・除外しない。
- ACPI: High Precision（0.1 K）を優先、なければ K。0 K 以下・非有限は `invalid_data`。°C への変換は −273.15 で、0.1 K 刻みの値は必ず 0.05 °C 端数になるため 0.01 °C に丸める（0.1 °C 丸めで値を動かさない）。
- ストレージ: 単位数 × セクタ/クラスタ × バイト/セクタを checked で計算。クラスタサイズ 0、total 0、free > total、available > total、オーバーフロー → `invalid_data`。ドライブ単位の失敗（未準備・アクセス拒否 `0x80070005` → `permission_denied`、その他 → `unavailable`）は他のドライブの読み取りを妨げない。ドライブ一覧自体が読めない場合だけ `OS_API_ERROR`。

## 6. 実行制御

- 要求ごとに PDH クエリ・DXGI ファクトリ・WMI 検索・D3DKMT ハンドルを開き、戻る前に閉じる。常時ポーリング・共有サンプラー・キャッシュ・履歴 DB・バックグラウンドスレッドはない。アダプターは状態を持たないので、独立した要求は並行に動く（executor の実行スロット上限 8 に従う）。
- 読み取り専用なのでロック・pending 監査・電源遷移ゲートの対象外（Phase 1E の方針どおり、遷移中・保持中も読み取りは成功する。Core テストで確認）。
- ネイティブ呼び出しがハングしてタイムアウト・キャンセルした場合、executor が実処理の終了までスロットと受付枠を保持し、新しい要求は上限で BUSY になる（新規処理を無制限に生まない。Core テストで確認）。4 ツールともトークンをアダプターに渡し、アダプターは取得元の切り替え（DXGI → PDH、WMI の各クエリ、レジストリ、トポロジー）、次の GPU・物理アダプター・ドライブの前でキャンセルを確認する。先行の読み取りがハング中にキャンセルされても、それが戻った後に次の取得は始まらない（Core のフェイクテストと、実コードの `SelectDrives` の単体テストで確認）。内部リトライは PDH バッファの再取得 3 回だけ。
- 実機の所要時間: status 約 1.3 秒（測定窓 1 秒を含む）、hardware 約 60 ms、temperatures 約 4 ms、storage 約 1 ms。タイムアウトは既定の 5 秒のまま。

## 7. 実機検証（2026-11-28、読み取りのみ）

Windows 11 Home（ビルド 26300 系、x64）の通常権限で、作業コピーを Windows 側の一時フォルダに置き、(1) `WindowsSystemMonitorTests`（7 件）を `dotnet test` で実行 → 全件成功、(2) アダプターを直接呼ぶ一時コンソール（リポジトリ外）で値を確認した。負荷生成・設定変更・アプリ起動はしていない。機種名・型番は記録から除いた。

| 項目 | 結果 |
|---|---|
| CPU | Intel Core Ultra（1 パッケージ、物理 20、論理 20）。使用率 10〜17 % で妥当。WMI の Win32_Processor（コア数）と GetLogicalProcessorInformationEx の数え上げが一致。タスクマネージャーとの目視比較は未実施 |
| RAM | 搭載 32 GiB（34,359,738,368）、OS 利用可能 33,985,626,112、used = total − available を確認 |
| 稼働時間 | 580,535 秒（約 6.7 日） |
| GPU | NVIDIA RTX 50 系 16 GB（hardware）、Microsoft Basic Render Driver（software）、DXGI に無い Compute 専用 LUID 1 件（`unknown`、エンジン `Compute`）。PDH の GPU Engine インスタンス約 1,000 件、名前形式は想定どおり。同時期の `nvidia-smi --query-gpu`（照会のみ）と比較: 利用率 10.3 % ⇔ 9 %（方式差）、専用メモリ使用 2,406,813,696 B（2,295 MiB）⇔ 2,271 MiB、容量 16,771,973,120 B（15,995 MiB、DXGI）⇔ 16,303 MiB（DXGI は予約分を除く）、32 ビットでの桁落ちなし |
| GPU 温度 | D3DKMT で 31.7〜31.9 °C、`nvidia-smi` は 32 °C（一致） |
| ACPI | `\_TZ.TZ00` が 301.0 K（27.85 °C）。多くのデスクトップのファームウェアが返す固定値と見られ、CPU 温度ではない（そのとおり別種で返している） |
| ストレージ | 固定ディスク C:・D:。total/free/available が DriveInfo（GetDiskFreeSpaceEx）とバイト単位で一致（クォータなし）。除外 0 件 |
| 個人情報 | hardware の出力にコンピューター名・ユーザー名が含まれないことをテストで確認 |

発見と修正: 初回の実機実行で NVIDIA の全エンジンが `invalid_data` になった。原因は `vmwp` の同名インスタンス（値 0）を「重複は常に不正」としていたこと。§5 の規則（全 0 は影響なし、異なる値は合計、同じ非 0 値は不正）に改め、テストを追加した。

未検証: 複数の物理 GPU（iGPU + dGPU、リンク構成）、AMD/Intel の GPU 温度、64 論理プロセッサ超（複数プロセッサグループ）、ノート PC の ACPI ゾーン、クォータ設定ボリューム、BitLocker でロックされたドライブ・SUBST・ネットワークドライブの割り当て、スリープをまたぐ測定、ARM64。実際の MCP クライアントからの呼び出し（manual-tests S1〜S6）とタスクマネージャーとの目視比較は未実施。

## 8. 取得不能な項目（この版の制約）

| 項目 | 理由 |
|---|---|
| CPU パッケージ/コア温度 | 通常権限で読める公開 API がない（MSR はカーネルドライバーが必要）。`not_collected` |
| GPU ホットスポット/メモリジャンクション | WDDM のパフォーマンスデータに無い。ベンダー API は未採用 |
| ドライブ温度 | 今回は実装しない（IOCTL での取得は物理ディスクのハンドルが必要で、範囲外とした） |
| 温度を報告しない GPU ドライバー（多くの iGPU） | `temperature_c: null`, `not_supported` |
| リンク構成 GPU の 2 台目以降の温度 | 物理アダプター 0 だけを照会する |
| `% Processor Utility`（周波数補正） | 返さない（混同を避ける） |
| プロセス別の CPU/GPU | 対象外（Phase 1G 以降） |

## 9. 仕様との差分

- spec §7.5 の実装候補「LibreHardwareMonitor」は採用しない（§2）。CPU 温度は取得不能として明示する。
- 計画 §9 の「GetSystemTimes の kernel/idle 差分」テストは、PDH を採用したため該当しない。代わりに測定窓（ゼロ分母・逆転・スリープ・短すぎる窓）、範囲検査、全グループの論理プロセッサ数をテストした。
- GPU 温度は `gpu_core` ではなく `gpu_temperature`（ドライバーの主温度。WDDM の文書がセンサー位置を定めないため）。
- spec の表に差分はない（名前・引数なし・既定 On）。

## 10. 残存リスク

- D3DKMT の列挙値 62 は文書に数値がなく、ヘッダーの欠番から導いた（実機で正しい値を確認済み）。将来の Windows で D3DKMT_ADAPTER_PERFDATA の意味が変わると誤った値になり得る（構造体サイズは OS 側で検査される）。
- D3DKMT の温度 0 を「未報告」とみなすため、真の 0 °C は報告されない。
- 窓内に終了したプロセスの寄与は数えない（窓内の短命プロセスで低めに出る）。同名インスタンスに非 0 値があると代表値は `invalid_data`（WSL2 等で GPU 準仮想化を使っている間に起こり得る）。
- 熱ゾーンはファームウェア定義で、固定値を返す機種がある。値の意味は機種依存。
- `GetDriveTypeW` は文書上ネットワークにアクセスするか明記がない。切断済みのネットワークドライブで遅延する可能性は未検証（executor のタイムアウトで打ち切られるが、そのときストレージ全体が `TIMEOUT` になる）。
- WMI がハングすると hardware 全体が 5 秒で `TIMEOUT` になる（項目単位のタイムアウトは、打ち切った処理が残り続けるため実装していない）。WMI の検索自体には 3 秒のタイムアウトを設定している。
- PDH の性能カウンターが破損・無効化された環境では CPU/GPU/ACPI が `unavailable` / `not_supported`。
- 機器名はサニタイズ（制御文字除去、パス・URI・スキーム形式は null）するが、名前の中の任意の情報を検出できる保証はない。`/` を含む正規の名前（例 "XT/XTX"）も null になる。

## 11. ビルド・テスト

- Linux (WSL2, .NET SDK 10.0.404): `dotnet build Sysacta.sln -c Release` 警告・エラーゼロ。`dotnet test Sysacta.sln -c Release`: Core 660、Infrastructure 51、MCP 119、Windows 65 成功（Windows 実機専用 32 件スキップ）。追加: Core `SystemMonitorToolTests`、MCP `SystemServerTests`、Infrastructure の既存 DB テスト、Windows `WindowsSystemMonitorTests`。
- Windows 実機: `WindowsSystemMonitorTests` 7 件成功（§7）。
- Windows CI（windows-latest、PR #17、run 46111354768、レビュー対応後のコミット）: ビルド成功、App 31・Core 663・Infrastructure 51・MCP 119・Windows 88 成功（Windows 11 件スキップ: 音声機器等がない）、実 exe のスモークテスト成功。CI ランナーでは実 GPU がなく、GPU 温度・利用率は欠損理由付きの応答だけを確認したことになる。

## 12. レビュー対応（規約・仕様の 2 軸レビュー）

- 規約: 明確な違反なし。対応: 同名インスタンスの扱いを熱ゾーン・GPU メモリで統一（`JoinByName`、重複は `invalid_data`）、`Array` → `ReadArray`、`EAccessDenied` 定数化、キャンセル確認の重複除去、PDH 配列要素サイズを構造体（`PdhFmtCounterValueItem`）から算出。
- 仕様: (1) ハングした取得元を再初期化しない → 取得元（PDH・DXGI・D3DKMT・WMI・ストレージ）ごとの `HangGuard`。5 秒を超えて終わらない呼び出しがある間は新規に開かず `timeout`（ストレージは `TIMEOUT` エラー）。通常の並行読み取りは直列化しない。(2) 予算内に取れない項目は欠損 → hardware の WMI 3 クエリに合計 3 秒の締め切り、超過分は `timeout`。(3) 100 超の丸め → 許容幅を ±0.05（浮動小数点誤差のみ）に縮小、100.9 は `invalid_data`。(4) 除外インスタンスの明示 → `excluded_new_instances` を追加。(5) 熱ゾーンの重複 → `invalid_data`・ID なし。(6) status の全体失敗が稼働時間で起きない → CPU・メモリ・GPU がすべて欠損ならエラー。(7) リンク構成 GPU → `KMTQAITYPE_PHYSICALADAPTERCOUNT`(30) で物理アダプターごとに照会、`sensor_id = <gpu_id>:phys_<n>:temperature`（取得不能なら 1 台として扱う）。(8) 種別判定前に容量取得しないことを `SelectDrives` の単体テストで検証。
- 追加の理由値 `timeout` を `UnavailableReason` に追加。§8 の「リンク構成 GPU の 2 台目以降」の制約は解消（実機はリンク構成でないため未検証）。§10 の WMI ハングのリスクは「締め切り超過は欠損、接続自体のハングは executor の TIMEOUT、再初期化しない」に更新。
- 再検証: Linux で Core 663・Infrastructure 51・MCP 119・Windows 67 成功（スキップ 32）、警告ゼロ。Windows 実機で `WindowsSystemMonitorTests` 9 件成功、status/temperatures/hardware/storage の値を再確認（物理アダプター数 1、温度 32 °C）。

## 13. PR レビュー対応（PR #17）

| 指摘 | 対応 |
|---|---|
| [中] 同一 PID・エンジンの重複を値の違いだけで加算しない | 重複は有効値がすべて 0 のときだけ受け入れ、非 0 を含めば（異なる値・0 との混在・`#n` 付きも）`invalid_data`。異なる PID の合計は従来どおり。回帰テストを追加・修正 |
| [中] PDH の無効値を新規インスタンスと断定しない | 1 回目の収集時のインスタンス名（生値の配列）と照合し、`CounterSample.InFirstSample` で新規/既存/不明を区別。新規だけ除外して数え、既存・不明の無効値はエンジンと代表値を null。全件無効・既存の一部失敗・新規出現・有効な 0 % のテスト、description・本書を更新。実機で照合が機能することを確認 |
| [中] 全アダプターにキャンセルを伝播 | `ReadHardware` / `ReadTemperatures` / `ReadStorage` にトークンを追加し、取得元・機器ごとに確認。先行取得のハング中に取消 → 終了後に次の取得が始まらないことを 4 ツールでテスト。ドライブ単位の停止を実コードでテスト。ハンドル・ゲートは従来どおり `finally` で解放 |

再検証: Linux で Core 667・Infrastructure 51・MCP 119・Windows 68 成功（スキップ 32）、警告ゼロ。Windows 実機で `WindowsSystemMonitorTests` 10 件成功。
