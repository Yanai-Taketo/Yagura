# 引き継ぎ: #351（ADR-0018 送信元の途絶検知）

最終更新: 2026-07-19 / ブランチ `claude/source-silence-reception-state-y2vvjn`（`feat/351-source-silence-config` の続き。push 済み・PR 未作成）

## 前提: このブランチは PR #366 にスタックしている

`feat/350-email-notification`（[PR #366](https://github.com/Yanai-Taketo/Yagura/pull/366)）から分岐している。#351 の委任 3 は #366 で作った**配列キーの差分検出機構**に直接依存するため、main から切ると機構ごと失う。

**#366 がマージされたら、このブランチを main へリベースしてから PR を作ること。**

## 完了した段（6 コミット）

| 段 | 内容 | 委任 |
|---|---|---|
| 1 | 設定キー + 構造化配列の解決・検証・エントリ単位無効化 | 2・3 |
| 2 | 追跡コンポーネント（単調クロック・CAS） | 7 |
| 3 前 | 判定器 + イベント ID 1027/1028/1029 | 1 |
| 3 後 | 周期評価・受信段・即時反映への配線 | — |
| 4 前 | drain 合流点からの遅延反映 | — |
| 4 後 | 受信断保留・回復時再アーム・Detail への受信経路併記 | 6 |

**機能の実装は完結した**（残りは UI・監査・設計書改訂）。

## 第 4 段 後半（委任 6）の実装内容

- **`IngestionPipeline.ListenerAvailability`**（`ListenerAvailabilitySnapshot` を返す公開プロパティ）を新設。起動 Outcome・再構成 Outcome・CF-6 再試行成功の 3 系統の帰結をリスナ別 `volatile bool` へ畳む。TLS 未構成（`_tlsListener is null`）は `AllListenersDown` の判定に数えない
- **`ActiveNotificationMonitor`** に `listenerAvailabilityProbe`（`Func<ListenerAvailabilitySnapshot>?`）を注入。`EvaluateSourceSilence()` が毎周期観測し、`AllListenersDown` を `Evaluate(receptionSuspended:)` へ渡す。**true → false の遷移で `RearmAfterReceptionRecovery()` を呼ぶ**
- **回復検知はイベント購読ではなくポーリング**にした: 判定器はスレッド安全でなく（監視ループの単一スレッド前提）、`ListenerBindRecovered` はバックグラウンドスレッドから発火する。周期評価内の遷移検知なら同期不要。保留解除が最大 1 周期（1 分）遅れるが閾値下限（10 分）に対して無害。**副作用として 1 周期未満で完結した全リスナ受信断は観測されない**（保留も再アームもされない——観測が欠けない側に倒れており許容）
- **Detail 併記**（委任 6 の 2 点目）: 1027/1028 の本文に「サーバ側受信経路の状態」（`UDP=受信中・TCP=受信不能・TLS=未構成` 形式）を追加。部分受信断はこれで対応する（決定 3——保留はしない）。プローブ未注入時は「不明」
- テスト: `ActiveNotificationMonitorSourceSilenceTests`（保留・再アーム・部分受信断・プローブ未注入の 5 件）+ `IngestionPipelineIntegrationTests` に状態面の 3 件

### 検証環境の注記

本コンテナは Linux のため、Windows 依存テスト（DPAPI・EventLog・Windows サービス・SQL Server LocalDB・IPv6 なし環境のデュアルスタック・E2E）が環境要因で落ちる。**変更領域（ActiveNotification・IngestionPipeline・SourceSilence）は全件グリーン**を確認済み。Windows でのフル 1647 件の確認は次のセッションか lab で。

### 調査中に見つけた既存の潜在問題（本変更とは独立・Issue 化はオーナー未裁定）

`ReconfigureListenersAsync` は冒頭で `CancelBindRetryAsync()` により**全リスナの CF-6 再試行を打ち切る**が、options が変わらなかったリスナ（`NotChanged`）には触れず再試行も張り直さない。つまり「UDP が起動時縮小継続（再試行中）+ 運用者が TCP のポートだけ再読み込みで変更」の組で、**UDP の再試行が巻き添えで止まり、サービス再起動まで UDP が復旧しなくなる**。#262（層2）と #291 の相互作用で、doc コメントの「望ましい構成が変わったため」という前提が部分変更の場合に成立していない。実コード確認済み（`IngestionPipeline.cs` の `ReconfigureListenersAsync` 冒頭と `NotChanged` 分岐）。

## その後の段

| 段 | 内容 | 委任 | メモ |
|---|---|---|---|
| 5 | UI（設定画面・UI-4 拡張）+ 監査 | 4・5 | **委任 4 は「導線を置かない」で決着させる方針**（下記） |
| 6 | 設計書改訂 | — | architecture.md §4.6 への追記など |

### 委任 4 は落とす結論で調査済み

キット生成画面（`/admin/forwarder-kit`）で選ぶ「宛先」は **Yagura 自身の受信アドレス**（NIC 候補 + 手入力）。ウォッチリストに登録したいのは**送信元**のアドレスで、キット生成時点では確定しない（配布して動かすまで分からない）。ADR が「軽く接続できる場合のみ。無理はしない」としている条件を満たさない。代替は決定 4 の候補選択（実受信のあるアドレスから選ぶ）で、そちらのほうが転記ミスも起きない。

## 実装中に確定させた設計判断（ADR に書かれていない or 前提が変わったもの）

1. **`ISourceActivityTracker` は `Yagura.Storage` に置いた。** ADR は「`SpoolSelfTestTracker` と同じ注入形」とだけ書いていたが、`Yagura.Ingestion` は `Yagura.Abstractions` を参照していない（受信段の下流はスプールのみ）。両方の書き手から見える層は `Yagura.Storage` しかない。

2. **契約はアドレスを文字列で受ける。** `RawDatagram.SourceAddress` も `LogRecord.SourceAddress` も文字列。ホットパスで `IPAddress` へ解析すると 1 データグラムごとに解析とアロケーションが出る。IPv4-mapped IPv6 の正規化は**ウォッチリスト適用時に前計算**し、同一スロットを両表記のキーで引けるようにしてある。

3. **既存の抑制窓（`NotifyIfDue`）は通していない。** 途絶検知はエントリ別の抑制窓を判定器側に持つ。二重に律速すると装置 A の発火が装置 B の初報を飲む（ADR が明示的に避けている事態）。

4. **委任 3 の前提が古かった。** Issue と ADR は既存配列キーを「比較対象外のため流用不可」と書いているが、PR #366 で配列キー全般が `ConfigurationChangePlanner` の対象になった。**ADR が警戒していた「3 点連鎖の破綻」（平坦化キーによる偽の再起動待ち）は起きない**——planner は論理キーを 1 件だけ積む設計。

5. **オブジェクト配列の平坦化はフィールド名まで照合する**（`KnownObjectArrayKeys`）。`Adress` と綴りを間違えたエントリが黙って無視されるのを防ぐため、未知キーとして警告に出る。

## 既知の非対称（実測済み・解消不能と整理）

配列キーに空文字 `""` を書くと、`YaguraConfigurationLoader`（構成システム）は受理し、`YaguraConfigurationWriter`（System.Text.Json）は `JsonException` で拒否する。

**解消できない**: 構成システム上 `[]` と `""` は完全に同一（実測 2026-07-19）なので、loader 側で拒否すると正常な空リスト（ADR 決定 1）まで巻き添えになる。writer 側で受理させると打ち間違いが黙って空リストに化ける。

**ただし利用者から見た挙動は揃っている**: 起動は writer を先に呼ぶため起動失敗（1024）、再読み込みは `JsonException` 捕捉で適用拒否（1021）。#312 の「再読み込みは通ったのに再起動で起動しない」潜伏の向きにはならない（厳しい側の writer が両経路に居る）。`SourceSilenceConfigurationTests.Load_EmptyStringInsteadOfArray_FailsLoudlyThroughTheWriter` が固定している。

**この性質は #351 固有ではなく既存の `*Groups` 配列キーも同じ。** Issue 化するかはオーナー未裁定。

## lab 検証項目（CI で担保できない受け入れ基準）

> 受信ホットパスへの追加コストの計測は、**ウォッチリスト上限まで登録済み + 登録外送信元が混在する負荷**で行う（空リストの素通し計測では検証にならない）

`Yagura.Bench.Tests` で条件を作れるかは未調査。追跡側は辞書引き 1 回・アロケーションなしに設計してあるが、実測はしていない。
