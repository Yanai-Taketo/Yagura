# Yagura MSI インストーラ

WiX Toolset 7(WixToolset.Sdk 7.0.0)による MSI インストーラプロジェクト(M9-1。Issue #74)。

## ビルド手順

```powershell
dotnet build installer\Yagura.Installer.wixproj -c Release
```

- ビルドは自動で `dotnet publish src\Yagura.Host -c Release -r win-x64 --self-contained true` を
  実行し(出力先 `installer\publish\`)、その出力一式を MSI に収める。publish 済み出力を
  再利用する場合は `-p:SkipYaguraHostPublish=true` を付ける
- 成果物: `installer\bin\Release\ja-JP\Yagura.msi`(約 43 MB。self-contained のため .NET
  ランタイムの事前導入は不要)。**MSI はビルド環境ごとに再現しない**ため、サイズ・SHA256 の
  byte-exact 値をドキュメントに書かない(docs/development/conventions.md「リリース」)
- **本プロジェクトは Yagura.sln に含めていない**: CI(ci.yml)は sln を対象にビルドしており、
  sln へ追加すると ci.yml に触れずに CI のビルド対象が変わるため(M9-1 では CI への組み込みを
  スコープ外とした)。CI への組み込みは後続 Issue で行う

### WiX v7 の OSMF EULA

WiX v7 はビルドに Open Source Maintenance Fee(OSMF)の EULA 承諾を要求する(未承諾は
エラー WIX7015)。本プロジェクトは公式の Direct Acceptance 方式
(`.wixproj` の `<AcceptEula>wix7</AcceptEula>`)で承諾している。支払い義務は
「年商 1 万ドル超の組織」にのみ課され(docs.firegiant.com/wix/osmf/、確認日 2026-07-06)、
個人運営の非営利 OSS である Yagura は該当しない。

## インストーラの責務(configuration.md §4.3 / ADR-0004 決定 4・5)

| 責務 | 実装 |
|---|---|
| Windows サービス登録 | `ServiceInstall`(サービス名 `Yagura` = `Program.WindowsServiceName`)。仮想サービスアカウント `NT SERVICE\Yagura`・自動起動・失敗時再起動(5 秒 × 3 回、`util:ServiceConfig`) |
| データルート作成 + ACL | `%ProgramData%\Yagura` を作成し、native `PermissionEx`(MsiLockPermissionsEx)の SDDL で「SYSTEM/Administrators = フル、サービスアカウント = 変更、継承無効化」を適用(security.md §5) |
| ファイアウォール規則 | `Yagura Syslog (UDP 514)` / `Yagura Syslog (TCP 514)` / `Yagura Viewer (TCP 8514)` の受信許可(WixToolset.Firewall.wixext)。**プロファイルは Domain + Private 限定**(Public は含まない。WiX Firewall 拡張の `Profile` 属性は単一値の列挙型で複数値の同時指定に非対応のため、系統ごとに `Profile="domain"` / `Profile="private"` の 2 規則に分けて作成する。規則数は実質 6 本)。管理 8515 は loopback 専用のため規則を作らない |
| 規則のオプトアウト | セットアップ UI のチェックボックス、またはサイレント時 `msiexec /i Yagura.msi /qn YAGURA_FIREWALL=""` |
| インストール記録 | 選択(`RulesRequested`)と作成規則一覧をデータルート直下の `firewall-rules.ini` に記録(初回起動時のイベントログ転記は Host 側の後続 Issue) |
| イベントログソース | ソース `Yagura` を Application ログへ事前登録(`util:EventSource`。Program.cs の M9 申し送り) |
| 完了画面 | 閲覧 URL(http://localhost:8514/)の案内 + 「今すぐブラウザで開く」チェックボックス(`WixShellExec`) |
| スタートメニュー | 「Yagura ログ閲覧」(.url、http://localhost:8514/) |
| アップグレード | `MajorUpgrade`(既定 Schedule = afterInstallValidate)。データルートは MSI 管理外のため設定・ログは保持される |
| アンインストール | 規則・ショートカット・記録 ini は削除。**データルートのログ・設定は保持**(ログは資産。空フォルダの場合のみ MSI が削除) |

## E2E 検証(M9-2。Issue #75 / ADR-0006 基準 1)

ゼロ設定ファーストラン(インストール直後、DB 設定なしで SQLite により即受信・即閲覧)を
実 MSI で検証する E2E。**設定ファイルの手編集は一切行わない**。

- スクリプト: [e2e/Invoke-YaguraInstallerE2E.ps1](e2e/Invoke-YaguraInstallerE2E.ps1)
  - Full モード(管理者権限必須): サイレントインストール(`msiexec /i /qn`)→ サービス
    `Yagura` の Running 待機 → インストール状態の証拠採取(規則 3 系統・6 本 = Domain+Private・
    データルート・firewall-rules.ini)→ UDP 514 へ syslog 送出 → 閲覧リスナ(http://localhost:8514/)の
    HTML で照合 → アンインストール(`msiexec /x /qn`)→ 残置物確認(サービス・規則・
    スタートメニュー消滅、**データルート保持** = 上の責務表どおり)
  - `-DryRun`: msiexec・サービス操作を行わず手順と出力の配管のみ検証(開発機用)
  - `-SendVerifyOnly -UdpPort <p> -ViewerBaseUrl <url>`: 送出・照合部分のみを起動済みの
    Yagura.Host に対して実行(開発機用。管理者権限不要)
  - 照合は ASCII の RunId トークンのみで行う(日本語本文の照合は en-US CI の CP437 で
    文字化けして誤判定するため)
- CI: [.github/workflows/installer-e2e.yml](../.github/workflows/installer-e2e.yml)
  (workflow_dispatch + installer/ 配下変更の pull_request)。MSI ビルド → E2E 実行 →
  証拠を artifact `installer-e2e-results` に保存する
- 証拠形式(ADR-0006 基準 1): 人間可読ログ(`*.log.txt`)+ 機械可読サマリ
  (`*.summary.json`。RunId・各手順の合否・所要時間・実行環境)+ msiexec 詳細ログ。
  CI 実行記録(workflow run と artifact)を基準 1 の証拠としてリンクする

## 検証状態

- ローカルビルド + WiX 標準の ICE 検証(ビルドに内蔵)を通過(警告 0)
- MSI テーブルの内容検証済み(ServiceInstall / Wix4ServiceConfig / MsiLockPermissionsEx /
  Wix5FirewallException / IniFile / Upgrade / ControlEvent を WindowsInstaller COM で照合)
- **ファイアウォール規則のプロファイル限定(Issue #125)**: ローカルビルドした MSI の
  `Wix5FirewallException` テーブルを WindowsInstaller COM で照合し、6 規則すべてで
  `Profile` 列が `1`(NET_FW_PROFILE2_DOMAIN)または `2`(NET_FW_PROFILE2_PRIVATE)のみで
  あること(`4` = Public・`0x7FFFFFFF` = All が含まれないこと)を確認済み(2026-07-09)。
  **実機での `Get-NetFirewallRule` によるプロファイル表示確認は未実施**(管理者権限での
  実インストールが要る。次回 Full E2E または M9-3 lab 手順で確認する)
- E2E スクリプトは開発機で `-DryRun` と `-SendVerifyOnly`(実ビルド出力の Yagura.Host に
  対する送出・照合)を検証済み。**Full モード(実インストール)の初回実行は CI
  (installer-e2e.yml)で行う**——GitHub ホストランナーの管理者権限の根拠は workflow 内の
  コメント参照。CI で成立しない場合は lab 手順書方式へ縮退(Issue #75)
- **インストール実行のうち ACL 適用・アップグレードの実挙動は未検証**: 実機検証は
  M9-3(lab)の管轄。オーナー実行の手順書:
  [lab/m9-3-lab-procedure.md](lab/m9-3-lab-procedure.md)(Issue #76。ACL = SEC-3・
  失敗時再起動・アップグレード・M-15・DB-8・イベントログ・ja-JP UI・E2E Full の lab 再現)
