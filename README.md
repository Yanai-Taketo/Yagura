# Yagura（やぐら）

![CI](https://github.com/Yanai-Taketo/Yagura/actions/workflows/ci.yml/badge.svg)

> **English**: Yagura is a Windows-native open-source syslog server, designed so that Windows administrators can set up log aggregation using only the skills they already have (Windows services, SQL Server, Active Directory) — no Linux VM, no license fees. It is in early development and has no release yet; the design records (ADRs, in Japanese) live under [docs/adr/](docs/adr/). License: Apache-2.0.

**Windows に syslog ツールを簡単に構築できる仕組みを作る** — それがこのプロジェクトの目的です。

商用製品のライセンス費用も、Linux サーバの構築・運用スキルも要らない、Windows 管理者のための OSS syslog 集約サーバを作っています。

## 現在の状態

**開発初期です。まだリリースはありません。リリース時期は未定です。**

設計フェーズを完了し、現在は v0.1 の実装フェーズにあります。意思決定は [ADR（アーキテクチャ決定記録）](docs/adr/) として、全体設計は [docs/design/](docs/design/) として公開しています。進捗を追うには、本リポジトリを Watch するか、Issue・Pull Request の履歴をご覧ください。

## ソースからのビルド

[.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)（`global.json` 参照）が必要です。

```
dotnet build Yagura.sln
dotnet test Yagura.sln
```

## 確定している設計原則

以下は「予定機能」ではなく、承認済みの設計原則です（各リンク先が一次情報です）。

- **導入体験の原則** — インストールから最初のログ閲覧まで、追加のサーバ製品の導入や設定ファイルの手編集なしで到達できる設計です。最初は同梱の組み込みデータベースで動き、本番運用時に SQL Server へ切り替えられます（他のデータベースへの対応は意図として検討中です）。[ADR-0001](docs/adr/0001-project-founding.md) / [ADR-0002](docs/adr/0002-architecture-principles.md)
- **品質の原則** — ログを失わないことを最優先し、失った場合には観測できることを目指す設計です。取りこぼしは発生箇所別に計測され、性能は実測で検証されます。なお「重複よりも欠損を避ける」方針のため、障害からの復旧時などにログの重複保存が起こり得ます。[ADR-0002](docs/adr/0002-architecture-principles.md)
- **セキュリティ** — 管理された社内ネットワークでの利用を前提とし、TLS 受信・AD 認証・HTTPS は必要な環境で有効化する方式です。既定でも管理操作はサーバ上からのみ実行できる設計です。[ADR-0004](docs/adr/0004-security-model.md)
- **スコープ** — syslog の受信・保存・閲覧・通知に特化します。SIEM（ログを横断的に分析するセキュリティ製品分野）の分析機能等には踏み込みません。[ADR-0001](docs/adr/0001-project-founding.md)

## ドキュメント

| 知りたいこと | 場所 |
|---|---|
| プロジェクトの目的・スコープ | [ADR-0001](docs/adr/0001-project-founding.md) |
| 設計の意思決定の一覧 | [docs/adr/](docs/adr/) |
| 現在形の全体設計書 | [docs/design/](docs/design/) |
| ドキュメントの体系 | [docs/README.md](docs/README.md) |
| 貢献の方法 | [CONTRIBUTING.md](CONTRIBUTING.md) |
| 脆弱性の報告 | [SECURITY.md](SECURITY.md) |

## ライセンス

[Apache License 2.0](LICENSE)

同梱する第三者ソフトウェアのライセンス表記は [NOTICE](NOTICE) を参照してください。
