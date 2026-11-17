# Yagura lab 第 4 回検証 報告

## 1. 実施日・実施者

- **実施日**: 2026-08-16
- **実施者**: Claude Code（lab 検証機 `WIN-DKF5O8U26MJ` 上で実施）

## 2. ビルド元と開始前確認

- **ビルド元コミット SHA**: `220f23a44460b2a3f940e2ab2f4c613678f69920`
  （`feat: 診断用計器を状態画面へ出し、昇格の接続検証で照合順序を確認する (#509 #515) (#517)`）
- ビルド SDK: .NET 10.0.302 / WixToolset.Sdk 7.0.0

### 開始前確認 3 点 — **すべて入っている**（A〜D すべて実施可）

| # | 確認内容 | 結果 | 根拠 |
|---|---|---|---|
| ① | MSI の LaunchCondition に `YAGURA_OS_BUILD` 行 | **あり** | 下記 2 行を実 MSI の LaunchCondition テーブルから取得 |
| ② | `/status` に「診断用の計器（再起動でリセットされます）」 | **あり** | 実画面で見出しと計器 2 件を確認 |
| ③ | 接続検証が `SELECT 1` だけでない | **あり** | `SqlServerConnectionValidator.cs:50-66` で `sys.fn_helpcollations()` 照会 |

①の実測（MSI から直接照会）:

```
NOT YAGURA_OS_BUILD OR YAGURA_OS_BUILD >= 17763 OR REMOVE~="ALL"
NOT WIX_DOWNGRADE_DETECTED
```

AppSearch / RegLocator も期待どおり:

```
YAGURA_OS_BUILD || YaguraOsBuildSearch
YaguraOsBuildSearch || 2 || SOFTWARE\Microsoft\Windows NT\CurrentVersion || CurrentBuildNumber || 18
```

## 3. 2 版の MSI — 版数と収録アセンブリの一致

`Directory.Build.props` の `<YaguraVersion>` を実際に書き換え、版ごとに
`installer/bin`・`installer/obj`・`installer/publish`・`src/**/bin,obj` を削除してビルド。

| | MSI ProductVersion | 収録 `Yagura.Host.exe` FileVersion | サイズ | SHA256 |
|---|---|---|---|---|
| 版 1 | **0.9.1** | **0.9.1.0** | 51,624,762 | `891982E2…1427B` |
| 版 2 | **0.9.2** | **0.9.2.0** | 51,620,666 | `F26E35AF…6618` |

- **一致を確認**。ProductVersion には SHA も埋まる（`0.9.1+220f23a…`）ため、ビルド元も追跡可能
- UpgradeCode 共通 `{2BF11897-…}` / ProductCode は別（`{B81135AF-…}` → `{DB065297-…}`）
- 参考: 前回の 2 本（`Yagura-0.5.0-x64.msi` / `Yagura-0.5.1-x64.msi`）は**サイズが 51620214 で完全一致**しており、#514 の症状を裏づけている

> なお `installer/Yagura.Installer.wixproj:128` には既に #514 の修正
> （publish へ `-p:YaguraVersion=$(YaguraVersion)` を渡す）が入っている。本 SHA では
> `-p:` 経由でも中身が上がるが、指示どおり `Directory.Build.props` 書き換えで実施した。

## 4. OS ビルド番号の実値

| | 機器 | OS | CurrentBuildNumber |
|---|---|---|---|
| ゲスト B | `WIN-DKF5O8U26MJ` (10.0.0.172) | Windows Server 2019 Standard Evaluation | **17763** |
| ゲスト A | `WIN-IVQ782H2OA4` (10.0.0.142) | Windows Server 2016 Standard Evaluation (10.0.14393, 64bit) | **14393** |

ゲスト A はレジストリ実測で 14393。**17763 未満であり A-1 の対象として妥当**。

## 5. 項目 A — MSI の対応 OS 下限

### A-1（対応外 OS で拒否されること）: **合格**

ゲスト A へ MSI を転送（SHA256 `F26E35AF…6618` を転送後に照合、一致）してから実施。

| 経路 | 終了コード | 結果 |
|---|---|---|
| サイレント `/qn` | **1603** | **拒否**（成功しない） |
| 基本 UI `/qb`（UI シーケンスも評価） | **1603** | **拒否** |

**拒否メッセージ（全文。両経路とも同一）**:

> 製品: Yagura -- この OS には Yagura をインストールできません(OS ビルド **14393**)。対応は OS ビルド **17763** 以上(Windows Server 2019 以降 / Windows 11 / Windows 10 LTSC・Enterprise)、推奨は Windows Server 2022 以降です。詳細と移行の相談先は README の「システム要件」を参照してください。

**検出したビルド番号（14393）と対応下限（17763）の両方が文面に出ている → 判定表の「合格」**。

`reject.log` の `YAGURA_OS_BUILD` 行:

```
MSI (s) (E4:7C) [16:40:01:295]: PROPERTY CHANGE: Adding YAGURA_OS_BUILD property. Its value is '14393'.
Property(S): YAGURA_OS_BUILD = 14393
操作終了 16:40:01: LaunchConditions。 戻り値 3。
```

インストールされていないことも確認:

```
Program Files\Yagura exe : 存在しない（期待どおり）
サービス Yagura          : 存在しない（期待どおり）
ARP エントリ             : 無し（期待どおり）
ProgramData\Yagura       : 無し（期待どおり）
```

#### 副産物: ADR-0024 の判断が第 3 の OS で裏づけられた

同じ `reject.log` から、Server 2016 実機での各プロパティの実測値:

```
Property(S): VersionNT     = 603
Property(S): VersionNT64   = 603
Property(S): WindowsBuild  = 9600
Property(S): YAGURA_OS_BUILD = 14393
```

ADR-0024 は windows-2022 / windows-2025 の 2 ランナーでしか実測していなかったが、
**Server 2016 でも `VersionNT`=603 / `WindowsBuild`=9600 に張り付く**ことが確認された。
`WindowsBuild >= 17763` を使っていた場合、対応外 OS の判定としては「たまたま」拒否できるが、
**対応環境も同じ 9600 で拒否される**ため全環境が更新不能になる——ADR-0024 決定 6 の
「レジストリの実値を直接読む」判断は正しい。レジストリ読みは 14393 と 17763 を正しく区別している。

### A-2（対応環境で通ること）: **合格**

```
exit code: 0
MSI (s) (9C:24): PROPERTY CHANGE: Adding YAGURA_OS_BUILD property. Its value is '17763'.
Property(S): YAGURA_OS_BUILD = 17763
```

- **`YAGURA_OS_BUILD = 17763` を実値で確認**。レジストリ読みが効いており、fail-open で通ったのではない
- インストール後: `Yagura.Host.exe` = 0.9.1.0 / ARP = 0.9.1 / サービス Running

### A-3（アンインストールできること）: **合格**（ただし手順書のコマンドは誤り）

- 手順書どおりの `msiexec /x Yagura-0.9.1-x64.msi /qn` → **exit 1605**（ERROR_UNKNOWN_PRODUCT）。
  B のアップグレードで ProductCode が `{DB065297-…}` に変わっているため、0.9.1 の MSI では消せない
- 実際に入っている 0.9.2 で実行 → **exit 0**。exe・サービス・ARP エントリすべて削除を確認
- 削除シーケンスでの起動条件評価も確認:

```
Command Line: REMOVE=ALL …
PROPERTY CHANGE: Adding YAGURA_OS_BUILD property. Its value is '17763'.
Doing action: LaunchConditions
操作終了 16:14:49: LaunchConditions。 戻り値 1。
```

## 6. 項目 B — アップグレードでバイナリが置換されること: **合格**

| | `Yagura.Host.exe` FileVersion | SHA256 |
|---|---|---|
| アップグレード前 | **0.9.1.0** | `0E6EF069…FFDEB` |
| アップグレード後 | **0.9.2.0** | `A22441F0…2B675` |

- `msiexec /i Yagura-0.9.2-x64.msi /qn` → exit 0
- サービス: **Running**（StartType Automatic）
- ARP は **0.9.2 の 1 エントリのみ**（旧エントリ残存なし）

`upgrade.log` の該当行 — **"Existing file is of an equal version" ではなく "lower version" で Overwrite**:

```
File: C:\Program Files\Yagura\Yagura.Host.exe;	Overwrite;	Won't patch;	Existing file is a lower version
File: C:\Program Files\Yagura\Yagura.Host.dll;	Overwrite;	Won't patch;	Existing file is a lower version
File: C:\Program Files\Yagura\Yagura.Web.dll;	Overwrite;	Won't patch;	Existing file is a lower version
File: C:\Program Files\Yagura\Yagura.Storage.dll;	Overwrite;	Won't patch;	Existing file is a lower version
File: C:\Program Files\Yagura\Yagura.Ingestion.dll;	Overwrite;	Won't patch;	Existing file is a lower version
File: C:\Program Files\Yagura\Yagura.Abstractions.dll;	Overwrite;	Won't patch;	Existing file is a lower version
```

インストール後の実ファイルも全 5 アセンブリが **0.9.2.0**。

`YAGURA_OS_BUILD` 行:

```
Property(N): YAGURA_OS_BUILD = 17763
Property(S): YAGURA_OS_BUILD = 17763
```

> 補足: サードパーティ依存 DLL（`Microsoft.AspNetCore.*` 等）は
> "Existing file is of an equal version" で据え置かれるが、これは 0.9.1 と 0.9.2 が
> 同一ソースで依存も同一のため**正常**。Yagura 自身のアセンブリはすべて置換されている。

**→ #479 / #514 は本検証で閉じられる見込み。**

## 7. 項目 C — 診断用計器の表示: **合格**

- `/status` に **「診断用の計器（再起動でリセットされます）」区画あり**
- 計器 2 件。ラベルは平易語で登録済み（「（対応表未登録の項目）」表示なし）

| 局面 | `yagura.ingestion.tcp.tls_handshake_failure` |
|---|---|
| 初期 | 0 |
| TLS 1.1 で 5 回失敗後 | **5** |
| サービス再起動後 | **0**（見出しどおりリセット） |

ラベル: `TLS の接続確立に失敗した回数（送信側との不一致）`

イベントログにも 5 件出た（設定画面の案内どおり、計器とイベントログの両方に出る）:

```
Category: Yagura.Ingestion.Tls.TlsSyslogListener
TLS 接続 127.0.0.1:49892 のハンドシェイクに失敗しました。
… Win32Exception (0x80090331): クライアントとサーバーは共通のアルゴリズムを処理していないので、通信できません。
```

> 実施上の注記: 起動時に `[firewall-rule-mismatch]`（TCP 6514 の受信許可規則なし）が出ていたため、
> ファイアウォールの影響を避けて **localhost から** プローブした。

## 8. 項目 D — 昇格の接続検証: **合格（回帰なし）**

- 対象: SQL Server 2019 Express (15.0.2000.5) / インスタンス `SQLEXPRESS`
- 入力: サーバ名 `localhost\SQLEXPRESS` / DB 名 `YaguraLab2` / Windows 統合認証 / サーバ証明書を信頼する
- 結果: **成功**。文面は以下（全文）:

> **SQL Server への接続を確認しました。**

照合順序チェックが実質的に効いていることの傍証:
- 必要な照合順序 `Latin1_General_100_CI_AS_KS_WS_SC` は当該インスタンスに**実在**（`sys.fn_helpcollations()` で 1 件）
- ただしサーバ既定の照合順序は `Japanese_CI_AS` であり、両者は別物。つまり「たまたま既定と一致して通った」のではない

> 指示どおり異常系（照合順序が無いインスタンス）は作っていない。

## 9. 「併せて確認」①〜⑤

| # | 確認点 | 結果 |
|---|---|---|
| ① | 証明書選択の候補一覧が 3 画面で表示されるか | **修正されている（合格）** |
| ② | `icacls` 案内が PowerShell でそのまま通るか | **修正されている（合格）** |
| ③ | 権限がある状態で ACL 付与の警告が出なくなったか | **未修正（不合格）** — 下記詳述 |
| ④ | 昇格切替後に「再起動待ちの変更」が出るか | **未観測**（昇格の切替は実施しなかったため。項目 10 参照） |
| ⑤ | 昇格直後の `[retention-catchup-query-failed]` が消えたか | **未観測**（同上） |

### ① 証明書選択 — 修正確認

3 画面すべてで候補が表示され、`証明書ストアの一覧を取得できませんでした` は出ない。

- `/admin/ingestion-tls`（TLS 受信）: `WIN-DKF5O8U26MJ` / 使用可能 / 発行者・有効期間・拇印を表示
- `/admin/remote-access`（管理リモート HTTPS）: 同上 / 使用可能
- `/admin/viewer-https`（閲覧 HTTPS）: 同上 / 選択可 + SAN 検査結果も表示

### ② `icacls` 案内 — 修正確認（実コマンドで両形を比較）

現行コード（`CertificatePrivateKeyAccessGranter.cs:158`）が出す形と、前回の形を PowerShell で実行:

```
--- 新（現行）: icacls "<key>" /grant "NT SERVICE\Yagura:R"
processed file: C:\ProgramData\Microsoft\Crypto\Keys\1c0ed733…
Successfully processed 1 files; Failed processing 0 files
exit code: 0

--- 旧: icacls "<key>" /grant "NT SERVICE\Yagura":R
Invalid parameter "NT SERVICE\Yagura"
exit code: 87
```

**前回症状（exit 87）を再現し、現行形が exit 0 で通ることを確認。**

### ③ ACL 付与の警告 — **未修正。原因を特定した**

**観測**: サービス起動のたびに、3 リスナすべてで次の警告が出る（**理由が `(null)`**）。

```
[ingestion-tls-private-key-grant-failed] TLS 受信証明書の秘密鍵読み取り権限を
NT SERVICE\Yagura へ自動付与できませんでした（理由: (null)）。…
[admin-https-private-key-grant-failed]  …（理由: (null)）。…
[viewer-https-private-key-grant-failed] …（理由: (null)）。…
```

一方、**権限は実際に付いている**（TLS 6514・管理 HTTPS 8516 とも TLS 1.2 で正常にハンドシェイクする）:

```
NT SERVICE\Yagura      Read, Synchronize      Allow
```

**原因**（コード確認で確定。`Program.cs` の 3 箇所 **1411 / 1465 / 1520** が同一パターン）:

```csharp
if (grantResult.Succeeded && !grantResult.WasAlreadyGranted)
{
    // 監査記録
}
else
{
    // ← 「自動付与できませんでした」警告
}
```

`AlreadyGranted` は `Succeeded = true, WasAlreadyGranted = true, FailureReason = null`
（`CertificatePrivateKeyAccessGranter.cs:264-265`）。
したがって**「既に権限がある」= #511 が黙らせたかった当の経路が、そのまま else に落ちて警告を出す**。
`FailureReason` が null なので理由が `(null)` と表示される。

`if/else` の分岐が「監査を残すか」の判定になっており、「警告を出すか」の判定に流用されているのが誤り。
`else if (!grantResult.Succeeded)` にする（= AlreadyGranted では何もしない）のが素直な修正と思われる。

## 10. 手順どおりに進まなかった箇所

### (a) 環境要因 — ゲスト A の準備と接続に時間を要した（最終的に A-1 は実施できた）

- 依頼時点でゲスト A は未用意で、lab 機側には仮想化基盤が無かった
  （QEMU ゲスト・メモリ 8GB・空き 45GB、Hyper-V 未導入、ISO/VHD なし、
   VBoxManage / vmrun / docker / wsl いずれも不在）ため、当初は「準備できず」の見込みだった
- 検証中に 10.0.0.142（Server 2016）を提供いただいた
- 当初共有された資格情報では WinRM 認証が通らず、
  `WSManFault Code 5 / 0x80070005 Access is denied` が返り続けた。
  `Administrator` / `10.0.0.142\Administrator` / `.\Administrator` の 3 形、
  `Test-WSMan -Credential`・`winrm identify` のいずれでも同一だったため、
  PowerShell エンドポイントの ACL ではなく **WSMan 層の認証失敗**と切り分けた。
  正しい資格情報の提供を受けて解決
- **GUI インストールは目視では未確認**。WinRM 経由には対話デスクトップが無いため、
  代わりに `/qb`（基本 UI。**UI シーケンスの LaunchConditions も評価される**）で拒否を確認し、
  ダイアログに出るのと同一の文面をログから全文取得した。
  「利用者の目に見える形」の最終確認だけは RDP での目視が残っている
- lab 機（クライアント）側で `TrustedHosts = 10.0.0.142` を設定した（この検証のため。元は空）。
  不要であれば戻す
- ゲスト A 側の後始末: 転送した MSI は削除済み。`C:\a1\reject.log` / `reject-ui.log` は証跡として残置

### (b) 手順書の誤り — A-3 のコマンド

`msiexec /x Yagura-0.9.1-x64.msi /qn` は **B のアップグレード後には成立しない**（exit 1605）。
B で ProductCode が変わるため。手順書は 0.9.2 の MSI（または ProductCode 指定）にすべき。

### (c) 実施しなかった判断 — ④⑤

④⑤ は「昇格の**切替を実行**した後」でないと観測できないが、項目 D の指示は
「接続を検証する」までであり、切替の実行は求められていない。
また本機の保存先は既に SQL Server であり、ここでの切替は人為的な再昇格になる。
**構成を変える判断を lab 側で勝手に行わない**ため実施せず、未観測として報告する。
必要であれば次回、明示指示をいただければ実施する。

### (d) 副次的な観測（依頼範囲外だが記録）

1. **`[firewall-rule-mismatch]`**: TLS 受信（TCP 6514）に対応する受信許可規則が無い状態で起動する。
   警告文自身が「この状態では受信がファイアウォールで破棄され、Yagura のカウンタにも現れません」
   と述べており、TLS 受信を有効化しただけでは外部からの TLS syslog が届かない可能性がある
2. **アンインストール前の状態が #514 の生き証拠だった**: 検証開始時、ARP の DisplayVersion は
   `0.5.1` だが実バイナリは `0.5.0.0` だった
3. `/status` を PowerShell で取得する場合、応答は HTML エンティティ符号化されているため
   `HtmlDecode` が必要（検証手順書に載せる価値があるかもしれない）

## 付録: 成果物の所在（lab 機 `WIN-DKF5O8U26MJ`）

| ファイル | パス |
|---|---|
| MSI 版 1 | `C:\build\msi\Yagura-0.9.1-x64.msi` |
| MSI 版 2 | `C:\build\msi\Yagura-0.9.2-x64.msi` |
| 新規インストールログ (A-2) | `C:\build\install.log` |
| アップグレードログ (B) | `C:\build\upgrade.log` |
| アンインストールログ (A-3) | `C:\build\uninstall.log` / `C:\build\uninstall-091.log` |
| ビルドログ | `C:\build\build-0.9.1.log` / `C:\build\build-0.9.2.log` |
| ProgramData バックアップ | `C:\build\backup-programdata-yagura-20260816-preflight` |
| A-1 拒否ログ（ゲスト A から回収） | `C:\build\guestA\reject.log` / `C:\build\guestA\reject-ui.log` |

ゲスト A（`WIN-IVQ782H2OA4` / 10.0.0.142）側にも `C:\a1\reject.log` / `reject-ui.log` を残置。
