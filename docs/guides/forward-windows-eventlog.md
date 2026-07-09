# Windows イベントログを Yagura へ転送する

Windows 端末・サーバのイベントログを、[Fluent Bit](https://fluentbit.io/)(Apache-2.0)経由で
Yagura へ syslog(RFC 5424)転送する手順。転送プロトコルは UDP(既定)/ TCP を
`install.ps1 -Mode` で選択できる(それぞれの特性は後述「UDP と TCP のどちらを使うか」参照)。
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

### 2. 配置フォルダに MSI を置く

配置先は**データルート配下の `forwarder` フォルダ**(既定インストールでは
`%ProgramData%\Yagura\forwarder\`)。このフォルダは Yagura のインストーラ(MSI)が
作成し、専用の ACL(Administrators のみ書き込み可。次節)を設定済みである。
管理者権限の PowerShell からコピーする:

```powershell
Copy-Item ".\fluent-bit-4.0.14-win64.msi" "$env:ProgramData\Yagura\forwarder\"
```

フォルダが存在しない場合(旧版インストーラで導入した後、最新版へ未アップグレードの
環境等)は、最新インストーラでの上書きインストールで作成・ACL 設定されるのを待つか、
手動で作成して次節の期待値どおりに ACL を設定すること。

**ファイル名は `fluent-bit-*-win64.msi` パターンに一致させること**
(大文字小文字は区別しない。例: `fluent-bit-4.0.14-win64.msi`)。packages.fluentbit.io から
そのまま取得したファイル名であれば通常このパターンに一致する。フォルダにはこのパターンに
一致する MSI が**常に 1 つだけ**存在する状態にする(複数あるとエラーになる。後述)。

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
| `winevt-severity.lua` | イベントログの Level → syslog severity 変換、Keywords(監査成功/失敗)の severity 反映、チャネル → facility 変換を行うフィルタ |

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
| `-YaguraPort` | `514` | Yagura の syslog 受信ポート |
| `-Channels` | `System,Application` | 収集するイベントログチャネル(カンマ区切り。`System`/`Application`/`Security` 以外の値や空要素(連続・末尾カンマ)を指定するとエラーで停止する——Issue #155) |
| `-Mode` | `udp` | 転送プロトコル(`udp` / `tcp`)。特性の違いは「UDP と TCP のどちらを使うか」を参照(Issue #156) |
| `-MsiPath` | (自動検出) | MSI のパス。省略時はスクリプトと同じフォルダから検出 |

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

## UDP と TCP のどちらを使うか

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
- **管理 UI(`/admin/forwarder-kit`)で生成するキットには `-Mode` に相当する選択肢がない。**
  生成キットは常に UDP で固定(`Yagura.Web.ForwarderKit.ForwarderKitRequest` に
  Mode を保持するフィールドがなく、`ForwarderKitBuilder` が conf の `@@MODE@@` を
  常に `udp` へ置換する)。生成キットで TCP を選べるようにするには、
  `ForwarderKitRequest` / `ForwarderKitBuilder` / 生成画面 UI への Mode パラメータ追加が必要

### 選択の目安

- 大きなイベント(特に挿入文字列の多い Security 監査・詳細な失敗イベント)を
  **欠落なく**届けたい環境は `-Mode tcp` を検討する。ただし本文に埋め込み改行がある
  イベントは受信側で複数レコードに分割され得ることを許容できるか判断すること
- 本文が短いイベントが中心、または改行を含む本文が分割されても実運用上問題ない環境は
  `-Mode udp`(既定)のままでよい
- 判断に迷う場合は、テスト端末で両モードを実機比較してから本配布することを推奨する
  (次節「実機スモーク手順(未実施)」参照)

### 実機スモーク手順

`-Mode tcp` は 2026-07-09 時点で実機検証(欠落比較・分割の実地確認)が未実施。
本配布の前に、以下の比較確認をテスト端末で実施することを推奨する。

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

`install.ps1` は再実行に対して安全(冪等)——導入済みなら MSI をスキップし、設定とサービス定義を更新して再起動する。
宛先変更やチャネル追加は、パラメータを変えて再実行すればよい。

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
- **送信**: RFC 5424 / UDP または TCP(`-Mode` で選択。既定 UDP、特性は前述「UDP と TCP の
  どちらを使うか」参照)。HOSTNAME = イベントの `Computer`、APP-NAME = `ProviderName`
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

- Windows Server 2025 + Fluent Bit **4.0.14**(`fluent-bit-4.0.14-win64.msi`)で
  サイレント導入 → サービス登録(遅延自動起動)→ 起動 → テストイベントが Yagura の
  閲覧画面に到達するまでの全経路と、導入済み端末への再実行(冪等)、OS 再起動後の
  自動起動・転送再開を実機確認済み(2026-07-06。**`-Mode udp`(既定)での確認**)
- 転送内容(本文 / ホスト名 / アプリ名 / 重大度の対応、イベント ID・チャネルの
  構造化データ)も同環境で実機確認済み(送信フレームの実測 + 閲覧画面への到達。`-Mode udp`)
- **`-Mode tcp` は 2026-07-09 時点で実機未検証。** 導入前に「実機スモーク手順」節の
  比較確認を実施すること
- それ以外の版を使う場合は、テスト端末で `install.ps1` → 動作確認 → 本配布の順を推奨
  (5.x 系は本キットでの転送内容検証を未実施)
- **Keywords による監査失敗 severity 底上げ・チャネル別 facility(Issue #153 / #154)は、
  実機 Fluent Bit での動作確認を未実施**(単体テストの対象外である Lua スクリプトの
  実行結果そのものは lab 検証が必要——conventions.md の実体検証原則)。実機での確認手順は
  当該 PR の説明を参照すること
