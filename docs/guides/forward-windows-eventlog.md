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
主に手動・少数端末導入の場面に限られる。

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
  自動起動・転送再開を実機確認済み(2026-07-06)
- 転送内容(本文 / ホスト名 / アプリ名 / 重大度の対応、イベント ID・チャネルの
  構造化データ)も同環境で実機確認済み(送信フレームの実測 + 閲覧画面への到達)
- それ以外の版を使う場合は、テスト端末で `install.ps1` → 動作確認 → 本配布の順を推奨
  (5.x 系は本キットでの転送内容検証を未実施)
