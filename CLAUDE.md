# Yagura — AI エージェント向けプロジェクトガイド

このファイルはセッション開始時に自動で読み込まれる。**ここに書かれた参照先を先に読むこと。旧リポジトリやチャット履歴を検索し直す前に、必ず docs/ 配下の共通情報を参照する。**

## プロジェクトの目的

**Yagura** は Windows ネイティブな OSS syslog 集約サーバ。核となる思想は
「**Windows に syslog ツールを簡単に構築できる仕組みを作る**」。

商用製品（Kiwi 等）と Linux ベース OSS（Graylog 等）の中間にある「Windows-shop 向け OSS syslog サーバ」という空白地帯を埋める。Windows 管理者が既に持つスキル（SQL Server・AD・Windows サービス運用）だけで導入・運用できることを最優先する。

## 本リポジトリの位置づけ（2026-07-03 決定）

- 旧リポジトリ `../yagura-dev`（GitHub: Yanai-Taketo/yagura-dev、v0.5.0 で開発中断）の**思想を引き継ぎ、コードは 1 から書き直す**再実装プロジェクト
- 旧リポジトリは「**教訓データベース + 機能チェックリスト**」としてのみ参照する。**旧設計の踏襲を既定にしない**。新しい思想・アーキテクチャを積極的に取り入れる
- 旧リポジトリの教訓の蒸留は [docs/lessons/legacy-lessons.md](docs/lessons/legacy-lessons.md) にある。**旧リポジトリを調べる前にまずこれを読む**

## 確定済みの方針（オーナー YANAI Taketo 合意済み）

1. **既定 DB は SQL Server** を維持。ただし DB provider 抽象を初日から設計に含め、PostgreSQL / MySQL 対応を積極的に行う（旧 SPEC-023 が後付けに失敗した教訓）
2. **ゼロ設定ファーストラン**: インストール直後、DB 設定なしで SQLite により即受信・即閲覧できる。SQL Server へは後から「本番昇格」
3. **セットアップの Web UI ウィザード化**を検討する（CLI と JSON 手編集を前提にしない）
4. **UI は全面刷新**。旧 UI はデザインシステム不在のまま画面を継ぎ足した結果、見た目の品質が低かった。新実装では先にデザインシステム（配色・タイポグラフィ・コンポーネント規約・ダークモード）を決めてから画面を作る
5. **ドキュメントは 2 層構造**: 「常に現在形の全体設計書」+「ADR（決定の履歴）」。旧方式の差分 SPEC 積み重ねは廃止。詳細は [docs/README.md](docs/README.md)
6. **バージョンは v0.1 から始めて v1.0 公開を目指す**
7. セキュリティは「信頼ネットワーク前提 + opt-in 強化」を継承（既定は平文受信・認証なし、TLS/AD 認証は opt-in）
8. スコープ制御: 商用 SIEM 領域（UEBA/SOAR/機械学習等）には踏み込まない（旧 ADR-0011 の NG 10 項目を継承）

## 開発体制

- **オーナー / 最終承認**: YANAI Taketo（対話的に詰めるスタイル。推奨は明確に、率直な第三者評価を添える。質問は選択式で出す）
- AI エージェント協働。設計文書はオーナー承認を得てから実装に着手する
- **設計文書はペルソナレビュー制度を通す**: 5 ペルソナのサブエージェントが draft をレビューし、対話は必ず PR コメントとして GitHub に残す（チャット内で完結させない）。詳細: [docs/development/persona-review.md](docs/development/persona-review.md)
- ブランチ戦略・コミット規約: [docs/development/conventions.md](docs/development/conventions.md)

## ドキュメントマップ（読む順）

| 目的 | 参照先 |
|---|---|
| ドキュメント体系のルール | [docs/README.md](docs/README.md) |
| 旧リポジトリの教訓（技術 trap・プロセス教訓） | [docs/lessons/legacy-lessons.md](docs/lessons/legacy-lessons.md) |
| 旧リポジトリの機能インベントリ（再実装チェックリスト） | [docs/lessons/legacy-feature-inventory.md](docs/lessons/legacy-feature-inventory.md) |
| 意思決定の記録 | docs/adr/ |
| 現在形の全体設計書（設計フェーズで作成） | docs/design/ |
| 開発規約（コミット・ブランチ・コーディング） | [docs/development/conventions.md](docs/development/conventions.md) |
| ペルソナレビュー制度（設計対話の進め方） | [docs/development/persona-review.md](docs/development/persona-review.md) |

## 執筆スタイル

- ドキュメントは日本語優先。コード識別子・固有名詞・確立した略語以外はむやみに英単語を混ぜない
- 技術的な同等性の主張（「X は Y と同等」「A で B を代用できる」）は推測で書かず、実体検証（公式ドキュメント引用または実機確認）を通す
- 外部依存の採用時は当日の最新版をライブ検証してから記録する（学習時点の知識を「最新」と書かない）
