# セキュリティポリシー / Security Policy

> **English**: Yagura's default configuration assumes a **trusted network** (a LAN segment under a single administrative authority, not reachable from the internet): syslog reception is plaintext, and the web viewer is unauthenticated (read-only). Write operations (configuration, database switch-over) are restricted to the server itself via a loopback-only admin listener. TLS reception, HTTPS, and AD authentication are planned as opt-in features by v1.0. Please report vulnerabilities via GitHub's **Private Vulnerability Reporting** on this repository (Security tab → "Report a vulnerability"; a GitHub account is required). Do **not** open a public issue for security problems. We aim to acknowledge reports within 7 days and provide an initial assessment within 14 days. Please refrain from public disclosure until a fix is released; if you receive no response within 90 days, we respect your decision to disclose. Reporters are credited in advisories (anonymity respected on request).

## 設計上の前提 — 信頼ネットワーク

Yagura の既定構成は、次の条件を満たすネットワークでの利用を前提としています（[ADR-0004](docs/adr/0004-security-model.md)）。

1. syslog の送信元・Yagura サーバ・閲覧者が**同一の管理主体**の管理下にある
2. 設置セグメントが**インターネットから直接到達できない**（LAN / 管理 VLAN 等で分離されている）
3. そのセグメントへ**接続できる者を管理主体が把握・統制している**

インターネットに露出するホスト、複数組織が共用するネットワーク、ゼロトラスト方針の環境では、既定構成のまま使わないでください。VPN 経由の在宅勤務端末が同一セグメントに入る構成、保守業者の持ち込み端末の一時接続、ゲスト無線と同居するフラットな LAN も、条件 3 を満たさない可能性があります。

## 既定構成の姿勢（v0.1）

**開いているもの**（信頼ネットワークに委ねる範囲）:

- syslog 受信は**平文**（UDP/TCP 514）
- 閲覧 UI（TCP 8514）は**認証なし**で LAN に公開（読み取りのみ。設定で localhost のみに縮小できます）。信頼ネットワーク内の全員がログを読める前提です——ログに機微情報が含まれる環境では、公開範囲の縮小をご検討ください

**既定でも守っているもの**:

- **管理操作は localhost 限定**: 設定変更・DB 接続先の切替・保持期間変更などの書き込み系操作は、loopback 専用リスナ（TCP 8515）に束縛されており、サーバ自身からのみ実行できます。この束縛を外す設定キーは存在せず、設定が壊れていても loopback 以外へは開きません（CI の回帰テストで固定しています）
- **低権限で動作**: Windows サービスは仮想サービスアカウント（`NT SERVICE\Yagura`）で動作し、LocalSystem では動きません
- **保存データの保護**: 設定ファイル・組み込み DB・スプールを含むデータルート（`%ProgramData%\Yagura`）は、サービスアカウントと Administrators のみがアクセスできる ACL で保護されます
- **管理操作の監査記録**: 管理操作と拒否された試行を記録し、Windows イベントログへも併記します
- **資格情報の暗号化保存**: 管理画面のウィザードで設定した SQL Server 接続文字列は、Windows DPAPI で暗号化して保存されます（`dpapi:` 接頭辞）。暗号化された設定は**そのマシンでのみ復号できる**ため、設定ファイルを別マシンへコピーした場合は組み込みデータベース（SQLite）へ切り替えて警告します——移行時はウィザードで再入力してください。なお、パスワードを扱わない **Windows 統合認証の利用を引き続き推奨**します
- **テレメトリ・外部送信は行いません**

**既知の制限（v0.1）**:

- 設定ファイルを**手で編集**して接続文字列を書いた場合は平文のまま扱われます（資格情報を含む場合は起動時に警告を出しますが、利用者のファイルを自動では書き換えません）。暗号化保存にするには管理画面のウィザードから設定してください
- 同一ホストにリバースプロキシを前置すると、外部からの要求が loopback 発として届き、管理操作の localhost 限定が成立しません。**リバースプロキシの前置は v0.x ではサポートしていません**

## opt-in 強化の提供予定

TLS の syslog 受信（RFC 5425）・Web UI の HTTPS・AD 連携認証（リモート管理の開放を含む）は、v1.0 公開までに opt-in 機能として提供します（[ADR-0006](docs/adr/0006-v1-release-criteria.md) 基準 1）。v0.1 には含まれていません。

## 脆弱性の報告方法

セキュリティ上の問題は、**公開 Issue には書かず**、GitHub の**非公開脆弱性報告**（リポジトリの Security タブ →「Report a vulnerability」）から報告してください。報告には GitHub アカウントが必要です。

### 応答目標

個人運用の OSS として、次の目標で対応します:

- **受領確認**: 7 日以内
- **初期評価**（該当性・重大度の見立て）: 14 日以内
- 修正の期限は約束できませんが、重大なものは最優先で対応します

### 開示について

- 修正の提供・公表まで、脆弱性の詳細の公開を控えていただくようお願いします（協調的開示）
- **報告から 90 日を過ぎても応答がない場合、報告者ご自身の判断で公開されることを尊重します**（応答が止まった場合に報告者が無期限に待たされない出口として明記します）
- 修正時には、報告者を Security Advisory 上でクレジットします（希望があれば匿名のままにします）

## サポート対象バージョン

| バージョン | 対応 |
|---|---|
| v0.1（最新リリース） | 対応します |
| `main` ブランチ | 対応します |

修正は最新リリースと `main` に対して提供します。過去のリリースへの遡及修正（バックポート）は行いません。
