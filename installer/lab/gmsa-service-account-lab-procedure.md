# SEC-14 実機検証手順 — gMSA サービス実行の受け入れ（決定 7 (a)〜(f)）

> **実施済み（2026-07-24。lab: DC `WIN-DV7G9OMOF4U` = Windows Server 2025 `10.0.26100`・`yagura.test` ドメイン、メンバー機 `DESKTOP-9MNR1CM` = Windows 10 Pro `10.0.26100`・保存先 SQL Server 2022 Express `SQLEXPRESS`・gMSA `YAGURA\gmsaYagura$`）**。
> 結果は [security.md](../../docs/design/security.md) §5.2・§7 SEC-14 行、および [ADR-0015](../../docs/adr/0015-gmsa-service-account.md) 改訂履歴 3・4 に確定として記録済み。本手順書は再実施用に残す。
>
> **要点**: (a)〜(f) すべて実測。実施中に**インストーラのサイレントアカウント切替が無言 no-op になる欠陥（[#426](https://github.com/Yanai-Taketo/Yagura/issues/426)）を検出し、CLI 上書き対応（PR #427）で修正のうえ (d) を成立させた**。残るのは (e) 2009 の実発火（SEC-13 と同じ CNG 自己付与制約——実行アカウント種別に非依存）と、(f) の認可済み管理者 Kerberos による監査 2008 `scheme=windows` の E2E（ADR-0013 の Cookie/circuit 自動化の残件と重複）のみ。

[ADR-0015](../../docs/adr/0015-gmsa-service-account.md) 決定 7 / [security.md](../../docs/design/security.md) §5.2・§7 SEC-14 / Issue #263 の lab 検証手順。設計の根拠は各設計書を参照し、本書は**再現手順と実測結果**に絞る。

## 実施環境と前提

- **DC**（`WIN-DV7G9OMOF4U`）: `yagura.test` ドメインコントローラ。SQL Server 2022 Express `SQLEXPRESS`（動的 TCP ポート——本 lab では `60418`。名前付きインスタンスのため接続は `Server=<fqdn>,<port>` の明示ポート指定が確実）。DC に SQL 用ファイアウォール規則（TCP `<port>`・UDP 1434 = SQL Browser）を追加する。
- **メンバー機**（`DESKTOP-9MNR1CM`）: 実際に gMSA で Yagura を動かす対象。
- **gMSA 準備**（[guide §2](../../docs/guides/gmsa-service-account.md)）:
  ```powershell
  Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))   # 単一 DC の即時化
  New-ADServiceAccount -Name gmsaYagura -DNSHostName gmsaYagura.yagura.test `
    -PrincipalsAllowedToRetrieveManagedPassword "DESKTOP-9MNR1CM$"
  # メンバー機で: Install-ADServiceAccount gmsaYagura; Test-ADServiceAccount gmsaYagura  # True
  ```
  `SeServiceLogonRight` の明示付与は**不要**（`10.0.26100` で実測確定——改訂履歴 2・Issue #422。未付与のまま実施）。
- **SQL ログイン**（切替の両側のネットワーク identity 分を作る）:
  ```sql
  CREATE LOGIN [YAGURA\gmsaYagura$] FROM WINDOWS;         -- gMSA
  CREATE LOGIN [YAGURA\DESKTOP-9MNR1CM$] FROM WINDOWS;    -- 仮想 SA のネットワーク identity（マシンアカウント）
  -- Yagura DB で両者に db_owner（v0.1 の導入時権限。database.md §5.2）
  ```
- **MSI**: `main`（#427 マージ済み）から `installer/README.md` の手順で x64 MSI をビルドする。(d) の**アップグレード**切替を実測するにはバージョン差が要るため、`-p:YaguraVersion=` でラボ用に版を振った 2 本（例 0.5.1 / 0.5.2）を使う。

## (a) 新規インストール E2E ＋ ACL

[ADR-0015](../../docs/adr/0015-gmsa-service-account.md) 改訂履歴 3 で実測済み（統合認証接続・書き込みの成功、接続失敗 3 態様の SqlException〔18456 / 4060 / SSPI〕= 1031 の分類根拠）。gMSA 構成のデータルート ACL は §5.2 に実出力を記録:

```text
%ProgramData%\Yagura            YAGURA\gmsaYagura$:(OI)(CI)(M)  + SYSTEM:(F) + Administrators:(F)
%ProgramData%\Yagura\forwarder  YAGURA\gmsaYagura$:(OI)(CI)(R)  + SYSTEM:(F) + Administrators:(F)
```

静的 SDDL（`PermissionEx`）が張った仮想 SA（`S-1-5-80-…`）の ACE は deferred CA の `icacls … /remove:g` で除去され、残存 0。

## (b) パスワードローテーション跨ぎ

**手順**: gMSA + SQL 書き込みが動いている状態で DC 上 `Reset-ADServiceAccountPassword -Identity gmsaYagura`。前後で受信を流し、SQL の行数と Yagura.Host プロセスの `StartTime` を観測。

**結果**: プロセス無停止（`StartTime` 不変）、SQL 行数はローテーション跨ぎで単調増（受信・書き込み無瞬断）、**1030/1031 いずれも不発**。gMSA のパスワードローテーションは LSA が透過処理し、アプリ・運用の追加作業は不要。

## (c) DC 停止状態でのサービス再起動

改訂履歴 3 で実測済み。**DC 停止中の再起動は gMSA パスワード取得不能で 1069、DC 復旧後の SCM 自動再起動（2 回目）で回復**。キャッシュ済み Kerberos チケット有効中は稼働中 DB 接続が DC 停止でも成功。回復設定の推奨値は [guide §7](../../docs/guides/gmsa-service-account.md)（`sc failure … reset=86400 … restart/120000×3` + `failureflag 1`）。

## (d) 既存データを持つアカウント切替（両方向 + 失敗着地）

**手順**: 仮想 SA で新規インストール → 受信実績 + スプール退避分（保存先 SQL を停止して受信 → スプールへ退避）を作る → `msiexec /i <新MSI> /qn YAGURA_SERVICE_ACCOUNT=YAGURA\gmsaYagura$` でアップグレード切替。逆方向は `YAGURA_SERVICE_ACCOUNT="NT SERVICE\Yagura"` を明示指定。失敗着地は実在しないアカウント指定で誘発。

**結果**:
- ① 仮想 SA → gMSA: 切替成立。データルート/forwarder に加え**既存子ファイル（`yagura.db` 等）まで継承（`(I)`）で gMSA ACE が反映**、旧仮想 SA の ACE を `/remove:g *S-1-5-80-…` で除去（再帰対象で残存 0）、保存先復旧でスプール drain、監査 2025「旧=NT SERVICE\Yagura、新=YAGURA\gmsaYagura$」発火。
- ② gMSA → 仮想 SA（明示指定）: 切替成立、gMSA ACE 残存 0。
- ③ 実在しないアカウント指定: 再構成失敗 1603 でロールバックし、**元アカウント・元 ACL へクリーン着地**（部分適用なし。CLI 値自体は正しく解決され、失敗はサービス構成段）。

**⚠ 検出した欠陥（[#426](https://github.com/Yanai-Taketo/Yagura/issues/426)）**: 素の remember-property では AppSearch が RegistrySearch の記憶値でコマンドライン指定値を上書きし、**既存インストール上のサイレント切替が無言 no-op**（msiexec は成功で終わるのにアカウントは変わらない）になる。PR #427 で RobMensching の「コマンドライン優先」拡張（既定 `Value` 撤去 + 退避/復元/既定の 3 段 SetProperty を UI/Execute 両シーケンスへ）を適用し是正。優先順位は**コマンドライン > 記憶値 > 既定**。回帰は `installer-e2e` の `msi-service-account-table` ステップ（Property 静的既定値の不在・`DefaultServiceAccount`/`Save+RestoreCmdLine` CA の存在を検証）で検出。

## (e) 監査証跡

- **2024**（構成転記・`service-account.ini`）: 初回起動で 1 回のみ、再起動で重複しない。
- **2025**（前回起動時からの変化）: インストーラ切替（(d) ①②）・**製品外 `sc config` 切替**の双方で発火（後者は「旧=YAGURA\gmsaYagura$、新=NT SERVICE\Yagura」を確認——決定 8 どおり）。
- **`[service-account-old-ace-remains]` 警告**: 旧アカウント ACE を意図的に残した起動で発火（「データルート … に旧アカウント YAGURA\gmsaYagura$ の ACE が残っています」）。
- **2009/2010**（証明書秘密鍵の付与先）: リモート管理 HTTPS（自己署名・`Admin:RemoteBinding` opt-in）を gMSA 実行で構成すると、`[admin-https-private-key-grant-failed]` 警告が**付与先として実効アカウント `YAGURA\gmsaYagura$` を明示**する（§5 の「付与先を実行アカウントから導出」どおり）。2009 の**実発火は成立しない**——SEC-13 で確定済みの制約（サービスアカウントは自身の CNG 鍵 ACL を書き換えられず自動付与が失敗、2009 は成功時のみ発火）で、**実行アカウント種別に依存しない**（gMSA 固有の退行ではない）。TLS を実際に成立させるには SEC-13 と同様の手動付与を要する。

## (f) HTTP SPN 登録手順の確定 + Kerberos SSO

**手順**（[guide §8](../../docs/guides/gmsa-service-account.md)）:
```powershell
setspn -Q HTTP/<FQDN>                              # 重複事前確認
setspn -S HTTP/<FQDN> YAGURA\gmsaYagura$           # FQDN
setspn -S HTTP/<host> YAGURA\gmsaYagura$           # 短縮名
setspn -L YAGURA\gmsaYagura$                       # 確認
```
`-S` は登録前に重複を自動チェックする。**別マシンから**ホスト名で管理 HTTPS（8516）へアクセスし、`WWW-Authenticate` トークン種別と `klist` を観測。

**結果**: 登録・重複事前確認とも成立。**別マシン（DC）から gMSA 実行の 8516 へアクセスすると Kerberos SSO が成立**——`WWW-Authenticate: Negotiate` が Kerberos SPNEGO トークン（MS-KILE OID）を返し、アクセス元の `klist` に `HTTP/DESKTOP-9MNR1CM.yagura.test @ YAGURA.TEST` のサービスチケットが入る（KDC が gMSA の鍵で発券）。**同一マシンからのアクセスは NTLM へ後退**（`WWW-Authenticate` が NTLMSSP・`klist` に HTTP チケットなし。Windows の同一ホスト向け既定挙動）。

**残**: 認可された管理者 ID（コンピューターアカウントは Yagura 管理者ではない）による監査 2008 `scheme=windows` の end-to-end。これは ADR-0013 の Windows 認証 → Cookie/circuit ログインフローの自動化（別途の残件）と重なる。

## 環境の残置

検証後、メンバー機はクリーンな gMSA + SQL 構成で稼働継続（テスト証明書・8516 規則は撤去、gMSA の HTTP SPN と DC の SQL ファイアウォール規則は残置）。既定の仮想 SA + SQLite へ戻すかはオーナー判断。
