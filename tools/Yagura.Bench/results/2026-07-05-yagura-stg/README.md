# M7-2 実測結果(YAGURA-STG。2026-07-05)

- 環境: Windows 10.0.26200 x64・8 論理コア・7 GiB・SQL Server 2022 Express(既定インスタンス)。詳細は各 JSON の Environment 節
- 実行者: オーナー(YANAI Taketo)。ベンチはブランチ feat/m7-2-measurement-sqlite(d277efd)時点のバイナリ
- **重要**: 本結果の SQL Server 実行の JSON の `IsReconciled: false` は**検証器のベースラインバグによる見かけ上の NG**(SQL Server は同一 DB を実行間で使い回すため保存件数に前実行の累積が混入。fix/m7-2-sqlserver-baseline で修正)。**実行前後の差分で補正すると全実行が損失ゼロで突合成立する**(例: 保存 15,896 − 前回累積 900 = 14,996 = 送信数)

## 補正後の主要な結果

| provider | 実効レート | 判定 |
|---|---|---|
| SQL Server Express(TCP) | 999〜1,998/s: 退避ゼロ / 3,877/s〜: 退避継続(実効 4.5〜5k で頭打ち) | **書き込み上限 ≈ 4,500〜5,000 msg/sec** |
| SQLite(TCP・同一マシン) | 4,994/s: 退避ゼロ / 14,988/s: 退避開始 / 実効上限 ≈ 15,260/s | **上限 ≈ 15,000 msg/sec(Express の約 3 倍)** |
| UDP(SQLite) | 9,978/s: 破棄ゼロ / 19,968/s〜: Q1 破棄(この箱では解析段律速のため OS バッファでなく Q1 に現れる) | 破棄ゼロ上限は 10k〜20k の間 |
