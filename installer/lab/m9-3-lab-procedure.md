# M9-3 lab 検証手順書(YAGURA-STG——MSI 実機・M-15・DB-8・アップグレード挙動)

Issue #76 の lab 検証をオーナーが実機(YAGURA-STG = 10.0.0.156)で実行するための手順書。
各項目は「コマンド・期待結果・採取してほしい出力」の 3 点セットで構成し、結果記入欄に
出力を貼り付けて PR コメントまたは本ファイルの追記コミットで返却する。

## 0. 前提と範囲

- **CI(windows-latest)で実証済みの項目は lab で繰り返さない**。MSI サイレントインストール →
  サービス起動 → UDP 受信 → 閲覧照合 → アンインストール → 残置物確認は CI で全ステップ
  Passed 済み(PR #84。証拠 run: https://github.com/Yanai-Taketo/Yagura/actions/runs/28749860767)。
  lab の範囲は **CI で原理的に検証できない実機依存項目**に絞る:
  ACL 実適用(SEC-3)・失敗時再起動・アップグレード・M-15・DB-8・イベントログ実書き込み・
  ja-JP インストーラ UI・E2E スクリプトの lab 環境での Full 再現
- 実行はすべて**管理者 PowerShell**(Windows PowerShell 5.1 を想定)
- 検証項目の反映先: architecture.md §9 M-15 / database.md §8 DB-8 / security.md §7 SEC-3

### 実行順序(依存関係)と所要時間の目安

| # | 手順 | 検証内容 | 目安 |
|---|---|---|---|
| A | E2E スクリプト Full 実行 | E2E の lab 再現(自動化部分を先に消化) | 10 分 |
| B | 対話インストール | MSI 実機インストール + ja-JP ダイアログ採取 | 15 分 |
| C | ACL 実適用 | SEC-3(icacls) | 5 分 |
| D | 失敗時再起動ポリシー | sc qfailure + プロセス kill 復帰 | 10 分 |
| E | M-15 | 仮想サービスアカウントでの IPGlobalProperties + OS 統計ノイズ | 20〜30 分 |
| F | イベントログ | ソース Yagura の実書き込み(ID 3001) | 5 分 |
| G | UI 動線 | 完了画面ブラウザ起動・スタートメニュー .url | 5 分 |
| H | アップグレード | 0.1.0 → 0.1.1 の MajorUpgrade・受信断計測 | 20〜30 分 |
| I | DB-8 | 小容量 VHD でのディスク満杯 → 回復 | 40〜60 分 |
| J | アンインストール・後片付け | 残置物確認・環境復元 | 15 分 |

lab 上の合計: **約 2.5〜3 時間**(別途、開発機での事前準備 30〜40 分)。

順序の理由: A(E2E)は「サービス Yagura が存在しない」ことを事前条件としてインストール〜
アンインストールを自己完結で行うため**最初**に置く。B のインストールを対話モードで行うことで
ja-JP ダイアログ(G の一部)を同時に採取する(ダイアログはインストール時にしか観測できない)。
H(アップグレード)は C〜G の検証を 0.1.0 環境で終えてから行う。I(DB-8)はデータルートを
一時的に VHD へ向ける破壊的試験のため最後に置き、J で環境を復元する。

## 1. 事前準備(開発機で実施)

### 1.1 MSI 2 バージョンのビルド

```powershell
# (1) v0.1.0(現状のまま)
dotnet build installer\Yagura.Installer.wixproj -c Release
Copy-Item installer\bin\Release\ja-JP\Yagura.msi C:\work\Yagura-0.1.0.msi

# (2) v0.1.1(アップグレード検証用): installer\Package.wxs の
#     <Package ... Version="0.1.0" を Version="0.1.1" に書き換えて再ビルド
dotnet build installer\Yagura.Installer.wixproj -c Release
Copy-Item installer\bin\Release\ja-JP\Yagura.msi C:\work\Yagura-0.1.1.msi

# 書き換えは検証後に元へ戻す(コミットしない)
git -C . checkout -- installer/Package.wxs
```

- 期待結果: 両ビルドとも警告 0 で完了し、MSI が 2 つ得られる
- 注意: MSI はビルド環境ごとに非再現のため、サイズ・SHA256 は記録しない(conventions.md)

### 1.2 lab へ持ち込むもの(C:\YaguraLab\ に配置)

| ファイル | 入手元 | 用途 |
|---|---|---|
| `Yagura-0.1.0.msi` / `Yagura-0.1.1.msi` | 1.1 のビルド | B・H |
| `tools\Invoke-YaguraInstallerE2E.ps1` | リポジトリ installer\e2e\ | A |
| `tools\sqlite3.exe` | sqlite.org の sqlite-tools-win-x64 zip(単体実行可) | H・I |
| `tools\dotnet-counters.exe` | learn.microsoft.com「dotnet-counters」ページの単体ダウンロードリンク(win-x64。SDK 不要) | E |
| `tools\PsExec64.exe`(任意) | Sysinternals PsTools | E の代替経路 |

lab に .NET SDK は不要(MSI は self-contained)。lab がインターネット非接続でも、上記を
開発機で取得して持ち込めば全手順が完結する。

### 1.3 lab 側の初期状態記録

```powershell
New-Item -ItemType Directory -Force C:\YaguraLab\results | Out-Null
cmd /c ver
[System.Environment]::OSVersion.VersionString
Get-Service Yagura -ErrorAction SilentlyContinue   # 何も出ないこと(未インストール)
Get-NetUDPEndpoint | Sort-Object LocalPort | Format-Table LocalAddress,LocalPort,OwningProcess
Get-Process -Id (Get-NetUDPEndpoint | Select-Object -ExpandProperty OwningProcess -Unique) -ErrorAction SilentlyContinue | Format-Table Id,ProcessName
```

- 期待結果: サービス Yagura が存在しない。OS バージョンが表示される
- 採取してほしい出力: 上記すべて(**OS バージョンは M-15 の判定の分岐に使うため必須**。
  同居 UDP サービスの一覧も M-15 のノイズ観測の前提記録)

```text
(結果記入欄: OS バージョン・UDP 同居サービス一覧)


```

## A. E2E スクリプトの Full 実行(検証項目 8)

CI で実証済みのシナリオが lab の実機でも同じ結果になることの確認。自動化できる部分を
先に消化する。終了時にアンインストールまで自動で行われる。

- コマンド:

```powershell
powershell -ExecutionPolicy Bypass -File C:\YaguraLab\tools\Invoke-YaguraInstallerE2E.ps1 `
    -MsiPath C:\YaguraLab\Yagura-0.1.0.msi -OutputDir C:\YaguraLab\results\e2e
```

- 期待結果: `Overall: Passed`。全ステップ Passed(install-msi / wait-service-running /
  installed-state-evidence / send-and-verify / uninstall-msi / residue-*)
- 採取してほしい出力: `C:\YaguraLab\results\e2e\` の `*.summary.json` と `*.log.txt` の内容
  (msiexec ログはファイルのまま保管)

E2E はデータルート(`C:\ProgramData\Yagura`)を設計どおり残す。以降の手順を**新規作成の
ACL 適用経路**で検証するため、次を実行してから B へ進む:

```powershell
Rename-Item C:\ProgramData\Yagura C:\ProgramData\Yagura.e2e-backup
```

```text
(結果記入欄: summary.json の貼り付け)


```

## B. 対話モードでの実機インストール(検証項目 7 の前半を兼ねる)

サイレントではなく**ダブルクリック(対話 UI)**でインストールし、ja-JP 表示を採取する。

- コマンド: エクスプローラーで `C:\YaguraLab\Yagura-0.1.0.msi` をダブルクリック
- 確認しながら進む(各画面のスクリーンショットを採取):
  1. Welcome → 使用許諾 → インストール先 → **ファイアウォール規則**(Yagura 固有画面)→
     インストールの確認 → 完了、の順で遷移し、**全画面が日本語**であること
  2. ファイアウォール画面: チェックボックス「Windows ファイアウォールに受信許可規則を作成する(推奨)」
     が既定 ON で、規則 3 本(UDP 514 / TCP 514 / TCP 8514)の一覧と 8515 を作らない旨の
     注記が表示されること
  3. 完了画面: 「受信したログは次の URL で閲覧できます: http://localhost:8514/ …」の案内文と
     チェックボックス「今すぐブラウザで閲覧画面を開く」が表示されること
  4. **チェック ON のまま完了** → 既定ブラウザで http://localhost:8514/ が開くこと(検証項目 7 の
     完了画面動線。開いた画面のスクリーンショットも採取)
- インストール後の基本状態:

```powershell
Get-Service Yagura | Format-List Name,Status,StartType
sc.exe qc Yagura
(Get-CimInstance Win32_Service -Filter "Name='Yagura'").StartName
```

- 期待結果: Status = Running / StartType = Automatic /
  `SERVICE_START_NAME : NT SERVICE\Yagura`(**仮想サービスアカウント指定が MSI の
  ServiceInstall 経由で成立することの実機確認**——Package.wxs の申し送り)
- 採取してほしい出力: スクリーンショット一式(1〜4)+ 上記 3 コマンドの出力

```text
(結果記入欄: sc qc 出力と UI 確認結果。スクリーンショットは PR 添付)


```

## C. ACL 実適用の確認(検証項目 1 = SEC-3)

- コマンド:

```powershell
sc.exe showsid Yagura
icacls C:\ProgramData\Yagura
(Get-Acl C:\ProgramData\Yagura).AreAccessRulesProtected
icacls C:\ProgramData\Yagura\audit      # 存在すれば(継承の確認用の参考採取)
icacls C:\ProgramData\Yagura\spool      # 同上
```

- 期待結果:
  - `sc.exe showsid` の SID = `S-1-5-80-2906685102-3215373564-3806320331-1391897514-1145735297`
    (Package.wxs の SDDL に埋め込んだ値と一致)
  - `icacls C:\ProgramData\Yagura` の ACE が**次の 3 つだけ**であること:
    - `NT AUTHORITY\SYSTEM:(OI)(CI)(F)`
    - `BUILTIN\Administrators:(OI)(CI)(F)`
    - `NT SERVICE\Yagura:(OI)(CI)(M)`
  - **`(I)`(継承)付き ACE が 1 つもない**こと・`AreAccessRulesProtected` = `True`
    (継承無効化の確認)
  - **`Users` / `Authenticated Users` / `Everyone` の ACE が存在しない**こと
  - 配下(audit・spool 等)には `(I)` 付きで同じ 3 主体が並ぶこと(上位からの継承のみ)
- 採取してほしい出力: 上記 5 コマンドの出力全文(**security.md §5 に「icacls 出力の形で記録する」
  と定めた SEC-3 の一次資料になるため、省略せずそのまま**)

```text
(結果記入欄)


```

## D. 失敗時再起動ポリシー(検証項目 2)

### D-1. 設定値の確認

- コマンド:

```powershell
sc.exe qfailure Yagura
```

- 期待結果: リセット期間 = `86400`(秒 = 1 日)、障害時の動作 = **再起動 × 3 回**、
  各行の遅延 = `5000` ミリ秒(Package.wxs の util:ServiceConfig: restart×3・5 秒・リセット 1 日)
- 採取してほしい出力: コマンド出力全文

### D-2. プロセス強制終了からの自動復帰

- コマンド:

```powershell
$oldPid = (Get-CimInstance Win32_Service -Filter "Name='Yagura'").ProcessId
"kill 前 PID: $oldPid  $(Get-Date -Format o)"
Stop-Process -Id $oldPid -Force
Start-Sleep -Seconds 15
$svc = Get-Service Yagura
$newPid = (Get-CimInstance Win32_Service -Filter "Name='Yagura'").ProcessId
"kill 後 状態: $($svc.Status)  新 PID: $newPid  $(Get-Date -Format o)"
Get-WinEvent -FilterHashtable @{LogName='System'; Id=7031} -MaxEvents 3 |
    Format-List TimeCreated,Message
```

- 期待結果: 15 秒後(ポリシー上は約 5 秒後に再起動)に Status = `Running`、PID が変わっている。
  System ログにサービス コントロール マネージャーのイベント 7031(予期しない終了 → 回復操作
  「サービスの再起動」)が記録されている
- 採取してほしい出力: 上記スクリプトの出力全文(旧 PID・新 PID・7031 の本文)

```text
(結果記入欄: D-1・D-2)


```

## E. M-15(検証項目 4)

architecture.md §4.2・§9 M-15: (a) 仮想サービスアカウント権限下で `IPGlobalProperties` が
例外なく動くこと、(b) OS 受信破棄ゲージの同居環境ノイズの実測。

**まず 1.3 で記録した OS バージョンで分岐する**: Windows 10.0.26200 系では UDP 統計 API 自体が
受信・破棄を一切計上しないことがクロスマシン実機検証で確定済み(architecture.md §4.2「覆域の
限界」・ADR-0002 改訂履歴)。lab が同版なら (b) は「観測不能の追認」を記録することが成果になる。

### E-1. 仮想サービスアカウント下での IPGlobalProperties 動作(直接確認)

製品は起動時(IngestionMetrics 構築時)に `IPGlobalProperties` の UDP 統計を読み、
成功した場合のみゲージ `yagura.os.udp.ipv4.datagrams_discarded` / 同 ipv6 を登録する
(失敗時は例外を握って登録しない = ゲージ不在が失敗の印)。dotnet-counters でサービス
プロセスの Meter `Yagura` を購読し、ゲージの存在で成功を直接確認する。

- コマンド:

```powershell
$yaguraPid = (Get-CimInstance Win32_Service -Filter "Name='Yagura'").ProcessId
C:\YaguraLab\tools\dotnet-counters.exe monitor --process-id $yaguraPid --counters Yagura
# 10 秒ほど表示を観察して Ctrl+C で終了
```

- サービスプロセス(NT SERVICE\Yagura 所有)への attach が拒否された場合の代替経路
  (診断パイプの ACL 都合で管理者でも接続できないことがある。**接続可否自体も記録対象**):

```powershell
C:\YaguraLab\tools\PsExec64.exe -s -accepteula C:\YaguraLab\tools\dotnet-counters.exe monitor --process-id $yaguraPid --counters Yagura
```

- 期待結果: 計器一覧に `yagura.os.udp.ipv4.datagrams_discarded` と
  `yagura.os.udp.ipv6.datagrams_discarded` が**表示される**(値は 0 で構わない)。
  表示される = コンストラクタでの統計読み取りが仮想サービスアカウント権限で成功しており、
  かつ購読中の観測コールバック(毎回 `IPGlobalProperties` を呼ぶ)も例外なく動いている
- ゲージが 2 本とも**表示されない**場合 = 仮想サービスアカウント権限で
  `NetworkInformationException` / `PlatformNotSupportedException` が発生している(M-15 不成立。
  例外種別の特定は開発側で追加調査するため、その旨を記入欄へ)
- 採取してほしい出力: dotnet-counters の表示(コンソールのコピー。どの経路——直接 / PsExec -s
  ——で attach できたかの記録を含む)

### E-2. 起動ログ・イベントログでの間接確認

- コマンド:

```powershell
Restart-Service Yagura
Start-Sleep -Seconds 10
Get-Service Yagura
Get-WinEvent -LogName Application -MaxEvents 50 |
    Where-Object { $_.ProviderName -eq 'Yagura' -or $_.Message -like '*Unable to log*' } |
    Format-List TimeCreated,Id,LevelDisplayName,Message
```

- 期待結果: 再起動後も Running。イベントログに起動失敗・例外系の Error が**ない**こと
  (捕捉対象外の例外が出ればサービスは起動に失敗するため、正常起動自体が
  「想定外例外なし」の傍証になる)
- 採取してほしい出力: コマンド出力全文(該当イベントが 0 件なら 0 件であることを記載)

### E-3. OS 統計ノイズの観測(または観測不能の追認)

外部送信元(開発機)から実 NIC 経由でトラフィックを与え、OS の UDP 統計が動くかを見る。

- コマンド(lab 側・送信前):

```powershell
netstat -s -p udp
```

- コマンド(**開発機**側・lab の 10.0.0.156 へ 1,000 通送出):

```powershell
$udp = New-Object System.Net.Sockets.UdpClient
1..1000 | ForEach-Object {
    $b = [Text.Encoding]::ASCII.GetBytes("<134>M15-NOISE-$_")
    [void]$udp.Send($b, $b.Length, '10.0.0.156', 514)
}
$udp.Close()
```

- コマンド(lab 側・送信後):

```powershell
netstat -s -p udp
# 閲覧画面で M15-NOISE が届いていることも確認(アプリ受信は成立している証拠)
(Invoke-WebRequest -Uri http://localhost:8514/ -UseBasicParsing).Content -match 'M15-NOISE' 
```

さらに E-1 の dotnet-counters を再度 attach し、ゲージ値を読む。

- 期待結果(OS = 10.0.26200 系の場合): 閲覧画面には M15-NOISE が届いている(アプリ受信成立)
  のに、netstat の UDP 受信カウンタ増分が送出数に対して不釣り合いに小さい/増えない・
  製品ゲージも 0 のまま → **「この OS 版では統計 API が計上しない」ことの lab 環境での追認**を
  成果として記録する(同居ノイズの実測は本 OS 版では原理的に不可能、が M-15 の確定内容になる)
- 期待結果(統計が動く OS 版だった場合): 送信前後の netstat 差分と製品ゲージの動きを記録した
  うえで、**Yagura を無負荷にした状態で 10 分放置**し、その間のゲージ増分(= 同居 UDP サービス
  由来のノイズ)を採取する(開始時・終了時の dotnet-counters 表示と netstat -s -p udp を各 1 回)
- 採取してほしい出力: 送信前後の `netstat -s -p udp` 全文・ゲージ読み値・(該当時)10 分放置の
  前後値。1.3 で採取済みの同居 UDP サービス一覧と合わせて M-15 の確定記録とする

```text
(結果記入欄: E-1〜E-3)


```

## F. イベントログの実書き込み(検証項目 6)

ソース `Yagura`(MSI の util:EventSource が事前登録)での実書き込みを、監査イベント
ID 3001(閲覧リスナへの管理系要求の拒否 = security.md §4.3)で成立させる。

- コマンド:

```powershell
# 閲覧リスナ(8514)へ管理系パス /admin を要求 → 404 で拒否され、監査記録される
try { Invoke-WebRequest -Uri http://localhost:8514/admin -UseBasicParsing } catch { $_.Exception.Response.StatusCode.value__ }
Start-Sleep -Seconds 3
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Yagura'; Id=3001} -MaxEvents 5 |
    Format-List TimeCreated,Id,LevelDisplayName,Message
Get-Content C:\ProgramData\Yagura\audit\audit.jsonl -Tail 3
```

- 期待結果:
  - HTTP 応答 = `404`
  - Application ログに **ソース `Yagura`・イベント ID `3001`・警告** のエントリが記録され、
    本文に `[audit] ViewerListenerAdminRequestRejected: …` と接続元・試行パスが含まれる
    (ソース登録が正しく効いていれば「ソース 'Yagura' からのイベント ID の説明が見つかりません」
    という縮退文言が**混ざらない**——メッセージファイル登録の検証を兼ねる)
  - データルートの `audit\audit.jsonl` 末尾に同じ事象の JSON 行がある(併記の確認)
- 採取してほしい出力: 上記 3 コマンドの出力全文

```text
(結果記入欄)


```

## G. UI 動線の残り(検証項目 7 の後半)

完了画面のブラウザ起動は B-4 で採取済み。残りはスタートメニュー。

- コマンド / 操作:

```powershell
Get-ChildItem "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Yagura"
Get-Content "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Yagura\Yagura ログ閲覧.url"
```

スタートメニューから「Yagura ログ閲覧」を実際にクリックする。

- 期待結果: `.url` ファイルが存在し、`URL=http://localhost:8514/` を含む。クリックで既定
  ブラウザに閲覧画面が開く
- 採取してほしい出力: 上記 2 コマンドの出力 + クリック結果(開いた/開かない)

```text
(結果記入欄)


```

## H. アップグレード挙動(検証項目 3)

v0.1.0 → v0.1.1 の MajorUpgrade で、(1) yagura.json・SQLite DB・スプールが残ること、
(2) アップグレード中の受信断区間の長さ、(3) 旧ファイアウォール規則が残存せず新規則に
置き換わること、を確認する。

### H-1. 事前状態の記録

```powershell
$sqlite3 = 'C:\YaguraLab\tools\sqlite3.exe'
Get-ChildItem C:\ProgramData\Yagura -Recurse | Select-Object FullName,Length,LastWriteTime
Get-FileHash C:\ProgramData\Yagura\yagura.json -Algorithm SHA256
& $sqlite3 -readonly C:\ProgramData\Yagura\yagura.db "SELECT COUNT(*) FROM LogRecords;"
Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\* |
    Where-Object DisplayName -eq 'Yagura' | Format-List DisplayName,DisplayVersion
Get-NetFirewallRule -DisplayName 'Yagura*' | Select-Object Name,DisplayName,Profile,Enabled,Direction,Action
```

- 期待結果: DisplayVersion = `0.1.0`。件数・ハッシュが取れる。ファイアウォール規則は
  旧版が作成したもの(Issue #125 修正前のビルドなら 3 規則・Profile=Any、修正後の
  ビルドなら 6 規則・Domain/Private)が一覧できる
- 採取してほしい出力: 上記全部(アップグレード後との比較対象。**規則一覧は Name 列 =
  規則 Id も含めて採取する**——アップグレード後に旧 Id の規則が消えたことの照合に使う)

### H-2. 連続送信を流しながらアップグレード

**別ウィンドウ 1**(管理者不要)で連番送信を開始する(100 ms 間隔・停止ファイルで終了):

```powershell
Remove-Item C:\YaguraLab\stop-sender.txt -ErrorAction SilentlyContinue
$udp = New-Object System.Net.Sockets.UdpClient
$i = 0
while (-not (Test-Path C:\YaguraLab\stop-sender.txt)) {
    $i++
    $b = [Text.Encoding]::ASCII.GetBytes(("<134>UPG-SEQ-{0:D6}" -f $i))
    [void]$udp.Send($b, $b.Length, '127.0.0.1', 514)
    Start-Sleep -Milliseconds 100
}
$udp.Close()
"total sent: $i"
```

**元のウィンドウ**でアップグレードを実行し、前後時刻を記録する:

```powershell
"upgrade start: $(Get-Date -Format o)"
Start-Process msiexec.exe -Wait -ArgumentList '/i C:\YaguraLab\Yagura-0.1.1.msi /qn /norestart /l*v C:\YaguraLab\results\msiexec-upgrade.log'
"upgrade end:   $(Get-Date -Format o)"
Get-Service Yagura
```

サービスが Running へ戻って 30 秒ほど待ってから送信を止める:

```powershell
New-Item C:\YaguraLab\stop-sender.txt -ItemType File | Out-Null
```

### H-3. 事後確認と受信断の算出

```powershell
Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\* |
    Where-Object DisplayName -eq 'Yagura' | Format-List DisplayName,DisplayVersion
Get-FileHash C:\ProgramData\Yagura\yagura.json -Algorithm SHA256
Get-ChildItem C:\ProgramData\Yagura -Recurse | Select-Object FullName,Length,LastWriteTime
& $sqlite3 -readonly C:\ProgramData\Yagura\yagura.db "WITH s AS (SELECT CAST(substr(Message,9,6) AS INTEGER) n FROM LogRecords WHERE Message LIKE 'UPG-SEQ-%') SELECT MIN(n) AS first, MAX(n) AS last, COUNT(*) AS received, (MAX(n)-MIN(n)+1)-COUNT(*) AS missing FROM s;"
& $sqlite3 -readonly C:\ProgramData\Yagura\yagura.db "WITH s AS (SELECT CAST(substr(Message,9,6) AS INTEGER) n FROM LogRecords WHERE Message LIKE 'UPG-SEQ-%') SELECT n+1 AS gap_start FROM s WHERE n+1 NOT IN (SELECT n FROM s) AND n < (SELECT MAX(n) FROM s) LIMIT 20;"
icacls C:\ProgramData\Yagura    # 参考: アップグレード後も C と同じ ACL のままか
Get-NetFirewallRule -DisplayName 'Yagura*' | Select-Object Name,DisplayName,Profile,Enabled,Direction,Action
```

- 期待結果:
  - DisplayVersion = `0.1.1`・サービス Running・製品エントリは 1 件のみ(旧版が残らない)
  - **ファイアウォール規則が新版の規則のみに置き換わる(Issue #125)**: H-1 で採取した
    旧 Id の規則が 1 件も残っておらず、新ビルドの 6 規則(3 系統 × Domain/Private)のみが
    存在し、**Profile 列に Any / Public を含む規則が 1 件もない**こと。MajorUpgrade の
    既定 Schedule(afterInstallValidate)は旧製品を全削除してから新製品をインストール
    するため設計上は旧規則(Profile=Any)も除去されるはずだが、これはここで初めて
    実証される——旧規則が残存した場合は Public 開放が固定化する回帰であり、必ず記録して
    Issue 化する
  - **yagura.json の SHA256 が H-1 と一致**(設定が保持される)・**yagura.db が同一ファイルの
    まま残り、UPG-SEQ 以前の既存レコード件数を含む**・スプールディレクトリが残る
  - `missing` = アップグレード中に失われた連番数。**受信断 ≒ missing × 0.1 秒**。
    MajorUpgrade は既定 Schedule(afterInstallValidate = 旧製品を全削除してから新製品を
    インストール)のため、**数十秒オーダーの受信断が発生するのが設計上の想定**——長さの実測値
    そのものが成果(短縮の要否は実測を見てから判断する)
  - gap_start の欠落が upgrade start〜end の時刻範囲と整合する(連続した 1 区間が理想。
    複数区間あればそれも記録)
- 採取してほしい出力: H-1・H-3 の全出力 + upgrade start/end 時刻 + 送信側の `total sent`
  (msiexec-upgrade.log はファイルのまま保管)

```text
(結果記入欄)


```

## I. DB-8: ディスク満杯からの回復(検証項目 5)

database.md §4・§8 DB-8: 「削除で回復し得る」が SQLite で実際に成立するか(行削除後の
ページ再利用・満杯状態での削除実行の可否)。**システムドライブを満杯にしないため、小容量
VHD をマウントしてデータルートを一時的にそこへ向ける**。

### I-1. VHD 作成と ACL 付与

```powershell
New-Item -ItemType Directory -Force C:\YaguraLab\db8 | Out-Null
@"
create vdisk file=C:\YaguraLab\db8\yagura-db8.vhdx maximum=256 type=fixed
attach vdisk
create partition primary
format fs=ntfs quick label=YAGURADB8
assign letter=V
"@ | Set-Content -Encoding ascii C:\YaguraLab\db8\create-vhd.txt
diskpart /s C:\YaguraLab\db8\create-vhd.txt
New-Item -ItemType Directory V:\YaguraData | Out-Null
icacls V:\YaguraData /inheritance:r /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "NT SERVICE\Yagura:(OI)(CI)M"
icacls V:\YaguraData
```

- 期待結果: V: に 256 MB ボリュームができ、V:\YaguraData に C と同じ 3 主体の ACL が付く
- 注意: **VHD は OS 再起動で自動再接続されない**。この手順の途中で再起動した場合は
  `diskpart` で `select vdisk file=C:\YaguraLab\db8\yagura-db8.vhdx` → `attach vdisk` を
  実行してからサービスを起動し直すこと

### I-2. データルートを VHD へ向ける(サービス単位の環境変数)

データルートは環境変数 `YAGURA_DATAROOT` で上書きできる(configuration.md §5)。サービス
プロセスへ渡すため、サービスのレジストリキーに `Environment` 値(REG_MULTI_SZ)を設定する:

```powershell
Stop-Service Yagura
reg add HKLM\SYSTEM\CurrentControlSet\Services\Yagura /v Environment /t REG_MULTI_SZ /d "YAGURA_DATAROOT=V:\YaguraData" /f
Start-Service Yagura
Start-Sleep -Seconds 10
Test-Path V:\YaguraData\yagura.json
Test-Path V:\YaguraData\yagura.db
```

- 期待結果: 両方 `True`(新データルートで初期化された)
- **`False` の場合**(サービス単位 Environment 値がこの OS で効かない場合の代替経路):
  `[Environment]::SetEnvironmentVariable('YAGURA_DATAROOT','V:\YaguraData','Machine')` を実行して
  **OS を再起動**する(マシン環境変数はサービスにも届くが反映に再起動が要る)。再起動後は
  I-1 の注意に従い VHD を再接続してからサービス開始。**どちらの経路で成立したかを記入欄に記録**
- 採取してほしい出力: Test-Path の結果と、使った経路(レジストリ Environment / マシン環境変数)

### I-3. 保持期間削除の対象になる「古い行」の播種

容量枯渇時の自走復旧(保持期間削除の前倒し実行)を実際に発火させるため、保持期間の既定
30 日(DB-1)より古い日付のダミー行を入れておく:

```powershell
$sqlite3 = 'C:\YaguraLab\tools\sqlite3.exe'
Stop-Service Yagura
& $sqlite3 V:\YaguraData\yagura.db "INSERT INTO LogRecords (ReceivedAt,SourceAddress,SourcePort,Protocol,ParseStatus,Message) WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM c WHERE i<100000) SELECT '2026-05-01T00:00:00.0000000Z','203.0.113.1',514,0,0,'DB8-OLD-'||i||'-'||hex(zeroblob(200)) FROM c;"
& $sqlite3 V:\YaguraData\yagura.db "PRAGMA wal_checkpoint(TRUNCATE); SELECT COUNT(*) FROM LogRecords WHERE Message LIKE 'DB8-OLD-%';"
(Get-Item V:\YaguraData\yagura.db).Length
Start-Service Yagura
```

- 期待結果: 100000 件・DB ファイルが数十 MB に成長
- 採取してほしい出力: 件数とファイルサイズ

### I-4. ディスクを満杯にして書き込み失敗を起こす

残り空き容量をバラストで 3 MB 程度まで詰めてから、UDP を流し込んで SQLITE_FULL に到達させる:

```powershell
$free = (Get-Volume -DriveLetter V).SizeRemaining
$ballast = [int64]$free - 3MB
fsutil file createnew V:\ballast.bin $ballast
Get-Volume -DriveLetter V | Format-List SizeRemaining

$udp = New-Object System.Net.Sockets.UdpClient
$pad = 'x' * 400
1..50000 | ForEach-Object {
    $b = [Text.Encoding]::ASCII.GetBytes("<134>DB8-FILL-$_ $pad")
    [void]$udp.Send($b, $b.Length, '127.0.0.1', 514)
    if ($_ % 5000 -eq 0) { Start-Sleep -Seconds 1 }
}
$udp.Close()
Start-Sleep -Seconds 30
Get-Service Yagura
Get-WinEvent -LogName Application -MaxEvents 30 | Where-Object ProviderName -eq 'Yagura' |
    Format-List TimeCreated,Id,LevelDisplayName,Message
```

- 期待結果・観測ポイント(**ここからが DB-8 の本体。結果がどちらに転んでも記録が成果**):
  - サービスは**落ちずに Running のまま**(容量枯渇は分類済みの失敗として扱われる設計)
  - イベントログ(または後述の SystemEvents)に容量枯渇・保持期間削除の前倒し実行の痕跡が出る
  - 満杯下では**スプール退避も同じディスクで失敗する**ため、永続化失敗系の警告が出るのは想定内
- 採取してほしい出力: 空き容量・サービス状態・イベントログ抜粋

### I-5. 回復の確認(自走経路)

```powershell
& $sqlite3 -readonly V:\YaguraData\yagura.db "SELECT COUNT(*) FROM LogRecords WHERE Message LIKE 'DB8-OLD-%';"
& $sqlite3 -readonly V:\YaguraData\yagura.db "SELECT Kind,StartAt,EndAt,Details FROM SystemEvents WHERE Kind='retention.delete' ORDER BY Id DESC LIMIT 5;"
& $sqlite3 -readonly V:\YaguraData\yagura.db "PRAGMA freelist_count;"
$sizeBefore = (Get-Item V:\YaguraData\yagura.db).Length; $sizeBefore

# 書き込みが回復したかをマーカー送信で確認
$udp = New-Object System.Net.Sockets.UdpClient
1..100 | ForEach-Object { $b=[Text.Encoding]::ASCII.GetBytes("<134>DB8-RECOVERY-$_"); [void]$udp.Send($b,$b.Length,'127.0.0.1',514) }
$udp.Close()
Start-Sleep -Seconds 15
& $sqlite3 -readonly V:\YaguraData\yagura.db "SELECT COUNT(*) FROM LogRecords WHERE Message LIKE 'DB8-RECOVERY-%';"
$sizeAfter = (Get-Item V:\YaguraData\yagura.db).Length; $sizeAfter
& $sqlite3 -readonly V:\YaguraData\yagura.db "PRAGMA freelist_count;"
```

- 期待結果(仮説どおりなら):
  - DB8-OLD 件数が 100000 から大きく減っている(前倒し削除が満杯状態でも実行できた)
  - `retention.delete` の SystemEvents 行が記録されている
  - DB8-RECOVERY が 100 件(または相当数)入る = **書き込みが回復**
  - `$sizeAfter` が `$sizeBefore` からほぼ増えていない(ファイルを成長させず**解放ページの
    再利用**で書けている)・freelist_count が回復書き込みで減る
- **削除自体が失敗する場合**(満杯下で WAL の追記余地すらないケース——これが DB-8 の
  未確定点そのもの): その事実とエラーメッセージを記録して I-6 へ
- 採取してほしい出力: 上記全出力(件数・サイズ・freelist の前後)

### I-6. 手動削除経路(I-5 で自走回復しなかった場合のみ)

```powershell
Stop-Service Yagura
& $sqlite3 V:\YaguraData\yagura.db "DELETE FROM LogRecords WHERE Id IN (SELECT Id FROM LogRecords WHERE Message LIKE 'DB8-OLD-%' LIMIT 50000);"
& $sqlite3 V:\YaguraData\yagura.db "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA freelist_count;"
# それでも失敗する場合は ballast を少し削って空きを作ってから再試行し、
# 「何 MB の空きがあれば削除が通るか」を記録する
# Remove-Item V:\ballast.bin  → fsutil で少し小さく作り直す
Start-Service Yagura
```

- 期待結果: 満杯のままの DELETE が通るか(通る/通らない + エラー全文)を記録。通らない場合は
  空きを段階的に作って通る閾値を記録する——**「満杯状態での削除実行の可否」の一次データ**
- 採取してほしい出力: 各 DELETE の成否・エラー全文・(該当時)削除が通った時点の空き容量

### I-7. 環境復元

```powershell
Stop-Service Yagura
reg delete HKLM\SYSTEM\CurrentControlSet\Services\Yagura /v Environment /f
# マシン環境変数の経路を使った場合はこちらも:
# [Environment]::SetEnvironmentVariable('YAGURA_DATAROOT', $null, 'Machine')
Start-Service Yagura
Start-Sleep -Seconds 5
Test-Path C:\ProgramData\Yagura\yagura.json   # 期待: True(既定データルートへ復帰)
@"
select vdisk file=C:\YaguraLab\db8\yagura-db8.vhdx
detach vdisk
"@ | Set-Content -Encoding ascii C:\YaguraLab\db8\detach-vhd.txt
diskpart /s C:\YaguraLab\db8\detach-vhd.txt
```

- 期待結果: 既定データルートへ戻り、V: が消える(VHD ファイルは DB-8 の一次資料として保管)
- 採取してほしい出力: Test-Path の結果

```text
(結果記入欄: I-2〜I-7。使った経路・各観測値)


```

## J. アンインストールと残置物確認・後片付け

```powershell
Start-Process msiexec.exe -Wait -ArgumentList '/x C:\YaguraLab\Yagura-0.1.1.msi /qn /norestart /l*v C:\YaguraLab\results\msiexec-uninstall.log'
Get-Service Yagura -ErrorAction SilentlyContinue                        # 期待: なし
Get-NetFirewallRule -DisplayName 'Yagura*' -ErrorAction SilentlyContinue # 期待: 0 件
Test-Path "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Yagura"  # 期待: False
Test-Path "$env:ProgramFiles\Yagura"                                     # 期待: False
Test-Path C:\ProgramData\Yagura\yagura.db                                # 期待: True(ログは資産 = 保持が設計)
```

- 期待結果: 各行のコメントどおり(アンインストールは 0.1.1 の MSI で行う点に注意)
- 採取してほしい出力: 上記コマンドの出力全部
- 後片付け(任意): 検証記録(C:\YaguraLab\results・VHD)を回収したら、
  `C:\ProgramData\Yagura` / `C:\ProgramData\Yagura.e2e-backup` は削除してよい

```text
(結果記入欄)


```

## 返却方法

- 各記入欄を埋めた本ファイル(または PR コメントへの貼り付け)+ スクリーンショット +
  `C:\YaguraLab\results\` 一式
- 反映先(検証結果を受けて別 PR で実施): architecture.md §9 M-15・database.md §8 DB-8・
  security.md §5/§7 SEC-3 の「✅ 確定」更新、installer/README.md の検証状態更新
