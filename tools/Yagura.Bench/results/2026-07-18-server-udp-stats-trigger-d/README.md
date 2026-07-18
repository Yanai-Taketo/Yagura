# Windows Server 実機 UDP 統計 検証記録（2026-07-18）— ADR-0016 再評価トリガ (d)

## 目的

[ADR-0016](../../../../docs/adr/0016-os-drop-gauge-mechanism.md) の再評価トリガ **(d)「M9 の lab 検証（Windows Server 実機）での本 API の動作確認」**の実施。
同 ADR 決定 3・チェックリスト⑦が要求する判定成立条件 3 点——(i) OS バッファ溢れの誘発、(ii) 受信総数側の観測、(iii) discarded 差分の観測——をすべて満たす形で実測した。

## 実施環境

| 項目 | 値 |
|---|---|
| ホスト | `WIN-H31KVKTHCKU`（yagura.test ドメインコントローラ兼検証機） |
| OS | Microsoft Windows Server 2025 Standard Evaluation 10.0.26100 |
| ランタイム | .NET 10.0.9（**製品と同一世代**。SDK 10.0.301） |
| 送信元 | **同一ホスト（自己宛。10.0.0.191:51999）** — 本 lab に第 2 のホストが無いための制約。下記「本検証の限界」参照 |

計測器: [`udp-stats-probe.cs.txt`](udp-stats-probe.cs.txt)（本ディレクトリ同梱。`IPGlobalProperties` を直接読む独立実装。製品コードに依存しない。**拡張子を `.cs.txt` としているのは、本ディレクトリが `Yagura.Bench` プロジェクト配下にあり `.cs` のままではトップレベルステートメントがビルド対象に含まれてエントリポイントが衝突するため**。再実行時は空の console プロジェクトへ `Program.cs` として複写する）
生出力: [`run-net10.log`](run-net10.log)

## 結果

### 1. 発見 A: `UdpStatistics` に `IncomingDatagrams` というプロパティは存在しない

.NET 10 の `UdpStatistics` の実際の公開プロパティ面（probe がリフレクションで列挙した生出力）:

```
  DatagramsReceived                Int64    = 734410
  IncomingDatagramsDiscarded       Int64    = 303
  IncomingDatagramsWithErrors      Int64    = 0
  DatagramsSent                    Int64    = 1494986
  UdpListeners                     Int32    = 2524
  GetProperty("IncomingDatagrams") -> NULL (does not exist)
```

受信総数の正しいプロパティ名は **`DatagramsReceived`** である。`IncomingDatagrams` / `OutgoingDatagrams` は存在しない（.NET Framework 4.8 でも同一。Windows PowerShell 5.1 上でリフレクション確認済み）。

**この事実は既存の検証資産に影響する**: [`../2026-07-05-cross-machine-udp-stats/stats-check.ps1`](../2026-07-05-cross-machine-udp-stats/stats-check.ps1) 11 行目は `$v4.IncomingDatagrams` を読んでいる。PowerShell は存在しないプロパティへのアクセスを `$null` として黙って返すため、同スクリプトが出力する `Incoming delta` は**実トラフィックと無関係に構造的に常に 0** になる。M7-2 の「受信総数の増分 0」という観測は、この読み取り誤りによる測定アーティファクトである（詳細と文書側の訂正は ADR-0016 改訂履歴 1 を参照）。

### 2. TEST A — 能動受信（受信バッファ 8 MiB、5000 通）

```
TEST A RESULT: sent=5000 app_received=5000 | dReceived=5000 dDiscarded=0 dErrors=0
```

`netstat -s -p udp` の `Datagrams Received` も 734410 → 739410（**+5000、送信数と完全一致**）。

**→ 受信総数カウンタ（`DatagramsReceived`）は Windows Server 2025 で正常に機能する。自己宛 UDP もカウント経路をバイパスしない。**

### 3. TEST B — OS バッファ溢れの誘発（受信バッファ 1 KiB、50000 通 × 200B、読み取らず）

```
effective ReceiveBufferSize = 1024
TEST B RESULT: sent=50000 app_received=6 overflow_lost=49994 | dReceived=50001 dDiscarded=0 dErrors=0
```

`netstat -s -p udp`: `Datagrams Received` 739410 → 789411（+50001）、`No Ports` 303 → 303（**不変**）、`Receive Errors` 0 → 0（**不変**）。

**→ 判定成立の 3 条件がすべて満たされている**:
- (i) 溢れの誘発: **成立**。49,994 通が実際に失われた（測定不成立ではない）
- (ii) 受信総数側の観測: **成立**。dReceived=50001 = 全datagram が OS のカウント経路を通過したことの直接証拠
- (iii) discarded 差分: **0**

すなわち「**datagram は確かに OS スタックに到達して計上され、その後ソケット受信バッファで 49,994 通が破棄されたにもかかわらず、`IncomingDatagramsDiscarded` は 1 も動かない**」。

### 4. `IncomingDatagramsDiscarded` の実体

本検証を通じ、`IncomingDatagramsDiscarded` の値は `netstat -s -p udp` の **`No Ports`**（宛先ポートに listener が無く破棄された数）と常に一致した（303 で終始不変）。同様に `IncomingDatagramsWithErrors` は `Receive Errors` と一致する。

Windows の UDP MIB（`MIB_UDPSTATS`）には**ソケット受信バッファ満杯による破棄を表すカウンタが存在しない**。したがって本 API が「OS ソケットバッファでの破棄」を観測できないのは、OS のバージョンや送信元の位置に依存する事象ではなく、**カウンタ体系そのものの構造的な帰結**である。

## 判定

**ADR-0016 再評価トリガ (d) の分岐は「Windows Server でも（破棄の観測手段としては）機能しない」。**

同 ADR の決定 3（撤去）・決定 4（証拠能力の否定）は**維持され、根拠はむしろ強化される**——従来の根拠は「Windows 10.0.26200 という単一ビルドでの経験的な機能不全」だったが、本検証により「`IncomingDatagramsDiscarded` は No Ports であってバッファ破棄ではない」という構造的な理由が特定されたため。

ただし ADR-0016 が記録する M7-2 の根拠のうち**受信総数に関する部分（および「自己宛はカウント経路をバイパスする」という説明）は誤りであり、訂正を要する**（決定は変わらない）。

## 本検証の限界

- **送信元が同一ホストである**（本 lab に第 2 のホストが無い。WSL・Hyper-V・コンテナのいずれも未導入で、DC 上への導入は本検証の範囲外と判断した）。
  ただし TEST B は「datagram が OS のカウント経路を通過したこと」を dReceived=50001 で直接示したうえで discarded が 0 であることを示しており、**破棄観測の否定的結論に外部送信元は必要ない**（溢れは受信側ソケットで起きており、送信元の位置に依存しない）。
- IPv6 側は本 probe では計測していない（IPv4 のみ）。`IncomingDatagramsDiscarded` の IPv6 版も同様に No Ports 相当と推定されるが、**未実測**。
