# 旧リポジトリ機能インベントリ（再実装チェックリスト）

旧リポジトリ（yagura-dev v0.5.0 + v0.6 途中）が到達していた機能の一覧。**再実装のスコープ検討時のチェックリスト**として使う。旧設計の踏襲を意味しない（設計は白紙から）。括弧内は旧 SPEC 番号。

**数値に関する注意**: 本文中の具体値（キュー容量 100,000、バッチ 100 件、drop-newest、上限 8KiB 等）はすべて**旧実装の最終値**であり、新実装ではベンチ実測で再決定する（legacy-lessons A-2）。チェックリスト消化時に数値をそのまま写さないこと。

## 受信（Receiver）

- [ ] UDP 514 受信（SPEC-001）
- [ ] TCP 514 受信、RFC 6587 octet-counting / non-transparent framing（SPEC-001）
- [ ] TLS 受信 TCP 6514、RFC 5425、opt-in、平文と共存可（SPEC-020）
- [ ] RFC 3164 / RFC 5424 自動判別パース（SPEC-001)
- [ ] 文字コード自動判別: UTF-8 / Shift-JIS / EUC-JP（SPEC-001、旧実装は Ude + CodePages）
- [ ] メッセージ上限 8KiB truncate(SPEC-001)
- [ ] パース失敗メッセージも破棄せず保存（ParseSucceeded=false）
- [ ] 送信元別流量制御: token bucket + CIDR/単一 IP allowlist、opt-in（SPEC-025）
- [ ] 内部キュー（旧: bounded Channel、最終容量 100,000、drop-newest）+ ドロップ箇所別カウンタ

## 永続化

- [ ] バッチ INSERT（旧: 100 件バッチ、smart drain）（SPEC-002）
- [ ] transient リトライ + 縮退運転（30 秒リトライ、上限超で停止）（SPEC-002）
- [ ] DB 切断時ディスクスプール: JSON Lines、ローテーション、容量上限、復旧後自動 flush、opt-in（SPEC-024）
- [ ] DB provider 抽象（旧 SPEC-023 は未実装。新実装では初日から: SQL Server 既定 / PostgreSQL / MySQL / SQLite）
- [ ] スキーマ: 時刻 + ID の clustered PK による時間範囲クエリ最適化

## 運用 CLI / セットアップ

- [ ] DB 初期化コマンド（旧: init-db、冪等、--dry-run、--json）（SPEC-005）
- [ ] セットアップ失敗時の実行可能 SQL 自動生成（SPEC-007）
- [ ] 保持期間管理 / 定期削除（旧: retention CLI + Task Scheduler 登録、chunked DELETE）（SPEC-010）
- [ ] 設定初期化（旧: config init CLI + MSI sample 配置）（SPEC-027）→ 新実装では Web UI ウィザード + ゼロ設定ファーストランに置換予定
- [ ] Windows サービス化（SCM 統合、開発時 console 実行と両立）（SPEC-003）
- [ ] MSI インストーラ（旧: WiX v5、ファイアウォールルール同梱、サービスリカバリ設定）（SPEC-003）

## 観測性・通知

- [ ] アプリ内メトリクス（parsed/failed/truncated/dropped 等、定期出力）（SPEC-002）
- [ ] OS レベル UDP ドロップ観測（パフォーマンスカウンタ突合 + SO_RCVBUF 設定化）（SPEC-008）
- [ ] ホスト開始/停止イベントのログテーブル保存（SPEC-018）
- [ ] メール通知: Critical 検知 / 受信停止 / OS ドロップ急増、SMTP-AUTH + STARTTLS、opt-in（SPEC-015）
- [ ] スループットベンチハーネス + CI 回帰検出（旧: 1k/3k/5k msg/sec × 60s）（SPEC-026）→ 新実装では早期整備

## Web UI（全面刷新対象）

- [ ] ログ検索: 時間範囲 / Severity / Facility / 送信元 / 自由文、絞り込み強制なし + 上限 cap + 軽量 projection（SPEC-004/009/012/022 の最終形）
- [ ] 検索結果のカーソルページング
- [ ] 詳細表示（一覧は軽量列、詳細は別取得）
- [ ] 現地時刻入力・表示（UTC 保存 + 表示層変換）（SPEC-019）
- [ ] CSV エクスポート（UTF-8 BOM、RFC 4180）（SPEC-011）
- [ ] ダッシュボード: 受信レート時系列 / Severity 分布 / Top Source / OS ドロップ推移（SPEC-013/016）
- [ ] ダッシュボードから検索への drilldown（SPEC-017）
- [ ] 保存検索（旧: localStorage。AD 認証導入後もサーバ側 per-user 保存に戻せなかった課題あり）（SPEC-014）
- [ ] AD 連携認証: Negotiate (Kerberos/NTLM)、opt-in、監査ログ（SPEC-021）
- [ ] ヘルスチェックエンドポイント（/health、匿名可）
- [ ] （新規）セットアップウィザード
- [ ] （新規）デザインシステム: 配色・タイポグラフィ・コンポーネント規約・ダークモード

## 明示的に作らないもの（旧 ADR-0007 / ADR-0011 継承）

- Sender Agent 自作（Windows EventLog → syslog 転送は NXLog CE を案内）
- 商用 SIEM 領域: UEBA / SOAR / 機械学習分析等 10 項目
