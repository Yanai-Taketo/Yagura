# 全体設計書（常に現在形）

システムの「今の姿」を記述する層。設計フェーズで以下の構成を予定。

- [architecture.md](architecture.md) — 全体アーキテクチャ（プロセス構成、受信パイプライン、信頼性機構、観測性、性能）
- [database.md](database.md) — 永続化（provider 抽象と契約、論理スキーマ、保持期間、本番昇格、v1.0 凍結対象）
- [configuration.md](configuration.md) — 設定とセットアップ（スキーマ原則、ファイル配置、既定ポートとファイアウォール、資格情報、HTTPS 証明書）
- [ui.md](ui.md) — デザインシステムと画面設計（トークン、共通コンポーネント、画面骨格、用語対応表、アクセシビリティ）
- セキュリティ設計文書（security.md 予定。ファイル名・置き場は起筆時に確定） — loopback 束縛の CI 回帰テスト仕様、circuit 上限、監査記録 ほか
- operations.md — セットアップ・運用手順
- [homework.md](homework.md) — ADR・レビューから委任された宿題の追跡一覧（起筆時にここから引き取る）

**機能を追加・変更する PR は、この層の該当文書の更新を同じ PR に含めること**（docs/README.md 参照）。
