# セキュリティポリシー / Security Policy

> **English**: Yagura's default configuration assumes a **trusted network** (a LAN segment under a single administrative authority, not reachable from the internet): syslog reception is plaintext, and the web viewer is unauthenticated by default (read-only). Write operations (configuration, database switch-over) are restricted to the server itself via a loopback-only admin listener. Opt-in hardening is available: TLS syslog reception, admin-UI authentication (Windows/AD or app credentials, with HTTPS required for remote admin access), and viewer-UI authentication (Windows integrated auth with AD-group role mapping, since v0.4.0), and HTTPS for the viewer UI (same port, no plaintext fallback on certificate failure). Please report vulnerabilities via GitHub's **Private Vulnerability Reporting** on this repository (Security tab → "Report a vulnerability"; a GitHub account is required). Do **not** open a public issue for security problems. We aim to acknowledge reports within 7 days and provide an initial assessment within 14 days. Please refrain from public disclosure until a fix is released; if you receive no response within 90 days, we respect your decision to disclose. Reporters are credited in advisories (anonymity respected on request).

## 設計上の前提 — 信頼ネットワーク

Yagura の既定構成は、次の条件を満たすネットワークでの利用を前提としています（[ADR-0004](docs/adr/0004-security-model.md)）。

1. syslog の送信元・Yagura サーバ・閲覧者が**同一の管理主体**の管理下にある
2. 設置セグメントが**インターネットから直接到達できない**（LAN / 管理 VLAN 等で分離されている）
3. そのセグメントへ**接続できる者を管理主体が把握・統制している**

インターネットに露出するホスト、複数組織が共用するネットワーク、ゼロトラスト方針の環境では、既定構成のまま使わないでください。VPN 経由の在宅勤務端末が同一セグメントに入る構成、保守業者の持ち込み端末の一時接続、ゲスト無線と同居するフラットな LAN も、条件 3 を満たさない可能性があります。

## 既定構成の姿勢（v0.5）

**開いているもの**（信頼ネットワークに委ねる範囲）:

- syslog 受信は**平文**（UDP/TCP 514）
- 閲覧 UI（TCP 8514）は**既定では認証なし**で LAN に公開（読み取りのみ。設定で localhost のみに縮小できます）。既定では信頼ネットワーク内の全員がログを読める前提です——ログに機微情報が含まれる環境では、公開範囲の縮小、または後述の閲覧 UI 認証（opt-in）による AD グループでの閲覧制限をご検討ください

**既定でも守っているもの**:

- **管理操作は localhost 限定**: 設定変更・DB 接続先の切替・保持期間変更などの書き込み系操作は、loopback 専用リスナ（TCP 8515）に束縛されており、サーバ自身からのみ実行できます。この束縛を外す設定キーは存在せず、設定が壊れていても loopback 以外へは開きません（CI の回帰テストで固定しています）
- **低権限で動作**: Windows サービスは仮想サービスアカウント（`NT SERVICE\Yagura`）で動作し、LocalSystem では動きません
- **保存データの保護**: 設定ファイル・組み込み DB・スプールを含むデータルート（`%ProgramData%\Yagura`）は、サービスアカウントと Administrators のみがアクセスできる ACL で保護されます
- **管理操作の監査記録**: 管理操作と拒否された試行を記録し、Windows イベントログへも併記します
- **資格情報の暗号化保存**: 管理画面のウィザードで設定した SQL Server 接続文字列は、Windows DPAPI で暗号化して保存されます（`dpapi:` 接頭辞）。暗号化された設定は**そのマシンでのみ復号できる**ため、設定ファイルを別マシンへコピーした場合は組み込みデータベース（SQLite）へ切り替えて警告します——移行時はウィザードで再入力してください。なお、パスワードを扱わない **Windows 統合認証の利用を引き続き推奨**します
- **テレメトリは行いません**。既定構成では外部への送信も一切行いません（opt-in のメール通知を有効化した場合に限り、利用者自身が設定した SMTP サーバへの送信が発生します——「opt-in 強化の提供状況」参照。外向き通信の全量は設計書 [security.md §1.1 の外向き通信台帳](docs/design/security.md) に列挙しています）

**既知の制限（v0.5）**:

- 設定ファイルを**手で編集**して接続文字列を書いた場合は平文のまま扱われます（資格情報を含む場合は起動時に警告を出しますが、利用者のファイルを自動では書き換えません）。暗号化保存にするには管理画面のウィザードから設定してください
- 同一ホストにリバースプロキシを前置すると、外部からの要求が loopback 発として届き、管理操作の localhost 限定が成立しません。**リバースプロキシの前置は v0.x ではサポートしていません**

## opt-in 強化の提供状況

提供済み（opt-in。既定は無効）:

- **TLS の syslog 受信**（RFC 5425。TCP 6514）: 証明書は Windows 証明書ストア参照方式、サーバ認証のみで相互 TLS は対象外。期限切れ・失効時も受信は止めません。証明書の選択・差し替えは管理画面（[ADR-0019](docs/adr/0019-ingestion-tls-cert-ui.md)）から行えます——このため、**管理面に到達できるローカル実行主体は TLS 受信の無効化・ポート変更・証明書差し替えを保存できます**（反映は再起動時で、独立の監査 ID が残ります）。TLS 受信が止まる事故は「送信元のサイレント脱落」として現れ気づきにくいため、第三者がローカル実行し得る環境では管理 UI 認証（loopback を含む opt-in）の併用を推奨します
- **能動通知のメール送信**（SMTP。既定無効）: 有効化すると、Yagura から利用者設定の SMTP サーバへの外向き通信が発生します（宛先は設定次第で管理セグメント外・公衆網上のサービスにもなり得ます）。SMTP パスワードは Windows DPAPI で暗号化保存し、認証交換の内容（サーバ応答を含む）はログ・監査に記録しません。設定変更・テスト送信は監査に記録されます。停止手段は `Notification:Email:Enabled=false`（[ADR-0017](docs/adr/0017-email-notification.md)）
- **管理 UI 認証**: Windows 統合認証（AD/Kerberos。Kerberos-only 可）またはアプリ独自 ID/パスワード認証。リモート公開時は HTTPS 必須（fail-closed）（[ADR-0010](docs/adr/0010-admin-ui-authentication.md)・[ADR-0011](docs/adr/0011-app-auth-failure-backoff.md)・[ADR-0012](docs/adr/0012-admin-https-cert-ui.md)）
- **閲覧 UI 認証**（v0.4.0）: Windows 統合認証 + AD グループマッピング（「閲覧」「管理」役割・ネストグループ対応・名/SID 両指定。管理 ⊇ 閲覧）。既定は現状どおり無認証（[ADR-0010](docs/adr/0010-admin-ui-authentication.md) Phase 4）
- **フォワーダ MSI の管理画面アップロード**（[ADR-0020](docs/adr/0020-forwarder-msi-upload.md)）: 配布キットに同梱する Fluent Bit MSI を管理画面から配置・置換・削除できます。**管理 UI 認証が有効で、かつ loopback にも認証を課した構成（= 管理リスナに無認証の到達経路が存在しない）でのみ有効化でき**、満たさない設定は起動時に fail-closed で拒否されます。有効化しても、配置フォルダの既定 ACL（サービスアカウント読み取りのみ）は変わらず、**OS 管理者が `icacls` で書き込み権限を明示付与している間だけ**書き込みが成立します（Yagura 自身は権限を変更しません。撤去も自由）。配置・削除・拒否はすべて操作者の利用者名つきで監査に記録されます

- **閲覧 UI の HTTPS**（[ADR-0022](docs/adr/0022-viewer-https.md)）: 閲覧リスナ（既定 8514）を同一ポートのまま HTTPS へ切り替えられます（平文の面は残りません）。証明書は Windows 証明書ストア参照方式で、**期限切れ・失効時は平文 HTTP へ落とさず HTTPS を停止します**（syslog 受信・管理リスナは影響を受けず、サーバ上の閲覧・復旧は管理画面から可能）。期限接近は能動通知（メール opt-in 併用可）で事前警告します。閲覧認証（特に `Viewer:AdminGroups`——管理等価 Cookie が平文で流れ得る構成）では HTTPS の併用を推奨します（該当構成には起動時警告 1038 が出ます）。**残存リスク**: 既定無認証 loopback に到達できるローカル実行主体は閲覧 HTTPS の無効化・証明書差し替えを保存でき、次回再起動で LAN の閲覧経路が平文化し得ます（反映は再起動時・独立監査 ID 2029 が残ります。loopback 認証 opt-in で緩和できます——TLS 受信の同種リスクと同じ構造）

## フォワーダ（Fluent Bit）について

Windows イベントログの転送には、第三者の [Fluent Bit](https://fluentbit.io/)（Apache-2.0）を利用します（[配布キット](docs/guides/forward-windows-eventlog.md)・[ADR-0008](docs/adr/0008-forwarder-kit-generation.md)）。セキュリティ上の位置づけは次のとおりです。

- **転送は平文 UDP または平文 TCP** です（信頼ネットワーク前提。上記「設計上の前提」と同じ扱い。配布キットの `install.ps1 -Mode` で選択）。**TLS 暗号化送信は本キットでは提供しません**——Yagura 側は RFC 5425 準拠の TLS 受信に対応しますが、Fluent Bit の `out_syslog` が octet-counting フレーミング非対応のため Yagura の TLS 受信の送信元にできないことを 2026-07-11 に実機確認したためです。TLS 暗号化転送が必要な場合は octet-counting 対応の送信実装（rsyslog・syslog-ng の RFC 5425 モード等）を使ってください（詳細は[利用者ガイド](docs/guides/forward-windows-eventlog.md)「TLS 暗号化転送について」）
- **検証済み Fluent Bit 版は単一**とし、その版は配布キットのガイドと生成物に記載します。Fluent Bit 自体の脆弱性は Fluent Bit プロジェクトの管轄です。Yagura は「検証済み版」を**重大な脆弱性（CVE）の公表時**と**各リリースの準備時**に見直します（手順は [開発規約](docs/development/conventions.md) の「フォワーダ配布キット（Fluent Bit）の版運用」）。依存の自動監視は行いません
- **Fluent Bit の MSI は既定で同梱しません**。管理者が明示的に配置した MSI をオプトインで同梱することはできますが（[ADR-0008](docs/adr/0008-forwarder-kit-generation.md) 設計条件 9）、その MSI は**管理者自身が取得・検証・配置したもの**です。Yagura は配置されたファイルを梱包し、その SHA256 を来歴として記録するのみで、**同梱 MSI の取得元・真正性・脆弱性対応の責任は負いません**。同梱を選んだ配布物には版が焼き込まれるため、CVE 公表時は検証済み版の更新後に再生成・再配布が必要になります

## リリース成果物のコード署名と検証

リリースの MSI と自前アセンブリ（`Yagura.Host.exe`・`Yagura.*.dll`）には Authenticode コード署名（RFC 3161 タイムスタンプ付き）を付与しています。仕組み・体制・共用証明書の限界の開示は[コード署名ポリシー](docs/code-signing-policy.md)を参照してください。

**検証は「署名が有効か」だけでなく「署名者が期待値と一致するか」まで確認してください**（他人の正規証明書で署名された偽物も「有効な署名」にはなります）。

1. **GUI**: MSI のプロパティ →「デジタル署名」タブ → 署名者が「柳井建人」で、証明書の拇印がポリシー記載の期待値と一致すること
2. **PowerShell**: `Get-AuthenticodeSignature <ファイル>` の `Status` が `Valid` で、`SignerCertificate` の Subject / Thumbprint が期待値と一致すること
3. あわせて Release 添付の `.sha256` との照合も利用できます

**WDAC / AppLocker を運用している環境への注意**: 署名対象は自前バイナリのみで、同梱の .NET ランタイム（Microsoft 署名）と第三者 OSS の DLL（多くは無署名）は署名しません。このためインストール内容全体を単一の「発行元」ルールで許可することはできません。パスルール・ハッシュルールとの併用を検討してください。

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

## サポート対象バージョンと、修正の届き方

### 修正を提供する対象

| バージョン | 対応 |
|---|---|
| v0.5（最新リリース） | 対応します |
| `main` ブランチ | 対応します |

修正は最新リリースと `main` に対して提供します。過去のリリースへの遡及修正（バックポート）は行いません。

**この方針を裏返すと、利用者が修正を受け取る条件は「最新リリースへ追随し続けること」だけです。** 特定の版に留まったまま修正を受け取る手段はありません。導入時に固定した版を長く使い続ける運用を予定している場合は、この点をご確認ください。

### v1.0 系列のサポート期限

**v1.0 系列のサポートは 2028-11-14 までです。** 根拠は同梱する .NET 10（LTS）のサポート終了日で、Yagura はこの日付を超えて v1.0 系列のサポートを表明しません（[ADR-0024](docs/adr/0024-supported-environments.md) 決定 7）。

**この期限は、対応 OS・DB のサポート期限より先に来ます。**「OS が生きている限り Yagura も使える」とは読まないでください。

| | 期限 |
|---|---|
| **Yagura v1.0 系列** | **2028-11-14** |
| Windows Server 2019 | 2029-01-09 |
| SQL Server 2019 | 2030-01-08 |
| Windows Server 2022 | 2031-10-14 |
| Windows Server 2025 | 2034-11-14 |

### 同梱している .NET ランタイムの脆弱性について

Yagura は .NET ランタイムを**同梱**して配布します（self-contained。利用者が .NET を別途導入する必要がない代わりに、次の帰結があります）。

- **ランタイムの修正は Windows Update では届きません。** Microsoft が .NET の脆弱性を修正しても、それが利用者に届くのは **Yagura が再ビルド・再署名してリリースしたとき**です。配布の責任は Yagura 側にあります
- **同梱されるランタイムの版は、明示的な依存として宣言されていません。** ビルド時に `global.json` の SDK 指定（`10.0.100` + `rollForward: latestFeature`）が解決したランタイムが焼き込まれます。**このため Dependabot の監視対象に含まれません**（Dependabot が見るのは NuGet パッケージと GitHub Actions のみです）

**対応方針**は、フォワーダ（Fluent Bit）の版運用と同じ形を採ります。

- **自動監視は行いません**（上記のとおり仕組みとして成立しないためです）
- 見直しの契機は次の 2 つです: **① .NET の重大な脆弱性が公表されたとき**、**② 各リリースの準備時**（その時点の最新パッチへ追随します）
- **修正版の提供までの所要期間は約束できません**（個人運用の OSS のため）。重大なものを最優先で扱う点は「応答目標」と同じです

.NET 自体の脆弱性情報は [.NET のリリースノート・アドバイザリ](https://github.com/dotnet/announcements/issues)で公開されています。緊急性の高いものをご存じの場合は、上記の非公開脆弱性報告からお知らせいただけると助かります。

### 対応環境から外れている場合

**v1.0 以降の MSI は、対応外の OS には導入できません**（インストーラが OS のビルド番号を判定して停止します）。これは新規インストールだけでなく**アップグレードにも適用されます**——対応外 OS 上で稼働している既存のインストールは、**そのまま動き続けますが新しい版を受け取れなくなります**。

バックポートを行わない方針（上記）と合わせると、**対応外の OS に留まることは、脆弱性修正を受け取る経路が無くなることを意味します**。対応環境の一覧は [README のシステム要件](README.md#システム要件)を、判断の根拠は [ADR-0024](docs/adr/0024-supported-environments.md) を参照してください。移行が難しい事情がある場合は [Discussions](https://github.com/Yanai-Taketo/Yagura/discussions) でご相談ください。
