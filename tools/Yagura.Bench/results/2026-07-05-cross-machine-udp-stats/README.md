# クロスマシン UDP 統計 検証記録(2026-07-05)

## 目的

[architecture.md §4.2](../../../../docs/design/architecture.md) の OS 受信破棄ゲージ(`IPGlobalProperties` の UDP 統計)が、**外部送信元からのトラフィックを計上するか**の実機確認(M7-2)。自己宛 UDP がカウント経路をバイパスする発見(開発機実測)を受けた追加検証。

## 環境

| 役割 | マシン | OS | スペック | IP |
|---|---|---|---|---|
| 受信 | YAGURA-STG | Windows 10.0.26200 x64 | 8 論理コア・7 GiB | 10.0.0.156 |
| 送信 | 開発機 | Windows 11 ARM64 10.0.26200 | — | 10.0.1.4 |

- ICMP 応答 ~320ms(ルータ越え)
- 両実行(1 回目・再実行)とも**同一スクリプト・同一環境・同一日**(2026-07-05)に実施

## 手順

- 受信側が `stats-check.ps1`(本ディレクトリに同梱)を実行(UDP 51999。一時ファイアウォール規則を自動追加・実行後に自動削除)
- 送信側は PowerShell の `UdpClient` で送出
- **TEST A**(受信側が能動的に読み取り): 10,000 通を約 2,000 通/秒で送出
- **TEST B**(受信側が読み取りを停止): 100,000 通を最速(≈1.3 秒)で送出——受信バッファの溢れが確実な条件

## 結果(生の出力行)

### 1 回目

```
TEST A: app received=8601 | Incoming delta=0 Discarded delta=0 Errors delta=0
```

- TEST B は送信タイミングの手違いにより観測窓の外(無効)。有効な TEST B は再実行分を参照

### 再実行

```
TEST A: app received=9999 | Incoming delta=0 Discarded delta=0 Errors delta=0
TEST B: Incoming delta=0 Discarded delta=0 Errors delta=0
```

- TEST B は送信完了後、数秒待ってから読み取り

## 対照(開発機・同日)

自己宛 UDP でも同様に全カウンタ増分 0:

- バースト実測(送信 2,000・アプリ受信 1,006・Q1 破棄 0)の状況で `IncomingDatagrams` / `Discarded` / `Errors` 全て増分 0
- 自ホスト LAN IP 宛 100 通でも増分 0

この対照実験はチャットセッション上の PowerShell 実行で実施したものであり、専用スクリプトは本ディレクトリに収載していない(読み取り API は `stats-check.ps1` と同一)。

## 限界の明示

- 受信側(YAGURA-STG)・送信側(開発機)とも同一 Windows ビルド(10.0.26200)であり、**他ビルド・Windows Server 系列は未検証**
- ベンチ実行中に 2 回観測された「+1」の背景増分の発生源は未特定
