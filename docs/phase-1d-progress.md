# Phase 1D 進捗

## 技術調査（途中）

- [GSMTC マネージャー](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager) はシステムに公開されたセッションの列挙、現在のセッション取得、変更イベントを提供する。現在のセッションは Windows が利用者の操作対象として最も可能性が高いと判断したものであり、列挙順では代用できない。
- [セッション API](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession) の公開プロパティは `SourceAppUserModelId` であり、アプリの ID から個々のセッションの一意性は保証できない。同 API のセッション型は Agile と文書化されている。Windows 10 1809 以降で導入され、ドキュメントには `globalMediaControl` capability が記載されている。非パッケージアプリでの実際の利用可否は未検証。
- [COM の QueryInterface 規則](https://learn.microsoft.com/en-us/windows/win32/com/rules-for-implementing-queryinterface) は、同一 COM 実体に対する `IID_IUnknown` の結果が同じポインターになることを規定する。ただし、この規則だけでは GSMTC の再列挙・現在セッション取得が同一セッションに対して同一 COM 実体を返すことは証明できない。C#/WinRT 投影からの取得方法と同一アパートメントでの限定的な読み取り実測を下記に記録した。ポインターや AUMID は公開 ID としない。
- [WinRT API のデスクトップ利用](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance) では .NET 6 以降に Windows バージョン付き TFM を指定すると Windows SDK の参照が追加される。TFM や依存関係は実装・ビルドで確認してから決定する。
- [シーク API](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.trychangeplaybackpositionasync) の単位は 100 ns ticks、戻り値は成功を示す bool。アプリでの反映を証明する読み戻しとは区別する。[タイムライン](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties) のメディア範囲とシーク可能範囲は別に扱う。
- [操作可否](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionplaybackcontrols) はセッションが現在公開しているフラグで判断する。

## 現状と未検証事項

- Core に 8 ツールの暫定契約と共通の変更フローを追加した。Windows アダプターを追加して本番登録したが、JSON 契約の最終確認・同一性の実機検証・アプリ別の対応状況は未完了。実メディア操作は行っていない。
- Core のフェイクを使った対象テスト 18 件で、入力拒否、現在セッション切替、明示 ID の固定、no-op、操作非対応、曲送りの accepted、API false・開始後の例外の unknown、シーク範囲と単位変換、準備中の権限剥奪、キャンセル後の排他保持、監査データの最小化を確認した。これは実機や COM 同一性の検証ではない。
- .NET SDK 10.0.404、`net10.0-windows10.0.19041.0` と SDK が解決する `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 の一時プロジェクトで、GSMTC 型、C#/WinRT の `IWinRTObject.NativeObject`、`QueryInterface(IID_IUnknown)`、`TryPlayAsync().AsTask()` のコンパイルを確認した（実行は未確認）。[C#/WinRT の相互運用資料](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md) は NativeObject と参照カウントを説明する。`QueryInterface` が返す参照の解放と保持中の寿命管理が必要。
- Linux 上で最新 main の `dotnet build Sysacta.sln -c Release` は SDK 10.0.404 で警告・エラーゼロ。変更前の `dotnet test Sysacta.sln -c Release --no-build -m:1` は Core 440、Infrastructure 50、MCP 91、Windows 27 件が成功し、Windows 実機向け 15 件はスキップ。この時点では Windows 実機、Windows CI、自立型配布、および GSMTC の実動作は未検証だった。
- 今回の Core 追加後も `dotnet build Sysacta.sln -c Release -m:1` は警告・エラーゼロ、`dotnet test Sysacta.sln -c Release --no-build -m:1` は Core 458、Infrastructure 50、MCP 91、Windows 27 件成功、Windows 実機向け 15 件スキップ。これはアダプター追加前の検証結果。
- Windows 実機での COM 同一性・セッション寿命・アプリ別の読み取り結果と、利用者が明示承認した操作の結果を記録する必要がある。実機未確認を成功と見なさない。

## 現在の実装と重要な制約（レビュー途中）

- `Sysacta.Windows`、App およびそれぞれのテストを `net10.0-windows10.0.19041.0` に変更した。Windows SDK の参照は .NET SDK の TFM 解決に任せ、csproj にパッケージバージョンを追加しない。Linux のクロスビルドは一時 NuGet キャッシュを使用して成功。SDK ライセンスは Windows SDK の配布条件に従う。自立型配布と実行時のライセンス・動作は未確認。
- GSMTC マネージャー初期化は共有タスクを使用する。セッション型は Agile、実処理は既存 executor のスレッドプール上で行う。セッション一覧を最大 256 件まで調べ、応答は 64 件まで。上限超過は識別不能として拒否する。セッションごとに `IUnknown` の参照を保持し、一覧から消えた参照を解放する。公開 ID は `m_` と乱数であり、AUMID やポインターを公開しない。
- 一覧変更イベント後は安全側に全公開 ID を失効させるため、変化していない別セッションの ID も失効する。内部ロックには COM 同一性を使うため、ID 失効前の処理と同一実体への新しい処理が並行しないことを狙っている。これはユーザーが要求した「同じセッションの ID を継続して保持」の制約を満たしておらず、実機検証と改善が必要。IUnknown が GSMTC の再取得・再列挙にわたって同じ実体を指すことは公式資料だけでは確認できず、現時点では実機未検証の重大なリスク。
- 現在セッションは解決時とロック取得後の読み取り時に確認し、WinRT 操作の準備完了直前にも現在セッションの同一性を確認する。WinRT 操作は 1 回、キャンセル用トークンを付けず実完了を待つ。タイムアウトした executor はロックとスロットを実完了まで保持する。最終確認と OS が操作を受ける間には原理的な競合が残る。
- 要求 JSON は `session_id` 省略または `m_` と 32 桁の小文字 16 進文字列、`media_seek` は加えて `position_ms`（0〜922337203685477 の整数）を必要とする。状態には状態、操作可否、メタデータ、位置、再生区間、シーク範囲、基準時刻を含む。シーク可能範囲はミリ秒変換時に内側へ丸める。これは暫定契約であり、MCP スキーマと全エラーケースの検証が残る。
- メタデータは制御文字と Unicode format 文字を取り除き、既知の URL・パス形式を除外し、最大 256 文字まで返す。未加工値のハッシュは、公開されるタイトルが除外された場合も曲の変化を検出する補助としてメモリー内でのみ使用する。同一メタデータの別トラック等は区別できず、タイトルと位置の観測時点も一致しない場合がある。アルバム画像、ファイル、URL は取得しない。
- 再生・一時停止・停止は読み戻した状態が目標なら `verified`、API が受理しただけなら `accepted`。曲送り・戻しは受理の確認に留める。シークは停止・一時停止中の厳密な ticks 一致による no-op、または同じメタデータの新しいタイムライン観測で厳密な位置一致を確認した場合だけ `verified`、それ以外は `accepted`。`false` や開始後の例外は副作用の有無が不明なため再試行不可の `unknown`。シーク結果と観測可能なメディア切替の検査には追加テストと Windows 実機での検証が必要。
- Firefox、他のブラウザ、メディアプレイヤー別の操作対応は未確認。Windows 上の読み取り専用テストを追加したが、Linux ではスキップ。実メディアの変更は一切行っていない。

- MCP のフェイクを使う統合テスト 9 件で、8 ツールの schema / outputSchema、書き込み 6 種の未許可拒否、認可済み読み取りと監査の情報最小化、新規 DB と既存 DB の初期許可を確認した。Core の対象テストは 22 件成功。実 OS の制御はテストしていない。

## Windows 実機の読み取り調査（2026-11-26）

通常権限、Windows 11 build 26300、非パッケージの .NET 10 コンソールから、タイトル・アプリ ID の値を記録せずに読み取りだけを実行した。2 セッションが存在し、GSMTC `GetSessions` の 2 回の列挙で 2 件の `IUnknown` が一致した。`GetCurrentSession` 自体の再取得では同一性が一致したが、一覧内のいずれの `IUnknown` とも一致しなかった。同じ AUMID の一覧項目が 1 件存在したが、AUMID はセッション固有ではないため関連付けに使用しない。PowerShell の WinRT 投影と .NET 10 C#/WinRT 投影の双方で同じ結果だった。

このため、現在セッションを安全に一覧の実体へ関連付けられない場合、`session_id` 省略呼び出しは `AMBIGUOUS_TARGET`、一覧・状態の `is_current` は `null` とする。明示 ID を使った操作は別実体にフォールバックしない。実装済みアダプターの読み取り専用プローブでは 2 件を列挙、再列挙でも 2 ID が一致し、2 件の状態・タイトル・位置が取得可能だった。タイトル・アーティスト・アプリ ID 自体は出力・保存していない。これは単一時点の限定的な確認であり、曲送り・セッション終了・再作成・アプリ別の差異や書き込みの検証ではない。

- Windows 側の .NET 10.0.15 で `Sysacta.Windows.Tests.WindowsMediaSessionsTests` のみを実行し、11 件成功・スキップなし。読み取り専用の GSMTC 列挙と、パス・URL の除外、時刻の丸めを確認した。メディア書き込みは含まない。
- 一覧変更後に同じ実体へ新しい公開 ID が割り当てられても旧 ID の処理と同じ内部ロックを使うことを Core フェイクで確認した。Core のメディア対象テストは 23 件成功。実機での一覧変化・終了時の同一性は未確認。

- Linux での直近の全体テストは Core 463、Infrastructure 50、MCP 100、Windows 37 件成功、Windows 専用 16 件スキップ。Windows のメディア読み取りテスト 11 件は別途実行して成功。Windows CI / アプリのスモークテスト、実メディア変更、アプリ別の対応は未確認。
- API が `true` を返した後は受理済みの進捗を保存する。読み戻しがタイムアウト・キャンセルしても `success` / `accepted` と返し、実処理が完了するまで executor のロック・スロットは保持する。受理の証拠がないときは従来どおり `unknown`。受理記録後の追加書き込みは禁止。キャンセル後のロック保持を Core のフェイクで確認した。

### アプリ別の読み取り結果（2026-11-27）

通常権限の読み取り専用プローブで、公開された対応フラグだけを確認した。アプリ種別は AUMID に既知名を含むかによる観測上の分類であり、セッションの識別には使用しない。タイトル・アーティスト・位置・AUMID の値は記録していない。フラグは観測時の値であり、実際の操作成功を意味しない。

| 分類 | Play | Pause | Stop | Next | Previous | Seek | 有効なシーク範囲 |
|---|---|---|---|---|---|---|---|
| Spotify と分類された 1 セッション | 対応 | 非対応 | 対応 | 対応 | 対応 | 対応 | あり |
| その他（アプリ未特定）1 セッション | 対応 | 非対応 | 対応 | 非対応 | 非対応 | 対応 | あり |
| Firefox 等のブラウザ | 未観測 | 未観測 | 未観測 | 未観測 | 未観測 | 未観測 | 未観測 |

利用者の承認がないため実際の再生・停止・曲送り・シークは実行していない。別の状態ではフラグが変化し得る。

### 2026-11-27 検証更新

- Linux: `dotnet test Sysacta.sln -c Release -m:1` は Core 464、Infrastructure 50、MCP 100、Windows 40 件成功、Windows 固有 16 件スキップ。`dotnet build Sysacta.sln -c Release -m:1` は警告・エラーゼロ。Windows 固有 TFM の参照パッケージ取得には書き込み可能な一時 NuGet キャッシュを使用した。
- Windows: .NET 10.0.15 でメディア読み取りテスト 14 件成功。再生操作テストは含まない。
- 監査テストで、他テストの SQLite 接続プール解放により WAL が検査途中に消える競合を確認した。DB/WAL を検査する間は対象 DB の接続を保持するようテストを修正し、MCP テストと全体テストを再実行して成功した。
- `accepted` 記録後の読み戻しキャンセルで受理結果を返しながらロックを保持するケースと、公開 ID が変わっても同じ内部対象のロックを共有するケースを Core のフェイクで確認した。実際の Windows の書き込みとセッション切替は未確認。
- レビューで外部メタデータのサイズ上限を空白走査より先に適用し、過大な文字列に対する処理量を制限した。パス・URL の判定と長さ境界の対象テストは成功。
- レビューで、一覧変更によるキャッシュ参照解放後も進行中の操作と読み戻しが対象の WinRT 投影を保持するよう修正した。キャンセルで応答を返した後もネイティブ操作が完了するまで準備済み参照が生存することをフェイクと GC を使って確認した。
- 参照寿命の修正後、Linux でソリューション全体を再テストし、Core 465、Infrastructure 50、MCP 100、Windows 40 件成功、Windows 固有 16 件スキップ。
- Windows CI（`4280001`）ではビルド・Core・Windows メディアテストは成功したが、DB/WAL の検査中に Windows の排他的な `File.ReadAllBytes` が SQLite 接続と競合し、MCP テスト 1 件が失敗した。共有読み取りで開くよう修正し、該当テストは Linux で成功。修正後の Windows CI は未確認。
- Windows CI（`0539f1d`）ではファイル共有の失敗は解消したが、複数プロジェクトのテストが同時実行され、既存の監査容量ストレステストが SQLite のロック待ちで失敗した。CI ではプロジェクト間を `-m:1` で直列化し、各テスト内の並行書き込み検証は維持する。修正後の CI は未確認。
- 実機で監査テストを単独実行した試行は応答がなく、対象のテストプロセスのみ終了した。実行結果として成功・失敗を数えない。
- レビューでキャンセル通知と副作用開始点の競合を発見し、キャンセル通知から開始前の放棄を原子的に行う登録を共通 executor に追加した。開始ゲート待機中のキャンセルで書き込みが発生しないテストが成功。
- キャンセル境界の修正後、Linux の全体テストは Core 466、Infrastructure 50、MCP 100、Windows 40 件成功、Windows 固有 16 件スキップ。
