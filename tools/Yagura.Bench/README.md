# Yagura.Bench — ベンチハーネス

Issue #60（M7-1）・[architecture.md §5.1](../../docs/design/architecture.md#51-ベンチハーネスv01の開発範囲) の実装。
Yagura の受信パイプラインへ実測負荷をかけ、「送信数 = 保存件数 + 全カウンタ + OS 統計」の突合を
自動で行う開発ツールである。**製品アセンブリには含まれない**（`src/` 配下のどのプロジェクトからも
本プロジェクトへの参照はない）。

本 Issue（M7-1）はハーネスの実装までがスコープであり、実測値の確定・全体設計書への転記は M7-2、
CI 回帰判定は M7-3（Issue #62）で行った。

## CI 回帰判定（M7-3）

`--compare-baseline <基準値ファイル>` を指定すると、実行結果を `tools/Yagura.Bench/baselines/`
配下の基準値ファイルと比較し、許容帯を超える劣化があれば終了コード `3` を返す
（architecture.md §5.2「CI の回帰判定は基準比とする」。絶対値の合否は行わない）。
`.github/workflows/ci.yml` はこのオプションを使い、代表シナリオの短縮版（SustainedZeroDrop・
ProviderWriteCeiling）を全テストの後に実行している。基準値の更新手続きは
[conventions.md](../../docs/development/conventions.md)「CI 回帰ベンチの基準値更新」節を参照。

## 前提

- Windows 上で `dotnet build` 済みであること（`tools/Yagura.Bench` は `Yagura.sln` に含まれる）
- SQL Server シナリオを使う場合のみ、到達可能な SQL Server（LocalDB 可）が必要

## 実行方法

```powershell
# ビルド後の実行ファイルを直接呼ぶ場合
& "C:\Program Files\dotnet\dotnet.exe" tools\Yagura.Bench\bin\Debug\net10.0\Yagura.Bench.dll <scenario> [オプション]

# または dotnet run 経由
& "C:\Program Files\dotnet\dotnet.exe" run --project tools\Yagura.Bench -- <scenario> [オプション]
```

`--help`（引数なしでも同じ）でオプション一覧を表示する。

## シナリオ一覧（Issue #60 記載の 5 種 + DB-9/DB-10 のストレージベンチ 2 種）

| シナリオ | 内容 |
|---|---|
| `Throughput` | 受信スループット計測（UDP/TCP 別。`--transport` で選択） |
| `SustainedZeroDrop` | 破棄ゼロで維持できる持続流量の確認 |
| `BurstQ1Drop` | バースト負荷時の Q1 破棄の発生有無（§3.1 の前提検証。UDP 固定） |
| `SpoolActivationRecovery` | スプール発動 → 追いつきの所要時間 |
| `ProviderWriteCeiling` | SQLite / SQL Server の書き込み上限（`--sqlserver` で SQL Server を対象化） |
| `QueryLatency` | **DB-9**（database.md §4・§8）: SQLite 自由文検索のクエリレイテンシ——ネイティブ `LIKE`（ASCII 限定）とアプリ定義比較関数候補案を行数規模別に比較する |
| `SchemaMigrationDdl` | **DB-10**（database.md §5.4・§8）: スキーマ v1→v2 移行の DDL 実行時間——`--sqlserver` で SQL Server（列 COLLATE 明示 + 索引再構築）、未指定で SQLite（索引のみ）を対象にする |

`QueryLatency`・`SchemaMigrationDdl` は他 5 シナリオ（受信パイプラインの負荷測定。送出・突合の概念を持つ）とは性質が異なり、`Yagura.Storage.ILogStore` へ直接書き込み・直接クエリすることでストレージ層の性能そのものを計測する（`Yagura.Host` 子プロセスは起動しない）。結果は `StorageBenchReport`（別形式）で出力する——詳細は「ストレージベンチ（DB-9/DB-10）の設計」節を参照。

## コマンド例

```powershell
# UDP スループット: 毎秒 2000 通を 15 秒間
Yagura.Bench Throughput --transport udp --rate 2000 --duration 15

# TCP の破棄ゼロ持続流量確認: 毎秒 500 通を 30 秒間
Yagura.Bench SustainedZeroDrop --transport tcp --rate 500 --duration 30

# バースト Q1 破棄の確認: 20000 通を一斉送出（送信側 8 socket）
Yagura.Bench BurstQ1Drop --burst-count 20000 --sender-sockets 8

# スプール発動 → 追いつき: スプール容量を 2MiB に縮小して誘発
Yagura.Bench SpoolActivationRecovery --spool-quota-bytes 2097152 --burst-count 5000

# SQLite の書き込み上限確認（既定 provider）
Yagura.Bench ProviderWriteCeiling --rate 5000 --duration 20

# SQL Server の書き込み上限確認（LocalDB 例）
Yagura.Bench ProviderWriteCeiling --rate 5000 --duration 20 `
    --sqlserver "Server=(localdb)\MSSQLLocalDB;Database=YaguraBench;Integrated Security=true;TrustServerCertificate=true;"

# DB-9: SQLite 自由文検索のクエリレイテンシ（行数規模別）
Yagura.Bench QueryLatency --rows 100000,1000000,10000000

# DB-10: スキーマ v1→v2 移行の DDL 実行時間（SQLite）
Yagura.Bench SchemaMigrationDdl --rows 100000,1000000

# DB-10: スキーマ v1→v2 移行の DDL 実行時間（SQL Server。LocalDB 例）
Yagura.Bench SchemaMigrationDdl --rows 100000,1000000 `
    --sqlserver "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;"
```

## 主なオプション

| オプション | 意味 | 既定値 |
|---|---|---|
| `--transport <udp\|tcp>` | 対象トランスポート | `udp` |
| `--rate <N>` | 持続流量シナリオの目標レート（毎秒） | `1000` |
| `--duration <秒>` | 持続流量シナリオの継続秒数 | `10` |
| `--burst-count <N>` | バーストシナリオの総送出数 | `5000` |
| `--sender-sockets <N>` | 送信側 socket 数（送信側のボトルネック回避。下記「設計上の注意」参照） | `4` |
| `--padding-bytes <N>` | メッセージ本文への追加パディング長 | `0` |
| `--data-root <path>` | Yagura.Host のデータルート | 一時ディレクトリを都度生成 |
| `--output-dir <path>` | 結果 JSON・サマリの出力先 | `./bench-results` |
| `--sqlserver <接続文字列>` | SQL Server provider を対象にする（`ProviderWriteCeiling` 専用） | 未指定（SQLite 対象） |
| `--spool-quota-bytes <N>` | スプール容量（`SpoolActivationRecovery` 専用） | `4194304`（4 MiB） |
| `--keep-data-root` | 終了後にデータルートを削除せず残す（障害調査用） | 残さない |
| `--compare-baseline <path>` | 基準値ファイルと比較し、許容帯超過の劣化を検知する（CI 回帰判定。M7-3） | 未指定（比較しない） |
| `--rows <N1,N2,...>` | `QueryLatency`/`SchemaMigrationDdl` の対象行数規模（カンマ区切り） | `100000,1000000` |

## 結果出力

`--output-dir` 配下に `<シナリオ名>-<タイムスタンプ>.json`（機械可読）と
`<シナリオ名>-<タイムスタンプ>.summary.txt`（人間可読サマリ。実行時に標準出力にも表示する）を書く。
両方に実行環境情報（OS・CPU・論理コア数・メモリ量・データルートのドライブ種別・.NET ランタイム）を
必ず含める。

終了コードは「突合成立（差分 0）」なら `0`、「突合不成立」なら `2`、CLI 引数エラー等は `1`、
`--compare-baseline` 指定時に基準比較が不合格なら `3`。

## 検証器の設計（カウンタ取得方式）

本体プロセス（`Yagura.Host.dll`）を子プロセスとして起動し、負荷生成器はソケット経由で実際に
送出する（tests/Yagura.E2E.Tests と同じ「実バイナリを起動して検証する」設計）。カウンタは
以下の 2 経路から集める:

1. **アプリ内カウンタ 12 種**（内部バッファ破棄・TCP 接続拒否・TCP 接続断・TCP 接続アイドル
   タイムアウト・TCP メッセージ破棄（上限超過。Issue #143・#140 で追加）・TCP 接続再同期上限・
   TCP フレーミング進捗タイムアウト（オーナー決定 2026-07-09 で追加）・スプール退避・
   スプール書込失敗・スプール破棄・永続化失敗・流量制御破棄）: `Meter("Yagura")` を外部プロセスから直接購読する
   標準手段は存在しない（HTTP メトリクスエンドポイントは本リポジトリに未実装）ため、
   architecture.md §4.3 の**メタデータ領域**（`observability-state.json`。一定間隔 + 正常停止時に
   ホストが自ら書き出す）を読む。この方式を機能させるには本体プロセスを**グレースフル停止**
   させる必要があり（Kill だと直近の定期永続化以降の増分が失われ、突合が成立しない——実機検証で
   確認済み）、本ハーネスは Ctrl+C 相当のコンソールシグナルを子プロセスへ送出して
   `IngestionHostedService.StopAsync`（§1.3 の停止手順）を実行させる。
2. **OS レベル UDP 受信破棄**（architecture.md §4.2）: `IPGlobalProperties.GetUdpIPv4/6Statistics()`
   をベンチプロセス自身から直接読む（システム全体統計であり、プロセスを問わず同じ値が返るため）。
   実行前後の差分を突合式に含める——大規模バーストでは OS ソケットバッファ自体が Q1 に届く前に
   溢れることがあり、この経路を含めないと突合が原理的に成立しない。

保存件数は `ILogStore.GetStatisticsAsync()`（database.md §1.2 契約 6）を SQLite/SQL Server の
両方に対して直接呼ぶ（本体の検索 API を経由しない最短経路）。

### グレースフル停止の既知の環境制約

Ctrl+C 相当のコンソールシグナル送出（`AttachConsole` + `GenerateConsoleCtrlEvent`）は、
Win32 の仕様上「呼び出し元プロセス自身が対象と同じコンソールを共有している」ことを要求する
（`GenerateConsoleCtrlEvent` 公式ドキュメント。確認日 2026-07-05）。そのため、**本ベンチ自身が
実 Win32 コンソールを持たない環境（Git Bash/MinTTY 経由の実行、`dotnet test` の VSTest
テストホスト経由の実行等）ではグレースフル停止が機能せず、自動的に `Kill` へフォールバックする**
（フォールバック時は突合精度が低下し得る——`bin/Debug/net10.0/Yagura.Bench.dll` 実行時に警告を
標準エラー出力へ書く）。**通常の PowerShell/コマンドプロンプトから直接 CLI を実行する運用では
正しく機能することを確認済み**。本番ベンチ実行（M7-2 の実測値確定作業）は通常のコンソールから
行うこと。

## ストレージベンチ（DB-9/DB-10）の設計

`QueryLatency`・`SchemaMigrationDdl`（`tools/Yagura.Bench/StorageBench/` 配下）は、他 5 シナリオの
「`Yagura.Host` 子プロセスを起動し、送出→突合で検証する」設計とは異なる:

- **合成データを直接投入する**（`SyntheticDataSeeder`）: 数百万〜数千万行規模のデータを UDP/TCP 送出
  経由で用意すると送受信自体がボトルネックになり非現実的な所要時間になるため、SQLite は 1
  トランザクションへまとめたバッチ INSERT、SQL Server は `SqlBulkCopy` で高速に投入する
  （**投入経路そのものは計測対象ではない**——計測対象はクエリレイテンシ・DDL 実行時間）。
- **スキーマ「v1」からの投入**: `SchemaMigrationDdl` は移行前（v1）の DDL（`tests/Yagura.Storage.ConformanceTests/SqlServerSchemaMigrationTests.cs`
  の `V1SchemaDdl` と同一内容）で投入してから `ILogStore.InitializeAsync()`（移行の実体）を計測する。
  `QueryLatency` も同じ v1 投入 → `InitializeAsync()` → クエリ計測、という手順を踏む（v2 の複合索引が
  実クエリのプランに影響するため——本 README「索引の効き方」の趣旨は `QueryLatencyBenchmark.cs`
  の doc コメント参照）。
- **DB-9 の 3 検索パターン**: ASCII 中選択性語（`error`。約 1% 一致）・非 ASCII 疎一致語（`café`。
  DB-6 保証集合の正例。7 件固定）・該当なし語（全表走査の最悪系）。中間一致は索引に乗らない
  （Issue #145）ため、一致行が少ない語ほど全表に近い走査になる——この最悪系を含めて計測する。
  合否判定は対話的検索のタイムアウト予算（`Budget`。M-10 仮値 30 秒）を基準にするが、**クエリ自体の
  打ち切りには使わない**（`DiagnosticQueryTimeout`。90 秒）——予算超過そのものが判断材料のため、
  超過幅を実測してから記録する。
- **DB-10 の受信継続性 probe**: SQL Server の移行は単一トランザクションで実行される
  （`SqlServerLogStore.InitializeAsync`）。移行開始から一定時間後に別接続から 1 行 INSERT を試み、
  その所要時間（＝実質的なブロック時間）を計測することで「移行中に書き込みがどれだけ継続できるか」
  を実測する。
- **結果は `StorageBenchReport`**（`Reporting/StorageBenchReport.cs`）: 送出・突合の概念を持たない
  軽量な結果型。`ReportWriter` の `StorageBenchReport` 版オーバーロードで JSON・人間可読サマリを
  書く（既存 `ScenarioReport` とは別経路——`Program.cs` の `RunStorageBenchAsync` 参照）。
- **大規模実行の注意**: 1000 万行規模の計測を複数同時に実行すると、開発機ではディスク I/O 競合で
  数値が大きく歪む（実測で確認済み——database.md §4/§5.4 の申し送り参照）。大規模行数の計測は
  1 シナリオずつ順に実行すること。

## 設計上の注意（製品アセンブリからの独立性）

- ソリューションには追加するが、製品の参照構造（architecture.md §1.1）は変えない: 本プロジェクトは
  `Yagura.Host`/`Yagura.Ingestion`/`Yagura.Storage` を参照するが、製品側から本プロジェクトへの
  参照は存在しない（テストと同格の「外側」の扱い）
- 送信側のボトルネック回避: 負荷生成器は `--sender-sockets` 個の並行ソケット（UDP）・接続（TCP）に
  送出を分散する。既定 4。単一ソケットでは送信側 API の同期コストが計測対象（受信側）の性能より
  先にボトルネックになり得るため
- 長時間ベンチ（実測値確定・公称値検証）は本 Issue の対象外。動作確認レベルの短時間実行のみ
