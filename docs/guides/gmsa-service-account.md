# gMSA でのサービス実行（AD 環境向け opt-in）

Yagura サービスの実行アカウントは、既定で仮想サービスアカウント `NT SERVICE\Yagura` です。ドメイン参加の有無にかかわらず追加準備なしで動作し、通常はこのままで問題ありません。

Active Directory 環境では、opt-in で **gMSA（グループ管理サービスアカウント）** によるサービス実行を選べます（[ADR-0015](../adr/0015-gmsa-service-account.md)）。gMSA はパスワードを AD が自動生成・自動ローテーションするサービス専用のドメインアカウントで、Yagura をドメイン上の固有の識別（`DOMAIN\name$`）で動かせます。

> **検証状態の注記**: 本ガイドのうち「lab 実測で確定」と記した項目（SCM 回復設定の推奨値・SPN 登録手順の確定）は、AD lab の受け入れ検証（security.md SEC-14）で確定後に更新します。それまでの記載は設計値・見込みです。

## 1. gMSA を使うべきかの判断

**gMSA を推奨するのは「リモート SQL Server + DB 側の最小権限付与を要する AD 環境」です。**

| 環境 | 推奨 |
|---|---|
| SQLite のまま運用 / SQL Server が同居 | 仮想サービスアカウントのままでよい |
| リモート SQL Server + マシンアカウント（`DOMAIN\HOST$`）への権限付与で足りる | 仮想サービスアカウントのままでよい |
| リモート SQL Server + **Yagura というサービス単位**で DB 権限を絞りたい | **gMSA を推奨** |
| 保存データ・監査の主体をドメインの管理体系（グループ・監査ポリシー）に載せたい | **gMSA を推奨** |

仮想サービスアカウントはネットワーク上ではマシンアカウント `DOMAIN\HOST$` として現れるため、DB 側の権限付与がホスト単位になり、同一ホストの他サービスと識別を共有します。gMSA はこれを解きます。

注意点（トレードオフ）:

- **gMSA 構成ではサービス起動が DC 到達性に依存します**（仮想サービスアカウントは外部依存ゼロ）。詳細は [§7](#7-dc-到達性への依存と回復設定)
- **v0.1 時点では、gMSA にしても DB 側権限は自動では最小化されません**。本番昇格ウィザードの提示 SQL は導入時権限として `db_owner` を付与します（database.md §5.2）。実行時最小権限への分離が実装されて初めて、gMSA の専用識別に対する最小権限付与が実効化します。「gMSA にすれば即座に DB が最小権限になる」わけではありません
- **リモート管理 HTTPS を Windows 統合認証（Kerberos SSO）で使っている場合、gMSA への切替で SSO が NTLM へ後退することがあります**。詳細は [§8](#8-リモート管理-https-の-kerberos-sso-への影響)

## 2. 前提条件（AD 管理者の作業）

gMSA の作成・取得許可は AD 側の操作であり、Yagura のインストーラは代行しません（AD への書き込みはドメイン管理者権限を要し、gMSA の配置——OU・命名・取得許可——は組織のポリシー領域のためです）。

### 2.1 KDS ルートキー（初回のみ）

ドメインで初めて gMSA を作る場合は KDS ルートキーの作成が必要で、**作成から有効化まで既定で 10 時間の伝播待ちが要ります**（全 DC への複製を保証するための仕様。初回導入者が全員通る道です）:

```powershell
Add-KdsRootKey    # 本番: 実行後 10 時間待つ
```

検証用の単一 DC 環境に限り、次の指定で即時有効化できます:

```powershell
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
```

### 2.2 gMSA の作成と取得許可

```powershell
New-ADServiceAccount -Name gmsaYagura -DNSHostName gmsaYagura.example.com `
  -PrincipalsAllowedToRetrieveManagedPassword "SYSLOGSV01$"
```

**取得許可（`PrincipalsAllowedToRetrieveManagedPassword`）はインストール先ホストのコンピューターアカウント単位（またはそのホストのみを含む専用グループ）に限定してください。** これはセキュリティ要件です——`Domain Computers` のような広域グループを指定すると、グループ内の**すべてのホスト**が gMSA のパスワードを取得でき、Yagura の識別を騙って DB へ接続できてしまいます（サービス単位の最小権限という gMSA の価値そのものが崩れます）。

グループ経由で許可した場合は、**対象ホストの再起動**でグループメンバーシップを反映してください。

### 2.3 ホスト側の準備と確認

インストール先ホストで:

```powershell
Install-ADServiceAccount -Identity gmsaYagura
Test-ADServiceAccount -Identity gmsaYagura      # True になること
```

### 2.4 「サービスとしてログオン」権利（SeServiceLogonRight）の付与 — 必須

**gMSA へ `SeServiceLogonRight`（サービスとしてログオン）をローカルセキュリティポリシーまたは GPO で事前に付与してください。これは任意の推奨ではなく必須手順です。** MSI のサービス登録も SCM もこの権利を自動付与しないため（実機確定——ADR-0015 改訂履歴 1）、付与しない限りサービスは一度も起動できず、**インストール自体がエラー 1920 → 1603 でロールバックします**。

- ローカルポリシー: `secpol.msc` → ローカルポリシー → ユーザー権利の割り当て → 「サービスとしてログオン」に `DOMAIN\gmsaYagura$` を追加
- GPO で集中管理している環境では該当 GPO に追加（GPO はローカル設定を上書きするため、ローカルへの追加だけでは次回のポリシー適用で失われます）

参考: 既定の仮想サービスアカウントでこの手順が不要なのは、この権利の既定値に `NT SERVICE\ALL SERVICES` が含まれ仮想アカウントが包含されるためです。gMSA はドメインアカウントなのでこの包含に与りません。

## 3. インストール時の指定

### 対話インストール

セットアップ画面の「サービス実行アカウント」で `DOMAIN\name$` 形式（例: `EXAMPLE\gmsaYagura$`）を入力します。**パスワード欄はありません**（gMSA はパスワード入力が不要——空パスワードでのサービス登録が仕様です）。

### サイレントインストール / GPO 配布

```
msiexec /i Yagura-x64.msi /qn YAGURA_SERVICE_ACCOUNT=EXAMPLE\gmsaYagura$
```

### 指定値の検証（fail-closed）

受理される値は次の 2 形のみです。それ以外（`LocalSystem`・`NetworkService`・`LocalService`、`$` なしの一般アカウント等）は**対話・サイレントを問わずインストールがエラーで失敗します**（警告付き続行はありません）:

- 既定の仮想サービスアカウント `NT SERVICE\Yagura`
- gMSA 形式 `DOMAIN\name$`

形式検証の限界: `DOMAIN\name$` 形式ではコンピュータアカウント・sMSA（旧世代のスタンドアロン管理サービスアカウント。対象外）をインストーラが AD へ照会せずに判別できません。誤って指定した場合はサービス起動失敗（[§6](#6-起動失敗エラー-1069-系の切り分け)）として顕在化します。

### アップグレード時の継承

一度指定した実行アカウントは記録され（remember property）、**アップグレード・修復時にプロパティを指定し直さなくても前回値が継承されます**。指定漏れで黙って仮想サービスアカウントへ戻ることはありません。

## 4. DB 接続（Windows 統合認証）

gMSA 実行時、SQL Server への Windows 統合認証は gMSA の識別（`DOMAIN\name$`）で行われます。**SQL Server 側の `CREATE LOGIN` は gMSA 名に対して作成してください**（本番昇格ウィザードが接続に使う実効アカウント名を画面に表示します——その名前がそのまま `CREATE LOGIN` の対象です）:

```sql
CREATE LOGIN [EXAMPLE\gmsaYagura$] FROM WINDOWS;
```

gMSA のパスワードローテーションは LSA が透過的に処理するため、ローテーションを跨いだ DB 接続にアプリ側・運用側の追加作業は不要です。

## 5. 稼働中インストールからの切替（手順書）

製品としての切替経路は**再インストール / アップグレード時の `YAGURA_SERVICE_ACCOUNT` 指定のみ**です（ADR-0015 決定 6。インストーラが ACL の付替——新アカウントへの付与と旧アカウントの ACE 除去——まで行います）。**切替経路でも保存データ・設定は保持されます**（変わるのは ACL の付与先のみ。設定内の DPAPI 保護値は machine スコープのため、同一マシン内のアカウント切替で復号は壊れません）。

再インストールを避けて稼働中に手動で切り替える場合は、次の手順を**この順で**実施してください（自動化スクリプト・管理 UI は提供していません）:

1. 前提条件（[§2](#2-前提条件ad-管理者の作業)）をすべて満たす（`Test-ADServiceAccount` が `True`・`SeServiceLogonRight` 付与済み）
2. サービス停止: `Stop-Service Yagura`
3. 実行アカウント変更: `sc.exe config Yagura obj= "EXAMPLE\gmsaYagura$" password= ""`
4. 保存データの ACL 付替（新アカウントへ付与し、旧アカウントの ACE を除去する——権限の残骸を残さない）:

   ```powershell
   icacls "$env:ProgramData\Yagura" /grant "EXAMPLE\gmsaYagura$:(OI)(CI)(M)" /remove "NT SERVICE\Yagura"
   icacls "$env:ProgramData\Yagura\forwarder" /grant "EXAMPLE\gmsaYagura$:(OI)(CI)(R)" /remove "NT SERVICE\Yagura"
   ```

   （フォワーダ配置フォルダは読み取りのみ——データルート本体と権限が異なる点に注意。security.md §5.1）
5. 証明書を使う構成（閲覧 HTTPS・リモート管理 HTTPS・TLS 受信）では、秘密鍵の読み取り権限は次回起動時に新アカウントへ自動付与されます（失敗時はイベントログの警告に従って certlm.msc から手動付与）
6. サービス起動: `Start-Service Yagura`

切替は次回起動時に監査記録（イベント ID 2025「サービス実行アカウントが前回起動時から変化」）として証跡化されます。旧アカウントの ACE がデータルートに残っている場合も起動時に警告が出ます。

## 6. 起動失敗（エラー 1069 系）の切り分け

gMSA 構成でサービスが「ログオンに失敗しました」（エラー 1069）で起動しない場合、次の 3 段で切り分けてください:

1. **ホストで `Test-ADServiceAccount -Identity <名前>` が `True` か** → `False` なら 2 へ
2. **gMSA の取得許可（`PrincipalsAllowedToRetrieveManagedPassword`）に当該ホストが含まれているか** → 含まれていれば 3 へ
3. **グループ経由で許可した場合、ホストを再起動してグループメンバーシップを反映したか**

このほか、インストール直後の起動失敗（エラー 1920）は `SeServiceLogonRight` の未付与（[§2.4](#24-サービスとしてログオン権利seserviceLogonrightの付与--必須)）が原因です。

なお、AD 側の事後変化（取得許可からの除外・gMSA の削除等）は**次回起動の 1069 まで顕在化しません**。運用の定期確認に `Test-ADServiceAccount` を含めることを推奨します。

## 7. DC 到達性への依存と回復設定

gMSA 構成では、**サービス起動時に DC からパスワードを取得できる必要があります**（仮想サービスアカウントにはこの依存がありません）。停電復旧などで syslog サーバが DC より先に起動すると、起動が 1069 で失敗します。

- インストーラが既定で設定する SCM の失敗時回復（5 秒間隔 × 3 回の再起動）では、DC の復旧（数分〜）まで届かない場合があります
- gMSA 構成では回復アクションの**間隔延長・回数増（または失敗時は常に再起動）**への変更を検討してください。推奨値は AD lab の実測（security.md SEC-14 (c)）で確定後に本節へ記載します（lab 実測で確定）
- SEC-14 (c) の lab 実測（2026-07-24）では、**DC 停止中のサービス再起動は 1069 で失敗し、DC 復旧後の SCM 自動再起動（2 回目）で回復する**こと、また**キャッシュ済み Kerberos チケットが有効な間は稼働中の DB 接続が DC 停止でも成功する**ことを確認済みです（[ADR-0015](../adr/0015-gmsa-service-account.md) 改訂履歴 2）。回復設定の推奨値の確定は引き続き残項目です
- 稼働中に統合認証の DB 接続が失敗し始めた場合は、イベントログの警告 **1031** が実行主体（アカウント名）と失敗種別（DC 起因 / SQL Server 起因）を含めて記録します（security.md §4.3）
- リモート DC 構成ではサービス依存関係（`sc config depend=`）による解決は効かないため、回復アクション側の再試行が主手段です

この依存は gMSA 採用の本質的な対価であり、構造的には解消できません。

## 8. リモート管理 HTTPS の Kerberos SSO への影響

リモート管理 HTTPS（opt-in）をホスト名 + Windows 統合認証（Kerberos SSO)で使っている環境では注意が必要です。gMSA はコンピューターアカウントとは別のドメインアカウントであり、ホスト名の `HOST` SPN の自動登録に与りません。そのため **gMSA へ切り替えると、`HTTP/<ホスト名>` SPN を gMSA へ明示登録しない限り、Kerberos SSO が NTLM へ後退することがあります**:

```powershell
setspn -S HTTP/<ホスト名 FQDN> EXAMPLE\gmsaYagura$
```

登録手順の確定（重複 SPN の事前確認を含む実機検証）は AD lab（security.md SEC-14 (f)）で行い、確定後に本節を更新します（lab 実測で確定）。

## 9. 監査証跡

| イベント ID | 内容 |
|---|---|
| 2024 | 構成されたサービス実行アカウントの初回起動時転記（インストール記録 `service-account.ini` から） |
| 2025 | 実効実行アカウントが前回起動時から変化した状態で起動（`sc config` による製品外の切替も含めて証跡化） |

いずれも Windows イベントログ（ソース `Yagura`）とアプリ監査記録の両方に残ります。
