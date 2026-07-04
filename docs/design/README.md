# 全体設計書（常に現在形）

システムの「今の姿」を記述する層。設計フェーズで以下の構成を予定。

- architecture.md — 全体アーキテクチャ（プロセス構成、受信パイプライン、永続化、UI）
- database.md — DB スキーマと provider 抽象
- configuration.md — 設定スキーマ（additive-only 原則）
- ui.md — デザインシステムと画面設計
- operations.md — セットアップ・運用手順
- [homework.md](homework.md) — ADR・レビューから委任された宿題の追跡一覧（起筆時にここから引き取る）

**機能を追加・変更する PR は、この層の該当文書の更新を同じ PR に含めること**（docs/README.md 参照）。
