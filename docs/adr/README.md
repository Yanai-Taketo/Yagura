# ADR（アーキテクチャ決定記録）

「なぜそうしたか」を時系列で積む場所。MADR 形式（[template.md](template.md)）。

## 運用ルール

- 決定が変わったら **supersession**（新 ADR を起こし、旧 ADR の status を superseded にして相互リンク）
- 決定は同じで補足・状況変化だけなら **amendment**（該当 ADR に「改訂履歴」セクションを新設して追記。status は accepted のまま）
- 何かを先送り（deferral）する ADR には、**再評価のトリガ条件を具体的に明文化**する（放置防止）

## 一覧

| # | タイトル | 状態 |
|---|---|---|
| [0001](0001-project-founding.md) | プロジェクト創設 — 目的・スコープ・開発の原則 | accepted |
| [0002](0002-architecture-principles.md) | アーキテクチャ原則 | accepted |
| [0003](0003-ui-policy.md) | UI 方針 — Blazor・デザインシステム先行・テーマ | accepted |
| [0004](0004-security-model.md) | セキュリティモデル — 信頼ネットワーク前提の定義と防御の既定 | accepted |
| [0005](0005-oss-packaging.md) | OSS 体裁 — README・貢献導線・脆弱性報告窓口 | accepted |

## 起案予定（設計フェーズ）

- 0006: v1.0 公開基準（機能凍結・実運用実績・文書整備・後方互換性の凍結・コード署名の 5 観点の具体化）
