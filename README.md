# Yagura（やぐら）

> **English**: Yagura is a Windows-native open-source syslog server, designed so that Windows administrators can set up log aggregation using only the skills they already have (Windows services, SQL Server, Active Directory) — no Linux VM, no license fees. It is in early development and has no release yet; the design records (ADRs, in Japanese) live under [docs/adr/](docs/adr/). License: Apache-2.0.

**Windows に syslog ツールを簡単に構築できる仕組みを作る** — それがこのプロジェクトの目的です。

商用製品のライセンス費用も、Linux サーバの構築・運用スキルも要らない、Windows 管理者のための OSS syslog 集約サーバを作っています。

## 現在の状態

**開発初期です。まだリリースはありません。**

現在は設計フェーズにあり、意思決定はすべて [ADR（アーキテクチャ決定記録）](docs/adr/) として公開しています。

## 確定している設計原則

- **導入体験の原則**: インストールから最初のログ閲覧まで、追加のサーバ製品の導入や設定ファイルの手編集なしで到達できること。最初は同梱の組み込みデータベースで動き、本番運用時に SQL Server 等へ昇格できます
- **品質の原則**: ログを失わないこと。失った場合に必ず観測できること。取りこぼしは発生箇所別に計測され、性能は実測で検証されます
- **セキュリティ**: 信頼ネットワーク（社内 LAN 等）での利用を前提とし、TLS 受信・AD 認証・HTTPS は必要な環境で有効化する方式です。既定でも管理操作はサーバ上からのみ実行できます
- **スコープ**: syslog の受信・保存・閲覧・通知に特化します。SIEM の分析機能等には踏み込みません（詳細は [ADR-0001](docs/adr/0001-project-founding.md)）

## ドキュメント

| 知りたいこと | 場所 |
|---|---|
| プロジェクトの目的・スコープ | [ADR-0001](docs/adr/0001-project-founding.md) |
| 設計の意思決定の一覧 | [docs/adr/](docs/adr/) |
| ドキュメントの体系 | [docs/README.md](docs/README.md) |
| 貢献の方法 | [CONTRIBUTING.md](CONTRIBUTING.md) |
| 脆弱性の報告 | [SECURITY.md](SECURITY.md) |

## ライセンス

[Apache License 2.0](LICENSE)
