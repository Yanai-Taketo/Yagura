# Yagura — AI エージェント向けプロジェクトガイド

このファイルはセッション開始時に自動で読み込まれる。**ここに書かれた参照先を先に読むこと。旧リポジトリやチャット履歴を検索し直す前に、必ず本ファイルと参照先の共通情報を確認する。**

## プロジェクトの目的

**Yagura** は Windows ネイティブな OSS syslog 集約サーバ。核となる思想は
「**Windows に syslog ツールを簡単に構築できる仕組みを作る**」。

商用製品（Kiwi 等）と Linux ベース OSS（Graylog 等）の中間にある「Windows-shop 向け OSS syslog サーバ」という空白地帯を埋める。Windows 管理者が既に持つスキル（SQL Server・AD・Windows サービス運用）だけで導入・運用できることを最優先する。正式な目的・スコープは [ADR-0001](docs/adr/0001-project-founding.md) を参照。

## 本リポジトリの位置づけ（AI 向け内部情報）

- 旧リポジトリ「yagura-dev」（v0.5.0 で開発中断）の**思想を引き継ぎ、コードは 1 から書き直す**再実装プロジェクト。**旧リポジトリの GitHub リポジトリはオーナーが削除予定**（2026-07-03 決定）のため、将来のセッションでは参照できない前提で動くこと。教訓・機能一覧・生データ（.claude/lessons/ 配下）が唯一の記録である
- **旧設計の踏襲を既定にしない**。設計は白紙から行い、新しい思想を積極的に取り入れる。**テストコードも含めて完全に新規で書く**（旧テストの選別移植はしない。2026-07-03 オーナー決定）
- **公式文書（docs/ 配下・README 等）には旧リポジトリの存在・経緯を書かない**（2026-07-03 オーナー指示）。新規プロジェクトとして自立した内容にする。旧リポジトリへの言及が許されるのは本ファイルと .claude/ 配下、および PR/Issue 上の対話のみ
- 旧リポジトリの教訓は [.claude/lessons/legacy-lessons.md](.claude/lessons/legacy-lessons.md)、機能チェックリストは [.claude/lessons/legacy-feature-inventory.md](.claude/lessons/legacy-feature-inventory.md) に蒸留済み。**旧リポジトリを調べる前にまずこれらを読む**

## 確定済みの方針（オーナー YANAI Taketo 合意済み）

1. **既定 DB は SQL Server** を維持。ただし DB provider 抽象を初日から設計に含め、PostgreSQL / MySQL 対応を積極的に行う
2. **ゼロ設定ファーストラン**: インストール直後、DB 設定なしで SQLite により即受信・即閲覧できる。SQL Server へは後から「本番昇格」
3. **セットアップの Web UI ウィザード化**を検討する（CLI と JSON 手編集を前提にしない）
4. **UI は Blazor**（.NET 10 Razor Components。通常画面は静的 SSR、ウィザード・ダッシュボード等の対話画面のみ Interactive Server）。**デザインシステムを先に決めてから画面を作る**。方向性は「ライト基調 + ダーク切替」
5. **ドキュメントは 2 層構造**: 「常に現在形の全体設計書」+「ADR（決定の履歴）」。差分仕様書の積み重ねはしない。詳細: [docs/README.md](docs/README.md)
6. **バージョンは v0.1 から始めて v1.0 公開を目指す**
7. セキュリティは「信頼ネットワーク前提 + opt-in 強化」（既定は平文受信・認証なし、TLS/AD 認証は opt-in）
8. スコープ制御: 商用 SIEM 領域には踏み込まない（ADR-0001 の明示 NG 一覧を参照）

## 開発体制

- **オーナー / 最終承認**: YANAI Taketo（対話的に詰めるスタイル。推奨は明確に、率直な第三者評価を添える。**質問は選択式で出す**）
- AI エージェント協働。設計文書はオーナー承認を得てから実装に着手する
- **設計文書はペルソナレビュー制度を通す**: 5 ペルソナのサブエージェントが draft をレビューし、対話は必ず PR コメントとして GitHub に残す（チャット内で完結させない）。詳細: [docs/development/persona-review.md](docs/development/persona-review.md)
- ブランチ戦略・コミット規約: [docs/development/conventions.md](docs/development/conventions.md)

## ドキュメントマップ（読む順）

| 目的 | 参照先 |
|---|---|
| 正式な目的・スコープ・運営原則 | [docs/adr/0001-project-founding.md](docs/adr/0001-project-founding.md) |
| ドキュメント体系のルール | [docs/README.md](docs/README.md) |
| 意思決定の記録と起案予定 | [docs/adr/README.md](docs/adr/README.md) |
| 現在形の全体設計書（設計フェーズで作成） | docs/design/ |
| 開発規約（コミット・ブランチ・コーディング・検証） | [docs/development/conventions.md](docs/development/conventions.md) |
| ペルソナレビュー制度（設計対話の進め方） | [docs/development/persona-review.md](docs/development/persona-review.md) |
| 旧リポジトリの教訓（AI 用内部資料） | [.claude/lessons/legacy-lessons.md](.claude/lessons/legacy-lessons.md) |
| 旧リポジトリの機能インベントリ（AI 用内部資料） | [.claude/lessons/legacy-feature-inventory.md](.claude/lessons/legacy-feature-inventory.md) |

## 執筆スタイル

- ドキュメントは日本語優先。コード識別子・固有名詞・確立した略語以外はむやみに英単語を混ぜない
- 技術的な同等性の主張（「X は Y と同等」「A で B を代用できる」）は推測で書かず、実体検証（公式ドキュメント引用または実機確認）を通す
- 外部依存の採用時は当日の最新版をライブ検証してから記録する（学習時点の知識を「最新」と書かない）
