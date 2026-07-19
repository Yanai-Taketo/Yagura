# 引き継ぎ: #351（ADR-0018 送信元の途絶検知）

最終更新: 2026-07-19 / ブランチ `feat/351-source-silence-config`（push 済み・PR 未作成）

## 前提: このブランチは PR #366 にスタックしている

`feat/350-email-notification`（[PR #366](https://github.com/Yanai-Taketo/Yagura/pull/366)）から分岐している。#351 の委任 3 は #366 で作った**配列キーの差分検出機構**に直接依存するため、main から切ると機構ごと失う。

**#366 がマージされたら、このブランチを main へリベースしてから PR を作ること。**

## 完了した段（5 コミット・全テスト 1639 件グリーン）

| 段 | 内容 | 委任 |
|---|---|---|
| 1 | 設定キー + 構造化配列の解決・検証・エントリ単位無効化 | 2・3 |
| 2 | 追跡コンポーネント（単調クロック・CAS） | 7 |
| 3 前 | 判定器 + イベント ID 1027/1028/1029 | 1 |
| 3 後 | 周期評価・受信段・即時反映への配線 | — |
| 4 前 | drain 合流点からの遅延反映 | — |

**現時点で機能として動作する**（ウォッチリストを設定すれば途絶を検知して 1027 を出す）。

## 次にやること: 第 4 段 後半（委任 6）

**唯一の未接続機能**。`SourceSilenceDetector.Evaluate(receptionSuspended:)` の引数を、現在 `ActiveNotificationMonitor.EvaluateSourceSilence()` が**常に `false`** で渡している。

### 何が要るか

ADR-0018 決定 3 は「全受信リスナが受信不能な間は途絶判定を保留し、受信経路の回復時に再アームする」と決めている。`IngestionPipeline` には材料が揃っているが、**「今この瞬間の可否」を問い合わせる口がない**。

| 既存 | 性質 |
|---|---|
| `StartListenerAsync` → `ListenerStartupResult`（`IsDegraded` を持つ） | 起動時 1 回の戻り値 |
| `ReconfigureListenersAsync` → `ListenerReconfigurationResult` | 再構成時 1 回の戻り値 |
| `ListenerBindRecovered` イベント（`ListenerBindRecovery`） | 回復の瞬間に 1 回発火 |

この 3 つを購読して 1 つの真偽値（全リスナ受信不能か）へ畳む小さな状態面を `IngestionPipeline` に足す。**新しい観測機構を作るわけではない**——調査の結果、当初 ADR の記述から想像したより軽い。

### 接続先

- `EvaluateSourceSilence()` の `receptionSuspended: false` を実際の状態へ差し替える
- 回復時に `SourceSilenceDetector.RearmAfterReceptionRecovery()` を呼ぶ（実装・テスト済み。呼び出し側が未接続）

### 現状で許容している挙動

保留しないことで起きるのは「サーバ側障害を装置側の途絶として通知する」偽陽性。逆（サーバ障害中に装置の途絶を見逃す）より**観測が欠けない側**に倒れているため、未完成のままでも安全側。

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
