# Windows イベントログを Yagura へ転送する

Windows 端末・サーバのイベントログを、[Fluent Bit](https://fluentbit.io/)(Apache-2.0)経由で
Yagura へ syslog(RFC 5424)転送する手順。転送プロトコルは UDP(既定)/ TCP / TLS(暗号化。
syslog over TLS。RFC 5425。Yagura 側は opt-in——security.md §6)を
`install.ps1 -Mode` で選択できる(それぞれの特性は後述「UDP と TCP のどちらを使うか」・
「TLS(`-Mode tls`)を使う」参照)。
リポジトリの [forwarder/fluent-bit/](../../forwarder/fluent-bit/) に**サイレント導入可能な配布キット**があり、
Intune / SCCM(Configuration Manager)/ GPO スタートアップスクリプト等の企業配布基盤で無人 push できる。
手動での導入にも使える。

**管理 UI(`/admin/forwarder-kit`)から、このサーバの宛先を設定済みのキットを生成できる(v0.2。
[ADR-0008](../adr/0008-forwarder-kit-generation.md))。** 生成したキットは `install.ps1` を
パラメータなしで実行できる(宛先の手入力による誤記を避けられる)。以下の手順は、生成 UI を
使わずキットを手動で組み立てる場合の**正の手順**として引き続き維持する。

## 対応アーキテクチャ(収集対象端末。ADR-0009 決定7・委任 #4)

**この節は Yagura サーバ本体のアーキ対応(x64・ARM64。[ADR-0009](../adr/0009-architecture-support.md))
とは独立の論点である。** 収集対象端末(イベントログを転送する側の Windows 機)のアーキは、
Yagura サーバのアーキと無関係に選べる——x64 サーバから ARM64 端末向けのキットを配布することも、
その逆も問題ない。

| 収集対象端末のアーキ | Fluent Bit MSI | Yagura 側の対応状況 |
|---|---|---|
| x64 | `fluent-bit-<版>-win64.msi` | 対応(Supported) |
| ARM64(Windows 11 on Arm 等) | `fluent-bit-<版>-winarm64.msi` | 試験的(Experimental)。ADR-0009 決定2 の水準定義に準拠 |
| x86(32bit) | `fluent-bit-<版>-win32.msi`(Fluent Bit は提供しているが) | **非対応**。本キット・Yagura 本体とも対象外(ADR-0009 決定1) |

Fluent Bit は Windows 向けに win32・win64・winarm64 の 3 アーキで MSI を公式提供している
([Fluent Bit Windows downloads](https://docs.fluentbit.io/manual/installation/downloads/windows)。
2026-07-10 ライブ確認)。Yagura が対応するのはこのうち **win64・winarm64 の 2 アーキ**のみ
(サーバ本体が x86 を不採用としたのと同じ判断——ADR-0009 決定1・選択肢 C 却下理由)。

**自分の端末のアーキを確認する方法**: 「設定 > システム > バージョン情報」の「システムの種類」を
確認する。「x64 ベース プロセッサ」なら win64、「ARM ベース プロセッサ」なら winarm64 の MSI を使う。
**迷ったら x64 を選んでください**(通常の Windows PC・Windows Server はほぼ x64)。

**`install.ps1` はアーキを自動判定する**(2026-07-10 実装): MSI 未指定(`-MsiPath` を渡さない)の
場合、実行している端末のプロセッサアーキテクチャを検出し、`fluent-bit-*-win64.msi` /
`fluent-bit-*-winarm64.msi` のうち該当する方のパターンでのみキットフォルダを検索する。
このため、**win64・ARM64 両方の MSI を同じキットフォルダに同居させて、混在フレット(x64 機と
ARM64 機が混在する環境)へ同一の Intune/SCCM/GPO パッケージを配布できる**——各端末は自分の
アーキに一致する MSI だけを見つけて導入し、もう一方の MSI は無視する。x86(32bit)端末で
実行した場合は、明確なエラーメッセージで停止する(Yagura は x86 を対象外としているため)。

判定の第一情報源は **WMI/CIM(`Win32_Processor.Architecture`)**である。WMI クエリは
ネイティブの WMI サービスが応答するため、スクリプトを実行している PowerShell ホスト自身の
ビット数・エミュレーション状態に左右されず、実マシンのアーキを返す。値の定義は
[Win32_Processor クラス](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processor)
(x86=0・ARM=5・x64=9・ARM64=12。確認日 2026-07-10。x64=9 は当日この検証環境(x64)で
実測一致を確認。ARM64=12 は公式ドキュメントの記載に基づく——ARM64 実機での実測は未実施)。
CIM が利用できない場合のみ環境変数(`PROCESSOR_ARCHITECTURE` / `PROCESSOR_ARCHITEW6432`)へ
フォールバックする。**環境変数だけに依拠しない理由**: ARM64 端末上で x64 エミュレーション
された PowerShell から実行すると、両変数とも `AMD64` を返し実 OS(ARM64)を反映しない
既知のパターンがある(`PROCESSOR_ARCHITEW6432` による昇格は古典的な 32bit WOW64 のみが
対象で、ARM64 上の x64 エミュレーションは対象外——PR #222 レビュー指摘)。環境変数のみの
判定ではこの場合に ARM64 端末を x64 と誤判定して win64 MSI を選んでしまう(エミュレーションで
動作はするがネイティブ ARM64 にならない)。**残余の限界**: CIM が失敗して環境変数
フォールバックに落ちた場合は上記の誤判定があり得る。確実を期す場合は ARM64 ネイティブの
PowerShell から実行するか、`-MsiPath` で MSI を明示指定すること。

**Yagura 管理 UI(`/admin/forwarder-kit`)で MSI を同梱する場合**は、生成画面で「収集対象端末の
アーキテクチャ」を明示選択する(既定 x64)。選択したアーキの MSI だけが配置フォルダから検出・
同梱の対象になる——選んでいない方のアーキの MSI が同じフォルダにあっても、検出や「複数検出」
エラーには影響しない(アーキごとに独立して検出するため。誤って別アーキの MSI を同梱して
しまう失敗様式は、この設計そのものによって構造的に防いでいる)。

**ARM64 の検証状況(実体検証)**: `fluent-bit-5.0.8-winarm64.msi` が
`https://packages.fluentbit.io/windows/` から公式に取得できること、その SHA256 が
23,796,327 バイトのファイルに対して算出できることは 2026-07-10 に実機(この検証環境。x64)で
確認済み(下記「検証済み環境」参照)。**ただし、この検証環境は x64 であり winarm64 バイナリ
自体を実行できないため、ARM64 端末上での実際の Fluent Bit 導入・サービス起動・イベント転送は
本 PR の時点では未実施である。** アーキ自動判定ロジック(`Get-LocalMsiFilenamePattern`)は、
CIM 経路(実 CIM での x64 判定 + モック CIM での ARM64/x86 分岐)・環境変数フォールバック経路
(変数差し替えでの x64/ARM64/WOW64 昇格/ネイティブ x86 の分岐)をユニット相当の検証で
確認済み。ただし「ARM64 実機上の各種 PowerShell ホスト(ネイティブ ARM64・x64 エミュレーション)
から実行した際に CIM・環境変数が実際に返す値」の実測は未実施(前述の残余の限界)。
実 ARM64 実機での導入 E2E は、Yagura サーバ本体の ARM64 スモーク(ADR-0009 決定6 Phase1。
`windows-11-arm` ランナー)とは別の検証対象であり、本 PR では ADR-0009 の ARM64 全体方針
(決定2「試験的」)を踏襲し、実機検証は試用フィードバック・lab 実施の申し送りとする
(実機がない状態で「検証済み」と過大に表明しない——conventions.md の実体検証原則)。

**MSI のオプトイン同梱(2026-07-07 amendment)**: 生成 UI では、サーバの所定フォルダ
(データルート配下 `forwarder`。既定 `%ProgramData%\Yagura\forwarder\`)に管理者が
Fluent Bit の MSI をあらかじめ手動配置しておくと、生成する ZIP に MSI を同梱するかを
選べる(既定は非同梱)。同梱を選ぶと ZIP 単体で自己完結する配布物になるが、**同梱 MSI の
取得元・真正性・脆弱性対応の責任は Yagura が負わない**——Yagura は配置されたファイルを
梱包し、その来歴(ファイル名・版・SHA256)を記録するのみである。Intune / SCCM 等の
企業配布基盤では最終的に別形式へ再パッケージするため、MSI 同梱の有無による恩恵は
主に手動・少数端末導入の場面に限られる。配置は管理画面からのアップロードではなく、
サーバのファイルシステムへの**手動配置**のみに対応する(手順は次節)。

## 管理 UI の生成キットに MSI を同梱する(手動配置手順)

管理画面(`/admin/forwarder-kit`)の生成 ZIP に Fluent Bit の MSI を同梱したい場合、
現時点では管理画面からのアップロード機能はなく、サーバへ管理者権限で MSI ファイルを
手動配置する。配置は RDP・コンソールログオン・リモート PowerShell(管理者セッション)の
いずれでもよい。以下は初めて行う場合でも 10 分程度で終わる手順。

### 1. MSI を入手して SHA256 を確認する

[packages.fluentbit.io](https://packages.fluentbit.io/) から、同梱先端末のアーキに応じて
`win64` 版(x64)または `winarm64` 版(ARM64)の MSI を取得し、`Get-FileHash` でハッシュ値を
確認する(取得元の改ざん・誤ファイル取り違えのチェック)。**迷ったら x64 を選んでください**
(前節「対応アーキテクチャ」参照)。

```powershell
# x64(既定)
Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-5.0.8-win64.msi" `
                  -OutFile ".\fluent-bit-5.0.8-win64.msi"
Get-FileHash ".\fluent-bit-5.0.8-win64.msi" -Algorithm SHA256

# ARM64(試験的。Windows 11 on Arm 等のクライアント端末向け)
Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-5.0.8-winarm64.msi" `
                  -OutFile ".\fluent-bit-5.0.8-winarm64.msi"
Get-FileHash ".\fluent-bit-5.0.8-winarm64.msi" -Algorithm SHA256
```

`Get-FileHash` が返す `Hash` の値を控えておく(後述の管理画面表示や、自組織の記録との
突き合わせに使う)。**Yagura は検証済み版(`5.0.8`)の公式配布 SHA256 を x64・ARM64 それぞれ
内部に保持している**(`ForwarderMsiConstraints.OfficialSha256ForVerifiedVersion` /
`OfficialSha256ForVerifiedVersionArm64`。いずれも 2026-07-10 に `packages.fluentbit.io` から
live 取得して確定。Fluent Bit は個別パッケージ向けの署名済みチェックサムファイルを公開して
いないため、実体は「公式ドメインから TLS 経由で取得した実測値」)ため、管理画面の
「公式配布 SHA256 との照合」は `5.0.8` の MSI であれば(選択したアーキと一致する限り)
「一致」と表示される。それ以外の版を配置した場合や、取得元を packages.fluentbit.io 以外に
変えた場合は、引き続き入手時点で管理者自身が真正性を確認すること。

### 2. 配置フォルダに MSI を置く

配置先は**データルート配下の `forwarder` フォルダ**(既定インストールでは
`%ProgramData%\Yagura\forwarder\`)。このフォルダは Yagura のインストーラ(MSI)が
作成し、専用の ACL(Administrators のみ書き込み可。次節)を設定済みである。
管理者権限の PowerShell からコピーする:

```powershell
Copy-Item ".\fluent-bit-5.0.8-win64.msi" "$env:ProgramData\Yagura\forwarder\"
# ARM64 端末向けにも同梱したい場合(同じフォルダに共存できる。次段落参照)
Copy-Item ".\fluent-bit-5.0.8-winarm64.msi" "$env:ProgramData\Yagura\forwarder\"
```

フォルダが存在しない場合(旧版インストーラで導入した後、最新版へ未アップグレードの
環境等)は、最新インストーラでの上書きインストールで作成・ACL 設定されるのを待つか、
手動で作成して次節の期待値どおりに ACL を設定すること。

**ファイル名は `fluent-bit-*-win64.msi`(x64)または `fluent-bit-*-winarm64.msi`(ARM64)の
いずれかのパターンに一致させること**(大文字小文字は区別しない。例:
`fluent-bit-5.0.8-win64.msi` / `fluent-bit-5.0.8-winarm64.msi`)。packages.fluentbit.io から
そのまま取得したファイル名であれば通常このパターンに一致する。**検出はアーキごとに独立して
行われる**(ADR-0009 決定7・委任 #4)ため、フォルダに一致する MSI が**同一アーキ内で常に
1 つだけ**存在する状態にする(同一アーキ内で複数あるとエラーになる。後述)。win64 と
winarm64 を 1 つずつ、計 2 つ置くのは正常な状態であり、エラーにはならない
(前節「対応アーキテクチャ」の混在フレット配布)。

### 3. フォルダの ACL を確認する

`forwarder` フォルダには、Yagura のインストーラが親フォルダ(データルート
`%ProgramData%\Yagura`)からの継承を断った専用の ACL を設定する
([ADR-0008](../adr/0008-forwarder-kit-generation.md) 設計条件 9・
[docs/design/security.md §5.1](../design/security.md)。ここに書ける者は
「全端末に配る MSI を差し替えられる者」であるため、書き込みを Administrators に限定する)。
次のコマンドで確認する:

```powershell
icacls "$env:ProgramData\Yagura\forwarder"
```

インストーラ既定 ACL(`installer/Package.wxs` の `ForwarderFolder` コンポーネント)に
基づき、次の 3 エントリが表示されるのが正常な状態(実機確認済み・2026-07-09):

```
NT AUTHORITY\SYSTEM:(OI)(CI)(F)
BUILTIN\Administrators:(OI)(CI)(F)
NT SERVICE\Yagura:(OI)(CI)(R)
```

読み方: `(F)` はフルコントロール、`(R)` は読み取りのみ(書き込み・削除・権限変更は不可)、
`(OI)(CI)` は配下のファイル・フォルダへ継承されることを示す。`NT SERVICE\Yagura` は
Yagura サービスの仮想サービスアカウント(実行アカウント)であり、環境によっては名前解決
されず SID(`S-1-5-80-...` で始まる文字列)のまま表示されることがある。データルート本体
(`%ProgramData%\Yagura`)ではサービスアカウントが `(M)`(変更)を持つのに対し、
`forwarder` フォルダのみ `(R)` に絞られているのが意図どおりの状態——Web/サービス
プロセスが侵害されても、全端末に配布される MSI をサービスアカウント権限では差し替え
られない。Yagura 側の読み取り(MSI の検出・SHA256 計算・版取得)は `(R)` で足りるため、
管理画面の動作に影響はない。

サービスアカウントの行が `(M)` や `(F)` になっている場合は、旧版インストーラの
既定(データルート ACL の継承)が残っている状態なので、最新インストーラでの
上書きインストールで是正するか、手動で `(R)` へ絞り込むこと(誤って `SYSTEM` /
`Administrators` の権限まで奪わないよう注意)。

### 4. 管理画面での見え方を確認する

`/admin/forwarder-kit` を開くと「MSI の同梱(任意)」欄に配置フォルダのフルパスと、
その下に**「収集対象端末のアーキテクチャ」の選択(x64 / ARM64。既定 x64)**が常に表示される
(ADR-0009 決定7・委任 #4)。選択したアーキに応じて、以降の検出表示が切り替わる:

| 状態 | 画面表示 |
|---|---|
| 未配置(フォルダが無い/選択中のアーキのパターンに一致するファイルが無い) | 「MSI 未検出。ここに `fluent-bit-*-win64.msi`(または選択中が ARM64 なら `fluent-bit-*-winarm64.msi`)を配置すると、生成する ZIP に MSI を同梱できます(任意)。」 |
| 選択中のアーキで正しく 1 つ配置 | 「MSI を同梱する」チェックボックス(既定オフ)。チェックすると検出ファイル名・版・SHA256・公式ハッシュ照合結果(現状は常に「未実施」)・ZIP サイズ予告(20 MB 超見込み)を表示 |
| 選択中のアーキのパターンに一致する MSI が複数 | 赤字で「複数の MSI が見つかりました。1 つだけ残してください。」+ 検出した全ファイル名一覧。同梱チェックボックス自体が出ず、MSI 同梱を選べない |

**アーキ選択と検出は連動する**: x64 を選んでいる間は win64 の MSI だけを検出対象とし、
同じフォルダに winarm64 の MSI が(複数)あっても無視する(逆も同様)。アーキを切り替えると
検出結果も切り替わり、チェックボックスの選択状態はリセットされる——別アーキの MSI を
誤って同梱要求してしまう事故を防ぐための挙動である。

**版不一致時の二段階確認**: 検出した MSI の版(MSI の `ProductVersion` を優先取得。
取得できない場合のみファイル名から推定した値を補助的に使う)が、生成 UI が表明する
「検証済み Fluent Bit 版」(現在 `5.0.8`)と異なる場合、黄色の警告
「検出した MSI の版(…)は検証済み版(…)と異なります。動作未検証の組み合わせになる
可能性があります。」が出て、「版が異なることを理解した上で、この MSI を同梱します」の
チェックを入れないと生成ボタンでエラーになり先へ進めない。これは同梱ミス(版違いに
気付かず配布してしまう)を防ぐための確認であり、拒否ではなく承認すれば同梱できる。

### よくある失敗

| 症状 | 原因 | 対処 |
|---|---|---|
| MSI を置いたのに「MSI 未検出」のまま | ファイル名が**選択中のアーキ**のパターン(`fluent-bit-*-win64.msi` または `fluent-bit-*-winarm64.msi`)に一致していない(例: 画面で x64 を選んでいるのに winarm64 の MSI しか置いていない、32bit(`win32`)版、`fluentbit-...`(ハイフン抜け)、拡張子違い等)。**一致しないファイルはエラーにならず単に無視される**ため、原因に気付きにくい | `Get-ChildItem "$env:ProgramData\Yagura\forwarder"` でファイル名を確認し、`fluent-bit-<版>-win64.msi` / `fluent-bit-<版>-winarm64.msi` の形に合わせる。画面のアーキ選択が導入先端末と一致しているかも確認する |
| 「複数の MSI が見つかりました」エラー | 旧版を消さずに新版を追加した等、**同一アーキの**パターンに一致するファイルがフォルダに 2 つ以上ある(win64 を 1 つ・winarm64 を 1 つ、計 2 つの状態はエラーにならない——アーキごとに独立して検出するため) | 残したい 1 つ以外を削除・退避し、同一アーキ内では常に 1 つだけにする |
| 版不一致の警告が出て生成できない | 検証済み版(`5.0.8`)と異なる MSI を配置した | 内容を理解した上で確認チェックを入れるか、検証済み版の MSI に差し替える |
| 「公式配布 SHA256 との照合」が「未実施」のまま | 検証済み版(`5.0.8`)以外の MSI を配置している(検証済み版のみ公式ハッシュ基準値を保持) | 検証済み版の MSI に差し替えるか、取得時点の `Get-FileHash` の値を自組織の記録(調達時のメモ・改ざん検知運用等)と突き合わせて確認する |
| フォルダ作成・ファイルコピーが権限エラーで失敗する | ログオンユーザーが Administrators に属していない、または非管理者権限の PowerShell で実行している | Administrators に属するアカウントで、管理者として実行した PowerShell から操作する |

## キットの内容

| ファイル | 役割 |
|---|---|
| `install.ps1` | サイレント導入スクリプト(MSI 無人導入 → 設定配置 → サービス登録・遅延自動起動 → 起動確認) |
| `uninstall.ps1` | 撤去スクリプト(サービス削除 + 設定削除。`-RemoveFluentBit` で MSI も削除) |
| `fluent-bit-yagura.conf` | 転送設定テンプレート(導入時に宛先等を自動置換) |
| `winevt-severity.lua` | イベントログの Level → syslog severity 変換、Keywords(監査成功/失敗)の severity 反映、チャネル → facility 変換を行うフィルタ |

Fluent Bit の MSI 本体はキットに**同梱しない**。
[packages.fluentbit.io](https://packages.fluentbit.io/) から、導入先端末のアーキに応じて
`fluent-bit-<版>-win64.msi`(x64)または `fluent-bit-<版>-winarm64.msi`(ARM64)を取得し、
キットと同じフォルダに置いて配布する(検証済みの版は後述。対応アーキの詳細は前節
「対応アーキテクチャ」を参照)。

## 導入手順

### 1. MSI を取得してキットに同梱する

```powershell
# x64(既定・迷ったらこちら)
Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-5.0.8-win64.msi" `
                  -OutFile ".\fluent-bit-5.0.8-win64.msi"

# ARM64(試験的。Windows 11 on Arm 等)
Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-5.0.8-winarm64.msi" `
                  -OutFile ".\fluent-bit-5.0.8-winarm64.msi"
```

**win64・winarm64 の両方をキットフォルダに置いても構わない**(むしろ x64/ARM64 混在の
フレットへ同一キットを配布する場合は推奨——`install.ps1` が導入先端末のアーキを自動判定して
該当する方だけを選ぶ。前節「対応アーキテクチャ」参照)。

### 2. サイレント導入を実行する(管理者権限)

```powershell
powershell -NoProfile -File .\install.ps1 -YaguraHost <Yagura サーバの IP またはホスト名>
```

`install.ps1` は実行している端末のプロセッサアーキテクチャ(x64 / ARM64)を自動判定し、
`-MsiPath` を指定しない限り、対応するファイル名パターンの MSI だけをキットフォルダから
検出する。Windows x86(32bit)上で実行した場合は、Yagura が x86 を対象外としている旨の
明確なエラーメッセージで停止する(ADR-0009 決定1)。

| パラメータ | 既定値 | 説明 |
|---|---|---|
| `-YaguraHost` | (必須) | Yagura サーバの IP / ホスト名 |
| `-YaguraPort` | `514` | Yagura の syslog 受信ポート |
| `-Channels` | `System,Application` | 収集するイベントログチャネル(カンマ区切り。`System`/`Application`/`Security` 以外の値や空要素(連続・末尾カンマ)を指定するとエラーで停止する——Issue #155) |
| `-Mode` | `udp` | 転送プロトコル(`udp` / `tcp` / `tls`)。UDP・TCP の特性の違いは「UDP と TCP のどちらを使うか」(Issue #156)、TLS は「TLS(`-Mode tls`)を使う」(Issue #137)を参照 |
| `-TlsCaFile` | (空) | `-Mode tls` 選択時のみ意味を持つ。Yagura の TLS 受信証明書/CA を信頼させる PEM ファイルのパス(省略時は OS 既定の信頼ストアのみを参照する) |
| `-TlsVerify` | `$true` | `-Mode tls` 選択時のみ意味を持つ。`$false` で証明書検証を無効化(暗号化のみ。ラボ用途限定) |
| `-MsiPath` | (自動検出) | MSI のパス。省略時はスクリプトと同じフォルダから、実行端末のアーキに一致する MSI( `fluent-bit-*-win64.msi` / `fluent-bit-*-winarm64.msi` )を自動検出する(ADR-0009 決定7・委任 #4) |

導入が成功すると標準出力に `INSTALL_SUCCESS` が出て終了コード 0 で終わる。
終了コード: `0` = 成功 / `1` = 失敗 / `3010` = 成功(OS 再起動が必要)。
チャネル指定・プロトコル指定の誤りは `INSTALL_SUCCESS` に達する前に明確なエラーメッセージ付きで
終了コード `1` になる(黙って一部チャネルが収集されない、または黙って UDP のままになる、という
事態は起きない)。

### 3. 動作確認

- サービス `fluent-bit` が「実行中」かつ「自動(遅延開始)」になっていること:
  `Get-Service fluent-bit | Format-List Name, Status, StartType`
  (遅延開始かどうかは `StartType` には出ない。`sc.exe qc fluent-bit` の
  `AUTO_START (DELAYED)` で確認できる)
- Yagura の閲覧画面(`http://<Yagura>:8514/`)に導入端末のイベントが届いていること
  (新規イベントのみ転送するため、届かない場合はテストイベントを書き込む:
  `eventcreate /T INFORMATION /ID 999 /L APPLICATION /SO YaguraKitTest /D "test"`)

### 既存導入端末の版更新(アップグレード)

旧版の Fluent Bit が導入済みの端末で、より新しい版の MSI を含むキットの `install.ps1` を
再実行すると、**エンジンを自動で上書き更新(in-place アップグレード)する**。導入済みの
エンジン版(`fluent-bit.exe` の ProductVersion)とキット MSI のファイル名の版を比較し:

- キット MSI の方が新しい → サービスを停止してから MSI を適用し、エンジンを更新する
  (4.0.14 → 5.0.8 のメジャー更新で実機確認済み。2026-07-10)
- 同じ版・キットの方が古い → MSI をスキップする(**ダウングレードはしない**)。
  設定とサービス定義の更新のみ行う
- どちらかの版が判定できない → 安全側に倒して既存エンジンへは触れず、警告ログを出して
  設定とサービス定義の更新のみ行う

つまり版更新の配布は「新しい MSI を同梱したキットを、初回導入と同じコマンドで再 push する」
だけでよい(Intune / SCCM / GPO の割り当てをそのまま使い回せる)。

`-Mode udp`(既定)と `-Mode tcp` は、どちらも一長一短があり無条件にどちらかを推奨できない
(Issue #156)。選択の判断材料を以下にまとめる。

### UDP(既定)の弱点

Windows のレンダリング済みイベント本文(挿入文字列付き・Security 監査など)は
パス MTU(通常 1500 バイト前後)を容易に超える。UDP はデータグラムが MTU を超えると
IP 層で断片化され、**1 フラグメントでも欠落すると再送されずイベント全体が消える**。
送信側(Fluent Bit)にはエラーが出ないため、この欠落は送信側から見えない
(「フォワーダキットが持つ沈黙する失敗様式」— ADR-0008)。

### TCP(`-Mode tcp`)の効果と既知の制約

TCP は輻輳制御・再送を伴うため、上記の IP 断片化ロスは避けられる。ただし、以下の制約を
把握したうえで選択すること(2026-07-09 時点で
[Fluent Bit 公式ドキュメント](https://docs.fluentbit.io/manual/data-pipeline/outputs/syslog)と
[`out_syslog` の実装(`plugins/out_syslog/syslog.c`)](https://github.com/fluent/fluent-bit/blob/master/plugins/out_syslog/syslog.c)を確認して検証済み):

- **`out_syslog` は TCP 時に RFC 6587 の octet-counting フレーミングを実装していない。**
  メッセージの末尾に単純に LF(`\n`)を 1 個追加するだけの
  non-transparent-framing(RFC 6587 §3.4.2)固定であり、`Mode tcp` でも切り替えオプションは
  存在しない(コード上、UDP 以外は無条件で LF を追加するのみ)。
- Yagura の TCP 受信側(`Yagura.Ingestion.Tcp.TcpFrameDecoder`)は接続の先頭バイトで
  octet-counting / non-transparent-framing を自動判別する実装を持つが、`out_syslog` が
  octet-counting を送ってこない以上、この判別は常に non-transparent-framing 側に倒れる。
  つまり **受信側の追加実装なしで相互運用できる**(TcpFrameDecoder は導入時から TCP に
  対応済みのため、この PR での受信側変更は不要)。
- non-transparent-framing は LF を唯一の境界として扱う(RFC 6587 自身が明記する既知の限界を
  受信側もそのまま踏襲している)。**Windows のイベント本文(特に Security 監査など、複数の
  項目が `\r\n` で区切られた本文)に LF が含まれていると、1 イベントが受信側で複数レコードに
  分割されて見える。** データは失われないが(UDP 欠落のような無音の消失とは異なり、
  分割はログの目視で気づける)、意図した 1 レコード = 1 イベントの構造は崩れる。
- この分割を Fluent Bit 側で解消する構成(例: Lua フィルタで本文中の CR/LF を無害な文字列へ
  事前置換する等)は未実装のフォローアップとして残る(次項)。

### 既知の制約・フォローアップ

- **本文中の埋め込み改行によるレコード分割の解消**: `winevt-severity.lua` で本文の
  CR/LF を事前に無害化すれば `-Mode tcp` の分割を避けられる可能性があるが未実装
- **管理 UI(`/admin/forwarder-kit`)で生成するキットは転送方式(UDP/TCP/TLS)を選択できる**
  (Issue #137 で追加)。ただし **TLS 選択時、生成キットは Yagura サーバの証明書を検証しない**
  (`tls.verify Off`。生成 UI が CA/サーバ証明書の同梱手段を持たないため——後述「TLS を使う」
  参照)。証明書を検証する TLS 送信は静的キット(`install.ps1 -Mode tls -TlsCaFile`)を使うこと

### 選択の目安

- 大きなイベント(特に挿入文字列の多い Security 監査・詳細な失敗イベント)を
  **欠落なく**届けたい環境は `-Mode tcp` または `-Mode tls` を検討する。ただし本文に
  埋め込み改行があるイベントは受信側で複数レコードに分割され得ることを許容できるか判断すること
  (この分割は TCP・TLS のいずれも同じ——後述「TLS を使う」参照)
- 本文が短いイベントが中心、または改行を含む本文が分割されても実運用上問題ない環境は
  `-Mode udp`(既定)のままでよい
- 通信経路を暗号化したい(盗聴・改ざんへの対策が必要な)環境は `-Mode tls` を検討する
- 判断に迷う場合は、テスト端末で両モードを実機比較してから本配布することを推奨する

## TLS(`-Mode tls`)を使う

Yagura の syslog over TLS 受信(RFC 5425。TCP 6514 既定。Yagura 側は opt-in——
[security.md](../design/security.md) §6)へ送信する。**Yagura サーバ側で TLS 受信を
有効化していない限り、`-Mode tls` は接続に失敗する**(Yagura の既定構成は平文受信のみ)。

> **既知の非互換(2026-07-11 実機確認。重要): 現時点の Fluent Bit は `-Mode tls` で Yagura へ
> メッセージを配送できない。** 詳細と回避策は次項「フレーミング」参照。TLS で暗号化した転送が
> 必要な場合は、octet-counting に対応した別実装(rsyslog・syslog-ng・NXLog 等)の使用を検討する
> こと。

- **フレーミング(非互換の原因)**: RFC 5425 は octet-counting フレーミングのみを許容し、
  Yagura の TLS 受信はこれを厳格に強制する(先頭バイトが数字でなければ即座に接続を切断する)。
  ところが `out_syslog` は TCP 転送(TLS はその上に乗る)で **RFC 6587 の octet-counting を
  実装していない**——`-Mode tcp` の LF 区切り制約(前述)は TLS を有効にしても解消されない
  ([`out_syslog` の実装](https://github.com/fluent/fluent-bit/blob/master/plugins/out_syslog/syslog.c)。
  ソース確認は 2026-07-09、この結論自体の実機検証は 2026-07-11)。
  **実機検証の結果(2026-07-11。Fluent Bit 5.0.8 → Yagura TLS 受信)**: TLS ハンドシェイクは
  正常に成立する(証明書検証込み)。しかしその直後、Yagura は LF 区切りのフレームを
  「octet-counting ではない = 回復不能なフレーミング破損」として接続ごと切断する。
  `out_syslog` は送達確認を待たない実装のため、**Fluent Bit 側はこの拒否を検知できず、
  リトライもエラー報告も行わない**(2 回の送信をそれぞれ別の keep-alive 接続で試行し、
  いずれも Yagura 側で拒否されたが Fluent Bit 側にはエラーが一切記録されなかったことを
  ラボで確認した——送信側から見た**無音の喪失**)。Yagura 側のログには接続ごとに警告
  (「再同期不能なフレーム破損を検出」)が残るが、**これは TLS ハンドシェイク失敗とは別経路**
  ——ハンドシェイク自体は成功しているため `yagura.ingestion.tcp.tls_handshake_failure`
  カウンタは増加しない。この警告ログを能動的に監視しない限り、配送されていないことに
  気づく手段がない
- **証明書検証**: 既定(`-TlsVerify $true`)は Yagura サーバの証明書を検証する(上記の非互換とは
  独立に、検証そのものは正常に機能することを実機確認済み)。Yagura は自己署名証明書の生成支援を
  提供しない設計判断(configuration.md §6)であり、内部 CA・自己署名証明書を使う場合は Yagura
  サーバの証明書(または発行 CA)を PEM 形式でエクスポートし、`-TlsCaFile` で指定すること。
  指定がない場合、Windows の OS 既定の信頼ストアのみが参照され、自己署名・内部 CA 発行の証明書は
  検証に失敗する(接続不可。黙って平文へフォールバックする経路は無い)
- **相互 TLS(クライアント証明書によるフォワーダ側の認証)は対象外**である(オーナー決定
  2026-07-10。security.md §6.1)。Yagura はサーバ証明書の提示のみを行い、フォワーダ側の
  クライアント証明書は要求しない
- **Yagura 側の期限切れ挙動**: Yagura の TLS 受信は証明書が期限切れになってもリスナを止めない
  (「ログを失わない」を通信の真正性より優先する設計判断——security.md §6)。期限切れの証明書を
  提示され続けた場合、`-TlsVerify $true` のクライアントはハンドシェイクに失敗し送信が止まる
  ——Yagura 側の TLS ハンドシェイク失敗カウンタ(送信元別)・状態画面で検知できる(openssl
  s_client によるラボ検証で、期限切れ証明書に対する `-CAfile` 検証が実際に失敗し接続が拒否される
  ことを確認済み。2026-07-11。Fluent Bit 自体での期限切れ拒否の確認は上記の非互換によりできて
  いない——そもそも通常の TLS 送信自体が現時点で成立しないため)
- **管理 UI 生成キットの制約**: 前述「既知の制約・フォローアップ」のとおり、生成キットは
  CA/サーバ証明書を同梱できないため `tls.verify Off` で生成される。ただし上記の非互換により、
  検証の有無に関わらず現時点の Fluent Bit では配送自体が成立しない
  (次節「実機スモーク手順」参照)

### 実機スモーク手順

`-Mode tcp` の到達と分割挙動は 2026-07-10 に実機確認済み(後述「検証済み環境」参照。
単一行イベントは 1 レコードで到達し、本文に `\r\n` を含む Security 監査イベント(4625 等)は
先頭行が解析済みレコード・残りの行が「解析失敗(原文のまま保存)」レコードとして分割到達する
——上記「既知の制約」の記述どおりで、データの消失はない)。手順 1 の UDP 欠落比較
(MTU 超えイベントの IP フラグメント喪失の実測)は未実施のため、大きなイベントの
欠落が懸念される環境では、本配布の前に以下の比較確認をテスト端末で実施することを推奨する。

1. テスト端末に `-Mode udp` で導入し、MTU(既定 1500 バイト)を超える大きな本文の
   イベントを書き込む(例: 多数の挿入文字列を持つ Security 監査イベントを発生させるか、
   `eventcreate` で長い `-D` 文字列を渡す)。パケットキャプチャ(`netsh trace` や
   Wireshark)で IP フラグメントが複数生成されることを確認し、Yagura 側でそのイベントが
   届くか(または欠落するか)を確認する
2. 同じ端末を `-Mode tcp` に切り替え(`install.ps1 -Mode tcp` を再実行。冪等なので
   安全)、同じ大きなイベントを再度発生させ、Yagura 側に**欠落なく**届くことを確認する
3. 本文に `\r\n` を含む大きめのイベント(Security 監査など)を `-Mode tcp` で送り、
   Yagura の閲覧画面で 1 イベントが複数レコードに分割されて見えるかどうかを確認する
   (上記「既知の制約」の実地確認)

## 企業配布基盤での push

- **Intune (Win32 アプリ)**: キット一式 + MSI を `.intunewin` 化し、インストールコマンドを
  `powershell -NoProfile -File install.ps1 -YaguraHost <IP>`、
  アンインストールコマンドを `powershell -NoProfile -File uninstall.ps1 -RemoveFluentBit` にする。
  検出規則はサービス `fluent-bit` の存在または `C:\ProgramData\fluent-bit-yagura\fluent-bit-yagura.conf`
- **SCCM**: パッケージ/アプリケーションとして同じコマンドラインを指定する
- **GPO**: コンピューターのスタートアップスクリプトとして割り当てる(SYSTEM 実行のため管理者権限要件を満たす)

`install.ps1` は再実行に対して安全(冪等)——導入済みの版がキット MSI 以上なら MSI をスキップし、
キット MSI の方が新しければエンジンを自動で上書き更新したうえで、設定とサービス定義を更新して再起動する
(前述「既存導入端末の版更新」)。宛先変更やチャネル追加は、パラメータを変えて再実行すればよい。

## 設定の内容

- **収集**: `winevtlog` 入力で System / Application チャネルを追跡(既読位置は
  `C:\ProgramData\fluent-bit-yagura\winevtlog.sqlite` に永続化。導入前の既存イベントは送らない。
  サービス停止中に書かれたイベントは、次回起動時に既読位置から追いついて転送される)
- **変換**: Lua フィルタでイベントの Level を syslog severity へ変換
  (Critical→crit、Error→err、Warning→warning、Information→info、Verbose→debug)
- **監査失敗の severity 底上げ**: Security チャネルの監査イベント(失敗ログオン 4625 等)の多くは
  Level=0(LogAlways)で送られ、Level だけでは info 相当に落ちてしまう。Windows は監査の成功/失敗を
  Keywords(WINEVENT_KEYWORD_AUDIT_FAILURE = bit52)に符号化しており、Lua フィルタはこのビットが
  立っている場合、Level 由来の severity が warning(4)より弱ければ warning(4)へ引き上げる
  (Level 由来がそれより強ければ格下げしない片方向の底上げ)
- **facility**: チャネル別に syslog facility を付与する(Security→authpriv(10)、System→daemon(3)、
  Application→local0(16)。未知チャネルは user(1)のまま)。付与しないと out_syslog の既定
  facility=user(1) に全チャネルが潰れ、受信側での facility によるルーティング/振り分けが効かない
- **送信**: RFC 5424 / UDP・TCP(いずれも平文)・TLS(暗号化。syslog over TLS。RFC 5425)から
  `-Mode` で選択(既定 UDP、特性は前述「UDP と TCP のどちらを使うか」・「TLS を使う」参照)。
  HOSTNAME = イベントの `Computer`、APP-NAME = `ProviderName`
  (イベントの「ソース」)、MSG = イベント本文
- **ソース名の無害化**: RFC 5424 の APP-NAME は空白を含まない ASCII・最大 48 文字のため、
  `Service Control Manager` のような空白入りソース名は `Service_Control_Manager` に
  変換して送る(そのまま送ると受信側の区切り解釈が壊れる)。変換が生じた場合のみ、
  元の名前を構造化データの `Provider` に原文のまま保持する
- **イベント ID とチャネル**: RFC 5424 の構造化データとして送信する
  (`[winevt EventID="7036" Channel="System"]`、ソース名を変換した場合は
  `Provider="Service Control Manager"` も付く)。Yagura の閲覧画面では
  ログの詳細表示の「構造化データ」欄で確認できる
- **運用**: Windows サービス(遅延自動起動 + 異常終了時 60 秒間隔で自動再起動)。
  Fluent Bit 自身のログは `C:\ProgramData\fluent-bit-yagura\fluent-bit.log`

### サービス登録の仕組み(実機確認済みの挙動)

Fluent Bit の MSI(5.0.8 で確認)は、インストール時に**自ら** `fluent-bit` サービスを
登録する(遅延自動起動・同梱の既定設定ファイルを指す)。`install.ps1` はこのサービスを
削除し、Yagura 用設定を指す同名サービスとして**作り直す**(PowerShell 5.1 の
`sc.exe config` は引用符入り binPath を壊すため、変更ではなく `New-Service` による
再作成を採用)。このため:

- Fluent Bit の MSI を(`install.ps1` を使わず)手動で上書き更新した場合、サービス定義が
  既定に戻る可能性がある。**MSI 更新後は `install.ps1` を再実行する**こと(冪等なので安全)。
  新しい MSI を含むキットの `install.ps1` を再実行する通常の版更新経路
  (前述「既存導入端末の版更新」)なら、アップグレードとサービス再作成が 1 回の実行で完結する

### Security チャネルを収集する場合

`-Channels "System,Application,Security"` を指定する。サービスは LocalSystem で動作するため
追加権限は不要だが、Security チャネルはイベント量が多く機微情報を含むため、
組織のポリシーで明示的に判断してから有効化すること。

### キット同梱ファイルの文字コード(Windows PowerShell 5.1 での表示に関する注意)

キット内のファイルはすべて UTF-8 だが、**BOM(byte order mark)の有無が用途で異なる**(Issue #127)。

| ファイル | BOM | 理由 |
|---|---|---|
| `fluent-bit-yagura.conf` / `winevt-severity.lua` | なし | Fluent Bit のパーサが BOM を想定しないため |
| `install.ps1` / `uninstall.ps1` | なし | 本文が ASCII のみで BOM 有無の影響を受けない |
| `README.md` / `GENERATED.txt` | **あり** | 人間が読む専用でどのプログラムもパースしないため、BOM を付けて文字コードを明示する |

`fluent-bit-yagura.conf` は BOM なしのまま維持されるため、**Windows PowerShell 5.1 の
既定コンソールや `Get-Content` でそのまま開くと日本語コメントが文字化けする**
(既定コードページが UTF-8 でないため)。正しく表示するには、以下のいずれかを使うこと。

```powershell
# 方法 1: エンコーディングを明示して読む(推奨)
Get-Content -Path C:\ProgramData\fluent-bit-yagura\fluent-bit-yagura.conf -Encoding UTF8

# 方法 2: コンソールのコードページを UTF-8 に切り替えてから、コンソール上に表示する
#         (chcp が効くのはそのコンソールセッション内での表示のみ)
chcp 65001
type C:\ProgramData\fluent-bit-yagura\fluent-bit-yagura.conf
```

メモ帳(Windows 10 1903 以降)は BOM なし UTF-8 を自動判定できるため、通常はそのまま
開いても正しく表示される(この自動判定はメモ帳自身の機能であり、コンソールの `chcp`
設定とは無関係。判定に失敗して文字化けした場合は方法 1 を使う)。

**PowerShell 7 以降は既定エンコーディングが UTF-8 のため、この問題の影響を受けない。**
`README.md` ・ `GENERATED.txt` は BOM 付きのため、PowerShell 5.1 の `Get-Content` や
メモ帳でもそのまま正しく表示できる。

## 検証済み環境

- Windows 10 Pro + Fluent Bit **5.0.8**(`fluent-bit-5.0.8-win64.msi`)で
  サイレント導入 → サービス登録(遅延自動起動)→ 起動 → テストイベントが Yagura の
  閲覧画面に到達するまでの全経路と、導入済み端末への再実行(冪等。宛先・チャネル・
  `-Mode` の変更を含む)、`uninstall.ps1 -RemoveFluentBit` による撤去(サービス・設定・
  MSI の完全除去)を実機確認済み(2026-07-10。`-Mode udp` / `-Mode tcp` の両方)
- 転送内容(本文 / ホスト名 / アプリ名の無害化と `Provider` 保持 / 重大度・分類の対応、
  イベント ID・チャネルの構造化データ)も同環境で実機確認済み(送信フレームの実測 +
  閲覧画面への到達)。**Keywords による監査失敗 severity 底上げ・チャネル別 facility
  (Issue #153 / #154)も実機 Fluent Bit 5.0.8 で確認済み**——Security の失敗ログオン
  (4625。Level 0 + Audit Failure キーワード)が PRI `<84>`(authpriv(10)・warning(4)へ
  底上げ)、監査成功が PRI `<86>`(authpriv(10)・info(6) のまま。片方向の底上げどおり)、
  System エラーが `<27>`(daemon(3)・err(3))、Application 情報が `<134>`
  (local0(16)・info(6))で送出されることをフレーム実測で確認した
- `-Mode tcp` は単一行イベントの 1 レコード到達と、本文に改行を含むイベントの
  分割到達(先頭行 = 解析済みレコード、以降の行 = 解析失敗として原文保存)を
  実機確認済み(2026-07-10)。UDP の MTU 超え欠落との比較実測は未実施
  (「実機スモーク手順」参照)
- **既存導入端末のアップグレード経路も実機確認済み(2026-07-10)**: 4.0.14 導入済みの
  端末で 5.0.8 の MSI を含むキットの `install.ps1` を再実行 → サービス停止 →
  in-place アップグレード(msiexec 終了コード 0・再起動不要)→ エンジン 5.0.8 →
  サービス再作成・起動 → テストイベントの閲覧画面到達、まで確認。あわせて
  「古い MSI のキットを新しい導入環境へ再実行してもダウングレードしない」
  「MSI なしキットの再実行は設定・サービス定義の更新のみ行う」ことも確認
- OS 再起動後の自動起動・転送再開は Windows Server 2025 + 4.0.14 で実機確認済み
  (2026-07-06)。5.0.8 の MSI もサービス自動登録(遅延自動起動)の挙動が同一である
  ことは確認済みだが、5.0.8 での再起動実測は未実施
- それ以外の版を使う場合は、テスト端末で `install.ps1` → 動作確認 → 本配布の順を推奨
- **`-Mode tls`(syslog over TLS。Issue #137)は 2026-07-11 に実機検証したが、Fluent Bit
  5.0.8 から Yagura への配送は成立しなかった**(前述「TLS を使う」の既知の非互換)。
  検証できたのは Yagura の TLS 受信側の単体動作のみ——`openssl s_client` を送信元として
  ①期限内証明書での TLS ハンドシェイク + octet-counting フレーミング送信が Yagura に到達・
  解析・保存されること、②Yagura に既に期限切れの証明書を構成しても TLS 受信リスナが
  起動・接続受理を継続すること(「止めない」設計)、③期限切れ証明書に対する厳格な証明書
  検証(`-verify_return_error`)がクライアント側でハンドシェイク拒否を起こし、Yagura 側は
  これをフレーミング失敗とは別経路の「TLS ハンドシェイク失敗」として記録すること、
  ④証明書の期限接近・期限切れの周期監視通知(EventId 1017/1018)が実プロセスで正しく
  発火すること、を確認した(security.md §6.1・SEC-11)

### ARM64(winarm64)の検証状況(ADR-0009 決定7・委任 #4)

**上記の実機検証はすべて x64 端末で行ったものである。** ARM64(`winarm64`)については、
本 PR の時点で次のみを実機・live 確認済みで、**Fluent Bit の実導入・サービス起動・
イベント転送の実機検証は未実施**である(理由: この検証環境自体が x64 であり、winarm64
バイナリを実行できない)。

- `https://packages.fluentbit.io/windows/fluent-bit-5.0.8-winarm64.msi` が公式に取得できること
  (2026-07-10、HTTPS/TLS 検証済みの公式ドメインから 23,796,327 バイトを取得)
- 取得物の SHA256(`9730cd2479276b2fd8f323c8c5ddbfe6be52e2f4e8ebb3caae1efda46d327860`)を
  `Get-FileHash` で算出できること(同日。`ForwarderMsiConstraints.OfficialSha256ForVerifiedVersionArm64`
  に記録)。同じ手順を win64 の MSI にも適用し、既存の公式ハッシュ定数と完全一致することを
  確認して手順自体の信頼性も検証した
- `install.ps1` のアーキ自動判定ロジック(`Get-LocalMsiFilenamePattern`)は、
  `PROCESSOR_ARCHITECTURE` / `PROCESSOR_ARCHITEW6432` 環境変数を差し替えるユニット相当の
  検証で、AMD64 → `win64` パターン、ARM64 → `winarm64` パターン、x86(WOW64 経由での
  ARM64/x64 判定を含む)→ 該当する親 OS のパターン、ネイティブ x86 → 明確な失敗、の
  各分岐を確認済み

**ARM64 実機での Fluent Bit 導入・サービス起動・イベント転送・OS 再起動後の再開の確認は
今回のスコープでは実施していない。** これは Yagura サーバ本体の ARM64 対応が
「試験的(Experimental)」水準に留まる([ADR-0009](../adr/0009-architecture-support.md) 決定2)
のと同じ考え方を、収集対象端末側にも一貫して適用したものである——実機がない状態で
「検証済み」と過大に表明しない(conventions.md の実体検証原則)。ARM64 端末での実機確認は、
Windows 11 on Arm 等の実機を持つ試用協力者からのフィードバック、または今後の lab 実施に
委ねる(Yagura サーバ本体の `windows-11-arm` CI ランナーによるスモークとは別の検証対象であり、
本 PR では release.yml への追加は行わない——フォワーダキットの検証はサーバ本体のリリース
パイプラインと独立して進行できるため、サーバのリリースを ARM64 端末実機の確保待ちにしない
判断による)。
