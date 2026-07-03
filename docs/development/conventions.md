# 開発規約

## コミットメッセージ

- Conventional Commits 形式、英語
- `<type>(<scope>): <subject>`
- type: feat / fix / docs / style / refactor / test / chore / perf / ci
- AI 協働コミットには `Co-Authored-By` フッターを付与する

## ブランチ戦略

- `main` への直接 push 禁止。すべて feature ブランチ（`<type>/<description>`）+ PR 経由
- squash merge を既定とし、マージ後に feature ブランチを削除
- **CI green を確認してから merge する**（修正 push の直後に merge すると、その修正が squash に取り込まれない競合が起こり得る）
- 個人運用期のブランチ保護は `enforce_admins: false` + admin merge（GitHub は自己 approve 不可のため）。contributor 参加時に `true` へ格上げする

## PR の要件

- 機能を追加・変更する PR は、**対応する全体設計書（docs/design/）の更新を同じ PR に含める**
- 外部依存を追加・更新する PR は、その日の最新版をライブ検証した記録を body に 1 行残す:
  `Vendored: <pkg> <ver> (verified <cmd> = <ver>, YYYY-MM-DD)`
  （自分の知識にある「最新」を検証なしで最新と書かない）
- 依存追加でインストーラサイズ等の非機能要件に影響する場合は、その影響を body に併記して承認を得る

## 技術的主張の検証

- **同等性の主張は推測で通さない**: 「X は Y と同等」「A で B を代用できる」「C は近似的に D」型の主張は、公式ドキュメントの引用または実機確認 1 回を通してから、設計文書・実装・PR body に書く
- **実環境依存の機能は lab 検証を受け入れ条件に含める**: AD/Kerberos 認証、MSI のアップグレード挙動など、単体テスト・CI では原理的に検証できない領域は、実環境（lab）での検証をその機能の受け入れ条件に組み込む。リリース直前の一括検証に先送りしない

## コーディング規約

- C# / .NET 標準慣習（PascalCase / camelCase / _camelCase）
- Nullable Reference Types 有効
- 非同期 I/O は async/await、`async void` 禁止
- SQL はパラメータ化クエリ必須
- 時間窓を扱うテストは 1 つの基準時刻から両端を構築する（`UtcNow` の複数回読取は微小なずれで不安定化するため禁止）
- 設定スキーマは additive-only。キー削除は「1 バージョン `[Obsolete]` 警告 → 次バージョン削除」の階段を踏む（キーを一方的に消すと、既存環境の設定ファイルが検証違反で起動不能になる）

## CI

- 依存パッケージの脆弱性スキャンを CI に組み込み、検出時は PR をブロックする
- リリース workflow の GITHUB_TOKEN には `contents: write` を明示付与する（既定は read-only）

## リリース

- 公開済みバージョンタグの再 push 禁止
- リリース artifact には `.sha256` チェックサムを必ず添付する（v0.1 の最初のリリースから適用）
- インストーラ（MSI）はビルドごとに再現しないため、byte-exact な size/SHA256 をドキュメントに書かない。「約 N MB」+ Release artifact の `.sha256` を正とする
