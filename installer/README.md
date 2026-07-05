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
| ファイアウォール規則 | `Yagura Syslog (UDP 514)` / `Yagura Syslog (TCP 514)` / `Yagura Viewer (TCP 8514)` の受信許可(WixToolset.Firewall.wixext)。管理 8515 は loopback 専用のため規則を作らない |
| 規則のオプトアウト | セットアップ UI のチェックボックス、またはサイレント時 `msiexec /i Yagura.msi /qn YAGURA_FIREWALL=""` |
| インストール記録 | 選択(`RulesRequested`)と作成規則一覧をデータルート直下の `firewall-rules.ini` に記録(初回起動時のイベントログ転記は Host 側の後続 Issue) |
| イベントログソース | ソース `Yagura` を Application ログへ事前登録(`util:EventSource`。Program.cs の M9 申し送り) |
| 完了画面 | 閲覧 URL(http://localhost:8514/)の案内 + 「今すぐブラウザで開く」チェックボックス(`WixShellExec`) |
| スタートメニュー | 「Yagura ログ閲覧」(.url、http://localhost:8514/) |
| アップグレード | `MajorUpgrade`(既定 Schedule = afterInstallValidate)。データルートは MSI 管理外のため設定・ログは保持される |
| アンインストール | 規則・ショートカット・記録 ini は削除。**データルートのログ・設定は保持**(ログは資産。空フォルダの場合のみ MSI が削除) |

## 検証状態

- ローカルビルド + WiX 標準の ICE 検証(ビルドに内蔵)を通過(警告 0)
- MSI テーブルの内容検証済み(ServiceInstall / Wix4ServiceConfig / MsiLockPermissionsEx /
  Wix5FirewallException / IniFile / Upgrade / ControlEvent を WindowsInstaller COM で照合)
- **インストール実行(サービス起動・ACL 適用・規則作成・アップグレード・アンインストールの
  実挙動)は未検証**: 実機検証は M9-3(lab)の管轄
