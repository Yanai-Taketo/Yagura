# 貢献の手引き / Contributing

> **English**: Issues and pull requests are welcome in English or Japanese. Code and commit messages are in English (Conventional Commits; since we squash-merge, please give your PR a Conventional-Commits-style title). Documentation is Japanese-first. Fork → feature branch → PR; direct pushes to `main` are not accepted. See [docs/development/conventions.md](docs/development/conventions.md) for the full rules.

Yagura への関心をありがとうございます。本プロジェクトは開発初期ですが、Issue・PR を歓迎します。

## 言語

- **Issue・PR・議論**: 日本語・英語のどちらでも構いません
- **コード・コミットメッセージ**: 英語（Conventional Commits 形式）
- **ドキュメント**: 日本語優先

## 変更の流れ

1. 大きな変更は、まず Issue で提案してください（手戻り防止のため）
2. すべての変更は feature ブランチ + Pull Request 経由です（`main` への直接 push は禁止）
3. マージは squash merge で行います。**PR のタイトルを Conventional Commits 形式にしてください**（squash 時の最終コミットメッセージになります。ブランチ内の個々のコミットは規約準拠でなくても構いません）
4. コミット規約・コーディング規約・検証ルールは [docs/development/conventions.md](docs/development/conventions.md) を参照してください

## 設計文書（ADR・全体設計書）への提案

設計文書の変更にはペルソナレビュー制度（[docs/development/persona-review.md](docs/development/persona-review.md)）が適用されますが、**外部コントリビュータがレビューを実行する必要はありません**。レビューの実施はオーナー側の責務です。提案の PR を出していただければ、こちらでレビューを回して結果を PR 上でお返しします。

## AI エージェントとの協働について

本プロジェクトは、オーナー（YANAI Taketo）の承認を最終ゲートとして、AI エージェント（Claude）と協働で開発しています。コミットや PR にある `Co-Authored-By: Claude ...` の表記はその記録です。設計判断の経緯は原則として PR 上のコメントと ADR に残しています。

## 行動規範

思いやりを持って接してください。技術的な批判は歓迎しますが、人への攻撃は受け入れません。
