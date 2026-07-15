# ADR（アーキテクチャ決定記録）

「なぜそうしたか」を時系列で積む場所。MADR 形式（[template.md](template.md)）。

## 運用ルール

- 決定が変わったら **supersession**（新 ADR を起こし、旧 ADR の status を superseded にして相互リンク）
  - **部分 supersession（旧 ADR の一部の決定のみを supersede する場合）の記述作法**: 旧 ADR 全体を `superseded` にすると不正確になる（他の決定は有効なまま）。この場合、**旧 ADR の status は `accepted` のまま維持し、①一覧表・状態行に「決定 N は ADR-YYYY により superseded」の注記、②該当決定の冒頭（および波及する他決定の該当箇所）に新 ADR への参照注記を加える**。新 ADR 側は status を `proposed`→`accepted` と通常どおり進め、supersede した範囲を本文で明示する。実例: ADR-0011（ADR-0010 決定 3 のロックアウト機構のみを supersede。本プロジェクト初の部分 supersession）
- 決定は同じで補足・状況変化だけなら **amendment**（該当 ADR に「改訂履歴」セクションを新設して追記。status は accepted のまま）
  - **判定の目安**: 採用した選択肢の適用条件を追加・限定するだけなら amendment。既存の無条件文を条件文に書き換える場合は変更の実質を吟味し、**既定（デフォルトの判断）を反転させるなら supersession、既定を保ったまま例外を足すだけなら amendment**（ADR-0008 改訂履歴 1 の自己吟味が実例）。疑わしければ supersession 側に倒す。なお「既定」の単位は起案者が恣意的に選べる余地があるため、疑わしい場合は**最小の単位で反転の有無を検証**する（大きな軸では「既定を保った」と言えても、より細かい軸では判断が反転していることがある）
  - amendment の日付は改訂履歴セクションに記載し、ヘッダーの日付は起案日のまま据える（ADR-0001 に倣う）
- 何かを先送り（deferral）する ADR には、**再評価のトリガ条件を具体的に明文化**する（放置防止）
- **並行起案の採番**: 複数の ADR が並行して起案中（PR オープン中）の場合、番号は**起案（PR 作成）順に確保し、後発はオープン中の PR が使用済みの番号を飛ばして採番する**。マージ順が前後しても番号は付け替えない（相互参照を壊さないため）。起案の取り下げによる欠番は許容する。本一覧表の行コンフリクトは後からマージされる側の PR が解消する（実例: PR #166 = ADR-0009 と PR #173 = ADR-0010 の並行起案）

## 一覧

| # | タイトル | 状態 |
|---|---|---|
| [0001](0001-project-founding.md) | プロジェクト創設 — 目的・スコープ・開発の原則 | accepted |
| [0002](0002-architecture-principles.md) | アーキテクチャ原則 | accepted（改訂 2026-07-05） |
| [0003](0003-ui-policy.md) | UI 方針 — Blazor・デザインシステム先行・テーマ | accepted |
| [0004](0004-security-model.md) | セキュリティモデル — 信頼ネットワーク前提の定義と防御の既定 | accepted |
| [0005](0005-oss-packaging.md) | OSS 体裁 — README・貢献導線・脆弱性報告窓口 | accepted |
| [0006](0006-v1-release-criteria.md) | v1.0 公開基準 | accepted |
| [0007](0007-reverse-dns-display.md) | 閲覧画面の送信元逆引き（PTR）ホスト名表示 | accepted |
| [0008](0008-forwarder-kit-generation.md) | フォワーダ配布キットの動的生成（管理 UI） | accepted |
| [0009](0009-architecture-support.md) | アーキテクチャ対応拡張 — x64 に加えて ARM64 を採用（x86 は不採用） | accepted |
| [0010](0010-admin-ui-authentication.md) | 管理 UI への認証追加（opt-in）とリモート管理の解禁 | accepted（決定 3 のロックアウト機構は ADR-0011、決定 3 の共存セッションモデルは ADR-0013 により superseded） |
| [0011](0011-app-auth-failure-backoff.md) | アプリ独自認証の失敗試行対策 — ロックアウトからバックオフ + レート制限へ（ADR-0010 決定 3 の supersession） | accepted |
| [0012](0012-admin-https-cert-ui.md) | 管理リモート HTTPS の証明書選択の UI 設定化 | accepted |
| [0013](0013-admin-winauth-session.md) | 管理 UI 認証の共存セッションモデル — 認証成立後の単一 Cookie セッションと Windows 認可の失効反映（ADR-0010 決定 3 の共存セッション部分の supersession。Issue #252） | accepted |
| [0014](0014-code-signing.md) | リリース成果物のコード署名 — Sectigo + Google Cloud KMS による Authenticode 署名（Yagura・ODV 共用。ADR-0006 基準 5・ADR-0009 委任事項 8・Issue #264 を閉じる） | proposed（決定 1 裁定済み。本文承認待ち） |

## 起案予定

（現在なし。新しい委任・決定事項が生じたらここに登録してから起案する）

なお TLS 受信（`Ingestion:Tls:*`）の証明書 UI は ADR-0012 のスコープから分離済み（オーナー裁定 2026-07-11・論点 4）。別 ADR・別 Issue で扱うため、起票時にここへ登録する。

