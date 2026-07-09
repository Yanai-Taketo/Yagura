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
| フォワーダ MSI 配置フォルダ作成 + ACL | `%ProgramData%\Yagura\forwarder` を作成し、データルートとは独立した SDDL(`ForwarderFolder` コンポーネント)で「SYSTEM/Administrators = フル、サービスアカウント = **読み取りのみ**、継承無効化」を適用(ADR-0008 設計条件 9・security.md §5.1・Issue #171)。データルートの ACL をそのまま継承すると生じるサービスアカウントの書込可を明示的に断つ |
| ファイアウォール規則 | `Yagura Syslog (UDP 514)` / `Yagura Syslog (TCP 514)` / `Yagura Viewer (TCP 8514)` の受信許可 3 規則(WixToolset.Firewall.wixext)。**各規則のプロファイルは Domain + Private の複合に限定**(Public は含まない。`Profile="[YaguraFwProfile]"` のプロパティ間接参照でビットマスク整数 `3` = Domain\|Private を Firewall 拡張の custom action へ渡す。方式の根拠と制約は Firewall.wxs 冒頭コメント参照)。管理 8515 は loopback 専用のため規則を作らない |
| 規則のオプトアウト | セットアップ UI のチェックボックス、またはサイレント時 `msiexec /i Yagura.msi /qn YAGURA_FIREWALL=""` |
| インストール記録 | 選択(`RulesRequested`)と作成規則一覧をデータルート直下の `firewall-rules.ini` に記録(初回起動時のイベントログ転記は Host 側の後続 Issue) |
| イベントログソース | ソース `Yagura` を Application ログへ事前登録(`util:EventSource`。Program.cs の M9 申し送り) |
| 完了画面 | 閲覧 URL(http://localhost:8514/)+ 管理 URL(http://localhost:8515/admin)の案内(併記) + 「今すぐブラウザで開く」チェックボックス(`WixShellExec`。開くのは閲覧 URL) |
| スタートメニュー | 「Yagura ログ閲覧」(.url、http://localhost:8514/、無条件)+ 「Yagura 管理」(.url、http://localhost:8515/admin、無条件) |
| デスクトップ | 「Yagura」(.url、http://localhost:8514/。閲覧 UI。**opt-in・既定 ON**) |
| ショートカットのオプトアウト | セットアップ UI の `YaguraShortcutDlg` チェックボックス、またはサイレント時 `msiexec /i Yagura.msi /qn YAGURA_DESKTOP_SHORTCUT=""`(デスクトップの閲覧ショートカットのみ対象。管理 UI のスタートメニューリンクは無条件のためオプトアウト不可) |
| アップグレード | `MajorUpgrade`(既定 Schedule = afterInstallValidate)。データルートは MSI 管理外のため設定・ログは保持される。`forwarder` フォルダの ACL も新製品インストール時に `PermissionEx` が再実行され再適用される |
| アンインストール | 規則・ショートカット(スタートメニュー・デスクトップとも)・記録 ini は削除。**データルートのログ・設定は保持**(ログは資産。空フォルダの場合のみ MSI が削除)。`forwarder` フォルダも同じ規則(MSI 未配置で空なら削除、配置済みで非空なら ACL ごと保持) |

管理 UI は **loopback 固定**(TCP 8515)のため、スタートメニューの「Yagura 管理」・完了画面の
案内はいずれも「このサーバー上で開いたときのみ機能する」ショートカットである(Issue #131)。
ファイアウォール規則は追加しない(管理ポートを外部公開しない不変条件はそのまま)。

## E2E 検証(M9-2。Issue #75 / ADR-0006 基準 1)

ゼロ設定ファーストラン(インストール直後、DB 設定なしで SQLite により即受信・即閲覧)を
実 MSI で検証する E2E。**設定ファイルの手編集は一切行わない**。

- スクリプト: [e2e/Invoke-YaguraInstallerE2E.ps1](e2e/Invoke-YaguraInstallerE2E.ps1)
  - Full モード(管理者権限必須): サイレントインストール(`msiexec /i /qn`)→ サービス
    `Yagura` の Running 待機 → インストール状態の証拠採取(規則 3 本・プロファイルが
    Domain+Private 複合で Public/Any を含まないこと・データルート・firewall-rules.ini)
    → UDP 514 へ syslog 送出 → 閲覧リスナ(http://localhost:8514/)の
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
  `Wix5FirewallException` テーブルを WindowsInstaller COM で照合し、3 規則すべてで
  `Profile` 列が `[YaguraFwProfile]`(プロパティ間接参照)であり、`Property` テーブルで
  `YaguraFwProfile` = `3`(NET_FW_PROFILE2_DOMAIN 0x1 | NET_FW_PROFILE2_PRIVATE 0x2)で
  あることを確認済み(2026-07-10)。実機での確認は E2E Full モードの
  `installed-state-evidence` ステップが行う——実インストール後の `Get-NetFirewallRule` の
  結果に対し「3 系統すべてが 1 規則ずつ、プロファイルがちょうど Domain + Private の複合で
  あり、Public/Any を含まない」ことをアサーションする(Profile 属性の欠落 = 既定 Any への
  逆戻り、および同名規則分割による片プロファイル残存という回帰を CI で自動検知する)。
  **アップグレード経路(旧 Profile=Any 規則の除去)の実機確認は M9-3 lab 手順 §H の
  管轄(未実施)**——MajorUpgrade の既定 Schedule から設計上は除去されるはずだが実証は
  lab で行う
- E2E スクリプトは開発機で `-DryRun` と `-SendVerifyOnly`(実ビルド出力の Yagura.Host に
  対する送出・照合)を検証済み。**Full モード(実インストール)の初回実行は CI
  (installer-e2e.yml)で行う**——GitHub ホストランナーの管理者権限の根拠は workflow 内の
  コメント参照。CI で成立しない場合は lab 手順書方式へ縮退(Issue #75)
- **インストール実行のうち ACL 適用・アップグレードの実挙動は未検証**: 実機検証は
  M9-3(lab)の管轄。オーナー実行の手順書:
  [lab/m9-3-lab-procedure.md](lab/m9-3-lab-procedure.md)(Issue #76。ACL = SEC-3・
  失敗時再起動・アップグレード・M-15・DB-8・イベントログ・ja-JP UI・E2E Full の lab 再現)
- **管理画面への入口(Issue #131)**: ローカルビルドした MSI を WindowsInstaller COM で照合し、
  以下を確認済み(2026-07-09)。
  - `Wix4InternetShortcut` テーブルに 3 行(`ViewerShortcut` → `http://localhost:8514/`・
    `AdminMenuShortcut` → `http://localhost:8515/admin`・`ViewerDesktopShortcut` →
    `http://localhost:8514/`)。Name 列がそれぞれ `Yagura ログ閲覧.url` / `Yagura 管理.url` /
    `Yagura.url` であること
  - `Component` テーブル: `ViewerShortcut` / `AdminMenuShortcut` は Condition 列が空(無条件)、
    `ViewerDesktopShortcut` は `Condition = YAGURA_DESKTOP_SHORTCUT = 1`(opt-in・既定 ON)
  - `Property` テーブル: `YAGURA_DESKTOP_SHORTCUT` の既定値が `1`
  - `Directory` テーブル: `DesktopFolder` の親が `TARGETDIR`(OS 標準のデスクトップそのもの。
    Yagura 専用サブフォルダを作らない設計どおり)
  - `RemoveFile` テーブルに 3 つの `.url` それぞれの削除エントリ + `YaguraMenuFolder` の
    `RemoveFolder`(アンインストール時にスタートメニュー・デスクトップとも残置しない設計の
    静的裏付け)
  - `Control` / `ControlEvent` テーブル: `YaguraShortcutDlg` が
    `YaguraFirewallDlg` → `YaguraShortcutDlg` → `VerifyReadyDlg` の順で配線され、
    チェックボックス `DesktopCheckBox` が `YAGURA_DESKTOP_SHORTCUT` に束縛されていること
  - `WIXUI_EXITDIALOGOPTIONALTEXT` に閲覧・管理の両 URL が含まれること
  - **実機でのショートカット動作(クリックして開く・アンインストールでの実削除)は未検証**:
    管理者権限での実インストールが要るため、installer-e2e(Full E2E)または M9-3 lab の
    次回実行での確認を申し送る。E2E スクリプトの残置物確認(`residue-startmenu-removed`)は
    現状「スタートメニューのフォルダ消滅」のみを見ており、デスクトップ側の確認は含まれて
    いない(次項参照)
