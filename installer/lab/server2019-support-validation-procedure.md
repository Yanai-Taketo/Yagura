# Windows Server 2019 対応区分の検証手順（ADR-0024 委任事項 1）

> **未実施**（2026-08-01 作成）。本手順は [ADR-0024](../../docs/adr/0024-supported-environments.md) 決定 1 が「対応（Supported）」に置いた **Windows Server 2019 の区分を確定させるためのゲート**である。
> 実施したら結果を ADR-0024 の改訂履歴へ記録し、本ファイル冒頭のこの注記を「実施済み（日付・環境）」へ書き換える。

[Issue #494](https://github.com/Yanai-Taketo/Yagura/issues/494) の lab 検証手順。

**本検証を通過するまで、README のシステム要件表に Server 2019 を「対応」として転記しない**（ADR-0024 決定 1・帰結のリスク欄）。現在の要件表は Server 2019 を「検証中」と表記している。

## なぜこの検証が要るのか

Server 2019 は決定 1 で対応区分に置いたが、**実機で一度も踏んでいない**。しかも **ADR 全体の約 1/3 がこの結果に依存する**——決定 1 の対応行・決定 3（ブラウザ）・決定 5（検証水準）・決定 6（LaunchCondition の下限）と、委任事項 1/3/6 が連鎖する。

| 項目 | CI で検証できない理由 |
|---|---|
| すべて | **`windows-2019` ランナーは退役済み**でランナー一覧に存在しない（確認日 2026-07-26）。Server 2019 は CI では原理的に踏めない |
| TLS の挙動 | Schannel の TLS 1.3 対応は OS 依存（Server 2022 / Windows 11 以降）。CI ランナーは推奨環境しか無い |
| ブラウザの有無 | ランナー image は素の OS ではなく、開発ツールが大量に導入されたカスタム image である |

## 実施環境

- **ホスト**: オーナー lab の Proxmox（x64）
- **ゲスト**: Windows Server 2019 評価版（[Microsoft Evaluation Center](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-server-2019)。180 日・**10 日以内にオンライン認証が必要**）
- **Desktop Experience** を選択する（Server Core は ADR-0024 決定 1 で対応外区分のため本手順の対象外）
- 比較対象として **Server 2022 または 2025 の環境**（§A-4 の暗号スイート比較に使う。既存 lab を流用してよい）

### 「素の状態」の定義（オーナー裁定 2026-08-01）

§C の「対応ブラウザ導入前の素の状態」は、**(b) Windows Update を適用した後**とする。

理由: 決定 3 が守ろうとしているのは「**利用者が実際に遭遇する状態**」である。実運用の Server 2019 はまず更新が当たっており、ISO 直後の状態で「到達できない」を確認しても現実の環境の答えにならない。

**この定義には結論が反転しうる分岐が含まれる**——Windows Update が Edge を導入した場合、「Server 2019 でも素で管理 UI に到達できる」ことになり、**決定 3 と README のシステム要件表の記述を見直す対象になる**。その場合も失敗ではなく、要件を緩められる発見として扱う（§C の判定を参照）。

## 事前準備

1. ゲストへ Windows Update を適用し、**適用後の状態を記録**する（`winver` の OS ビルド、`Get-HotFix | Select -Last 5`）
2. **ブラウザを導入しない**まま §C を先に実施する（導入してしまうと §C が観測できなくなる）
3. §C の後に対応ブラウザ（Edge / Chrome / Firefox のいずれか）を導入し、§B 以降へ進む
4. MSI は検証対象のバージョンの `Yagura-<版>-x64.msi` を使う。ビルド元のコミット SHA を記録する

---

## A. TLS 固定指定の挙動（最重要）

**対応する未確認事項**: Yagura は TLS を使う 3 か所すべてで `SslProtocols.Tls12 | SslProtocols.Tls13` を固定している。Schannel の TLS 1.3 は **Server 2022 / Windows 11 以降**でのみ利用可能であり、**Server 2019 でこの指定がどう振る舞うかは未検証**である。

### 検証する仮説

TLS 1.3 非対応の OS でも、**TLS 1.2 へ縮退して接続が成立する**こと。かつ、**仮に失敗する場合でもその失敗が観測可能**であること（[#482](https://github.com/Yanai-Taketo/Yagura/issues/482) の修正により `yagura.ingestion.tcp_connection.faulted` へ計上されるはず）。

### 共通の測定手段

以下の PowerShell を lab 機に置く（`Probe-YaguraTls.ps1`）。`SslStream` が実際にネゴシエートしたプロトコルと暗号スイートを報告する。

```powershell
param(
  [Parameter(Mandatory)][string]$TargetHost,
  [Parameter(Mandatory)][int]$Port
)
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($TargetHost, $Port)
$ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, ({ $true } -as [Net.Security.RemoteCertificateValidationCallback]))
try {
  $ssl.AuthenticateAsClient($TargetHost)
  [pscustomobject]@{
    Target            = "$TargetHost`:$Port"
    SslProtocol       = $ssl.SslProtocol          # 期待: Tls12（Server 2019）
    CipherSuite       = $ssl.NegotiatedCipherSuite
    CipherAlgorithm   = $ssl.CipherAlgorithm
    HashAlgorithm     = $ssl.HashAlgorithm
    Result            = 'Handshake OK'
  }
} catch {
  [pscustomobject]@{ Target = "$TargetHost`:$Port"; Result = "FAILED: $($_.Exception.GetType().FullName): $($_.Exception.Message)" }
} finally { $ssl.Dispose(); $client.Dispose() }
```

### A-1. syslog TLS 受信（6514）

1. 管理画面から TLS 受信を有効化し、証明書を選択する（自己署名でよい）
2. **サービスが起動し、6514 が listen していること**を確認: `Get-NetTCPConnection -LocalPort 6514 -State Listen`
   - **listen していない場合**: 起動時に失敗している。イベントログ（Yagura プロバイダ）を確認し、記録して §A-4 へ進む
3. `Probe-YaguraTls.ps1 -TargetHost <サーバ名> -Port 6514` を実行し、出力を記録する
4. **`yagura.ingestion.tcp_connection.faulted` を確認する**（状態画面のカウンタ一覧、または `/status`）

| 観測結果 | 判定 |
|---|---|
| `SslProtocol = Tls12` で Handshake OK | **合格**。決定 1 の「TLS は 1.2 まで」が実測で裏付けられる |
| Handshake FAILED **かつ** `faulted` カウンタが増える | **条件付き合格**（観測性は成立）。ただし TLS 受信は使えないため決定 1 の対応行の記述を見直す |
| Handshake FAILED **かつ** カウンタもログも動かない | **不合格**。#482 の修正が不完全である。**実装の是正が先、対応表明はその後**（ADR-0024 委任 1 項目①(iii)） |

### A-2. 管理 UI の HTTPS（8515 / リモート有効化時は 8516）

管理リモート HTTPS を opt-in で有効化し、A-1 と同じ手順で測定する。**loopback 専用のままでも `127.0.0.1` に対して測定できる**。

### A-3. 閲覧 UI の HTTPS（8514）

`Viewer:Https:Enabled` を有効化し、同様に測定する。**期限切れ・失効時は平文へ落とさず HTTPS を停止する**設計（ADR-0022）のため、証明書が有効であることを先に確認しておく。

### A-4. 暗号スイートの比較（推奨環境との差）

**同じ `Probe-YaguraTls.ps1` を Server 2022 または 2025 の環境でも実行し、`CipherSuite` を並べて記録する。**

決定 1 は「Yagura の実装都合で意図的な機能差は設けない」と**方針**を述べるにとどめており、**実際に観測される差の全体は本検証で確定する**。Yagura が固定しているのはプロトコル版のみで、**暗号スイートの選択は OS の Schannel ポリシーに委ねられている**ためである。

| 観測結果 | 判定 |
|---|---|
| スイート集合が同等（CBC・SHA-1・3DES を含まない） | 差は「TLS 1.3 が使えない」1 点。決定 1 の記述どおり |
| Server 2019 側に弱いスイートが残る | **決定 1 の「差は 1 点」を修正する**。要件表とセキュリティ文書へ「対応環境での暗号強度は OS 側のハードニングに依存する」旨を追記する |

---

## B. 中核機能の通し（導入 → 受信 → 保存 → 閲覧 → 管理 UI 到達）

対応ブラウザを導入したうえで実施する。

1. `msiexec /i Yagura-<版>-x64.msi /qn /l*v install.log` でサイレントインストール
2. サービスの起動確認: `Get-Service Yagura`
3. UDP 514 へテスト送信し、閲覧 UI（`http://localhost:8514/`）で到達を確認
4. 管理 UI（`http://localhost:8515/admin`）に到達できることを確認

**判定**: 4 段すべてが成立すること。1 つでも失敗した場合は、その時点で ADR-0024 の再評価トリガ（選択肢 C への転換）に該当する可能性があるため、詳細を記録して中断してよい。

---

## C. 素の状態でのブラウザ不在確認（§「素の状態」の定義を参照）

**ブラウザを導入する前**に実施すること。

1. Windows Update 適用後の状態で、**インストールされているブラウザを列挙**する
   ```powershell
   Get-AppxPackage *Edge* | Select Name, Version
   Test-Path "$env:ProgramFiles (x86)\Microsoft\Edge\Application\msedge.exe"
   Test-Path "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
   Get-ItemProperty 'HKLM:\SOFTWARE\Clients\StartMenuInternet\*' -ErrorAction SilentlyContinue | Select PSChildName
   ```
2. Yagura を導入し、**サーバ機上で `http://localhost:8515/admin` を開けるか**を試す

| 観測結果 | 判定 |
|---|---|
| 対応ブラウザが存在せず管理 UI に到達できない | **決定 3 の根拠が実測で裏付けられる**。要件表の記述を維持する |
| **Windows Update により Edge が導入されており到達できる** | **決定 3 と README の記述を見直す対象**。「Server 2019 では MSI 実行前にブラウザ導入が必要」という前提が崩れるため、要件を緩められる。**失敗ではない**——観測結果を ADR の改訂として記録する |
| Internet Explorer しか無く、IE では管理 UI が正しく動作しない | 決定 3 のとおり（Blazor は IE 非対応）。IE での挙動（白画面 / スクリプトエラー等）も記録する |

---

## D. 観測性カウンタの出力

状態画面のカウンタ一覧、または `/status` に `yagura.ingestion.*` の計器が出力されることを確認する。

**§A で `faulted` を見るため、B の前に一度ベースライン（全カウンタの初期値）を記録しておくこと。**

---

## E. スプール発動 → drain 追いつきの 1 サイクル

**静的スモークだけでは不十分**である。バックプレッシャ経路は高負荷でしか発火せず、「未検証のまま埋め込まれた欠陥」と「副ゆえ許容する劣化」を区別できない（ADR-0009 が「試験的」の ARM64 に課したのと同水準を、「対応」の Server 2019 にも課す）。

1. 保存先を停止する（SQL Server 構成ならサービス停止、SQLite なら書き込み不能にする）
2. 負荷を掛けて**スプール退避を発生させる**（`yagura.ingestion.spool.evacuated` が増えること）
3. 保存先を復旧する
4. **drain が追いつき、退避分が保存されること**を確認する
5. **突合が成立すること**（送信数 = 保存 + 全カウンタ）を確認する

**判定**: (a) 退避カウンタ > 0（実際に発動した）(b) drain 完了 (c) 突合成立、の 3 点すべて。

---

## F. SQLite → SQL Server 昇格の 1 通し

決定 1 の対応行は「昇格」を中核機能に含めている。[#495](https://github.com/Yanai-Taketo/Yagura/issues/495)（SQL Server 2019 / 2025 の検証）と**合同実施してよい**。

管理画面の昇格ウィザードで SQLite → SQL Server を通し、切替中の受信継続と件数突合を確認する。

---

## G. 受信バッファ既定（1 MiB）の有効性

M-2 は既定値 1 MiB を**開発機 1 台・各セル 1 回**の実測で確定しており、「基準環境・複数回試行での再確認は M-6 に委ねる」という留保が付いている。

Server 2019 で `Ingestion:Udp:ReceiveBufferBytes` の既定が実際に適用されること（起動時ログ / 状態画面）を確認する。**OS レベルの取りこぼしは runtime では観測できない**（ADR-0016 で恒久受容済み）ため、ここでは**設定が効いていることの確認までを範囲とする**。

---

## 判定と分岐

| 結果 | 対応 |
|---|---|
| A〜G がすべて合格 | **Server 2019 の「対応」区分が確定**。README のシステム要件表へ転記し、ADR-0024 の改訂として記録する |
| A で TLS が失敗するが観測可能 | 決定 1 の対応行から opt-in 強化の該当項目を外し、要件表に明記する |
| **A で失敗が観測できない** | **実装の是正が先**（#482 の修正が不完全）。対応表明はその後 |
| A-4 で暗号スイートに差がある | 決定 1 の「差は 1 点」を修正し、暗号強度が OS 側のハードニングに依存する旨を追記する |
| B で中核機能が不成立 | **選択肢 C（対応 = Server 2022 以上）への転換を supersession で検討**（ADR-0024 再評価トリガ） |
| E が通らない | Server 2019 を「試験的」へ落とすことを検討（ADR-0024 再評価トリガ） |
| C で Edge が存在した | **決定 3 と要件表を緩める方向で見直す**（失敗ではない） |

## 記録様式

conventions.md の実体検証記録の作法に従い、**実施した PR の body に残す**。

- 実施日・実施者
- ゲストの OS ビルド（`winver`）と Windows Update 適用状況
- 検証した Yagura のバージョンとビルド元コミット SHA
- §A の `Probe-YaguraTls.ps1` の出力（3 か所 + 比較対象環境）
- §C のブラウザ列挙結果
- §E の突合結果（送信数・保存数・各カウンタ）
- 判定と、上表のどの分岐に該当したか

## 参照

- [ADR-0024](../../docs/adr/0024-supported-environments.md) 決定 1・決定 3・決定 5・委任事項 1
- [ADR-0009](../../docs/adr/0009-architecture-support.md) 決定 6 Phase 1（ARM64 に課した負荷項目。本手順の §E はこれと同水準）
- [ADR-0016](../../docs/adr/0016-os-drop-gauge-mechanism.md)（OS レベル取りこぼしを runtime で観測しないことの恒久受容）
- [#482](https://github.com/Yanai-Taketo/Yagura/issues/482)（接続ハンドラの無音脱落。§A-1 の観測性はこの修正に依存する）
- [#494](https://github.com/Yanai-Taketo/Yagura/issues/494)（本手順の追跡 Issue）
