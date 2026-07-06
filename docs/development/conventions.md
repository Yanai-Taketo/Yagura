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
- 機能・状態の記述に影響する PR は、**入口文書（README・SECURITY・CONTRIBUTING）の該当箇所も同じ PR で更新する**（入口文書は「常に現在形」の層。ADR-0005）
- 外部依存を追加・更新する PR は、その日の最新版をライブ検証した記録を body に 1 行残す:
  `Vendored: <pkg> <ver> (verified <cmd> = <ver>, YYYY-MM-DD)`
  （自分の知識にある「最新」を検証なしで最新と書かない）
- 依存追加でインストーラサイズ等の非機能要件に影響する場合は、その影響を body に併記して承認を得る

## 技術的主張の検証

- **同等性の主張は推測で通さない**: 「X は Y と同等」「A で B を代用できる」「C は近似的に D」型の主張は、公式ドキュメントの引用または実機確認 1 回を通してから、設計文書・実装・PR body に書く
- **実環境依存の機能は lab 検証を受け入れ条件に含める**: AD/Kerberos 認証、MSI のアップグレード挙動など、単体テスト・CI では原理的に検証できない領域は、実環境（lab）での検証をその機能の受け入れ条件に組み込む。リリース直前の一括検証に先送りしない

## フォワーダ配布キット（Fluent Bit）の版運用

フォワーダ配布キット（[docs/guides/forward-windows-eventlog.md](../guides/forward-windows-eventlog.md)・[ADR-0008](../adr/0008-forwarder-kit-generation.md)）が「検証済み」と表明する Fluent Bit の版を、どう保守するかのルール。

- **表明する版は単一**とする。実体は `ForwarderKitConstraints.VerifiedFluentBitVersion` を正とし、利用者ガイド・生成 README テンプレートとの一致は `ForwarderKitVersionSyncTests` が CI で機械検知する（人手運用の更新忘れに依存しない）。複数版のサポートレンジは持たない（静的キットは利用者が任意版に差し替えできるが、それは利用者の自己責任であり Yagura の「検証済み」表明の対象外）
- **版を上げるトリガは次の 2 つに限る**。日常的な最新追従はしない（opt-in 機能に対する過剰な保守を避ける）:
  1. **Fluent Bit に重大な脆弱性（CVE）が公表されたとき**（随時）
  2. **各 Yagura リリースの準備時**に、その日の最新版をライブ確認し、必要なら上げる（外部依存の当日ライブ検証の原則と同じ）
- **版を上げる PR の受け入れ条件**: 実機で **導入 → 転送（本文・ホスト名・severity・EventID・Channel）→ 閲覧到達**のスモーク E2E を 1 回通し、その記録を PR body に残す（「検証済み」と表明する以上、ドキュメント変更だけの版更新は認めない——上記「技術的主張の検証」の実体検証原則）。あわせて当日ライブ検証の記録も残す:
  `Vendored: fluent-bit <ver> win64 MSI (verified <方法> = <ver>, YYYY-MM-DD)`
- **CVE の監視**: 自動監視（Dependabot 等）は組まない。各リリース準備時の手動確認を基本とし、既知の制限・現在の検証済み版は [SECURITY.md](../../SECURITY.md) に現在形で記載する。同梱 MSI（ADR-0008 設計条件 9 のオプトイン同梱）を選んだ配布物には版が焼き込まれるため、CVE 公表時は「検証済み版」の更新と、同梱運用者向けの再生成・再配布の案内（利用者ガイド）で対処する

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

### CI 回帰ベンチの基準値更新（architecture.md §5.2 / Issue #62）

CI の回帰ベンチ（`.github/workflows/ci.yml` の「Regression bench」ステップ）は
`tools/Yagura.Bench/baselines/ci-baseline.json` に記録した基準値との**比**で合否判定する
（絶対値の合否は CI では行わない。基準値・許容帯の設計は architecture.md §5.2）。

- **更新は明示の PR で行う**: 基準値ファイルの変更を他の変更に紛れ込ませない（差分が
  レビューで見える形にする）
- **引き下げ方向の更新には理由の分類を PR body に明記する**（どちらか必須）:
  1. **実行環境の変更**: GitHub Actions ランナーのスペック変更・OS イメージ更新など、
     Yagura 自身の変更によらない外部要因
  2. **機能追加による意図的なトレードオフ**: 新機能が性能に恒常的なコストを課す設計判断
     （例: 監査記録の同期書き込み追加）。この場合はトレードオフの妥当性を PR body で説明する
- **「環境変更への追従」名目の小さな引き下げの積み重ねを禁止する**: 性能公称値（M-6）確定後は、
  更新 PR に基準値と公称値の余裕率を明示し、余裕率が閾値を割る引き下げはリリース判定と同じ重さ
  （オーナー承認 + 公開の場での記録）で扱う
- **`toleranceRatio`（許容帯）自体の変更**は基準値の引き下げよりさらに重い変更として扱う——
  許容帯を緩めると基準比較そのものが検知力を失うため、変更には CI 環境の揺らぎの実測データを
  添えること（現行帯は M-5 初回実測で確定済み——architecture.md §9 M-5 と
  `tools/Yagura.Bench/results/2026-07-06-ci-windows-latest-m5/` を参照。実測データの添え方の例を兼ねる）
- **`enforceRatio`（比判定を blocking にするか情報表示に留めるか）の変更**も同様に実測データ必須——
  情報表示への降格は「そのシナリオの CI 上の分布に意味のある帯を引けない」ことの実測
  （例: M-5 初回実測での SustainedZeroDrop の双峰性）を根拠とし、blocking への復帰は安定分布の
  実測を根拠とする
- 基準値ファイルの `_meta.status` は確定状況を機械可読に保つフィールドであり、暫定値のままの
  間は `"provisional-..."` を維持し、CI 実測を経て確定したら更新 PR で `"confirmed"` 等に変更する

## リリース

- 公開済みバージョンタグの再 push 禁止
- リリース artifact には `.sha256` チェックサムを必ず添付する（v0.1 の最初のリリースから適用）
- インストーラ（MSI）はビルドごとに再現しないため、byte-exact な size/SHA256 をドキュメントに書かない。「約 N MB」+ Release artifact の `.sha256` を正とする
