# DB-9/DB-10 実測キャンペーン: SQLite 比較関数の性能 / SQL Server COLLATE 移行の DDL 実行時間

database.md §8 の DB-9（SQLite アプリ定義比較関数の性能実測 → 比較関数方式の採否）と
DB-10（SQL Server `ALTER COLUMN ... COLLATE` 移行の実機検証）の実測記録。判定・実測値の要約は
[docs/design/database.md](../../../../docs/design/database.md) §4・§5.4・§8 を正とする——本 README は
生データの索引と実行環境の記録。

## 実行環境

- マシン: DESKTOP-3FI51LK
- OS: Microsoft Windows 10.0.19045 (X64)
- CPU: Intel64 Family 15 Model 107 Stepping 1, GenuineIntel / 論理コア数 8
- メモリ: 11 GiB
- .NET ランタイム: .NET 10.0.9
- SQL Server: SqlLocalDB（MSSQLLocalDB。`.github/workflows/ci.yml` と同じ公式 MSI 導入パターンで
  本セッション中に導入。バージョン 15.0.2000.5）
- 実行日: 2026-07-10

## ファイル一覧

| ファイル | シナリオ | 行数 | 対象 provider |
|---|---|---|---|
| `QueryLatency-20260710-065344.*` | DB-9 | 10 万・100 万 | SQLite |
| `QueryLatency-20260710-070241.*` | DB-9 | 1000 万 | SQLite（単独実行で再測定——後述） |
| `SchemaMigrationDdl-20260710-065346.*` | DB-10 | 10 万・100 万 | SQLite |
| `SchemaMigrationDdl-20260710-065405.*` | DB-10 | 10 万・100 万 | SQL Server（LocalDB） |
| `SchemaMigrationDdl-20260710-070939.*` | DB-10 | 1000 万 | SQLite（単独実行で再測定——後述） |

## 実行コマンド

```powershell
# DB-9: SQLite クエリレイテンシ
Yagura.Bench QueryLatency --rows 100000,1000000 --output-dir <dir>
Yagura.Bench QueryLatency --rows 10000000 --output-dir <dir>   # 単独実行

# DB-10: スキーマ移行 DDL 実行時間（SQLite）
Yagura.Bench SchemaMigrationDdl --rows 100000,1000000 --output-dir <dir>
Yagura.Bench SchemaMigrationDdl --rows 10000000 --output-dir <dir>   # 単独実行

# DB-10: スキーマ移行 DDL 実行時間（SQL Server）
Yagura.Bench SchemaMigrationDdl --rows 100000,1000000 --output-dir <dir> `
    --sqlserver "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;"
```

## 同時実行に関する注意（重要な留保）

10 万・100 万行の 3 実行（QueryLatency・SchemaMigrationDdl×SQLite・SchemaMigrationDdl×SQL Server）は
最初、開発機上で 3 本同時に実行した。1000 万行規模の初回実測はこれと同じ形で 3 本同時に実行したところ、
ディスク I/O 競合により明らかな外れ値（例: QueryLatency の非 ASCII 疎一致語で `LIKE` の 1 試行が
47 秒——同条件の他 2 試行は 6.8 秒——という異常なばらつき）が観測された。**1000 万行規模はこの
初回実測を破棄し、単独実行で再測定した**（上表の `-070241`・`-070939` の 2 ファイル）。
10 万・100 万行規模は同時実行のままだが、値の一貫性（行数比とおおむね整合する伸び方）から
競合の影響は軽微と判断し、再測定は行っていない。

## SQL Server 1000 万行規模: 実測未完遂の記録

SQL Server の 1000 万行規模移行（`SchemaMigrationDdl --sqlserver`）は、単一トランザクション内で
進行する `ALTER COLUMN`（4 列を `NVARCHAR(255)`→`NVARCHAR(MAX)` へ拡張）によりトランザクションログが
大きく肥大し、開発機のディスク空き容量（実測開始時点で約 30 GB）を実行開始から数分で 6 GB 台まで
圧迫した。ディスク枯渇による機材への影響を避けるため、安全側の判断として実測プロセスを中断した
（結果ファイルは残らない）。ロールバック（変更前イメージへの巻き戻し）自体も数分〜十数分規模の
所要時間がかかることを、後片付け作業（`ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE`）
の実測（`sys.dm_exec_requests.percent_complete` で進捗監視。完了まで約 10 分・99% 経過後もしばらく
かかった）で確認した。この事象自体を database.md §5.4 の DB-10 実測結果へ記録した
（トランザクションログ容量計画・分割実行検討の動機として申し送り）。

この過程で発見した副産物: `SqlServerLogStore` のスキーマ管理 DDL コマンドが `CommandTimeout` を
明示しておらず ADO.NET 既定の 30 秒のままだったため、大規模移行で `ALTER COLUMN` が
「実行タイムアウトの期限が切れました」で失敗する欠陥を確認した。本 PR で
`InitializeAsync` 内の全 DDL コマンドを無制限（`CommandTimeout = 0`）へ修正した
（`src/Yagura.Storage/SqlServer/SqlServerLogStore.cs`）。
