# Windows イベントログを Yagura へ転送する

Windows 端末・サーバのイベントログを、[Fluent Bit](https://fluentbit.io/)(Apache-2.0)経由で
Yagura へ syslog(RFC 5424 / UDP)転送する手順。
リポジトリの [forwarder/fluent-bit/](../../forwarder/fluent-bit/) に**サイレント導入可能な配布キット**があり、
Intune / SCCM(Configuration Manager)/ GPO スタートアップスクリプト等の企業配布基盤で無人 push できる。
手動での導入にも使える。

**管理 UI(`/admin/forwarder-kit`)から、このサーバの宛先を設定済みのキットを生成できる(v0.2。
[ADR-0008](../adr/0008-forwarder-kit-generation.md))。** 生成したキットは `install.ps1` を
パラメータなしで実行できる(宛先の手入力による誤記を避けられる)。以下の手順は、生成 UI を
使わずキットを手動で組み立てる場合の**正の手順**として引き続き維持する。

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

[packages.fluentbit.io](https://packages.fluentbit.io/) から `win64` 版(64bit)の MSI を
取得し、`Get-FileHash` でハッシュ値を確認する(取得元の改ざん・誤ファイル取り違えの
チェック):

```powershell
Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-4.0.14-win64.msi" `
                  -OutFile ".\fluent-bit-4.0.14-win64.msi"
Get-FileHash ".\fluent-bit-4.0.14-win64.msi" -Algorithm SHA256
```

`Get-FileHash` が返す `Hash` の値を控えておく(後述の管理画面表示や、自組織の記録との
突き合わせに使う)。**Yagura は現時点で公式配布の基準ハッシュを内部に保持していない**
(`ForwarderMsiConstraints.OfficialSha256ForVerifiedVersion` は未設定のプレースホルダ)ため、
管理画面の「公式配布 SHA256 との照合」は常に「未実施」と表示される。取得元の真正性は
入手時点で管理者自身が確認すること。

### 2. 配置フォルダを作成し、MSI を置く

配置先は**データルート配下の `forwarder` フォルダ**(既定インストールでは
`%ProgramData%\Yagura\forwarder\`)。このフォルダはインストーラ(MSI)・Yagura 本体の
どちらも自動作成しない(委任事項の申し送りとして残っている未実装項目)。初回は管理者が
自分で作成する:

```powershell
New-Item -ItemType Directory -Path "$env:ProgramData\Yagura\forwarder" -Force
Copy-Item ".\fluent-bit-4.0.14-win64.msi" "$env:ProgramData\Yagura\forwarder\"
```

**ファイル名は `fluent-bit-*-win64.msi` パターンに一致させること**
(大文字小文字は区別しない。例: `fluent-bit-4.0.14-win64.msi`)。packages.fluentbit.io から
そのまま取得したファイル名であれば通常このパターンに一致する。フォルダにはこのパターンに
一致する MSI が**常に 1 つだけ**存在する状態にする(複数あるとエラーになる。後述)。

### 3. フォルダの ACL を確認する

`forwarder` フォルダは、作成時に親フォルダ(データルート `%ProgramData%\Yagura`)の
ACL を継承する。次のコマンドで確認する:

```powershell
icacls "$env:ProgramData\Yagura\forwarder"
```

データルートのインストーラ既定 ACL(`installer/Package.wxs`)に基づき、次の 3 エントリが
継承されて表示されるのが正常な状態:

```
NT AUTHORITY\SYSTEM:(OI)(CI)(F)
BUILTIN\Administrators:(OI)(CI)(F)
NT SERVICE\Yagura:(OI)(CI)(M)
```

読み方: `(F)` はフルコントロール、`(M)` は変更(読み書き・削除は可だが権限変更は不可)、
`(OI)(CI)` は配下のファイル・フォルダへ継承されることを示す。`NT SERVICE\Yagura` は
Yagura サービスの仮想サービスアカウント(実行アカウント)であり、環境によっては名前解決
されず SID(`S-1-5-80-...` で始まる文字列)のまま表示されることがある(名前解決の可否は
実機依存で未検証)。

**注意(設計意図と現状の差)**: [ADR-0008](../adr/0008-forwarder-kit-generation.md) 設計条件 9 は
「配置フォルダは既定で Administrators のみ書き込み可」を意図しているが、上記のとおり
`forwarder` フォルダはデータルートの ACL をそのまま継承するため、**現状は Yagura サービス
アカウントにも変更(書き込み)権限がある**([docs/design/security.md §5.1](../design/security.md)
に記載の既知のギャップ。フォルダの明示的な ACL 個別設定はインストーラの未実装項目)。
意図どおり Administrators のみに絞りたい場合は、配置後に手動で ACL を絞り込むこと
(例: `icacls` でサービスアカウントの ACE を明示的に除去する。誤って `SYSTEM` /
`Administrators` の権限まで奪わないよう注意)。

### 4. 管理画面での見え方を確認する

`/admin/forwarder-kit` を開くと「MSI の同梱(任意)」欄に配置フォルダのフルパスが
常に表示される。状態ごとの見え方:

| 状態 | 画面表示 |
|---|---|
| 未配置(フォルダが無い/パターンに一致するファイルが無い) | 「MSI 未検出。ここに `fluent-bit-*-win64.msi` を配置すると、生成する ZIP に MSI を同梱できます(任意)。」 |
| 正しく 1 つ配置 | 「MSI を同梱する」チェックボックス(既定オフ)。チェックすると検出ファイル名・版・SHA256・公式ハッシュ照合結果(現状は常に「未実施」)・ZIP サイズ予告(20 MB 超見込み)を表示 |
| パターンに一致する MSI が複数 | 赤字で「複数の MSI が見つかりました。1 つだけ残してください。」+ 検出した全ファイル名一覧。同梱チェックボックス自体が出ず、MSI 同梱を選べない |

**版不一致時の二段階確認**: 検出した MSI の版(MSI の `ProductVersion` を優先取得。
取得できない場合のみファイル名から推定した値を補助的に使う)が、生成 UI が表明する
「検証済み Fluent Bit 版」(現在 `4.0.14`)と異なる場合、黄色の警告
「検出した MSI の版(…)は検証済み版(…)と異なります。動作未検証の組み合わせになる
可能性があります。」が出て、「版が異なることを理解した上で、この MSI を同梱します」の
チェックを入れないと生成ボタンでエラーになり先へ進めない。これは同梱ミス(版違いに
気付かず配布してしまう)を防ぐための確認であり、拒否ではなく承認すれば同梱できる。

### よくある失敗

| 症状 | 原因 | 対処 |
|---|---|---|
| MSI を置いたのに「MSI 未検出」のまま | ファイル名がパターン `fluent-bit-*-win64.msi` に一致していない(例: 32bit/ARM64 版、`fluentbit-...`(ハイフン抜け)、拡張子違い等)。**一致しないファイルはエラーにならず単に無視される**ため、原因に気付きにくい | `Get-ChildItem "$env:ProgramData\Yagura\forwarder"` でファイル名を確認し、`fluent-bit-<版>-win64.msi` の形に合わせる |
| 「複数の MSI が見つかりました」エラー | 旧版を消さずに新版を追加した等、パターンに一致するファイルがフォルダに 2 つ以上ある | 残したい 1 つ以外を削除・退避し、フォルダには常に 1 つだけにする |
| 版不一致の警告が出て生成できない | 検証済み版(`4.0.14`)と異なる MSI を配置した | 内容を理解した上で確認チェックを入れるか、検証済み版の MSI に差し替える |
| 「公式配布 SHA256 との照合」が常に未実施 | Yagura はこの版の公式ハッシュ基準値を未設定(既知の制限。将来 PR で確定予定) | 取得時点の `Get-FileHash` の値を自組織の記録(調達時のメモ・改ざん検知運用等)と突き合わせて確認する |
| フォルダ作成・ファイルコピーが権限エラーで失敗する | ログオンユーザーが Administrators に属していない、または非管理者権限の PowerShell で実行している | Administrators に属するアカウントで、管理者として実行した PowerShell から操作する |

## キットの内容

| ファイル | 役割 |
|---|---|
| `install.ps1` | サイレント導入スクリプト(MSI 無人導入 → 設定配置 → サービス登録・遅延自動起動 → 起動確認) |
| `uninstall.ps1` | 撤去スクリプト(サービス削除 + 設定削除。`-RemoveFluentBit` で MSI も削除) |
| `fluent-bit-yagura.conf` | 転送設定テンプレート(導入時に宛先等を自動置換) |
| `winevt-severity.lua` | イベントログの Level → syslog severity 変換フィルタ |

Fluent Bit の MSI 本体はキットに**同梱しない**。
[packages.fluentbit.io](https://packages.fluentbit.io/) から `fluent-bit-<版>-win64.msi` を取得し、
キットと同じフォルダに置いて配布する(検証済みの版は後述)。

## 導入手順

### 1. MSI を取得してキットに同梱する

```powershell
Invoke-WebRequest -Uri "https://packages.fluentbit.io/windows/fluent-bit-4.0.14-win64.msi" `
                  -OutFile ".\fluent-bit-4.0.14-win64.msi"
```

### 2. サイレント導入を実行する(管理者権限)

```powershell
powershell -NoProfile -File .\install.ps1 -YaguraHost <Yagura サーバの IP またはホスト名>
```

| パラメータ | 既定値 | 説明 |
|---|---|---|
| `-YaguraHost` | (必須) | Yagura サーバの IP / ホスト名 |
| `-YaguraPort` | `514` | Yagura の syslog 受信ポート(UDP) |
| `-Channels` | `System,Application` | 収集するイベントログチャネル(カンマ区切り) |
| `-MsiPath` | (自動検出) | MSI のパス。省略時はスクリプトと同じフォルダから検出 |

導入が成功すると標準出力に `INSTALL_SUCCESS` が出て終了コード 0 で終わる。
終了コード: `0` = 成功 / `1` = 失敗 / `3010` = 成功(OS 再起動が必要)。

### 3. 動作確認

- サービス `fluent-bit` が「実行中」かつ「自動(遅延開始)」になっていること:
  `Get-Service fluent-bit | Format-List Name, Status, StartType`
  (遅延開始かどうかは `StartType` には出ない。`sc.exe qc fluent-bit` の
  `AUTO_START (DELAYED)` で確認できる)
- Yagura の閲覧画面(`http://<Yagura>:8514/`)に導入端末のイベントが届いていること
  (新規イベントのみ転送するため、届かない場合はテストイベントを書き込む:
  `eventcreate /T INFORMATION /ID 999 /L APPLICATION /SO YaguraKitTest /D "test"`)

## 企業配布基盤での push

- **Intune (Win32 アプリ)**: キット一式 + MSI を `.intunewin` 化し、インストールコマンドを
  `powershell -NoProfile -File install.ps1 -YaguraHost <IP>`、
  アンインストールコマンドを `powershell -NoProfile -File uninstall.ps1 -RemoveFluentBit` にする。
  検出規則はサービス `fluent-bit` の存在または `C:\ProgramData\fluent-bit-yagura\fluent-bit-yagura.conf`
- **SCCM**: パッケージ/アプリケーションとして同じコマンドラインを指定する
- **GPO**: コンピューターのスタートアップスクリプトとして割り当てる(SYSTEM 実行のため管理者権限要件を満たす)

`install.ps1` は再実行に対して安全(冪等)——導入済みなら MSI をスキップし、設定とサービス定義を更新して再起動する。
宛先変更やチャネル追加は、パラメータを変えて再実行すればよい。

## 設定の内容

- **収集**: `winevtlog` 入力で System / Application チャネルを追跡(既読位置は
  `C:\ProgramData\fluent-bit-yagura\winevtlog.sqlite` に永続化。導入前の既存イベントは送らない。
  サービス停止中に書かれたイベントは、次回起動時に既読位置から追いついて転送される)
- **変換**: Lua フィルタでイベントの Level を syslog severity へ変換
  (Critical→crit、Error→err、Warning→warning、Information→info、Verbose→debug)
- **送信**: RFC 5424 / UDP。HOSTNAME = イベントの `Computer`、APP-NAME = `ProviderName`
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

Fluent Bit の MSI(4.0.14 で確認)は、インストール時に**自ら** `fluent-bit` サービスを
登録する(遅延自動起動・同梱の既定設定ファイルを指す)。`install.ps1` はこのサービスを
削除し、Yagura 用設定を指す同名サービスとして**作り直す**(PowerShell 5.1 の
`sc.exe config` は引用符入り binPath を壊すため、変更ではなく `New-Service` による
再作成を採用)。このため:

- Fluent Bit の MSI を後から上書き更新した場合、サービス定義が既定に戻る可能性がある。
  **MSI 更新後は `install.ps1` を再実行する**こと(冪等なので安全)

### Security チャネルを収集する場合

`-Channels "System,Application,Security"` を指定する。サービスは LocalSystem で動作するため
追加権限は不要だが、Security チャネルはイベント量が多く機微情報を含むため、
組織のポリシーで明示的に判断してから有効化すること。

## 検証済み環境

- Windows Server 2025 + Fluent Bit **4.0.14**(`fluent-bit-4.0.14-win64.msi`)で
  サイレント導入 → サービス登録(遅延自動起動)→ 起動 → テストイベントが Yagura の
  閲覧画面に到達するまでの全経路と、導入済み端末への再実行(冪等)、OS 再起動後の
  自動起動・転送再開を実機確認済み(2026-07-06)
- 転送内容(本文 / ホスト名 / アプリ名 / 重大度の対応、イベント ID・チャネルの
  構造化データ)も同環境で実機確認済み(送信フレームの実測 + 閲覧画面への到達)
- それ以外の版を使う場合は、テスト端末で `install.ps1` → 動作確認 → 本配布の順を推奨
  (5.x 系は本キットでの転送内容検証を未実施)
