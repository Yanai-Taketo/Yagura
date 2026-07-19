# 引き継ぎ: #351（ADR-0018 送信元の途絶検知）

最終更新: 2026-07-19（同日 2 回目）/ ブランチ `claude/source-silence-reception-state-y2vvjn` = `feat/351-source-silence-config`（main ベースへリベース済み・push 済み・PR 未作成）

## 前提の更新: PR #366 はマージ済み・スタック解消済み

PR #366（#350 メール通知）はレビュー対応 4 コミット（Logging フィルタ・送信例外ガード・パスワード削除・中止時監査）を足したうえで squash マージ済み（main `d82a63f`）。本ブランチは main へリベース済みで、スタックは解消している。レビューの残指摘は Issue #370〜#372、調査中に見つけた CF-6 巻き添え停止は Issue #373 として起票済み。

## 完了した段（全段完了）

| 段 | 内容 | 委任 |
|---|---|---|
| 1 | 設定キー + 構造化配列の解決・検証・エントリ単位無効化 | 2・3 |
| 2 | 追跡コンポーネント（単調クロック・CAS） | 7 |
| 3 前 | 判定器 + イベント ID 1027/1028/1029 | 1 |
| 3 後 | 周期評価・受信段・即時反映への配線 | — |
| 4 前 | drain 合流点からの遅延反映 | — |
| 4 後 | 受信断保留・回復時再アーム・Detail への受信経路併記 | 6 |
| 5 | allowlist 登録（1027/1028）・判定器のロック + 状態スナップショット・管理サービス + 監査 2023・設定画面 `/admin/source-silence`・UI-4 の登録済みマーク/途絶中強調 | 1・4・5 |
| 6 | 設計書改訂（security.md 2023 行 + 採番・architecture.md §4.6・ui.md §4・configuration.md §8・ADR-0018 改訂履歴 1 = 委任 1〜7 の実施結果） | — |

**全段完了。次の作業は PR 作成**（本メモ末尾参照）。

## 第 4 段 後半（委任 6）の実装内容

- **`IngestionPipeline.ListenerAvailability`**（`ListenerAvailabilitySnapshot` を返す公開プロパティ）を新設。起動 Outcome・再構成 Outcome・CF-6 再試行成功の 3 系統の帰結をリスナ別 `volatile bool` へ畳む。TLS 未構成（`_tlsListener is null`）は `AllListenersDown` の判定に数えない
- **`ActiveNotificationMonitor`** に `listenerAvailabilityProbe`（`Func<ListenerAvailabilitySnapshot>?`）を注入。`EvaluateSourceSilence()` が毎周期観測し、`AllListenersDown` を `Evaluate(receptionSuspended:)` へ渡す。**true → false の遷移で `RearmAfterReceptionRecovery()` を呼ぶ**
- **回復検知はイベント購読ではなくポーリング**にした: 判定器はスレッド安全でなく（監視ループの単一スレッド前提）、`ListenerBindRecovered` はバックグラウンドスレッドから発火する。周期評価内の遷移検知なら同期不要。保留解除が最大 1 周期（1 分）遅れるが閾値下限（10 分）に対して無害。**副作用として 1 周期未満で完結した全リスナ受信断は観測されない**（保留も再アームもされない——観測が欠けない側に倒れており許容）
- **Detail 併記**（委任 6 の 2 点目）: 1027/1028 の本文に「サーバ側受信経路の状態」（`UDP=受信中・TCP=受信不能・TLS=未構成` 形式）を追加。部分受信断はこれで対応する（決定 3——保留はしない）。プローブ未注入時は「不明」
- テスト: `ActiveNotificationMonitorSourceSilenceTests`（保留・再アーム・部分受信断・プローブ未注入の 5 件）+ `IngestionPipelineIntegrationTests` に状態面の 3 件

### 検証環境の注記

本コンテナは Linux のため、Windows 依存テスト（DPAPI・EventLog・Windows サービス・SQL Server LocalDB・IPv6 なし環境のデュアルスタック・E2E）が環境要因で落ちる。**変更領域（ActiveNotification・IngestionPipeline・SourceSilence）は全件グリーン**を確認済み。Windows でのフル 1647 件の確認は次のセッションか lab で。

### 調査中に見つけた既存の潜在問題（→ Issue #373 として起票済み）

`ReconfigureListenersAsync` は冒頭で `CancelBindRetryAsync()` により**全リスナの CF-6 再試行を打ち切る**が、options が変わらなかったリスナ（`NotChanged`）には触れず再試行も張り直さない。つまり「UDP が起動時縮小継続（再試行中）+ 運用者が TCP のポートだけ再読み込みで変更」の組で、**UDP の再試行が巻き添えで止まり、サービス再起動まで UDP が復旧しなくなる**。#262（層2）と #291 の相互作用で、doc コメントの「望ましい構成が変わったため」という前提が部分変更の場合に成立していない。実コード確認済み（`IngestionPipeline.cs` の `ReconfigureListenersAsync` 冒頭と `NotChanged` 分岐）。

## 第 5・6 段の実装内容（2026-07-19 実装）

- **allowlist**: 1027/1028 を `EmailNotificationAllowlist` へ Warning で登録（security.md が先に宣言済みだった実装ギャップの解消）。1029 は対象外のまま
- **判定器**: 全操作をロックで直列化（従来から ApplyWatchlist は再読み込みスレッド・Evaluate は監視ループの並行があり得た）+ `SnapshotEntryStatuses()`（正規化済みアドレス・途絶フラグ）を公開
- **閲覧契約**: `IYaguraSystemStatusReader.ReadSourceSilenceEntries()` を追加（新契約を作らず既存の読み取り専用契約を拡張——閲覧面の参照分離検査の許容リストを増やさない）
- **管理サービス**: `ISourceSilenceAdminService`（状態・候補・全量保存）。保存時検証は拒否に倒す。新規登録は閾値必須・既存の省略は保持。監査 2023 は added/removed/changed のエントリ列挙つき。即時反映は再読み込み経路と同一デリゲート
- **画面**: `/admin/source-silence`（候補選択主経路 + 手入力・値 + 単位の閾値入力・ローカル編集 → 一括適用・「無効（設定を確認）」の識別表示）。ダッシュボード UI-4 に登録済みマーク（表示名つき）+ 途絶中強調 chip
- **委任 4 は「導線を置かない」で決着**（ADR-0018 改訂履歴 1 に記録済み——下記の調査根拠のとおり）

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

## 次の作業: PR 作成

1. `git fetch origin main` で最新化し、乖離があればリベース（現時点では main = `d82a63f` ベース）
2. **PR は `feat/351-source-silence-config` から main へ**。body には ADR-0018 の委任 1〜7 の裁定（ADR 改訂履歴 1 と同内容の要約）・受け入れ基準の充足状況・lab 検証項目（下記）を含める
3. CI green を確認してから merge（規約）。E2E の 30 秒タイムアウト複数同時失敗はランナー負荷フレークを疑いリラン

## lab 検証項目（CI で担保できない受け入れ基準）

> 受信ホットパスへの追加コストの計測は、**ウォッチリスト上限まで登録済み + 登録外送信元が混在する負荷**で行う（空リストの素通し計測では検証にならない）

`Yagura.Bench.Tests` で条件を作れるかは未調査。追跡側は辞書引き 1 回・アロケーションなしに設計してあるが、実測はしていない。
