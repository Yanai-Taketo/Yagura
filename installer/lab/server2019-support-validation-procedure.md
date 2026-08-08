# Windows Server 2019 対応区分の検証手順（ADR-0024 委任事項 1）

> **条件付き実施済み**（2026-08-02。Windows Server 2019 Standard Evaluation 10.0.17763.3650 / Desktop Experience / Proxmox）。
> 結果は [ADR-0024 改訂 7](../../docs/adr/0024-supported-environments.md) に記録済みで、**Server 2019 の「対応」区分は確定**し README のシステム要件表へ転記した。
> **残条件はすべて解消した**（2026-08-08 の第 2 回検証で A を完了。§A-4 の推奨環境比較は 2026-08-02 に CI で完了）。ADR-0024 改訂 8・10 を参照。

[Issue #494](https://github.com/Yanai-Taketo/Yagura/issues/494) の lab 検証手順。

## 残条件（再実施が必要な項目）

| # | 項目 | 理由 | 実施方法 |
|---|---|---|---|
| ~~**A**~~ | ~~**§A-1 の観測性判定と §A-3**~~ **完了（2026-08-08）**。main ビルド（`41be3fd`）で再実施し合格 | 2026-08-02 の実施は**公開リリース v0.5.0（`7f638da`、2026-07-24）**を対象にした。閲覧 HTTPS（2026-07-26）と `yagura.ingestion.tcp_connection.faulted`（2026-08-01）はいずれもそれより後に main へ入っており、当時のビルドに存在しなかった | **main からビルドした MSI** で §A-1 と §A-3 のみ再実施する（§B・§D〜§G の再実施は不要——OS 側の性質を見る項目であり版に依存しない） |
| ~~**B**~~ | ~~**§A-4 の推奨環境との比較**~~ | **完了（2026-08-02）**。CI で採取し、2019 と 2022 の既定が完全に同一であることを確認した（§A-4 参照） | — |

**残条件 A は本手順書の指定漏れが原因である**（「検証対象のバージョンの MSI」としか書かず、main ビルドを要求しなかった）。§「事前準備」で是正済み。

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
4. **MSI は main（またはリリース候補ブランチ）からビルドしたものを使う。公開済みリリースの MSI を流用しない**——本手順は「v1.0 で対応表明する全機能」を見るゲートであり、リリース済み版には**まだ収録されていない機能が含まれうる**（2026-08-02 の実施では、閲覧 HTTPS と `faulted` カウンタが検証対象の v0.5.0 に無く、§A-1 と §A-3 が残条件として持ち越しになった）。ビルド元のコミット SHA を記録する
   - lab 機に .NET SDK が無い場合は、**開発機で MSI をビルドして lab 機へ持ち込む**（lab 機に SDK を入れる必要はない）
5. **§E は §F の後に実施する**——§E のスプール発動は SQL Server サービスの停止で起こすため、先に §F で SQL Server へ昇格しておく必要がある（理由は §E 冒頭）

---

## A. TLS 固定指定の挙動（最重要）

**対応する未確認事項**: Yagura は TLS を使う 3 か所すべてで `SslProtocols.Tls12 | SslProtocols.Tls13` を固定している。Schannel の TLS 1.3 は **Server 2022 / Windows 11 以降**でのみ利用可能であり、**Server 2019 でこの指定がどう振る舞うかは未検証**である。

### 検証する仮説

TLS 1.3 非対応の OS でも、**TLS 1.2 へ縮退して接続が成立する**こと。かつ、**仮に失敗する場合でもその失敗が観測可能**であること（[#482](https://github.com/Yanai-Taketo/Yagura/issues/482) の修正により `yagura.ingestion.tcp_connection.faulted` へ計上されるはず）。

### 共通の測定手段

以下の PowerShell を lab 機に置く（`Probe-YaguraTls.ps1`）。`SslStream` が実際にネゴシエートしたプロトコルと暗号を報告する。

**クライアント側の提示プロトコルを明示指定する（既定に委ねない）。** Server 2019 の素の PowerShell 5.1 は `SchUseStrongCrypto` / `SystemDefaultTlsVersions` がいずれも未設定で、`SslStream` の既定提示が **TLS 1.0 まで**になる。Yagura の下限は 1.2 のため、既定のまま実行すると**サーバの状態にかかわらず必ず失敗する**（2026-08-02 の実施で実際に踏んだ。`SEC_E_UNSUPPORTED_FUNCTION 0x80090302`）。これは**サーバ側の欠陥ではなくクライアント側の既定**であり、明示指定しないと §A-1 の判定が下せない。

```powershell
param(
  [Parameter(Mandatory)][string]$TargetHost,
  [Parameter(Mandatory)][int]$Port,
  # 既定に委ねない（上記参照）。TLS 1.1 以下しか提示しない場合の挙動を見るときだけ変える。
  [System.Security.Authentication.SslProtocols]$Protocols =
    [System.Security.Authentication.SslProtocols]::Tls12
)
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($TargetHost, $Port)
$ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, ({ $true } -as [Net.Security.RemoteCertificateValidationCallback]))
try {
  # checkCertificateRevocation = $false（自己署名の閉域検証のため）
  $ssl.AuthenticateAsClient($TargetHost, $null, $Protocols, $false)
  [pscustomobject]@{
    Target              = "$TargetHost`:$Port"
    Offered             = $Protocols
    SslProtocol         = $ssl.SslProtocol          # 期待: Tls12（Server 2019）
    CipherAlgorithm     = $ssl.CipherAlgorithm
    CipherStrength      = $ssl.CipherStrength
    HashAlgorithm       = $ssl.HashAlgorithm
    KeyExchangeAlgorithm= $ssl.KeyExchangeAlgorithm
    Result              = 'Handshake OK'
  }
} catch {
  [pscustomobject]@{ Target = "$TargetHost`:$Port"; Offered = $Protocols; Result = "FAILED: $($_.Exception.GetType().FullName): $($_.Exception.Message)" }
} finally { $ssl.Dispose(); $client.Dispose() }
```

> **スクリプトは BOM 付き UTF-8 で保存すること。** BOM 無し UTF-8 だと Windows PowerShell 5.1 が日本語コメントを誤読してスクリプトが壊れる（2026-08-08 の実施で実際に踏んだ）。`Set-Content -Encoding UTF8`（5.1 既定で BOM 付き）か、エディタで「BOM 付き UTF-8」を選ぶ。

**厳密な暗号スイート名（`TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384` 等）が要る場合は PowerShell 7 で実行する。** `SslStream.NegotiatedCipherSuite` は .NET Core 3.0 以降のプロパティで、**Windows PowerShell 5.1（.NET Framework）には存在しない**。5.1 で取れるのは上記の `CipherAlgorithm` / `HashAlgorithm` / `KeyExchangeAlgorithm` の分解値までで、そこから**候補を絞り込むことはできてもスイート名の断定はできない**。5.1 のまま進める場合は、分解値を記録したうえで「厳密名は未取得」と明記すること（推測でスイート名を書かない）。

### A-1. syslog TLS 受信（6514）

1. 管理画面から TLS 受信を有効化し、証明書を選択する（自己署名でよい）
2. **サービスが起動し、6514 が listen していること**を確認: `Get-NetTCPConnection -LocalPort 6514 -State Listen`
   - **listen していない場合**: 起動時に失敗している。イベントログ（Yagura プロバイダ）を確認し、記録して §A-4 へ進む
3. `Probe-YaguraTls.ps1 -TargetHost <サーバ名> -Port 6514` を実行し、出力を記録する
4. **`yagura.ingestion.tcp_connection.faulted` を確認する**（状態画面のカウンタ一覧、または `/status`）

| 観測結果 | 判定 |
|---|---|
| `SslProtocol = Tls12` で Handshake OK | **合格**。決定 1 の「TLS は 1.2 まで」が実測で裏付けられる |
| Handshake FAILED **かつ** `faulted` カウンタ**または**イベントログ警告が出る | **条件付き合格**（観測性は成立）。ただし TLS 受信は使えないため決定 1 の対応行の記述を見直す |
| Handshake FAILED **かつ** カウンタもログも動かない | **不合格**。#482 の修正が不完全である。**実装の是正が先、対応表明はその後**（ADR-0024 委任 1 項目①(iii)） |

**判定の前に、失敗がクライアント側の既定によるものでないことを切り分けること。** 上記「共通の測定手段」のとおり、Server 2019 の PowerShell 5.1 は既定で TLS 1.0 までしか提示しない。`-Protocols` を明示せずに得た FAILED は**サーバの評価にならない**。切り分けの目安:

- `SEC_E_UNSUPPORTED_FUNCTION`（0x80090302）/「共通のアルゴリズムを処理していない」→ **クライアント側の提示プロトコルを疑う**。`Tls12` 明示で再実行する
- `Tls12` 明示でも FAILED → サーバ側の評価に進む（上表）
- 参考: `-Protocols Tls11` で FAILED になることを確認しておくと、**サーバの下限 1.2 が効いている**ことの裏付けになる

### A-2. 管理 UI の HTTPS（8515 / リモート有効化時は 8516）

管理リモート HTTPS を opt-in で有効化し、A-1 と同じ手順で測定する。測定自体は `127.0.0.1` に対して行える。

> **`Admin:Https:Enabled = true` だけでは 8516 は開かない。** HTTPS はリモートバインド面専用であり、**リモートバインドの有効化（= 管理 UI の認証設定）が前提**になる（2026-08-02 の実施で判明。lab では独自 ID/パスワード認証の初期アカウントを作成して有効化した）。8516 が listen していない場合はまずここを疑う。

### A-3. 閲覧 UI の HTTPS（8514）

`Viewer:Https:Enabled` を有効化し、同様に測定する。**期限切れ・失効時は平文へ落とさず HTTPS を停止する**設計（ADR-0022）のため、証明書が有効であることを先に確認しておく。

> **A-2 が成立していれば、本項の TLS ネゴシエーション部分は同一コード経路で被覆されている。** `Program.cs` の `ConfigureHttpsIfRequired` は管理面・閲覧面で共通の 1 か所であり、`SslProtocols` 指定も共有している。本項で固有に見るのは**証明書の供給元が閲覧面向けに分かれていること**と、**HTTPS が開かなかったときに平文 HTTP へ落ちないこと**（ADR-0022 決定 2）である。
>
> 逆に、**平文 HTTP のまま応答したなら**（`Probe` が `unexpected packet format` で失敗する）**閲覧 HTTPS が有効になっていない**——設定の反映漏れか、そのビルドに機能が無いかを先に確かめること。

### A-4. 暗号スイートの比較（推奨環境との差）

Yagura が固定しているのは**プロトコル版のみ**で、**暗号スイートの選択は OS の Schannel ポリシーに委ねられている**。したがって「区分間の差」はハンドシェイク 1 回の観測では出ず、**OS の既定有効スイート集合そのものを比べる**必要がある。

**lab では 2019 側だけを採る。比較対象（2022 / 2025）は CI で採る**——lab 機から推奨環境の実機へ到達できるとは限らず、また比較したいのは Yagura の挙動ではなく **OS の既定**だからである（GitHub ホストランナーの Windows image で足りる）。

```powershell
# lab（Server 2019）で実行し、出力を全文記録する
Get-TlsCipherSuite | Select-Object Name, Protocols, Cipher, CipherLength, Hash, Exchange |
  Format-Table -AutoSize
```

**注意**: `Get-TlsCipherSuite` の `Protocols` 欄に `0x0304`（TLS 1.3）のスイートが列挙されても、**Server 2019 の Schannel では TLS 1.3 は利用できない**。列挙と実効は一致しない——判定には §A-1 で実測した `SslProtocol` の値を使うこと。

| 観測結果 | 判定 |
|---|---|
| 2019 と推奨環境でスイート集合が同等 | 弱いスイートの残存は**全区分共通の性質**である。「対応環境固有の差」としては書かず、全区分に掛かる注意として記す |
| 2019 側にだけ弱いスイート（3DES・SHA-1 系 CBC・NULL）が残る | **区分間の差として記す**。要件表に「対応環境では暗号強度が OS 側のハードニングにより依存度が高い」旨を追記する |

この観測を受けて **ADR-0024 決定 1 の「差は TLS 1.3 の 1 点」は既に撤回済み**（改訂 7）。残るのは「2019 固有か全区分共通か」の切り分けのみ。

#### 2019 側の実測値（比較の基準。2026-08-02）

Windows Server 2019 Standard Evaluation **10.0.17763.3650**（Windows Update 2022-11 適用水準）の `Get-TlsCipherSuite` 出力。既定有効は **31 スイート**（DTLS 専用の `0xFEFF` / `0xFEFD` 表記は省略）。

```
TLS_AES_256_GCM_SHA384                    0x0304
TLS_AES_128_GCM_SHA256                    0x0304
TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384   (protocols 表示なし)
TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256   0x0303
TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384     0x0303
TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256     0x0303
TLS_DHE_RSA_WITH_AES_256_GCM_SHA384       0x0303
TLS_DHE_RSA_WITH_AES_128_GCM_SHA256       0x0303
TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384   (protocols 表示なし)
TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256   0x0303
TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384     0x0303
TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256     0x0303
TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA      0x0301,0x0302,0x0303
TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA      0x0301,0x0302,0x0303
TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA        0x0301,0x0302,0x0303
TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA        0x0301,0x0302,0x0303
TLS_RSA_WITH_AES_256_GCM_SHA384           0x0303
TLS_RSA_WITH_AES_128_GCM_SHA256           0x0303
TLS_RSA_WITH_AES_256_CBC_SHA256           0x0303
TLS_RSA_WITH_AES_128_CBC_SHA256           0x0303
TLS_RSA_WITH_AES_256_CBC_SHA              0x0301,0x0302,0x0303
TLS_RSA_WITH_AES_128_CBC_SHA              0x0301,0x0302,0x0303
TLS_RSA_WITH_3DES_EDE_CBC_SHA             0x0300,0x0301,0x0302,0x0303
TLS_RSA_WITH_NULL_SHA256                  0x0303
TLS_RSA_WITH_NULL_SHA                     0x0300,0x0301,0x0302,0x0303
TLS_PSK_WITH_AES_256_GCM_SHA384           (protocols 表示なし)
TLS_PSK_WITH_AES_128_GCM_SHA256           0x0303
TLS_PSK_WITH_AES_256_CBC_SHA384           0x0303
TLS_PSK_WITH_AES_128_CBC_SHA256           0x0303
TLS_PSK_WITH_NULL_SHA384                  0x0303
TLS_PSK_WITH_NULL_SHA256                  0x0303
```

**注意**: TLS 1.3 のスイート（`0x0304`）が列挙されるが、**Schannel の 17763 では TLS 1.3 は利用できない**。§A-1 の実測（`SslProtocol = Tls12`）が正であり、この列挙を根拠に「TLS 1.3 が使える」と読まないこと。

#### 推奨環境側の採取（CI）— **実施済み（2026-08-02）**

`.github/workflows/tls-cipher-suite-survey.yml` が `windows-2025` / `windows-2022` で採取する。**lab は不要**——測っているのは Yagura の挙動ではなく OS の既定だからである。再測は `workflow_dispatch` で行う。

> **`Get-TlsCipherSuite` の出力を「OS 既定」と読まないこと。** スイート順序はグループポリシーで上書きでき、上書きされていれば同コマンドは**そのポリシーの内容**を返す。GitHub ホストランナーは実際に上書きしており（強い 10 種のみ）、初回計測は「弱いスイート 0」という**誤った結論**を返した。OS 既定は `HKLM\SYSTEM\CurrentControlSet\Control\Cryptography\Configuration\Local\SSL\00010002` が持つ。**lab 機で再測する場合も、まずこのキーとポリシーキーの有無を確認すること。**

#### 比較結果（§A-4 の答え）

| | Server 2019（本手順） | Server 2022（CI） | Server 2025（CI） |
|---|---|---|---|
| 既定有効スイート数 | **31** | **31** | **28** |
| 弱いスイート数（3DES / RC4 / NULL / SHA-1 CBC） | **11** | **11** | **10** |
| 3DES | あり | あり | **なし** |
| DHE-RSA GCM（2 種） | あり | あり | **なし** |
| SHA-1 CBC（6 種） | あり | あり | あり |
| NULL 系（4 種） | あり | あり | あり |

**判定表の 1 行目に該当した**——2019 と推奨環境（2022）の集合は**完全に同一**で、弱いスイートの残存は**全区分共通の性質**である。ADR-0024 改訂 8 に記録済みで、**本項の残条件は消えた**。

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

**保存先は SQL Server 構成にし、SQL Server サービスの停止で発火させる**（`Stop-Service 'MSSQL$SQLEXPRESS'` 等）。

> **SQLite の書き込み不能化は採らない。** 稼働中プロセスが既に開いているハンドルには ACL 変更が効かず、**手順書に無い介入なしには再現できない**（2026-08-02 の実施で確認）。SQL Server 停止方式なら非破壊で確実に発火する。この都合上、**§E は §F（SQL Server への昇格）の後に実施する**。

1. 保存先の SQL Server サービスを停止する
2. 負荷を掛けて**スプール退避を発生させる**（`yagura.ingestion.spool.evacuated` が増えること）
3. 保存先の SQL Server サービスを起動して復旧する
4. **drain が追いつき、退避分が保存されること**を確認する
5. **突合が成立すること**（送信数 = 保存 + 全カウンタ）を確認する

**判定**: (a) 退避カウンタ > 0（実際に発動した）(b) drain 完了 (c) 突合成立、の 3 点すべて。

---

## F. SQLite → SQL Server 昇格の 1 通し

決定 1 の対応行は「昇格」を中核機能に含めている。[#495](https://github.com/Yanai-Taketo/Yagura/issues/495)（SQL Server 2019 / 2025 の検証）と**合同実施してよい**。

管理画面の昇格ウィザードで SQLite → SQL Server を通し、**切替の前後で件数突合が成立する**ことを確認する。

- **切替前に SQLite へ入れた件数が、SQLite 側に欠けずに残っていること**
- **切替後に送った件数が、SQL Server 側にすべて保存されること**

> **「切替中の受信継続」は確認項目にしない。** 昇格は**サービス再起動を伴う方式**であり、製品自身が「反映にはサービスの再起動が必要です（再起動中は受信できません）」と表示する。無瞬断切替は database.md §6.1 の後続実装であって現行の設計ではない。**受信断そのものは仕様どおりであり、不合格の根拠にならない**（改訂前の本節は存在しない挙動を確認項目にしていた。2026-08-02 の実施で判明）。
>
> **切替を実行してもサービスは自動再起動されない。手動で再起動すること。** これを忘れると、
> 切替直後に送ったログは**まだ SQLite 側に入る**ため「SQL Server に保存されていない」と
> 誤認する（2026-08-08 の実施で誤認しかけた）。再起動してから件数突合を行う。
>
> 参考として**受信断の長さ**（製品自身の計測値。2026-08-02 は約 1.7 秒、2026-08-08 は 31.3 秒
> ——うちサービス停止に 30.3 秒。環境差が大きいので値そのものは判定に使わない）は記録しておくとよい。

**判定**: 上記 2 点の突合が成立すること。**「退避 / 削除」の処分は現行では実行されない**（[#502](https://github.com/Yanai-Taketo/Yagura/issues/502)）ため、旧 DB ファイルが残置されていても本項の不合格にはしない。

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
| A-4 で暗号スイートに差がある | 決定 1 の「差は 1 点」を修正し、暗号強度が OS 側のハードニングに依存する旨を追記する（**2026-08-02 に実施済み**——ADR-0024 改訂 7） |
| B で中核機能が不成立 | **選択肢 C（対応 = Server 2022 以上）への転換を supersession で検討**（ADR-0024 再評価トリガ） |
| E が通らない | Server 2019 を「試験的」へ落とすことを検討（ADR-0024 再評価トリガ） |
| C で Edge が存在した | **決定 3 と要件表を緩める方向で見直す**（失敗ではない） |

## 記録様式

conventions.md の実体検証記録の作法に従い、**実施した PR の body に残す**。

- 実施日・実施者
- ゲストの OS ビルド（`winver`）と Windows Update 適用状況
- 検証した Yagura のバージョンとビルド元コミット SHA
- §A の `Probe-YaguraTls.ps1` の出力（3 か所 + `Get-TlsCipherSuite` の全文）
- §C のブラウザ列挙結果
- §E の突合結果（送信数・保存数・各カウンタ）
- 判定と、上表のどの分岐に該当したか
- **手順どおりに進まなかった箇所をすべて**（環境要因・手順書の誤り・実施不能項目を区別して書く）

**「手順書どおりに進まなかった箇所」は付記ではなく必須項目である。** 2026-08-02 の実施では、この記載から手順書側の誤り 3 点（Probe の既定プロトコル・§E の SQLite 書込不能化・§F の「切替中の受信継続」）と、製品側の欠陥 4 件（[#500](https://github.com/Yanai-Taketo/Yagura/issues/500)〜[#503](https://github.com/Yanai-Taketo/Yagura/issues/503)）が判明した。**合否だけを報告すると、これらは失われる。**

## 実施記録

| 実施日 | 環境 | 対象 | 結果 |
|---|---|---|---|
| 2026-08-02 | Windows Server 2019 Standard Evaluation 10.0.17763.3650（Desktop Experience / Proxmox） | v0.5.0（`7f638da`） | **条件付き合格**。§A-1・§A-2・§B・§D・§E・§F・§G 合格。§A-3 実施不能・§A-4 未実施・§C 観測不能 → [ADR-0024 改訂 7](../../docs/adr/0024-supported-environments.md) に全文記録。残条件は本ファイル冒頭 |

## 参照

- [ADR-0024](../../docs/adr/0024-supported-environments.md) 決定 1・決定 3・決定 5・委任事項 1
- [ADR-0009](../../docs/adr/0009-architecture-support.md) 決定 6 Phase 1（ARM64 に課した負荷項目。本手順の §E はこれと同水準）
- [ADR-0016](../../docs/adr/0016-os-drop-gauge-mechanism.md)（OS レベル取りこぼしを runtime で観測しないことの恒久受容）
- [#482](https://github.com/Yanai-Taketo/Yagura/issues/482)（接続ハンドラの無音脱落。§A-1 の観測性はこの修正に依存する）
- [#494](https://github.com/Yanai-Taketo/Yagura/issues/494)（本手順の追跡 Issue）
