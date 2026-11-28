# Phase 2B ネットワーク管理 — 進捗と判断

対象: [phase-2b.md](phase-2b.md)、spec §7.7 / §8〜§11。ツールは `network_get_status` / `network_list_adapters` / `network_get_traffic` / `network_set_adapter_enabled` の 4 つと、管理画面「デバイス」のネットワークカテゴリ。

## 状態

| 項目 | 状態 |
|---|---|
| 実装 | 完了(読み取り 3 ツール + デバイス画面、管理状態の変更を別コミット) |
| Linux/WSL の Release build / test | 成功(警告・エラー 0)。Windows 専用テストはスキップ、App.Tests は対象外 |
| Windows CI(build / test / smoke) | PR で確認する(下の「検証結果」に追記) |
| 実機での読み取り(一覧・状態・通信量) | 未確認 |
| 実機での有効化・無効化 | 未確認(利用者の個別承認・未使用の予備物理 NIC・独立した復旧経路がそろっていないため実施していない) |
| 管理者起動での動作 | 未確認 |

## 技術調査と API の採否(2026-11-29)

| 用途 | 採用 | 不採用・理由 |
|---|---|---|
| アダプター・管理/稼働状態・リンク速度・カウンター | `GetIfTable2`(`MIB_IF_ROW2`)、`FreeMibTable` | `GetIfTable` / `MIB_IFROW`(32 bit カウンター)、`NetworkInterface.GetAllNetworkInterfaces`(無効なアダプターを返さず、32 bit 由来の統計を含む・フラグが取れない)、`GetIfEntry2`(1 件ずつ。表で足りる) |
| IP アドレスとプレフィックス | `GetUnicastIpAddressTable(AF_UNSPEC)`(平坦な表。`OnLinkPrefixLength`、`DadState`) | `GetAdaptersAddresses`(可変長の連結リストで、境界検査が増える。MAC・DNS サフィックスなど不要な情報を含む) |
| 経路 | `GetIpForwardTable2(AF_UNSPEC)`。保持するのは LUID・アドレスファミリー・宛先プレフィックス長だけ(次ホップは読まない) | `GetBestRoute2`(宛先 IP が必要。外部宛先を選ぶこと自体を避ける) |
| 依存関係 | `GetIfStackTable`(インターフェース番号の上下関係) | — |
| 接続レベル | WinRT `NetworkInformation.GetInternetConnectionProfile()` → `GetNetworkConnectivityLevel()`(OS 自身の NCSI 観測値。Sysacta は何も送信しない) | `INetworkListManager`(COM 定義が増えるだけで同じ情報)、独自の HTTP/DNS/ping |
| 管理状態の変更 | CIM `root\StandardCimv2` の `MSFT_NetAdapter`(固定クエリ `SELECT * FROM MSFT_NetAdapter` で列挙し、InterfaceGuid が一致する 1 インスタンスに `Enable` / `Disable` を引数なしで呼ぶ。System.Management、既存依存) | `SetIfEntry` / `MIB_IFROW.dwAdminStatus`(古い API、管理状態の持続や対象範囲が不明確)、PnP デバイスの無効化(`SetupDiSetClassInstallParams` / `CM_Disable_DevNode`: インターフェースではなくデバイス全体・ドライバーに作用し、目的外)、`Win32_NetworkAdapter.Enable/Disable`(旧クラス)、netsh / PowerShell(CLI フォールバック禁止) |
| 時間 | `TimeProvider.GetTimestamp` / `GetElapsedTime`(単調時計。本番は `Stopwatch`)、`Task.Delay(TimeSpan, TimeProvider, CancellationToken)` | 壁時計(`DateTime`)による経過時間 |

- 追加の NuGet 依存はない。DLL は `iphlpapi.dll` を `DefaultDllImportSearchPaths(System32)` で解決する。WinRT は既存 TFM(`net10.0-windows10.0.19041.0`)の投影を使う。
- 表は OS が確保し、`try/finally` で必ず `FreeMibTable` する。件数を読んでから内部上限(インターフェース 512、アドレス 4096、経路 8192、積層 4096 行)までだけ読み、超過は `truncated` として完全扱いしない。行の幅は `Marshal.SizeOf<T>()`(SDK の `sizeof` と一致することを Windows.Tests で検査)、先頭行のオフセットは 8 byte 境界の表で 8、`MIB_IFSTACK_TABLE` で 4。対応しないアドレスファミリーの行が 1 つでもあれば表全体を `invalid_data` にする(黙って捨てない)。`ERROR_NOT_FOUND`(空)は正常な空、`ERROR_ACCESS_DENIED` は `permission_denied`、その他は `unavailable`。
- 構造体: `MIB_IF_ROW2` 1352 byte(Alias +28、Type +1128、フラグ +1152、OperStatus +1156、TransmitLinkSpeed +1192、InOctets +1208、OutOctets +1280)、`SOCKADDR_INET` 28 byte(4 byte 境界)、`MIB_UNICASTIPADDRESS_ROW` 80 byte、`MIB_IPFORWARD_ROW2` 104 byte(`IP_ADDRESS_PREFIX` を展開し、PrefixLength +40、NextHop +44)、`MIB_IFSTACK_ROW` 8 byte。フラグは `InterfaceAndOperStatusFlags` の bit0 HardwareInterface、bit1 FilterInterface、bit2 ConnectorPresent。
- 呼び出しはすべて同期でキャンセル不能。executor のタイムアウト後もスロット(変更ではロックと電源ゲート)を実完了まで保持する。WMI の列挙には 5 秒のタイムアウトを付け、インスタンスが 512 を超えたら失敗にする。

### 参照(2026-11-29 に確認)

- MIB_IF_ROW2(フィールド順、フラグ、AdminStatus/OperStatus/MediaConnectState の値、リンク速度は bit/s の ULONG64、InOctets/OutOctets は ULONG64、InterfaceIndex は無効化→有効化で変わり得る): https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
- GetIfTable2(FreeMibTable で解放、論理・物理インターフェースを列挙): https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getiftable2
- GetUnicastIpAddressTable(AF_UNSPEC、空は ERROR_NOT_FOUND): https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getunicastipaddresstable
- GetIpForwardTable2(空は ERROR_NOT_FOUND): https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipforwardtable2
- GetIfStackTable(Higher が Lower の直上、空は ERROR_NOT_FOUND): https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getifstacktable
- MSFT_NetAdapter(Root\StandardCimv2、InterfaceGuid string、NetLuid uint64、InterfaceIndex uint32(非永続)、State 0 Unknown / 1 Present / 2 Started / 3 Disabled、HardwareInterface / Virtual / Hidden / ConnectorPresent boolean、NdisPhysicalMedium 9 = Native 802.11 / 14 = 802.3 / 10 = BlueTooth、MediaConnectState 0/1/2、メソッド Enable / Disable): https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh968170(v=vs.85)(アーカイブ文書)
- GetInternetConnectionProfile(優先インターフェースのプロファイル、無ければ null): https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.networkinformation.getinternetconnectionprofile
- NetworkConnectivityLevel(None / LocalAccess / ConstrainedInternetAccess / InternetAccess、「Windows API の報告はヒントにすぎない」): https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.networkconnectivitylevel

**未確認の公式契約**: MSFT_NetAdapter の `Enable` / `Disable` メソッドの個別ページはクラス文書のリンク先が 404 で、戻り値の一覧・同期/非同期・必要権限を公式文書で確認できなかった。このため戻り値は成否の根拠にせず、**同じインスタンスの State の読み戻しだけで verified を判定**する(戻り値 0 でも読み戻しが一致しなければ unknown)。管理者権限は、構成変更を伴う CIM メソッドとして必要と仮定し、通常権限ではツールを非公開にする(fail-closed)。実機で確認するまで「管理者起動で動作する」とは報告しない。

## ID と別名

- canonical ID は `net-` + InterfaceGuid の 32 桁小文字 16 進(`Guid.ToString("N")`)。照合は完全一致で、大文字や `{}` 付きは受け付けない。InterfaceGuid が空、または同じ表に重複する行(フィルターを除いた後)には ID を発行せず `unidentified_count` に数える。NDIS フィルター(`FilterInterface`)は一覧に出さない。
- LUID・InterfaceIndex・名前・MAC・IP は ID の代替にしない。LUID と Index は実行時の同一性として使う(通信量の 2 サンプル間、変更前の IP Helper とプロバイダーの照合)。Index は MIB_IF_ROW2 と MSFT_NetAdapter の両方の文書が「無効化→有効化で変わり得る」としている。Index は IPv4/IPv6 で共通の NDIS ifIndex(MIB_IF_ROW2)を使い、プロトコル別の `MIB_IPINTERFACE_ROW` は使わない。経路・アドレスの対応付けは LUID で行う。
- 識別子が変わり得る場面: ドライバーの再インストール・アダプターの取り外しと再装着(GUID が変わる場合がある)、Hyper-V/WSL の仮想スイッチや VPN(Wintun 等)の再生成(新しい GUID)。変わった場合は旧 ID を「見つかりません」として保持し、別名は利用者が明示的に再紐付けする。
- 別名は既存の `device_aliases`(カテゴリ `network`)と、カテゴリ内一意・予約語(`all` / `default`)・ID との衝突検査をそのまま使う。登録・変更は管理画面だけ(MCP からは不可)。解決時に canonical ID へ固定し(`ToolTarget`)、ロック待ちの間に別名が再紐付けされても解決済みの ID に作用する(テストあり)。別名が ID 形式でない値を指していれば `TARGET_NOT_FOUND`、指す先が存在しなければ `DEVICE_DISCONNECTED`。
- 引数 `adapter` は 1〜64 文字・制御文字なし・前後の空白なし・予約語不可。IP・ホスト名・インターフェース名・番号は受け付けない(ID 形式か登録済みの別名だけ)。

## 種別と状態の区別

| 区分 | 判定(公式の型・フラグだけ。名前・製造元は使わない) |
|---|---|
| `loopback` | ifType 24 |
| `tunnel` | ifType 131(tunnel)・23(ppp)、または TunnelType ≠ 0 |
| `virtual` | HardwareInterface = false(Hyper-V/WSL の vEthernet、Wintun を使う Tailscale 等の VPN、WAN Miniport など) |
| `wired` | HardwareInterface かつ ifType 6 かつ PhysicalMedium 14(802.3) |
| `wifi` | HardwareInterface かつ ifType 71 かつ PhysicalMedium 9(Native 802.11) |
| `unknown` | 上記以外のハードウェア(Bluetooth PAN、モバイルブロードバンド等) |

- Hyper-V/WSL を `virtual` から区別する公式のフラグはないため、独立した区分にしない(名前による推定をしない)。
- `admin_state`: AdminStatus 1 かつ OperStatus ≠ NotPresent → `enabled`、AdminStatus 2 → `disabled`、それ以外 → `unknown`。Windows で無効化されたアダプターが「Admin up / NotPresent」で報告される可能性があるため、その組み合わせを `enabled` とも `disabled` とも断定しない。変更ツールは MSFT_NetAdapter の `State` を管理状態の正本として読む。
- `oper_state`(up/down/testing/unknown/dormant/not_present/lower_layer_down)と `media_state`(connected/disconnected/unknown)は管理状態と独立に返す。down を disabled と扱わない。
- リンク速度はメディア接続中で、0 でも全ビット 1 でもなく long に収まる場合だけ返し、それ以外は null。通信量ではない。

## 通信速度の測定方式

1. Sysacta 自身の変更世代を読む → `GetIfTable2`(1 回目)→ 単調時計の時刻 t1。
2. `Task.Delay(1 秒, TimeProvider)`(キャンセル可能。間隔は固定で引数にしない)。
3. `GetIfTable2`(2 回目)→ t2 → 変更世代を再読。
4. アダプターごとに `delta = InOctets₂ − InOctets₁`(OutOctets も同様。ulong)、`rate_bps = floor(delta × 8 × TimestampFrequency / (t2 − t1))` を UInt128 で厳密に計算し、long に収まらなければ `invalid_data`。

速度を null にする条件(`rate_unavailable_reason`): 2 回目に無い(`adapter_removed`)/ 1 回目に無い(`adapter_added`)/ どちらかの表が不完全で有無を判断できない・2 回目の読み取り失敗(`sample_failed`)/ LUID または Index の変化(`identity_changed`)/ AdminStatus・OperStatus・MediaConnectState の変化(`state_changed`)/ 区間中に Sysacta の管理状態変更が実行中・発生(`configuration_changed`)/ t2 − t1 ≤ 0 または 4 秒超(`invalid_elapsed`)/ カウンターの減少(`counter_decreased`、wrap 補正をしない)。同値なら実測 0。

- 2 つの表の読み取りは OS 上で原子的ではなく、t1/t2 は各読み取りの直後に取るため、カウンターの値が取られた瞬間との間には表の読み取り時間分のずれがある。2 回の読み取り時間の差がそのまま窓の誤差になる(通常 ms 未満。フィルターの多い環境で 100 ms 程度かかれば、その差の分だけ速度が数 % ずれ得る。前後の時刻の中点を使う改善は実機の測定を見て判断する)。
- **観測できない限界**: 区間内でリセットされた後に元の値を超えて増えたカウンター、状態が一度変わって元に戻った場合、外部ソフトウェアによる構成変更は検出できない。ループバック・トンネル・VPN・仮想アダプターは同じ通信を重ねて数えるため合算しない(合算値を返さない)。
- JSON: 累積値と差分は 64 bit 整数(byte)、速度は 64 bit 整数(bit/s、切り捨て)。2^53 を超える累積値は IEEE 倍精度の JSON クライアントで精度が落ちる(description に明記)。
- タイマー・バックグラウンドキャッシュ・履歴は作らない。1 要求最大 128 件。

## 通信経路の安全性判定(network_set_adapter_enabled)

### 判断の根拠

MCP 接続の実際の経路は、ソケットからは確定できない。127.0.0.1 / ::1 の接続はローカルのクライアントと Tailscale Serve(ループバックの逆プロキシ)を区別できず、プロキシの上流がどの NIC を通るかはソケット情報に現れない。転送ヘッダーは偽造できる。このため**経路の推定に頼らず、「今その NIC を通る通信が存在し得ない」ことを OS の表から確かめられた場合だけ無効化を許可する**方式にした。この判定は接続元に依存しないので、Kestrel/RequestGuards/Caller から接続情報を Core に渡す拡張は行っていない(既存の認証・予算・監査・接続元の契約は不変)。接続元アドレス・Host/Origin・Forwarded/X-Forwarded-For は判定に一切使わない(テストあり)。

### 無効化の条件(すべて満たすときだけ)

1. 物理の有線/Wi-Fi: IP Helper で `wired`/`wifi` かつ ConnectorPresent、かつ MSFT_NetAdapter で HardwareInterface・ConnectorPresent・Virtual でない・Hidden でない・NdisPhysicalMedium 14/9。
2. 4 つの表(インターフェース・アドレス・経路・積層)がすべて完全に読めた(失敗・切り詰め → `information_incomplete`)。
3. 積層: 対象から上下に辿り、NDIS フィルター以外のインターフェース(表に無い番号を含む)に到達しない(チーム・多重化・上に載ったトンネル等 → `dependency_present`)。フィルターは全ミニポートの上に積まれるため経由して辿る。
4. OperStatus が down / lower_layer_down、かつ MediaConnectState が disconnected(IP Helper とプロバイダーの両方)。up・dormant・testing・unknown、メディア接続・不明 → `active_adapter`。
5. アドレス表・経路表が空でない(稼働中の Windows には必ずループバックのアドレスと経路があるため、空の結果を「使われていない」証拠にしない → `information_incomplete`)。
6. IP アドレスが 1 つもない(DAD 状態を問わず、リンクローカルを含む → `address_assigned`)。
7. その LUID を使う経路が 1 つもない(`route_in_use`)。
8. Wi-Fi の場合: ハードウェアでない ifType 71 のインターフェース(モバイル ホットスポット・Wi-Fi Direct の仮想アダプター。物理 NIC と積層関係を持たない)が up・メディア接続・不明、またはアドレスを持つなら拒否(`dependency_present`)。
9. IP Helper の LUID・Index と MSFT_NetAdapter の NetLuid・InterfaceIndex が一致(`identity_mismatch`)。IP Helper の表に無い対象・同じ GUID の行が複数ある対象は無効化しない。
10. プロバイダーの列挙(数秒かかり得る)の後、開始点の直前にインターフェース表をもう一度読み、同じ LUID・Index の同じ対象について 1〜4・8 を再判定する(その間にリンクが上がれば拒否)。

単なる低トラフィック、他の NIC が up、既定経路でないこと、名前の一致、ループバック接続は条件にしない。

これにより、現在の MCP 接続・Tailscale(Wintun の仮想アダプターは対象外、その下位の物理 NIC はリンク接続中で拒否)・既定経路・稼働中の NIC・Hyper-V 外部仮想スイッチの上流(リンク接続中で拒否)・チーム/ブリッジのメンバー(リンク接続中、または積層で拒否)は無効化されない。結果として無効化できるのは「ケーブルが抜けている/未接続の予備の物理 NIC」だけで、利便性は低い。これは指示どおり安全性を優先した結果である。

### 有効化の条件(再有効化の経路)

無効化したアダプターを同じ ID で戻せることを優先し、無効化中に NDIS が報告を変え得る値(HardwareInterface・ConnectorPresent・物理媒体・InterfaceIndex。両文書とも Index は無効化→有効化で変わり得るとする)には依存しない。条件は、MSFT_NetAdapter で物理の有線/Wi-Fi(上の 1 のプロバイダー側)、インターフェース表と積層表が完全、IP Helper に載っていれば NetLuid と LUID が一致、同じ GUID の行が複数ないこと、分かる Index(IP Helper、無ければプロバイダー)があれば積層に依存がないこと。Index が分からなければ積層は確認しない(有効化を不能にしないため)。

MSFT_NetAdapter が無効化中のアダプターを列挙し続けること(`Get-NetAdapter` が Disabled として表示する挙動)と、そのときプロバイダーの物理フラグが保たれることは**実機で未確認**。保たれない環境では MCP から再有効化できず(`unsupported`)、ローカルの Windows 設定で戻す必要がある。

有効化は経路・メトリック・VPN の挙動を変え得るため、接続の継続は保証しない(description に明記)。

### 実行の順序

`ResolveTargetAsync`(ID/別名 → canonical ID、OS 呼び出しなし)→ 電源ゲート(通常の状態変更)→ ロック `network:topology` + `network:adapter:{id}`(全変更で topology を共有するので、2 つの NIC を同時に判定・無効化しない)→ pending 監査 → ロック内で表 4 つとプロバイダーを読み直し、no-op・判定 → (無効化のみ)インターフェース表の再読と再判定 → `BeginSideEffect()`(最終認可)→ 変更世代を開始 → `Enable`/`Disable` を 1 回 → State を最大 10 回(300 ms 間隔、新しい読み戻しは開始から 6 秒まで)読み戻す。最終認可で拒否された場合は変更世代を進めない。

### 判定の限界

- 開始点直前の再読から呼び出しまでの間(ms 単位)にケーブルが挿される、外部ソフトウェア・ドライバーが構成を変える、といった競合は排除できない。
- 表は OS の観測値で、ドライバーが不正確な状態を報告する場合には判定も誤る。
- Wi-Fi の Direct 仮想アダプター等、ドライバー独自の構成で物理 NIC が積層表に出る場合は `dependency_present` で拒否される(安全側)。

## JSON 契約(要点)

- `network_list_adapters`: `measured_at`、`adapters[]`(`id`、`name`(エイリアス名を `UntrustedText` で検証、パス・URI に見えるものは null)、`aliases`、`kind`、`admin_state`、`oper_state`、`media_state`、`transmit_link_speed_bps` / `receive_link_speed_bps`(null 可)、`ipv4_addresses` / `ipv6_addresses`(各 16 件まで、`address`(スコープ ID なし)・`prefix_length`・`scope` = loopback/link_local/unicast、使用可能(preferred/deprecated)なものだけ)、`ipv4_truncated` / `ipv6_truncated`、`admin_change_blocked_reason`(現在の管理状態の逆への変更が今拒否される理由。null は「ここでは否定されない」。実行時に再判定))、`count`、`truncated`、`unidentified_count`、`addresses_unavailable_reason`。MAC・SSID/BSSID・DNS サフィックス・説明(製品名)・PnP パス・ユーザー/PC 名は返さない。
- `network_get_status`: `adapter_count`、`up_adapter_ids`(ループバック除く)、`ipv4_default_route_adapter_ids` / `ipv6_default_route_adapter_ids`(全件。経路表が不完全なら null と `routes_unavailable_reason`。空配列は「無い」)、`unidentified_default_route_count`、`os_connectivity`(null 可、`os_connectivity_unavailable_reason`)、`unidentified_count`。経路表全体・ゲートウェイ・相手側アドレスは返さない。
- `network_get_traffic`: `measured_at`、`elapsed_ms`、`adapters[]`(`adapter_id`、`name`、`kind`、`oper_state`、`received_bytes_total` / `sent_bytes_total`(2 回目、2 回目が読めなければ 1 回目)、`*_bytes_delta`、`*_bits_per_second`、`rate_unavailable_reason`)、`count`、`truncated`。合算値はない。
- `network_set_adapter_enabled`: `adapter_id`、`requested_enabled`、`previous_admin_state`、`current_admin_state`、`changed`、`blocked_reason`。結果と status/verification:

| 状況 | status / verification / code |
|---|---|
| 読み戻しで既に要求状態(no-op) | success / verified、`changed: false` |
| 変更後に読み戻しが一致 | success / verified、`changed: true` |
| 安全判定で拒否(何もしない) | error、`TARGET_PROTECTED`(種別が対象外なら `NOT_SUPPORTED`)、`blocked_reason`、retryable=false |
| 管理者起動でない | 非公開。直接呼び出しは `ADMIN_REQUIRED` |
| 管理状態が読めない(State が Unknown/Present) | error `OS_API_ERROR`(何もしない) |
| 戻り値が 0・4096 以外かつ読み戻しで状態不変 | error `OS_API_ERROR`、retryable=false |
| 戻り値 0 または 4096(CIM の「ジョブ開始」)で読み戻しが一致しない / 読み戻し不能 | unknown `VERIFICATION_FAILED`、retryable=false |
| 戻り値が 0 以外でも読み戻しが要求状態 | success / verified |
| 呼び出し中の例外 | unknown `OS_API_ERROR`(例外文は返さない) |
| 開始後のタイムアウト・取消 | unknown(executor)。ロック・スロット・電源ゲートは OS 呼び出しの完了まで保持 |

監査: pending を永続化してから変更する。記録する引数は `enabled` と解決済みの `target`(canonical ID)だけ。拒否理由・IP・名前・接続元(既存の監査接続元を除く)・生の WMI 例外は残さない。

## 権限

- 読み取り 3 ツールは通常権限で動く(IP Helper・WinRT の読み取りは昇格不要)。
- 変更ツールは Sysacta のプロセストークンが管理者のときだけ公開する(`WindowsPrincipal.IsInRole(Administrator)`。UAC で昇格していないトークンでは false)。MCP からの昇格・UAC・特権の有効化・資格情報・サービス/タスク作成は行わない。既定 Off で、ツール画面とクライアント権限で明示的に許可する必要がある。既存のアクセス許可は経路の安全判定の代わりにならない。

## 復旧手段

自動の再有効化・タイマー復旧・別 NIC へのフォールバックは実装しない。無効にしたアダプターは PC のローカル操作で戻す:

- 「設定」>「ネットワークとインターネット」>「ネットワークの詳細設定」で対象アダプターの「有効にする」
- または「コントロール パネル」>「ネットワークと共有センター」>「アダプターの設定の変更」で右クリック >「有効にする」
- 管理画面「デバイス」のネットワークカテゴリにも同じ案内を表示する(画面からは変更しない。ローカル復旧ボタンは設けていない)。

これらの手順は実機で未確認(Windows の標準機能)。

## テスト

| 層 | 内容 |
|---|---|
| Core(`NetworkToolTests` / `NetworkSetAdapterEnabledToolTests`、フェイク) | 4 ツールの既定・schema・監査引数・タイムアウト、厳格な引数(予約語・空・前後空白・null・型不正・配列・未知引数・制御文字・過長・大文字 ID・名前・番号・IP)で OS 呼び出し 0、未許可・無効・失効、昇格なしで非公開。一覧の種別・状態・速度・アドレス(スコープ ID 除去、16 件の切り詰め、使えない DAD 状態の除外)、未識別(空・重複 GUID)、切り詰め・部分失敗、無効なアダプターと別名のカテゴリ分離。状態の既定経路(複数・不完全なら null・空との区別)と接続レベル。通信量の差分・単調時間・ゼロ・64 bit 境界・128 bit 演算・減少・消失・追加・LUID/Index/状態の変化・変更世代・2 回目の失敗・経過時間不正・キャンセル・上限。変更の verified、no-op、IP Helper に無い対象の有効化、戻り値エラー・読み戻し不一致・例外・読み戻し不能、25 通りの拒否(いずれも OS 呼び出し 0)、有効化の拒否、接続元アドレスによらない判定、別 NIC の同時変更の直列化と待機後の再判定、別名の固定、待機中の失効、pending 監査失敗、電源遷移、ハング中のロック保持と変更世代、開始前の取消 |
| MCP(`NetworkServerTests` / `NetworkSetServerTests`) | 新規 DB の既定(読み取り On・変更 Off)、既存 DB への追加は無効・既存クライアントに未付与、未付与・無効・昇格なしの拒否、偽の Forwarded / X-Forwarded-For / X-Real-IP で保護が外れないこと、監査に `enabled` と `target` だけ、合成の秘密・IP・名前・拒否理由が DB/WAL/CSV/JSON に残らないこと |
| Windows(`WindowsNetworkTests`) | 構造体のサイズ・オフセット、SOCKADDR_INET の変換、実機の表が完全に読めループバックが識別されること、実際の 1 秒窓の測定、プロバイダーの読み取り(メソッドは呼ばない)、接続レベルの読み取り。**有効化・無効化・切断・経路変更は一切行わない** |
| App(`DevicesViewModelTests`) | ネットワーク項目の表示(種別・状態・速度・アドレス・拒否理由)、見つからない別名の保持 |

## 検証結果

- 独立コードレビュー(2026-11-29、サブエージェント、読み取り専用): 重大 0。重要 4 件(再有効化の経路が無効化中の IP Helper の値に依存、戻り値 4096 の扱い、WMI 列挙のタイムアウトが半同期でないため効かない、文書未更新)と軽微 4 件(重複 GUID、不在アダプターの一覧表示、測定窓のずれ、拒否された開始でも変更世代が進む)、テストの弱点(待機の確認、Windows の測定テストの検査不足、未検証の分岐)をすべて修正または文書化した(窓のずれは文書化のみ)。
- 独立セキュリティレビュー(2026-11-29、サブエージェント、読み取り専用): 重大・高 0。中 2 件(モバイル ホットスポット/Wi-Fi Direct が稼働中の Wi-Fi の無効化、実機確認と進捗の記録不足)、低 5 件(判定と呼び出しの間隔、空の表を完全とみなす、有効化による経路変化、読み戻しの長さ、一覧の可否表示)。中 2 件と低の 1・2・4 を修正、低 5 は一覧で「不在」の場合に `information_incomplete` を返すよう修正、低 3 は残存リスクとして受容(仕様どおり既定 Off・管理者のみ・自動で戻さない)。
- 2026-11-29 Linux/WSL: `dotnet build Sysacta.sln -c Release` 警告・エラー 0、`dotnet test` 成功(Windows 専用はスキップ)。Infrastructure の既存テスト `AuditCapacityTests.Concurrent_writes_...` が全体並列実行時に 1 回失敗し、単独では 2 回成功(Phase 2B の変更はない。負荷による既存の不安定さ)。

## 仕様との差分

- MCP 経由の無効化は「明確に未使用・未接続で IP/経路/依存関係がない物理 NIC」に限定した(phase-2b.md §5 の初期方針どおり)。接続中の NIC はたとえ MCP 経路でなくても無効化できない。
- Hyper-V/WSL は `virtual` に含め、独立した種別にしない(公式フラグで区別できないため)。
- 管理画面にはネットワークのローカル復旧ボタンを設けず、Windows 設定での復旧を案内する。
- エラーコード `TARGET_PROTECTED` を追加した(docs/tools.md の表を更新)。

## 未対応事項

- 実機での読み取りの照合(タスクマネージャー・Windows 設定・`Get-NetAdapter` 等との比較)、無効化済みアダプターが GetIfTable2 にどう現れるか、Wi-Fi/VPN/Hyper-V 環境での種別、管理者起動での Enable/Disable、Enable/Disable の戻り値と所要時間(タイムアウト 15 秒は初期案のまま)。
- 無効化済みアダプターの `admin_state` が IP Helper で `unknown` になる場合、一覧から MSFT_NetAdapter を参照して補う改善(実機の挙動を確認してから判断する)。

## 残存リスク

- 判定と OS 呼び出しの間の外部変更(ケーブル接続、他のソフトウェア)は排除できない。
- ドライバーが報告する状態・フラグが誤っている場合、種別や未使用の判定も誤り得る。
- 有効化は経路・VPN・既定ゲートウェイの選択を変え得る(接続の継続を保証しない)。
- MSFT_NetAdapter の Enable/Disable の公式な戻り値の契約を確認できていない(読み戻しで補っている)。
- 一覧・状態の IP アドレスは認可されたクライアントに返る(ネットワーク状態の正当な結果)。監査・診断ログには残さない。
- 同一ユーザー権限のローカルプロセスによる改ざん(既存の前提どおり対象外)。
