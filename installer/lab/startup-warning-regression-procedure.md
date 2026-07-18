# 起動時警告の回帰検証手順 — SEC-9-a / SEC-13

> **未実施**（2026-07-19 作成）。本手順は #346（SEC-9-a）と #345（SEC-13）の修正が正しく効いていることを実機で確認するためのもの。
> 実施したら結果を security.md の該当節へ記録し、本ファイル冒頭のこの注記を「実施済み（日付・環境）」へ書き換える。

Issue #356 / #346 / #345 の lab 検証手順。
**実施環境**: Yagura サービスがインストール済みの lab（仮想サービスアカウント `NT SERVICE\Yagura` が存在すること）。SEC-9-a はドメイン参加環境が必要。

## なぜ CI ではなく lab なのか

両件とも **CI では回帰を検出できない**ことが実装時に判明した（オーナー裁定 2026-07-19 により lab 手順として文書化）。

| 件 | CI で検出できない理由 |
|---|---|
| SEC-9-a（#346） | 既存の E2E ハーネスはホストを別プロセスで起動して **stdout を監視**する方式。しかし修正前も bootstrap ロガーが**コンソールへは出力していた**ため、stdout では修正前後の差が出ない。実際の欠陥は「**Windows イベントログに到達しない**」ことであり、CI で再現するにはイベントソース登録と管理者権限が要る |
| SEC-13（#345） | 既存の `AdminRemoteBindingRegressionTests` が修正前から通っているのは、**テストが管理者権限のプロセスで走るため秘密鍵を開けてしまう**ため。同じ理由で新しいテストも修正前から通ってしまい、回帰テストとして機能しない |

いずれも「**テストプロセスの権限・ロギング経路が本番と違う**」ことが原因である。この非対称自体が、両 Issue が実機検証でしか見つからなかった理由でもある。

## 実施の原則: 修正前後の両方で実施する

手順の妥当性は「**修正前に実施すると失敗し、修正後は成功する**」ことで担保される。可能なら修正前のビルド（`main` の該当 PR マージ前）でも実施し、両方の結果を記録すること。修正後だけを見ても「元々通っていた手順」と区別できない。

---

## A. SEC-9-a — 解決できない AD グループ指定の警告が運用者に届くこと

**対応する修正**: PR #354（`WindowsGroupAuthorizationOptions` の解決を DI の `ILoggerFactory` 経由へ変更し、`app.Build()` 直後に解決を固定）

### 検証する仮説

存在しないグループ名を設定したとき、`[sec9-group-unresolved]` の警告が **Windows イベントログ（Yagura プロバイダ）へ到達する**こと。修正前はコンソールにしか出ず、イベントログには 0 件だった（2026-07-18 実測）。

### 手順

#### 1. 設定

`%ProgramData%\Yagura\yagura.json` を編集し、**実在するグループと存在しないグループを併記**する。

```json
{
  "Viewer": {
    "Authentication": {
      "Windows": {
        "Enabled": "true",
        "ViewerGroups": ["YAGURA\\YaguraViewers", "YAGURA\\NoSuchGroup12345"]
      }
    }
  }
}
```

> 実在する側を併記するのは、**認可自体が壊れていないこと**（下記手順 4）を同時に確認するため。存在しない指定だけにすると「全部落ちた」のか「警告だけ出た」のか切り分けられない。

#### 2. サービス再起動

```powershell
Restart-Service Yagura
```

#### 3. イベントログの確認（本手順の核心）

```powershell
Get-WinEvent -ProviderName Yagura -MaxEvents 400 |
  Where-Object { $_.Message -like "*sec9-group-unresolved*" } |
  Format-List TimeCreated, Id, LevelDisplayName, Message
```

**期待結果**: `NoSuchGroup12345` を指す警告が **1 件以上**記録されている。

**修正前はここが 0 件になる**（2026-07-18 実測。Yagura プロバイダ 400 件を走査して 0 件だった）。対照として、他の起動時警告（`Yagura.Host.Administration.Https` / `...ViewerAuth` / `...Firewall.FirewallStartupInspector` / `...Observability.Auditing`）は修正前から到達しているので、それらが見えるのに sec9 だけ無い状態が退行の徴候である。

#### 4. 認可が壊れていないことの確認

実在する `YaguraViewers` の所属者（**ネストグループ経由の所属者を含める**）で閲覧 UI にサインインし、「閲覧」役割が付与されることを確認する。存在しないグループ側は誰にも権限を与えない（fail-closed）。

---

## B. SEC-13 — 秘密鍵へアクセスできなくてもサービスが起動すること

**対応する修正**: PR #357（`ResolveCngKeyFilePath` の `CryptographicException` を捕捉し、既存の警告経路へ落とす）

### 検証する仮説

サービスアカウントが証明書の秘密鍵にアクセスできない状態でも、**サービスが起動し**、ACL 付与失敗の**警告が出る**こと（security.md §2.5 の設計どおり）。修正前は警告に到達する前に未処理例外でプロセスが落ちていた。

### 手順

#### 1. 証明書の作成

```powershell
$cert = New-SelfSignedCertificate -DnsName "yagura-lab.local" `
  -CertStoreLocation "Cert:\LocalMachine\My" `
  -KeyExportPolicy Exportable
$cert.Thumbprint
```

> 既定で **CNG ソフトウェア KSP** の鍵になる。鍵ファイルの既定 ACL は
> `CREATOR OWNER:(F)` / `NT AUTHORITY\SYSTEM:(F)` / `BUILTIN\Administrators:(F)` で、
> **サービスアカウントの ACE は無い**——これが本検証で必要な「アクセスできない状態」である。
> 特別な細工は要らず、**作りっぱなしがそのまま検証対象の状態**になる。

#### 2. 鍵ファイルの ACL を記録（ベースライン）

```powershell
$keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys"
$keyFile = Get-ChildItem $keyPath | Sort-Object LastWriteTime | Select-Object -Last 1
icacls $keyFile.FullName
```

`NT SERVICE\Yagura` の ACE が**無い**ことを確認する。

#### 3. 管理リモート HTTPS を有効化して再起動

`yagura.json`:

```json
{
  "Admin": {
    "Https": { "Enabled": "true", "CertificateThumbprint": "<手順 1 の拇印>" },
    "RemoteBinding": { "Enabled": "true" }
  }
}
```

```powershell
Restart-Service Yagura
```

#### 4. 起動と警告の確認（本手順の核心）

```powershell
Get-Service Yagura | Format-List Status, StartType
Get-WinEvent -ProviderName Yagura -MaxEvents 200 |
  Where-Object { $_.Message -like "*private-key-grant-failed*" } |
  Format-List TimeCreated, Id, LevelDisplayName, Message
```

**期待結果**:

- サービスが **Running** である（SCM に 7000 / 7009「時間内に応答しない」が記録されない）
- `[admin-https-private-key-grant-failed]` の警告が記録され、`certlm.msc` からの手動付与を案内している

**修正前はサービスが起動せず**、`System.Security.Cryptography.CryptographicException: キー セットがありません` で落ちる（2026-07-18 実測）。

#### 5. TLS 受信側でも同じ確認を行う（#345 の調査で判明した追加項目）

`TryGrantReadAccess` は `Program.cs` から **2 箇所**（管理リモート HTTPS と **TLS 受信**）で呼ばれており、どちらも同じ経路で落ちていた。Issue #345 の本文は前者しか記載していないため、**TLS 受信側は未検証のまま**である。

```json
{
  "Ingestion": {
    "Tls": { "Enabled": "true", "CertificateThumbprint": "<手順 1 の拇印>" }
  }
}
```

再起動し、手順 4 と同様に**サービスが Running** であることと、`[ingestion-tls-private-key-grant-failed]` の警告を確認する。

#### 6. 付随確認: 権限を与えれば TLS ハンドシェイクが成立すること

```powershell
icacls $keyFile.FullName /grant "NT SERVICE\Yagura:(R)"
Restart-Service Yagura
curl.exe -k -o NUL -w "%{http_code} %{ssl_verify_result}\n" https://localhost:8516/admin
```

**期待結果**: `302 0`（2026-07-18 実測で確認済みの挙動）。

> なお**この状態でも自動付与自体は失敗する**（サービスアカウントは鍵ファイルの ACL を書き換える権限 `WRITE_DAC` を持たないため）。付与が失敗しても TLS が成立するのは、付与とは独立に「読める」状態になっているためである。

---

## 既知の論点（本手順では判定しない）

**監査 2009（`AdminHttpsCertificatePrivateKeyAccessGranted`）は手順 4・5 のいずれでも記録されない。** 2009 は付与に**成功したときのみ**発火するが、既定の鍵 ACL ではサービスアカウントに `WRITE_DAC` が無いため付与は基本的に成功しない。

「付与は監査対象」（ADR-0010 Phase 2 決定 4）が実務上ほぼ発火しない契約になっていないかは**設計上の論点**であり、本手順の合否判定には含めない（#345 に記載のとおり別途検討）。

## 参照

- #356（本手順書の追跡）/ #346（SEC-9-a）/ #345（SEC-13）
- PR #354（SEC-9-a の修正）/ PR #357（SEC-13 の修正）
- PR #340（2026-07-18 の lab 実機検証。両件の元となった実測結果）
- [security.md](../../docs/design/security.md) §2.5（秘密鍵権限付与）・§7（SEC-9 / SEC-13）
- [sec-3-audit-acl-procedure.md](sec-3-audit-acl-procedure.md)（体裁の先例）
