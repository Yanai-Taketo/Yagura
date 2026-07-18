<!-- PR タイトルは Conventional Commits 形式で(squash 時の最終コミットメッセージになります) -->

## 概要

<!-- 何を・なぜ。関連 Issue があれば #番号 で参照 -->

## チェックリスト

正本は [conventions.md「PR の要件」](docs/development/conventions.md)。該当しない行は削除してください。

- [ ] 機能の追加・変更 → 全体設計書(docs/design/)の更新を同じ PR に含めた
- [ ] 機能・状態の記述に影響 → 入口文書(README・SECURITY・CONTRIBUTING)の該当箇所も更新した
- [ ] 設定キーの追加・変更 → [configuration.md §8](docs/design/configuration.md) を更新し、反映方式と不正時挙動を宣言した
- [ ] ADR の決定に関わる → ADR の記帳(status 更新・決定の追記)を同じ PR に含めた
- [ ] 外部依存の追加・更新 → 当日ライブ検証の記録を下に残した

<!-- 外部依存を追加・更新した場合のみ:
Vendored: <pkg> <ver> (verified <cmd> = <ver>, YYYY-MM-DD)
-->
