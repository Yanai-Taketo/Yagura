# バックアップ・復旧・サーバの移設と交換

[運用ガイドの目次へ戻る](../operations.md)

Yagura の設定とデータを守り、壊れたときに戻し、別のサーバへ移すための手順。**Yagura 自身には、設定とデータをまとめてバックアップ・復元する機能はない**（閲覧画面の CSV エクスポートは検索結果の書き出しであり、戻す手段にはならない）。データルートのファイルをサービス停止中に複製し、SQL Server を保存先にしている場合はそのデータベースを SQL Server の標準機能でバックアップする。

| 場面 | 見る節 |
|---|---|
| 定期的にバックアップを取る | [バックアップを取る](#バックアップを取る)・[SQL Server のデータベースをバックアップする](#sql-server-のデータベースをバックアップする) |
| 同じサーバで、消えた・壊れたデータを戻す | [同じサーバへ戻す（復旧）](#同じサーバへ戻す復旧) |
| Yagura を別のサーバへ移す（サーバの入れ替え） | [別のサーバへ移す（移設）](#別のサーバへ移す移設) |
| 保存先の SQL Server だけを別のサーバへ替える | [SQL Server を別のサーバへ替える](#sql-server-を別のサーバへ替える) |

本書の約束事:

- コマンドは PowerShell で書く。「管理者 PowerShell」は「管理者として実行」で開いた Windows PowerShell を指す。各手順の冒頭に**実行場所**を書く
- データルートは既定の `C:\ProgramData\Yagura`（PowerShell では `$env:ProgramData\Yagura`）を前提にする。環境変数 `YAGURA_DATAROOT` でデータルートを既定以外の場所にしている場合は読み替える（インストーラがアクセス権を設定するのは既定の場所だけなので、移設先でも同じアクセス権を自分で付ける）
- パスワードなどの資格情報は、本書の手順で作るファイルに書かない。組織の資格情報の管理方法で保管する

## バックアップの対象

### データルートの中身

**データルートは丸ごと複製する。** 中身と、戻すときの扱いは次のとおり。

| ファイル・フォルダ | 中身 | 扱い |
|---|---|---|
| `yagura.json` | 設定ファイル。暗号化した資格情報を含むことがある（[暗号化された設定値（DPAPI）とサーバの関係](#暗号化された設定値dpapiとサーバの関係)） | 必ず含める |
| `yagura.json.last-good` | 最後に正常に読み込めた設定の写し | 含める |
| `last-applied-configuration.json` | 最後に適用した設定の記録（起動時に設定ファイルの変更を監査記録へ残すための基準） | 含める |
| `yagura.db`・`yagura.db-wal`・`yagura.db-shm` | 組み込み DB。組み込み DB で運用している間のログ、受信できなかった時間帯などの記録、アプリ独自の管理者アカウントが入る | 必ず 3 つを組で扱う（`-wal`・`-shm` は無いこともある）。本体だけを複製すると最新の書き込みが欠けることがある |
| `spool\` | 保存先にまだ書けていないログの一時保管。サービス停止時には、メモリ上にあった未保存のログもここへ退避される | 必ず含める（次の起動で、そのときの保存先へ書き込まれる。SQL Server で運用していて別のサーバへ移す場合は、書き戻す時期に注意する: [手順 5](#手順-5-起動して暗号化された設定値を入れ直す)） |
| `audit\` | 監査記録（`audit-yyyyMMdd.jsonl`。既定の保持は 365 日） | 必ず含める |
| `observability-state.json` | カウンタの累積値と、最後に停止した時刻 | 含める（戻すとカウンタが続きから数え、停止から起動までが「受信できなかった時間帯」として記録される） |
| `dataprotection-keys\`・`admin-session-generation.txt` | サインインの Cookie の鍵と、緊急全失効の世代番号 | 含める。戻すときは**両方を組で**戻す（鍵を失っても、サインイン中の利用者が再サインインを求められるだけで済む）。戻すと世代番号もバックアップの時点に戻るため、バックアップの後に緊急全失効を行っていた場合は、戻した後にもう一度実行する |
| `last-service-account.json` | 前回起動時のサービス実行アカウント | 含める |
| `log-migration-checkpoint.json` | 蓄積ログの移行（組み込み DB → SQL Server）の進み具合 | あれば含める |
| `forwarder\` | 管理者が置いたフォワーダ（Fluent Bit）の MSI | あれば含める |
| `firewall-rules.ini`・`service-account.ini`（と、それぞれの `.transcribed`） | インストーラがそのサーバについて書いた記録 | 含めてよいが、**別のサーバへは戻さない**（移設先のインストーラが自分の記録を書く） |

- **設定でスプールの置き場所（`Spool:Directory`）を変えている場合**は、そのフォルダもバックアップの対象にする。組み込み DB のファイル名（`Storage:SqliteFileName`）を変えている場合は、その名前で読み替える
- データルートのアクセス権は、`SYSTEM` と `Administrators` がフルコントロール、サービスの実行アカウント（既定 `NT SERVICE\Yagura`。gMSA で動かしている場合はその gMSA）が変更、である。`forwarder` だけはサービスの実行アカウントが読み取りのみ。`Users` には権限がない
- **バックアップにはログ・監査記録・設定・Cookie の鍵が入る。** 置き場所も、管理者（`Administrators` と `SYSTEM`）だけが読めるようにする。下の手順はその設定を含む。保存先のディスクの暗号化など、組織の統制もあわせて検討する

### データルートの外にあるもの

| 対象 | 扱い |
|---|---|
| SQL Server のデータベース（本番昇格している場合） | ログ・受信できなかった時間帯などの記録・アプリ独自の管理者アカウントは、このデータベースにある。SQL Server の標準機能でバックアップする（[SQL Server のデータベースをバックアップする](#sql-server-のデータベースをバックアップする)） |
| 証明書（TLS 受信・閲覧 UI の HTTPS・管理画面のリモートアクセスを有効にしている場合） | `yagura.json` は証明書の**拇印**を記録しているだけで、証明書と秘密鍵は Windows の証明書ストア（ローカル コンピューター\個人）にある。データルートの複製には含まれない |
| 自分で追加したファイアウォール規則 | データルートの複製には含まれない。移設先で作り直す（[ファイアウォール規則を自分で足す](firewall.md)） |
| データルートに自分で付けたアクセス権（監査記録の削除権限の分離、フォワーダ配置フォルダへの書き込み許可など） | 複製には含まれない。移設先で付け直す |
| 環境変数 `YAGURA_*`（設定している場合） | `YAGURA_DATAROOT`・`YAGURA_UDP_PORT`・`YAGURA_TCP_PORT`・`YAGURA_HTTP_PORT`・`YAGURA_ADMIN_PORT`・`YAGURA_ADMIN_HTTPS_PORT`・`YAGURA_INGESTION_TLS_PORT` は、設定ファイルより優先される。データルートの複製には含まれないので、移設先にも同じ値を設定する（設定していないと、移設先では待ち受けポートやデータルートが変わる） |
| Windows イベントログ（アプリケーション、ソース `Yagura`） | 監査記録と警告が併記されている。Windows が管理するログであり、データルートの複製には含まれない |
| `C:\Program Files\Yagura`（プログラム本体） | バックアップは要らない（MSI で入る） |

## 暗号化された設定値（DPAPI）とサーバの関係

Yagura は、設定ファイルの次の値を Windows の DPAPI（コンピューター単位の鍵）で暗号化して保存する（[ADR-0004](../adr/0004-security-model.md) 決定 5）。暗号化した値は `dpapi:` で始まる文字列になる。

| 設定キー | 中身 | 暗号化されるとき |
|---|---|---|
| `Storage:SqlServer:ConnectionString` | SQL Server の接続文字列 | 管理画面の「保存先を SQL Server に切り替える」で保存したとき（**Windows 統合認証でも暗号化される**） |
| `Notification:Email:Smtp:Password` | メール通知の SMTP 認証のパスワード | 管理画面の「メール通知」で保存したとき |

- **暗号化した値は、暗号化したサーバでしか復号できない。** `yagura.json` を別のサーバへ戻しても、これらの値は使えない。別のサーバでは管理画面で入れ直す
- 同じサーバでも、OS を入れ直した場合は復号できるとは限らない。入れ直す前提で準備しておく
- 同じサーバの中でサービスの実行アカウントを変えても、復号は壊れない
- アプリ独自の管理者アカウントのパスワードは DPAPI の対象ではない（データベースと一緒に移る）

復号できなかったときの動き:

| 値 | Yagura の動き | 戻し方 |
|---|---|---|
| SQL Server の接続文字列 | 起動を止めず、**組み込み DB に切り替えて受信を続ける**。イベントログに `Storage:SqlServer:ConnectionString` の警告が出る | 「保存先を SQL Server に切り替える」（`/admin/promotion`）で接続情報を入れ直し、サービスを再起動する |
| SMTP のパスワード | メール通知を止める（認証なしでは送らない）。イベントログに `Notification:Email:Smtp:Password` の警告が出る | 「メール通知」（`/admin/email-notification`）でパスワードを入れ直して保存する（再起動は要らない） |

> **最も注意すること**: 接続文字列を復号できない間は、SQL Server ではなく**データルートの組み込み DB が保存先になる**。この間に受信したログだけでなく、起動時にスプールから書き込まれるログ（旧サーバの停止時に退避された未保存のログを含む）と、受信できなかった時間帯の記録も組み込み DB に入り、後で接続情報を入れ直しても SQL Server には移らない。閲覧画面にも SQL Server のログは出ない（組み込み DB の中身が見える）。別のサーバへ移すときは、スプールなどを戻す前に接続情報を入れ直して SQL Server に接続できたことを確かめ、その後で送信元を新しいサーバへ向けること（[別のサーバへ移す（移設）](#別のサーバへ移す移設)の手順はこの順になっている）。

- **入れ直しに要る情報を用意しておく**: SQL Server のサーバ名・データベース名・認証方式（SQL Server 認証ならログイン名とパスワード）、SMTP 認証のパスワード。**設定ファイルの接続文字列は暗号化されていて読めない**ので、本番昇格のときに入力した内容を記録しておく必要がある
- **手編集で平文のまま書いた値は暗号化されない**（バックアップにも平文のまま入る）。パスワードを含む値（SMTP のパスワード、SQL Server 認証のパスワードを含む接続文字列）には起動のたびに警告が出る。資格情報を含まない平文の接続文字列（Windows 統合認証）は警告が出ず、暗号化もされていないので、別のサーバでも入れ直さずにそのまま使える。管理画面で入れ直すと、暗号化して保存し直される

## サービス停止の要否

| 作業 | Yagura サービスの停止 |
|---|---|
| データルートの複製 | **要る** |
| データルートへの書き戻し | **要る** |
| SQL Server のデータベースのバックアップ | 要らない（SQL Server の標準のバックアップは、使用中のデータベースを対象にできる） |
| Yagura が接続しているデータベースを、同じ SQL Server 上でバックアップから戻す | 要る（[SQL Server のデータベースを同じサーバで戻す場合](#sql-server-のデータベースを同じサーバで戻す場合)） |
| SQL Server を別のサーバへ替える | 途中で止める（[SQL Server を別のサーバへ替える](#sql-server-を別のサーバへ替える)） |

**Yagura には、稼働したまま組み込み DB やスプールの写しを取る機能はない。** 稼働中のファイルをそのまま複製した写し（バックアップソフトで稼働中に取得したものを含む）が復元に使えるかは、Yagura として確認していない。本書の手順どおり、サービスを止めてから複製する。

止めている間に起きること:

- syslog を受信できない。UDP で送られたログは失われる。TCP は送信側の実装によっては再送される
- 停止から起動までの区間は、状態画面（`/status`）の「受信できなかった時間帯の履歴」に記録される
- 停止の直前にメモリ上にあった未保存のログはスプールへ退避され、次の起動で保存先へ書き込まれる（スプールもバックアップに含めるのはこのため）

## バックアップを取る

**実行場所: Yagura サーバ・管理者 PowerShell。** 退避先のパス（例では `D:\YaguraBackup`）は環境に合わせて変える。

```powershell
$Root = "$env:ProgramData\Yagura"
$Dest = "D:\YaguraBackup\$(Get-Date -Format 'yyyyMMdd-HHmmss')"

# 1. 退避先を作り、SYSTEM と Administrators だけが読めるようにする
New-Item -ItemType Directory -Path $Dest -Force | Out-Null
icacls $Dest /inheritance:r /grant "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)"
icacls $Dest                                             # SYSTEM と Administrators の 2 つだけが表示されること

# 2. 戻すときに要る構成を記録する（版・実行アカウント・ファイアウォール規則・証明書の拇印・環境変数）
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
  Where-Object DisplayName -eq 'Yagura' |
  Select-Object DisplayName, DisplayVersion | Out-File "$Dest\yagura-version.txt"
(Get-CimInstance Win32_Service -Filter "Name='Yagura'").StartName | Out-File "$Dest\service-account.txt"
Get-NetFirewallRule -DisplayName 'Yagura*' -ErrorAction SilentlyContinue |
  Select-Object DisplayName, Enabled, Profile,
    @{ n = 'Protocol';  e = { ($_ | Get-NetFirewallPortFilter).Protocol } },
    @{ n = 'LocalPort'; e = { ($_ | Get-NetFirewallPortFilter).LocalPort } } |
  Out-File "$Dest\firewall-rules.txt"
Select-String -Path "$Root\yagura.json" -Pattern 'CertificateThumbprint' -ErrorAction SilentlyContinue |
  Out-File "$Dest\certificate-thumbprints.txt"
[Environment]::GetEnvironmentVariables('Machine').GetEnumerator() |
  Where-Object Key -like 'YAGURA_*' | Format-Table Key, Value -AutoSize |
  Out-File "$Dest\environment.txt"                       # システムの環境変数
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Yagura' -ErrorAction SilentlyContinue).Environment |
  Where-Object { $_ -like 'YAGURA_*' } |
  Out-File "$Dest\environment.txt" -Append               # サービス単位の環境変数

# 3. サービスを止める（ここから受信が止まる）
Stop-Service Yagura
Get-Service Yagura                                       # Status が Stopped であること
Get-Process Yagura.Host -ErrorAction SilentlyContinue    # 何も表示されないこと

# 4. データルートを丸ごと複製し、ファイル数を突き合わせる
robocopy $Root "$Dest\data" /E /R:1 /W:1
$LASTEXITCODE                                            # 8 未満なら成功（0〜7）
(Get-ChildItem $Root -Recurse -File -Force).Count
(Get-ChildItem "$Dest\data" -Recurse -File -Force).Count # 上と同じ数であること

# 5. サービスを起動する（ここで受信が再開する）
Start-Service Yagura
Get-Service Yagura                                       # Status が Running であること
```

- `robocopy` は既定でアクセス権を複製しない。複製したファイルは退避先のアクセス権（管理者だけ）を引き継ぐ
- **受信が止まる時間は、手順 3 から 5 まで**である。データルートの大きさ（大半は組み込み DB）で所要時間が決まるので、事前に `(Get-ChildItem $Root -Recurse -File -Force | Measure-Object Length -Sum).Sum / 1GB` で GB 数を見ておく。停止時間を短くしたい場合は、退避先を同じサーバの別のドライブにし、別のサーバや外部媒体への転送はサービスを起動した後に行う
- スプールの置き場所（`Spool:Directory`）を変えている場合は、手順 4 で `robocopy <スプールのフォルダ> "$Dest\spool-custom" /E /R:1 /W:1` も実行する
- 証明書を秘密鍵ごと移す予定がある場合は、`certificate-thumbprints.txt` の拇印を使って PFX ファイルに書き出しておく（[手順 4: 証明書を用意する](#手順-4-証明書を用意する)）
- 定期的に取る場合は、上の手順をスクリプトにしてタスク スケジューラから実行する。受信が止まるので、送信の少ない時間帯を選ぶ。何世代残すかは、ログの保持期間と監査の要件から決める。ディスク障害に備え、退避先の写しは別のサーバや媒体にも置く
- **スクリプトにする場合は、起動の失敗に気づける作りにする。** 複製に失敗しても、サービスの起動（手順 5）は必ず行う。起動の後に `(Get-Service Yagura).Status` が `Running` であることを確かめ、そうでなければもう一度起動を試み、それでも動かなければタスクを失敗で終わらせて、組織の監視へ知らせる。サービスが止まっている間は、Yagura 自身のイベントログの警告もメール通知も出ないため、この確かめが無いと受信が止まったまま誰も気づかない

## SQL Server のデータベースをバックアップする

SQL Server へ本番昇格している場合の、Yagura に固有の事実:

- **本番昇格のときに指定した 1 つのデータベース**に、ログ（`dbo.LogRecords`）、受信できなかった時間帯などの記録（`dbo.SystemEvents`）、アプリ独自の管理者アカウント（`dbo.AdminAccounts`）、スキーマの版の記録が入っている。**データベースを丸ごと**バックアップする（一部のテーブルだけを取らない）
- バックアップの間、Yagura を止める必要はない
- SQL Server を使っていても、設定・スプール・監査記録はデータルートにある。[バックアップを取る](#バックアップを取る)の手順も別に要る
- **接続情報（サーバ名・データベース名・認証方式）を記録しておく。** 設定ファイルの接続文字列は暗号化されていて、そこからは読めない

定期的なバックアップの組み方（復旧モデル、トランザクション ログのバックアップ、保存する世代）は SQL Server の標準の運用に従う。SQL Server Express には SQL Server エージェントがないため、定期実行は Windows のタスク スケジューラなどで組む。

移設や交換の直前に 1 回だけ取る場合の例（**実行場所: SQL Server Management Studio または sqlcmd。SQL Server の管理者が実行する**）:

```sql
BACKUP DATABASE [<データベース名>]
  TO DISK = N'<バックアップファイルのパス>'
  WITH COPY_ONLY, CHECKSUM;
```

- `COPY_ONLY` は、定期バックアップの順序に影響を与えない単発のバックアップにする指定である
- パスは **SQL Server が動いているサーバ上**のパスで、SQL Server のサービスアカウントが書き込める場所を指定する

## 同じサーバへ戻す（復旧）

データルートのファイルを誤って消した・壊した、ディスクを交換した（OS は入れ直していない）といった場合に、同じサーバへバックアップを戻す手順。**同じサーバ（同じ OS のまま）なら、暗号化された設定値はそのまま復号できる**ので入れ直しは要らない。OS を入れ直した場合は、[別のサーバへ移す（移設）](#別のサーバへ移す移設)の手順に従う。

- **Yagura が「プログラムと機能」に残っていない場合**は、先に**バックアップを取ったときと同じ版**の MSI を入れる（版は `yagura-version.txt`）
- **データルートのフォルダ（`C:\ProgramData\Yagura`）そのものが無い場合**は、Yagura をアンインストールしてから同じ版の MSI を入れ直す。インストーラがアクセス権付きでフォルダを作り直す

**実行場所: Yagura サーバ・管理者 PowerShell。**

```powershell
$Root   = "$env:ProgramData\Yagura"
$Backup = 'D:\YaguraBackup\20261116-090000\data'          # 戻すバックアップの data フォルダ
$Aside  = "D:\YaguraBackup\before-restore-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

# 1. サービスを止める
Stop-Service Yagura
Get-Process Yagura.Host -ErrorAction SilentlyContinue      # 何も表示されないこと

# 2. いまの中身を控えとして残す（調査用。不要になったら消す）
New-Item -ItemType Directory -Path $Aside -Force | Out-Null
icacls $Aside /inheritance:r /grant "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)"
icacls $Aside                                               # SYSTEM と Administrators の 2 つだけが表示されること
robocopy $Root $Aside /E /R:1 /W:1

# 3. いまの中身を消す（forwarder フォルダそのものは残す）
Get-ChildItem $Root -Force | Where-Object Name -ne 'forwarder' | Remove-Item -Recurse -Force

# 4. バックアップを書き戻す
robocopy $Backup $Root /E /R:1 /W:1
$LASTEXITCODE                                               # 8 未満なら成功
```

**スプールの置き場所（`Spool:Directory`）を変えている場合**は、続けてそのフォルダも戻す。いまの中身を控えに残してから消し、バックアップの `spool-custom` を書き戻す:

```powershell
$Spool = 'E:\YaguraSpool'                                   # yagura.json の Spool:Directory の値
robocopy $Spool "$Aside\spool-custom" /E /R:1 /W:1          # いまの中身を控えに残す
Get-ChildItem $Spool -Force | Remove-Item -Recurse -Force   # いまの中身を消す
robocopy 'D:\YaguraBackup\20261116-090000\spool-custom' $Spool /E /R:1 /W:1
```

ディスクを交換した場合などでフォルダそのものが無いときは、控えと消去の 2 行は飛ばし、[手順 3: 新サーバにバックアップを書き戻す](#手順-3-新サーバにバックアップを書き戻す)の後半と同じ方法でアクセス権を付けてフォルダを作ってから書き戻す（インストーラはこのフォルダにアクセス権を付けない）。

続けて、[アクセス権を確かめる](#アクセス権を確かめる)を行ってから `Start-Service Yagura` で起動し、[移設・復旧のあとで確かめること](#移設復旧のあとで確かめること)を確認する。

- **`forwarder` フォルダを消して作り直さないこと。** このフォルダはインストーラが専用のアクセス権（サービスの実行アカウントは読み取りのみ）で作っている。作り直すとデータルートのアクセス権を引き継ぎ、サービスの実行アカウントに書き込み権が付いてしまう
- 組み込み DB は、いまの `yagura.db*` を手順 3 ですべて消してから、バックアップの `yagura.db*` をまとめて戻す。**新旧の `yagura.db` と `-wal`・`-shm` を混ぜない**
- **バックアップを取った後に受信したログは戻らない**（組み込み DB で運用している場合）。バックアップの停止時刻から復旧後の起動までが「受信できなかった時間帯」として記録される
- SQL Server で運用していて**データルートだけを戻す**場合、バックアップの時点でスプールに残っていたログは、戻した後の起動でもう一度データベースへ書き込まれる。バックアップの後にすでに書き込み済みだった分は、重複して見えることがある
- 同じく SQL Server で運用していてデータルートだけを戻す場合、`observability-state.json` も戻すと、バックアップの停止時刻から復旧後の起動までが「受信できなかった時間帯」として SQL Server に記録される（実際にはその間も受信して保存していた）。カウンタもバックアップの時点の値に戻る。いまのファイルが壊れていなければ、手順 4 の後に `Copy-Item "$Aside\observability-state.json" $Root -Force` で控えから戻し、いまのものを使う
- バックアップの後に緊急全失効（[認証](authentication.md#緊急全失効退職者漏洩疑い時)）を行っていた場合は、起動した後にもう一度実行する。戻した世代番号のままだと、失効させたセッションが再び有効になる
- 保持期間（既定 30 日）を過ぎたログは、起動後の保持期間の削除で消える。古いバックアップを戻して昔のログを見たい場合はこの点に注意する

### SQL Server のデータベースを同じサーバで戻す場合

Yagura を止め（`Stop-Service Yagura`）、SQL Server の標準の手順でデータベースを戻してから、Yagura を起動する。接続先は変わらないので、接続情報の入れ直しは要らない。**バックアップを取った時点より後に保存されたログは、戻したデータベースには無い。**

### アクセス権を確かめる

**実行場所: Yagura サーバ・管理者 PowerShell。**

```powershell
icacls "$env:ProgramData\Yagura"
icacls "$env:ProgramData\Yagura\forwarder"
icacls "$env:ProgramData\Yagura\yagura.json"
```

| 対象 | 期待する表示（順序は問わない） |
|---|---|
| データルート | `NT AUTHORITY\SYSTEM:(OI)(CI)(F)`・`BUILTIN\Administrators:(OI)(CI)(F)`・`NT SERVICE\Yagura:(OI)(CI)(M)` の 3 つだけ |
| `forwarder` | 同じ 3 つで、`NT SERVICE\Yagura` だけが `(OI)(CI)(R)`（読み取りのみ） |
| ファイル（`yagura.json` など） | 同じ 3 つが `(I)`（親から継承）付きで表示される |

gMSA で動かしている場合は、`NT SERVICE\Yagura` を gMSA の名前（`DOMAIN\name$`）に読み替える。フォワーダ MSI のアップロード機能のために `forwarder` へ書き込み権を付けている場合は、それも表示される（[転送ガイド](../guides/forward-windows-eventlog.md#書き込み経路の開放と撤去os-管理者の明示操作)）。

期待と違う場合は、[アップグレードに失敗した / 製品が消えた・起動しないとき](upgrade-recovery.md#再インストールしても-access-to-the-path--is-denied-で起動しない場合)の 3 行のコマンドでアクセス権を既定に戻す（3 行で 1 組。注意書きも読むこと）。

## 別のサーバへ移す（移設）

Yagura を別のサーバへ移す手順。OS を入れ直したサーバへ戻す場合も、この手順に従う。流れは次のとおり。

1. 旧サーバでバックアップを取り、旧サーバが受信を再開しないようにする
2. 新サーバに**旧サーバと同じ版**の Yagura を入れる
3. 新サーバにバックアップを書き戻す（インストーラの記録は戻さない）
4. 証明書を用意する（使っている場合）
5. 起動して、暗号化された設定値を入れ直す（SQL Server を保存先にしている場合は、接続できたことを確かめてからスプールなどを戻す）
6. ファイアウォール規則を合わせる
7. 確かめてから、送信元を新サーバへ向ける
8. 旧サーバを片付ける

### 準備

- **新サーバは対応 OS にする**（[README のシステム要件](../../README.md#システム要件)）
- **旧サーバと同じ版の MSI** を [Releases](https://github.com/Yanai-Taketo/Yagura/releases) から入手する（版は `yagura-version.txt`）。新しい版へ上げるのは、移設を終えて動作を確かめてから、通常のアップグレードとして行う。バックアップを取った版より古い版へデータを戻さない
- **旧サーバが 0.5 系の場合**は、先に旧サーバを 1.0 へ更新して動作を確かめてから、1.0 同士で移設する。修正は最新リリースにしか提供されないため（[SECURITY.md](../../SECURITY.md#サポート対象バージョンと修正の届き方)）、新しいサーバに 0.5 系を入れ直さない。旧サーバの OS に 1.0 を導入できない場合（OS ビルドが 17763 未満。1.0 の MSI は実行した直後に止まる）に限り、新サーバに旧サーバと同じ版を入れて移設し、動作を確かめたらすぐに 1.0 へ更新する
- **サーバ名と IP アドレスをどうするか決める。** 新サーバは新しい名前とアドレスで用意し、確かめてから送信元を切り替えるのを基本にする。旧サーバのアドレスを新サーバへ引き継ぐ場合は、旧サーバを止め、新サーバでの確認（手順 7）を済ませてから付け替える
- **送信元を洗い出す**: ネットワーク機器・サーバの syslog の送信先設定と、フォワーダ配布キットで導入した Windows 端末。送信先を**名前**で書いている送信元は、その名前の解決（DNS）を新サーバへ向ければ切り替わる。**IP アドレス**で書いている送信元は、DNS を変えても切り替わらない。送信先の設定を変えるか、旧サーバのアドレスを新サーバへ付け替える
- **フォワーダ配布キットには、キットを作ったときの宛先が書き込まれている。** キットの画面が宛先の候補として示すのはサーバの IP アドレスで、名前が書き込まれるのは手入力した場合だけである。宛先が名前のキットは、上の DNS の付け替えで足りる。宛先が IP アドレスのキットは、新サーバの管理画面（`/admin/forwarder-kit`）でキットを作り直して配り直すか、旧サーバのアドレスを新サーバへ付け替える
- **SQL Server を保存先にしている場合**は、新サーバの実行アカウントのログインを SQL Server に用意する（データベースの管理者に依頼する）:

  | 接続の方式 | SQL Server に要るログイン |
  |---|---|
  | Windows 統合認証・既定の仮想サービスアカウント・SQL Server が別のサーバ | 新サーバのコンピューターアカウント `DOMAIN\新サーバ名$`（旧サーバの名前とは別のログインになる） |
  | Windows 統合認証・gMSA | 旧サーバと同じ gMSA の名前のまま使える。ただし AD 管理者が、新サーバに gMSA の取得許可を与える（[gMSA ガイド](../guides/gmsa-service-account.md) §2.2・§2.3） |
  | SQL Server 認証 | 同じログインをそのまま使える。パスワードは入れ直す |

  SQL Server も旧サーバに同居している場合は、手順 1 で Yagura を止めた後に[データベースもバックアップ](#sql-server-のデータベースをバックアップする)し、新サーバの SQL Server へ復元しておく。この場合の新サーバのログインは、手順 5 のウィザードの画面に表示されるアカウントで作る
- **gMSA で動かしている場合**は、新サーバでも gMSA の前提条件を満たしておく（[gMSA ガイド](../guides/gmsa-service-account.md) §2）
- **暗号化された設定値の入れ直しに要る情報**を手元に用意する（[暗号化された設定値（DPAPI）とサーバの関係](#暗号化された設定値dpapiとサーバの関係)）
- 管理 UI の Windows 統合認証は、既定では**そのサーバのローカル Administrators** で管理者を判定する（[管理 UI 認証と AD グループ](authentication.md#管理-ui-認証と-ad-グループsec-9)）。新サーバのローカル Administrators の構成を確かめておく

### 手順 1: 旧サーバでバックアップを取る

**実行場所: 旧サーバ・管理者 PowerShell。** [バックアップを取る](#バックアップを取る)の手順 1〜4 を行い、**手順 5（起動）は行わずに**、次で自動起動を止める。

```powershell
Set-Service Yagura -StartupType Disabled    # 移設の間に旧サーバが受信を再開しないようにする
```

- 組み込み DB で運用している場合、旧サーバが受信を続けると、バックアップの後に受けたログは新サーバに入らない
- SQL Server で運用している場合も、新旧 2 台から同じデータベースへ同時に書き込む使い方は想定していない
- **ここから新サーバで受信を始めるまで、受信は止まる。** 作業の時間帯を周知しておく
- 証明書を秘密鍵ごと移す場合は、旧サーバにいるこの時点で[手順 4](#手順-4-証明書を用意する)の書き出しも済ませる

バックアップのフォルダ（`$Dest`）を新サーバへ運ぶ。共有フォルダや外部媒体を使う場合も、運ぶ途中で管理者以外が読めないようにする。

### 手順 2: 新サーバに同じ版を入れる

**実行場所: 新サーバ。** MSI を実行する。インストーラで選ぶ項目（ファイアウォール規則の作成、サービスの実行アカウント、デスクトップのショートカット）は、旧サーバに合わせる（`firewall-rules.txt`・`service-account.txt`）。gMSA の指定方法は [gMSA ガイド](../guides/gmsa-service-account.md) §3。

インストールが終わるとサービスが起動し、空の状態で動き始める。送信元はまだ旧サーバを向いているので、この時点で受信するログは無い。

旧サーバで環境変数 `YAGURA_*` を使っていた場合（`environment.txt` に表示がある場合）は、この時点で新サーバにも旧サーバと同じ方法（システムの環境変数か、サービス単位の環境変数か）で同じ値を設定する。反映されるのは次のサービスの起動からである（システムの環境変数は、OS を再起動するまでサービスに反映されないことがある）。`YAGURA_DATAROOT` を使っていた場合は、手順 3 の `$Root` もその値にし、そのフォルダのアクセス権を自分で付ける（本書冒頭の約束事を参照）。

### 手順 3: 新サーバにバックアップを書き戻す

**実行場所: 新サーバ・管理者 PowerShell。**

```powershell
$Root   = "$env:ProgramData\Yagura"
$Backup = 'E:\YaguraBackup\20261116-090000\data'           # 運んできたバックアップの data フォルダ
$Aside  = "E:\YaguraBackup\new-server-initial-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

# 1. サービスを止める
Stop-Service Yagura
Get-Process Yagura.Host -ErrorAction SilentlyContinue      # 何も表示されないこと

# 2. インストール直後の中身を控えとして残す（インストール直後の監査記録を含む）
New-Item -ItemType Directory -Path $Aside -Force | Out-Null
icacls $Aside /inheritance:r /grant "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)"
icacls $Aside                                               # SYSTEM と Administrators の 2 つだけが表示されること
robocopy $Root $Aside /E /R:1 /W:1

# 3. インストール直後の中身を消す（forwarder フォルダと、このサーバのインストーラの記録は残す）
Get-ChildItem $Root -Force |
  Where-Object { $_.Name -ne 'forwarder' -and
                 $_.Name -notlike 'firewall-rules.ini*' -and
                 $_.Name -notlike 'service-account.ini*' } |
  Remove-Item -Recurse -Force

# 4. バックアップを書き戻す（旧サーバのインストーラの記録は戻さない）
robocopy $Backup $Root /E /R:1 /W:1 /XF firewall-rules.ini* service-account.ini*
$LASTEXITCODE                                               # 8 未満なら成功
```

**SQL Server を保存先にしている場合**は、上の 4. の代わりに次を実行し、スプール（`spool`）と `observability-state.json` はまだ戻さない。この 2 つは、[手順 5](#手順-5-起動して暗号化された設定値を入れ直す)で SQL Server に接続できたことを確かめてから戻す（理由は手順 5 に書く）。バックアップのフォルダにある写しは、そのまま残しておく:

```powershell
# 4.（SQL Server の場合）スプールと observability-state.json を除いて書き戻す
robocopy $Backup $Root /E /R:1 /W:1 /XF firewall-rules.ini* service-account.ini* observability-state.json /XD "$Backup\spool"
$LASTEXITCODE                                               # 8 未満なら成功
```

続けて[アクセス権を確かめる](#アクセス権を確かめる)。まだ起動しない（手順 4 の証明書を先に用意する）。

**スプールの置き場所（`Spool:Directory`）を変えている場合**は、新サーバにも同じパスのフォルダを作り、データルートと同じアクセス権を付けてから、スプールの写しを書き戻す（インストーラはこのフォルダにアクセス権を付けない）。SQL Server を保存先にしている場合は、フォルダを作ってアクセス権を付けるところまでを行い、最後の `robocopy`（書き戻し）は手順 5 で行う:

```powershell
$Spool = 'E:\YaguraSpool'                                   # yagura.json の Spool:Directory の値
New-Item -ItemType Directory -Path $Spool -Force | Out-Null
icacls $Spool /inheritance:r /grant "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "NT SERVICE\Yagura:(OI)(CI)(M)"
icacls $Spool                                               # 上の 3 つだけが表示されること
robocopy 'E:\YaguraBackup\20261116-090000\spool-custom' $Spool /E /R:1 /W:1
```

（gMSA で動かしている場合は `NT SERVICE\Yagura` を gMSA の名前に読み替える。）

### 手順 4: 証明書を用意する

TLS 受信・閲覧 UI の HTTPS・管理画面のリモートアクセスのいずれかを使っている場合に行う。方法は 2 つある。

| 方法 | 拇印 | 向いている場合 |
|---|---|---|
| 旧サーバの証明書を秘密鍵ごと移す | 変わらない（同じ証明書のため） | サーバ名を引き継ぐ場合。秘密鍵を書き出せない設定の証明書は移せない |
| 新サーバで証明書を用意し直す | **変わる** | サーバ名が変わる場合。閲覧 UI の HTTPS と管理画面のリモートアクセスは、証明書のホスト名（SAN）にアクセスに使う名前が入っている必要がある |

**秘密鍵ごと移す場合**（旧サーバ・管理者 PowerShell で書き出し、新サーバ・管理者 PowerShell で取り込む）:

```powershell
# 旧サーバ: 拇印は certificate-thumbprints.txt を見る。書き出し先はバックアップのフォルダ
$Pwd = Read-Host -AsSecureString 'PFX ファイルに付けるパスワード'
Export-PfxCertificate -Cert 'Cert:\LocalMachine\My\<拇印>' `
  -FilePath 'D:\YaguraBackup\20261116-090000\<用途>.pfx' -Password $Pwd

# 新サーバ
$Pwd = Read-Host -AsSecureString 'PFX ファイルのパスワード'
Import-PfxCertificate -FilePath 'E:\YaguraBackup\20261116-090000\<用途>.pfx' `
  -CertStoreLocation Cert:\LocalMachine\My -Password $Pwd
```

**用意し直す場合**は、閲覧 UI の HTTPS なら[閲覧 UI の HTTPS（8514）を有効化する](viewer-https.md)の手順 1・2 で作成・配布する（ルート証明書を配布している場合は、新旧両方が信頼された状態にしてから切り替え、旧証明書は後で撤去する）。手順 5 で起動した後、管理画面の「閲覧 UI の HTTPS 設定」「TLS 受信の証明書設定」「管理 UI のリモートアクセス（HTTPS）」で新しい証明書を選び直し、サービスを再起動する。

- どちらの方法でも、サービスの実行アカウントに秘密鍵の読み取り権限が要る。Yagura は起動時に自動付与を試みる。付与されたかは[閲覧 UI の HTTPS（8514）を有効化する](viewer-https.md)の手順 3 の方法で確かめる
- 設定にある拇印の証明書が新サーバに無い間は、閲覧 UI の HTTPS を有効にしていれば閲覧のリスナを開かず（イベント ID 1035）、TLS 受信は開かず（1016）、管理画面のリモートアクセスは使えない（1013）。syslog の UDP / TCP 受信は影響を受けない

### 手順 5: 起動して、暗号化された設定値を入れ直す

**実行場所: 新サーバ・管理者 PowerShell と、新サーバのブラウザ。**

```powershell
Start-Service Yagura
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Yagura'; StartTime = (Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue |
  Where-Object Message -match 'ConnectionString|Smtp:Password' |
  Format-List TimeCreated, Id, Message
```

旧サーバで暗号化した値があれば、ここで警告が表示される（何も表示されなければ、入れ直す値は無い）。管理画面（`http://localhost:8515/admin`）で入れ直す。

- **`Storage:SqlServer:ConnectionString` の警告**（組み込み DB へ切り替えた旨）: 「保存先を SQL Server に切り替える」（`/admin/promotion`）で、サーバ名・データベース名・認証方式を入力し、接続を検証して実行する。続けて、下の[SQL Server を保存先にしている場合の続き](#sql-server-を保存先にしている場合の続き)の手順で再起動し、SQL Server に接続したことを確かめる
  - **SQL Server が別のサーバにあり、既定の仮想サービスアカウントで動かしている場合**、画面の「接続に使うアカウント」と、検証に失敗したときに表示される SQL には `NT SERVICE\Yagura` と出る。しかし SQL Server に作るログインは、新サーバのコンピューターアカウント `DOMAIN\新サーバ名$`（[準備](#準備)の表）である。ネットワーク越しの接続はコンピューターアカウントとして届くため、別のサーバの SQL Server に `NT SERVICE\Yagura` のログインを作っても使われない。データベースの管理者に SQL を渡すときは、名前を `DOMAIN\新サーバ名$` に置き換える（gMSA の場合と、SQL Server が新サーバに同居している場合は、表示どおりの名前でよい）
  - Windows 統合認証で、新サーバのアカウントのログインがまだ無い場合は検証が失敗し、ログイン作成などの SQL が表示される。データベースの管理者に渡すときは、**データベースを復元した（または既存のデータベースに別のサーバから接続する）ことも伝える**——表示される SQL は新規作成を前提にしているため、既にあるユーザーとの関係は管理者に判断してもらう
  - 実行の前の確認で「これまでに保存したログは（中略）画面から参照できなくなります」と表示される。これは組み込み DB に保存したログについての予告であり、SQL Server のデータベースにあるログは、切り替えた後も見える
  - ウィザードは旧・組み込みデータベースの扱い（退避 / 削除）の選択も求める。データルートに残っている組み込み DB を残したい場合は「退避」を選ぶ（選択の意味は[昇格後に旧データベースファイルを処分する](promotion-old-db.md)）
- **`Notification:Email:Smtp:Password` の警告**: 「メール通知」（`/admin/email-notification`）でパスワードを入れ直して保存し、テスト送信で確かめる

**SQL Server で運用している場合、接続情報を入れ直すまでの間（組み込み DB で動いている間）は、次の点に注意する。**

- **SQL Server に保存しているアプリ独自の管理者アカウントではサインインできない**（このとき Yagura が読む管理者アカウントの台帳は組み込み DB の側になる）。Windows 統合認証（新サーバのローカル Administrators）でサインインする。`Admin:Authentication:RequireForLoopback` を有効にしてアプリ独自認証だけで運用している場合は、[管理画面に入れなくなる構成](degraded-operation.md#管理画面に入れなくなる構成)と同じ手当てで入り、入れ直しが済んだら設定を戻す
- 逆に、**本番昇格の前に組み込み DB で作ったアプリ独自のアカウントが組み込み DB に残っていれば、その古い ID とパスワードでサインインできる**（昇格の後に変えたり削除したりした分は反映されていない）。入れ直しが済むまでは、管理画面のリモートアクセスを外から使える状態にしない（手順 6 でその規則を足すのは、入れ直しの後にする）
- 起動時の保持期間の削除も組み込み DB に効く。本番昇格の前のログが組み込み DB に残っていれば、そのうち保持期間を過ぎた分はここで消える（バックアップの `yagura.db*` には残っている）

#### SQL Server を保存先にしている場合の続き

手順 3 で戻さなかったスプールと `observability-state.json` を、SQL Server に接続できたことを確かめてから戻す。

1. `ConnectionString` の警告が出ていた場合は、入れ直しの後に次で再起動し、再起動の後に同じ警告が出ていないことを確かめる:

   ```powershell
   $t = Get-Date
   Restart-Service Yagura
   Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Yagura'; StartTime = $t } -ErrorAction SilentlyContinue |
     Where-Object Message -match 'ConnectionString' |
     Format-List TimeCreated, Id, Message                    # 何も表示されないこと
   ```

2. 閲覧画面で、**本番昇格より後に保存されたログ**（たとえば旧サーバを止めた日のもの）が検索できることを確かめる。組み込み DB で動いている間は、このログは見えない（本番昇格より前のログは組み込み DB にもあるので、それでは判断しない）
3. サービスを止め、スプールと `observability-state.json` を戻してから起動する:

   ```powershell
   $Root   = "$env:ProgramData\Yagura"
   $Backup = 'E:\YaguraBackup\20261116-090000\data'          # 手順 3 と同じバックアップの data フォルダ
   Stop-Service Yagura
   Get-Process Yagura.Host -ErrorAction SilentlyContinue      # 何も表示されないこと
   robocopy "$Backup\spool" "$Root\spool" /E /R:1 /W:1
   $LASTEXITCODE                                              # 8 未満なら成功
   Copy-Item "$Backup\observability-state.json" $Root -Force
   Start-Service Yagura
   ```

   スプールの置き場所（`Spool:Directory`）を変えている場合は、`Start-Service` の前に、手順 3 で作ったフォルダへ `robocopy 'E:\YaguraBackup\20261116-090000\spool-custom' <そのフォルダ> /E /R:1 /W:1` で書き戻す。

起動すると、スプールの中身（旧サーバの停止時に退避された未保存のログなど）が SQL Server へ書き込まれる。状態画面（`/status`）では、カウンタが旧サーバの値から続き、旧サーバを止めてからこの起動までが「受信できなかった時間帯の履歴」に記録される（1. で再起動した場合は、その区間も別に記録されるため、履歴では重なって見える）。

**なぜ後から戻すのか**: 旧サーバで暗号化した接続文字列は、新サーバの最初の起動では復号できず、組み込み DB が保存先になる。このときスプールがあれば、その中身は組み込み DB へ書き込まれる。`observability-state.json` があれば、移設で止めていた区間の記録も組み込み DB に書かれる。どちらも、後で接続情報を入れ直しても SQL Server には移らない（旧サーバで蓄積ログの移行を済ませている場合は、移行の対象にもならない）。

### 手順 6: ファイアウォール規則を合わせる

**実行場所: 新サーバ・管理者 PowerShell。** MSI が作る規則は **UDP 514・TCP 514・TCP 8514 の 3 本だけ**（インストーラで規則の作成を外さなかった場合）。旧サーバで受信・閲覧のポートを変えていた場合や、TLS 受信（6514）・管理画面のリモートアクセス（8516）を使っていた場合は、規則を自分で足す（[ファイアウォール規則を自分で足す](firewall.md)。旧サーバの規則は `firewall-rules.txt`）。

起動時の突合で不一致があれば、イベント ID 1023 が出る:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Yagura'; Id = 1023; StartTime = (Get-Date).AddMinutes(-30) } -ErrorAction SilentlyContinue |
  Format-List TimeCreated, Message
```

規則を直した後の突合は次回のサービス起動時に行われる。ファイアウォールを GPO などで集中管理している場合は、集中管理の側で新サーバに規則を配る。

### 手順 7: 確かめてから、送信元を新サーバへ向ける

[移設・復旧のあとで確かめること](#移設復旧のあとで確かめること)を確認する。別の端末から試験用のログを 1 件送り、届くことも確かめる（ファイアウォール規則の確認を兼ねる）。

```powershell
# 実行場所: 新サーバ以外の端末（一般ユーザーでよい）
$udp = New-Object System.Net.Sockets.UdpClient
$b = [Text.Encoding]::ASCII.GetBytes('<134>YAGURA-MOVE-CHECK')
[void]$udp.Send($b, $b.Length, '<新サーバの名前またはアドレス>', 514)
$udp.Close()
```

閲覧画面（`http://<新サーバの名前>:8514/`）の検索で `YAGURA-MOVE-CHECK` が見つかればよい。その後、送信元の送信先設定を新サーバへ変える（または旧サーバのアドレス・名前を新サーバへ付け替える）。閲覧 URL が変わる場合は、利用者に新しい URL を周知する。

### 手順 8: 旧サーバを片付ける

- **しばらくは旧サーバを止めたまま残す。** 移設を取りやめる場合は、新サーバのサービスを止めてから、旧サーバで `Set-Service Yagura -StartupType Automatic` と `Start-Service Yagura` を実行し、送信元を旧サーバへ戻す（組み込み DB で運用していた場合、新サーバで受けたログは新サーバにだけ残る）
- 旧サーバのイベントログは新サーバへ移らない。必要なら廃棄の前に保存する: `wevtutil epl Application D:\YaguraBackup\old-server-yagura-events.evtx "/q:*[System[Provider[@Name='Yagura']]]"`（旧サーバ・管理者 PowerShell）
- 旧サーバから Yagura をアンインストールしても、**データルート（ログ・設定・監査記録）は消えない**。不要になったら組織の規程に従って消去する。Yagura はファイルを通常の方法で削除するだけで、ディスク上の痕跡を消す機能はない
- 自分で追加したファイアウォール規則はアンインストールでは消えない（[ファイアウォール規則を自分で足す](firewall.md#ポートを変えたとき無効に戻したときアンインストールしたとき)）
- 証明書を用意し直した場合は、旧証明書と、配布したルート証明書を撤去する（[撤去・棚卸し](viewer-https.md#撤去棚卸し配布の対)）
- SQL Server の旧サーバ用のログイン（`DOMAIN\旧サーバ名$` など）は、データベースの管理者と相談して削除する。gMSA の取得許可から旧サーバを外す。gMSA でリモート管理の Kerberos を使っていた場合は、新サーバの名前で SPN を登録し直す（[gMSA ガイド](../guides/gmsa-service-account.md) §8）

## SQL Server を別のサーバへ替える

Yagura サーバはそのままで、保存先の SQL Server だけを別のサーバ（またはインスタンス）へ替える手順。

- **Yagura には、保存先のデータを SQL Server 間で移す機能はない。** SQL Server の標準のバックアップと復元で移し、Yagura の接続先は「保存先を SQL Server に切り替える」で替える（設定ファイルの接続文字列は暗号化されているため、手で書き換えない）
- **バックアップを取った後に、Yagura が旧データベースへ書き込まないようにする。** 書き込まれた分は新しいデータベースに入らない。下の手順では旧データベースをオフラインにする。保存先に接続できない間、Yagura は受信を続けてスプールへ退避し、接続先を替えた後にその分を新しいデータベースへ書き込む（[データベースに接続できないとき（縮退運転）](degraded-operation.md)）
- 替える先の SQL Server は対応する版にする（[README のシステム要件](../../README.md#システム要件)。SQL Server 2025 は検証を終えておらず、対応を表明していない）
- 先に[バックアップを取る](#バックアップを取る)の手順でデータルートのバックアップも取っておく

1. **Yagura を止める**（実行場所: Yagura サーバ・管理者 PowerShell）: `Stop-Service Yagura`。ここから受信が止まる
2. **旧データベースをバックアップし、オフラインにする**（実行場所: 旧 SQL Server に接続した SSMS または sqlcmd。SQL Server の管理者）:

   ```sql
   BACKUP DATABASE [<データベース名>]
     TO DISK = N'<バックアップファイルのパス>'
     WITH COPY_ONLY, CHECKSUM;
   ALTER DATABASE [<データベース名>] SET OFFLINE WITH ROLLBACK IMMEDIATE;
   ```

3. **Yagura を起動する**（Yagura サーバ・管理者 PowerShell）: `Start-Service Yagura`。受信が再開する。旧データベースに接続できないため、受信したログはスプールへ退避される。イベントログに 1030 または 1031（どちらもエラー）や 1039 の警告が出て、メール通知を使っていれば通知も届くが、これは想定どおりである（通知の受け手には事前に知らせておく）。**この間、アプリ独自の管理者アカウントではサインインできない**（[管理画面に入れなくなる構成](degraded-operation.md#管理画面に入れなくなる構成)も参照）
4. **新しい SQL Server に復元する**（SQL Server の管理者）: SQL Server の標準の手順（SSMS の「データベースの復元」など）で復元する。続けて、Yagura の実行アカウントのログインを作り、復元したデータベースへのアクセス権（本番昇格のときと同じ `db_owner`）を与える。ログインの名前は[移設の準備](#準備)の表と同じ考え方で決まる（SQL Server が Yagura とは別のサーバにあれば、既定の仮想サービスアカウントでは Yagura サーバのコンピューターアカウント `DOMAIN\Yagura サーバ名$`、gMSA ではその名前、SQL Server 認証ではそのログイン）。SQL Server 認証のログインを使う場合、復元したデータベースのユーザーと新しいサーバのログインの対応が外れることがある（SQL Server の「孤立ユーザー」）。SQL Server の標準の手順で対応付け直す
5. **接続先を替える**（Yagura サーバのブラウザ）: 管理画面の「保存先を SQL Server に切り替える」（`/admin/promotion`）で新しいサーバ名・データベース名・認証方式を入力し、接続を検証して実行する（旧・組み込みデータベースの扱いの選択は[手順 5 の注意](#手順-5-起動して暗号化された設定値を入れ直す)と同じ）。続けて `Restart-Service Yagura` で再起動する
   - 実行の前の確認で「これまでに保存したログは（中略）画面から参照できなくなります」と表示されるが、これは組み込み DB に保存したログについての予告であり、この交換には当てはまらない。復元したデータベースのログは、切り替えた後もそのまま見える
   - SQL Server が Yagura とは別のサーバにあり、既定の仮想サービスアカウントで動かしている場合、画面の「接続に使うアカウント」には `NT SERVICE\Yagura` と出るが、SQL Server に要るログインは手順 4 のとおり `DOMAIN\Yagura サーバ名$` である（[移設の手順 5 の注意](#手順-5-起動して暗号化された設定値を入れ直す)と同じ）。検証に失敗して SQL が表示された場合は、その中の名前を `DOMAIN\Yagura サーバ名$` に置き換えてから使う
6. **確かめる**: 状態画面（`/status`）で一時保管（スプール）の使用量が 0 へ向かって減ること、閲覧画面で切り替え前のログと切り替え中に受けたログの両方が見えること、再起動後にイベント ID 1030・1031・1039 が出ていないことを確かめる（[移設・復旧のあとで確かめること](#移設復旧のあとで確かめること)）。ただし、**手順 1〜3 で止めていた区間は「受信できなかった時間帯の履歴」に残らない**。手順 3 の起動時に記録しようとする先が、オフラインにした旧データベースだからである（書けなかった記録はメモリ上に預けられるだけで、手順 5 の再起動で失われる）。この区間は作業の記録として別に残しておく
7. **旧データベース**は、しばらくオフラインのまま残し、問題がなければデータベースの管理者の手順で削除する

- **受信が止まるのは、手順 1〜3（旧データベースのバックアップの所要時間）と、手順 5 の再起動の間**である
- **手順 3〜5 の間はスプールに溜まり続ける。** 無損失でいられる時間の上限はスプールの容量である。復元に時間がかかる場合は、状態画面でスプールの使用量を見ながら進める（[「再起動できるようになった」は「直った」ではない](degraded-operation.md#再起動できるようになったは直ったではない)）

## 移設・復旧のあとで確かめること

**実行場所: Yagura サーバ（PowerShell は管理者）と、閲覧用の端末のブラウザ。**

1. サービスが動いている: `Get-Service Yagura` の Status が Running
2. 起動後に警告・エラーが出ていない:

   ```powershell
   Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Yagura'; Level = 2, 3; StartTime = (Get-Date).AddHours(-1) } -ErrorAction SilentlyContinue |
     Format-Table TimeCreated, Id, LevelDisplayName -AutoSize
   ```

   特に見るもの: 暗号化された設定値の警告（`Storage:SqlServer:ConnectionString`・`Notification:Email:Smtp:Password`）、1023（ファイアウォール規則の不一致）、1013・1016・1035（証明書）、1030・1031・1039（保存先）、1001（スプールなしでの起動）。`Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Yagura'; Id = <ID> } -MaxEvents 1 | Format-List Message` で本文を読める
3. 状態画面（`/status`）: カウンタが戻したデータの値から続いている。一時保管（スプール）の使用量が 0 へ向かって減る。止めていた区間が「受信できなかった時間帯の履歴」に記録されている（[SQL Server を別のサーバへ替える](#sql-server-を別のサーバへ替える)場合は、その手順 1〜3 の区間は記録されない）
4. 閲覧画面: 移設・復旧より前のログが検索できる。新しいログ（試験用のログ）が届く
5. 管理画面: 保持期間などの設定が旧サーバと同じ。アプリ独自の管理者アカウントを使っている場合はサインインできる。メール通知を使っている場合はテスト送信が届く
6. 閲覧 UI の HTTPS を使っている場合は、閲覧端末から `https://<サーバ名>:8514/` が警告なしで開ける
