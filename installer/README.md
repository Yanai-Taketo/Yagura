# Yagura MSI インストーラ

WiX Toolset 7(WixToolset.Sdk 7.0.0)による MSI インストーラプロジェクト(M9-1。Issue #74)。
ADR-0009 決定4 によりアーキ別パラメータ化済み(x64 既定 / ARM64 は `-p:YaguraArch=arm64`)。

## ビルド手順

```powershell
# x64(既定。省略時と同じ)
dotnet build installer\Yagura.Installer.wixproj -c Release -p:YaguraArch=x64

# ARM64(ADR-0009 決定4。x64 ホストからのクロスビルドで足りる——WiX の InstallerPlatform=arm64
# ビルド自体はネイティブ ARM64 環境を要求しない。実行時の動作確認は windows-11-arm 実アーキで行う)
dotnet build installer\Yagura.Installer.wixproj -c Release -p:YaguraArch=arm64
```

- ビルドは自動で `dotnet publish src\Yagura.Host -c Release -r <win-x64|win-arm64> --self-contained true`
  を実行し(出力先 `installer\publish\<x64|arm64>\`。アーキごとにサブフォルダを分け、
  取り違えを防ぐ)、その出力一式を MSI に収める。publish 済み出力を再利用する場合は
  `-p:SkipYaguraHostPublish=true` を付ける
- 成果物: `installer\bin\Release\ja-JP\Yagura-<x64|arm64>.msi`(x64 は約 43 MB。self-contained
  のため .NET ランタイムの事前導入は不要)。**リリース成果物の命名は全アーキで明示サフィックス
  とする**(x64 の無サフィックス名は維持しない。ADR-0009 決定4)。リリースワークフロー
  (`.github/workflows/release.yml`)がバージョンを冠した名前(例 `Yagura-0.3.0-x64.msi`)へ
  リネームして公開する。**MSI はビルド環境ごとに再現しない**ため、サイズ・SHA256 の byte-exact
  値をドキュメントに書かない(docs/development/conventions.md「リリース」)
- アーキ別ビルドを同じ作業ツリーで連続して行う場合、MSBuild の増分ビルド判定が
  `installer\obj` 配下のキャッシュを誤って再利用することがある(実ビルドで確認、2026-07-10)。
  `installer\obj`・`installer\bin` を削除してからビルドし直すこと(CI のマトリクスビルドは
  ジョブごとに独立した作業ディレクトリのため影響を受けない)
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
| Windows サービス登録 | `ServiceInstall`(サービス名 `Yagura` = `Program.WindowsServiceName`)。実行アカウントはプロパティ間接参照 `Account="[YAGURA_SERVICE_ACCOUNT]"`(既定 `NT SERVICE\Yagura`)・自動起動・失敗時再起動(5 秒 × 3 回、`util:ServiceConfig`) |
| サービス実行アカウントの gMSA opt-in(ADR-0015・Issue #263) | MSI プロパティ `YAGURA_SERVICE_ACCOUNT`(既定 `NT SERVICE\Yagura`)+ セットアップ画面の入力欄(`YaguraServiceAccountDlg`。パスワード欄なし)。fail-closed 検証(type 19 CA `ValidateYaguraServiceAccount`: 既定値または `DOMAIN\name$` 形式のみ受理。LocalSystem 等は対話・サイレントを問わず失敗)。remember property でアップグレード時に前回値を継承。gMSA 指定時のみ deferred CA(icacls)がデータルート `(M)`・forwarder `(R)` の ACL を後追い付与し、静的 SDDL の仮想 SA ACE と旧アカウント ACE を除去(security.md §5.2)。インストール記録はデータルート直下 `service-account.ini` + `HKLM\SOFTWARE\Yagura\ServiceAccount`。`SeServiceLogonRight` は検証済みビルド 26100 では不要を実測確定(ADR-0015 改訂履歴 2・Issue #422)——未検証ビルド向けの推奨と 1920/1603 時のトラブルシュートを利用者ガイド([docs/guides/gmsa-service-account.md](../docs/guides/gmsa-service-account.md) §2.4)に記載し、インストーラは付与しない |
| データルート作成 + ACL | `%ProgramData%\Yagura` を作成し、native `PermissionEx`(MsiLockPermissionsEx)の SDDL で「SYSTEM/Administrators = フル、サービスアカウント = 変更、継承無効化」を適用(security.md §5) |
| フォワーダ MSI 配置フォルダ作成 + ACL | `%ProgramData%\Yagura\forwarder` を作成し、データルートとは独立した SDDL(`ForwarderFolder` コンポーネント)で「SYSTEM/Administrators = フル、サービスアカウント = **読み取りのみ**、継承無効化」を適用(ADR-0008 設計条件 9・security.md §5.1・Issue #171)。データルートの ACL をそのまま継承すると生じるサービスアカウントの書込可を明示的に断つ |
| ファイアウォール規則 | `Yagura Syslog (UDP 514)` / `Yagura Syslog (TCP 514)` / `Yagura Viewer (TCP 8514)` の受信許可 3 規則(WixToolset.Firewall.wixext)。**各規則のプロファイルは Domain + Private の複合に限定**(Public は含まない。`Profile="[YaguraFwProfile]"` のプロパティ間接参照でビットマスク整数 `3` = Domain\|Private を Firewall 拡張の custom action へ渡す。方式の根拠と制約は Firewall.wxs 冒頭コメント参照)。管理 8515 は loopback 専用のため規則を作らない |
| 規則のオプトアウト | セットアップ UI のチェックボックス、またはサイレント時 `msiexec /i Yagura-x64.msi /qn YAGURA_FIREWALL=""` |
| opt-in プロパティの記憶(remember property) | `YAGURA_FIREWALL` / `YAGURA_DESKTOP_SHORTCUT` はどちらも「修復(Repair)・アップグレード実行時はダイアログを経由しない」ため、対策なしだと既定値 `1` に戻ってしまう(Issue #203)。WiX 公式の "remember property" パターン(`RegistrySearch` で `HKLM\SOFTWARE\Yagura` の記憶値を読み戻し、無条件コンポーネントで毎回書き込む)により、初回インストールで選んだ値(既定 ON のオプトアウトを含む)を修復・アップグレードでも保持する。詳細は Package.wxs / Firewall.wxs のコメント参照 |
| インストール記録 | 選択(`RulesRequested`)と作成規則一覧をデータルート直下の `firewall-rules.ini` に記録(初回起動時のイベントログ転記は Host 側の後続 Issue) |
| イベントログソース | ソース `Yagura` を Application ログへ事前登録(`util:EventSource`。Program.cs の M9 申し送り) |
| 完了画面 | 閲覧 URL(http://localhost:8514/)+ 管理 URL(http://localhost:8515/admin)の案内(併記) + 「今すぐブラウザで開く」チェックボックス(`WixShellExec`。開くのは閲覧 URL) |
| スタートメニュー | 「Yagura ログ閲覧」(.url、http://localhost:8514/、無条件)+ 「Yagura 管理」(.url、http://localhost:8515/admin、無条件) |
| デスクトップ | 「Yagura」(.url、http://localhost:8514/。閲覧 UI。**opt-in・既定 ON**)。perMachine(ALLUSERS=1)のため作成先は **`%PUBLIC%\Desktop` = 全ユーザー共有のパブリック デスクトップ**(特定ユーザーのデスクトップではない。learn.microsoft.com/windows/win32/msi/desktopfolder、確認日 2026-07-09)。この性質はセットアップ画面(`YaguraShortcutDlg`)の注記でも利用者に明示する |
| ショートカットのオプトアウト | セットアップ UI の `YaguraShortcutDlg` チェックボックス、またはサイレント時 `msiexec /i Yagura-x64.msi /qn YAGURA_DESKTOP_SHORTCUT=""`(デスクトップの閲覧ショートカットのみ対象。管理 UI のスタートメニューリンクは無条件のためオプトアウト不可) |
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
    スタートメニュー・`HKLM\SOFTWARE\Yagura` キー消滅、**データルート保持** = 上の責務表
    どおり)→ 修復時の opt-out 記憶検証(Issue #203。主フローとは独立: オプトアウト
    インストール → プロパティ無指定で修復 `msiexec /fa /qn` → 規則・デスクトップ
    ショートカットが復活しないことを確認 → アンインストール → HKLM キー消滅確認)
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

## ARM64 対応(ADR-0009 決定4・委任事項2)

- **BinaryRef のアーキ依存(実ビルドで発見・修正)**: `LaunchYaguraViewer` カスタムアクションの
  `BinaryRef` を `Wix4UtilCA_X64` に決め打ちしていたため、ARM64 ビルドでも x64 版 CA バイナリを
  参照する不具合があった(ARM64 実機では x64 ネイティブ DLL を msiexec.exe(ARM64)にロードできず
  実行時失敗になり得る)。`util:ServiceConfig`・`util:InternetShortcut`・Firewall 拡張の
  CustomAction は拡張の wixlib フラグメントが InstallerPlatform に応じて自動的に
  `Wix4UtilCA_A64`/`Wix5FWCA_A64` へ解決していた一方、素の `<CustomAction BinaryRef="...">` は
  自動解決されないことを WindowsInstaller COM での CustomAction テーブル照合で確認した
  (確認日 2026-07-10)。WiX 標準のプリプロセッサ変数 `$(sys.BUILDARCH)` で `Wix4UtilCA_X86` /
  `Wix4UtilCA_X64` / `Wix4UtilCA_A64` を切り替えるよう `Package.wxs` を修正し、x64・ARM64
  それぞれのビルドで CustomAction テーブルの `Source` が正しいアーキの Binary を指すことを
  再ビルド後に確認済み
- **誤アーキ MSI 実行時の利用者体験(実機確認)**: 本開発機(x64)で ARM64 版 `Yagura-arm64.msi` を
  `msiexec /i /qn` で実行したところ、Windows Installer 標準のエラー **1633**
  (`ERROR_INSTALL_PLATFORM_UNSUPPORTED`)で即座に失敗し、ログに次のメッセージが出力されることを
  確認した(確認日 2026-07-10):
  `このインストール パッケージはこの種類のプロセッサでサポートされていません。製品の製造元に問い合わせてください。`
  この判定は WiX が MSI の SummaryInformation `Template` プロパティに書き込むアーキマーカー
  (x64 ビルドは `x64;1041`、ARM64 ビルドは `Arm64;1041` — 実ビルドで確認)を Windows Installer
  が自動照合する既定動作であり、追加のローンチコンディションなしで「対応していないプロセッサです」
  という利用者が自力で状況を理解できるメッセージが出る。したがって本 PR では追加のローンチ
  コンディションを実装しない(既定動作で十分と判断)

## 検証状態

- ローカルビルド + WiX 標準の ICE 検証(ビルドに内蔵)を通過(警告 0。x64・ARM64 両方)
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
  失敗時再起動・アップグレード・ADR-0016 トリガ (d)(旧 M-15)・DB-8・イベントログ・ja-JP UI・E2E Full の lab 再現)
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
- **修復(Repair)時の opt-in プロパティ記憶(Issue #203)**: WiX 公式の "remember property"
  パターン(robmensching.com/articles/the-remember-property-pattern/、確認日 2026-07-10。
  機構自体は WixToolset.Sdk 7.0.0 のソース(`ParsePropertyElement` / `ParseRegistrySearchElement`。
  src/wix/WixToolset.Core/Compiler.cs)で直接確認)を `YAGURA_FIREWALL` / `YAGURA_DESKTOP_SHORTCUT`
  へ導入。`YaguraFwProfile`(#187 でプロパティ化したビットマスク)はどのダイアログからも
  変更されない固定値のため対象外と判断した(Firewall.wxs のコメント参照)。
  - 静的検証: ローカルビルドした MSI を WindowsInstaller COM で照合し、`AppSearch` テーブルに
    `YAGURA_FIREWALL` → `YaguraFirewallOptInSearch` / `YAGURA_DESKTOP_SHORTCUT` →
    `YaguraDesktopShortcutOptInSearch` の 2 行が存在し、対応する `RegLocator`(HKLM の
    `SOFTWARE\Yagura\FirewallOptIn` / `DesktopShortcutOptIn` を raw 型で検索)へつながっている
    こと、`Registry` テーブルに同じ場所へ `[YAGURA_FIREWALL]` / `[YAGURA_DESKTOP_SHORTCUT]` を
    書き込む行が Condition なしの `FirewallInstallRecord` / `DesktopShortcutInstallRecord`
    コンポーネント(明示 `KeyPath`)に属することを確認済み(2026-07-10)
  - **実機検証済み(2026-07-10。管理者権限での実インストール)**: `msiexec /i ... YAGURA_FIREWALL=""
    YAGURA_DESKTOP_SHORTCUT=""` でオプトアウトインストール → `msiexec /fa ... /qn`
    (**プロパティ無指定**。ARP の「修復」実行時と同じ入力条件 — YaguraFirewallDlg /
    YaguraShortcutDlg を経由しないため)で修復 → `Get-NetFirewallRule` に Yagura の 3 規則が
    **再作成されないこと**・デスクトップショートカットが**再作成されないこと**・
    `HKLM\SOFTWARE\Yagura` の記憶値が空文字列のまま保たれることを確認(修正前の設計であれば
    既定値 `1` に戻り規則・ショートカットが復活する形の回帰)。既定(opt-in)側の
    「修復しても規則・ショートカットが消えない」ことも同じ手順で確認済み(remember property が
    opt-in 方向にも正しく機能する退行防止確認)
  - この実機シナリオは installer/e2e/Invoke-YaguraInstallerE2E.ps1 の手順 7
    (`repair-remember-optout-*`)として自動化し、CI(installer-e2e.yml)で継続検知する
    (Issue #125 の Profile ビットマスク回帰に E2E アサーションを追加した #187 と同じ方針)
  - **アンインストール後の `HKLM\SOFTWARE\Yagura` 残置なしを実機確認済み(2026-07-10。
    PR #214 レビュー指摘への対応)**: 記憶値の導入によりオプトアウト側でも本キーが作られる
    ようになったが、キー配下の値はすべて MSI 管理のため、Registry Table の公式仕様
    ("the installer removes a registry key after removing the last value or subkey under
    the key"。learn.microsoft.com/windows/win32/msi/registry-table)によりアンインストールで
    キーごと削除される。オプトイン・オプトアウト両方のインストール→アンインストールで
    キーが残らないことを実機確認し、`RemoveRegistryKey` は不要と判断。E2E の残置物確認に
    `residue-hklm-registry-removed`(opt-in 側)と `repair-remember-optout-residue-registry`
    (opt-out 側)を追加して継続検知する
  - 既知の設計上の割り切り(レビューで指摘・スコープ外を維持): 記憶値が存在する環境では、
    修復時に msiexec コマンドラインで明示指定した値(例 `YAGURA_FIREWALL="1"` での再オプトイン)
    も AppSearch の記憶値で上書きされる(RobMensching 記事の「追加の落とし穴」)。Issue #203 の
    要求範囲(オプトアウトが修復で失われない)外であり、対応する CMDLINE 退避カスタム
    アクションは導入しない。将来「明示指定で記憶値を上書きしたい」要望が出た際の設計負債として
    ここに記録する(Package.wxs のコメントにも同旨を記載)
- **サービス実行アカウントの gMSA opt-in(ADR-0015・Issue #263)の検証マトリクス**
  (security.md SEC-14 の「CI で継続検知する範囲 / lab でしか検証できない項目」の明示):
  - **CI(installer-e2e。AD なしで検証できる範囲)**:
    - `msi-service-account-table`: MSI テーブル照合(`Property` の `YAGURA_SERVICE_ACCOUNT`
      既定値 = `NT SERVICE\Yagura`・`ServiceInstall.StartName` = `[YAGURA_SERVICE_ACCOUNT]` の
      間接参照のまま・`ValidateYaguraServiceAccount` CA の存在)
    - `service-account-evidence`: 既定インストール後の `Win32_Service.StartName` が仮想 SA で
      あること(間接参照化による既定経路の非退行)+ `service-account.ini` と
      `HKLM\SOFTWARE\Yagura\ServiceAccount`(remember property 記録)の書き込み
    - `service-account-reject-invalid`: `YAGURA_SERVICE_ACCOUNT=LocalSystem` のサイレント
      インストールが失敗し(fail-closed)、サービス・レジストリの残置がないこと
    - Host 側単体テスト: `ServiceAccountStartupInspectorTests`(転記 2024 の一回性・
      変化検出 2025 のレール)
  - **AD lab の管轄(security.md SEC-14 (a)〜(f)。yagura.test DC。未実施)**:
    (a) gMSA 指定の新規インストール E2E(icacls 後追い付与の実出力記録を含む)
    (b) パスワードローテーション跨ぎの受信・DB 書き込み継続
    (c) DC 停止状態での再起動(1069 実挙動・SCM 回復設定の実効値の確定)
    (d) 既存データありのアカウント切替(ACL 付替の全域性・旧 ACE 除去・スプール drain・
        失敗時着地。`/remove:g *S-1-5-80-…` の SID 指定除去の成立確認を含む)
    (e) 監査証跡(2024/2025・秘密鍵付与先の実効アカウント追随)
    (f) gMSA への HTTP SPN 登録手順の確定・SeServiceLogonRight 要否のビルド差の記録
        (26100 = 不要を実測確定済み〔2026-07-24 lab。Issue #422〕。1920 ロールバックの観測は
        現行ビルドで成立不能のため受け入れ項目から除外——gMSA 固有の実失敗モードは (c) の 1069)
