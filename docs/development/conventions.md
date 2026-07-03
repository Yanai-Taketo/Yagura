# 開発規約

旧リポジトリで機能していた規約を基に、教訓（[../lessons/legacy-lessons.md](../lessons/legacy-lessons.md) §C）を織り込んだ改訂版。

## コミットメッセージ

- Conventional Commits 形式、英語
- `<type>(<scope>): <subject>`
- type: feat / fix / docs / style / refactor / test / chore / perf / ci
- AI 協働コミットには `Co-Authored-By` フッターを付与する

## ブランチ戦略

- `main` への直接 push 禁止。すべて feature ブランチ（`<type>/<description>`）+ PR 経由
- squash merge を既定とし、マージ後に feature ブランチを削除
- **CI green を確認してから merge する**（fix push 直後の merge で fix が取り込まれない race が旧リポジトリで実際に発生。PR #140）
- 個人運用期のブランチ保護は `enforce_admins: false` + admin merge（GitHub は自己 approve 不可のため）。contributor 参加時に `true` へ格上げ

## PR の要件

- 機能を追加・変更する PR は、**対応する全体設計書（docs/design/）の更新を同じ PR に含める**
- 外部依存を追加・更新する PR は、当日の最新版をライブ検証した記録を body に 1 行残す:
  `Vendored: <pkg> <ver> (verified <cmd> = <ver>, YYYY-MM-DD)`
- 依存追加でインストーラサイズ等の NFR に影響する場合は、その影響を body に併記して承認を得る

## コーディング規約

- C# / .NET 標準慣習（PascalCase / camelCase / _camelCase）
- Nullable Reference Types 有効
- 非同期 I/O は async/await、`async void` 禁止
- SQL はパラメータ化クエリ必須
- 時間窓を扱うテストは 1 つの基準時刻から両端を構築する（`UtcNow` の複数回読取禁止）
- 設定スキーマは additive-only。キー削除は「1 バージョン `[Obsolete]` 警告 → 次バージョン削除」の階段を踏む

## リリース

- 公開済みバージョンタグの再 push 禁止
- インストーラ（MSI）はビルド非再現のため、byte-exact な size/SHA256 をドキュメントに書かない。「約 N MB」+ Release artifact の .sha256 を正とする
- リリース workflow の GITHUB_TOKEN には `contents: write` を明示付与する
