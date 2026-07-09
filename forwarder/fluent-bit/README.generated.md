<!-- このファイルは管理 UI（/admin/forwarder-kit）が生成するキットの README テンプレートです。
     @@YAGURA_HOST@@ / @@YAGURA_PORT@@ / @@CHANNELS@@ / @@GENERATED_AT@@ / @@FLUENTBIT_VERSION@@ /
     @@YAGURA_VERSION@@ / @@MSI_SECTION@@ は Yagura.Web.ForwarderKit.ForwarderKitBuilder が
     生成時に置換します（@@MSI_SECTION@@ は MSI 同梱時 / 非同梱時で内容を出し分けます
     ——ADR-0008 設計条件 9）。
     手動配布用の静的キットの説明は forwarder/fluent-bit/README.md を参照してください。 -->
# Yagura フォワーダキット（宛先設定済み）

このキットは、生成時点の Yagura サーバの宛先を設定済みの Fluent Bit 配布キットです
（[ADR-0008](../../docs/adr/0008-forwarder-kit-generation.md)）。**`install.ps1` はパラメータなしで実行できます。**

- 宛先: `@@YAGURA_HOST@@:@@YAGURA_PORT@@`（syslog / UDP）
- 収集チャネル: `@@CHANNELS@@`
- 生成日時: `@@GENERATED_AT@@`
- 検証済み Fluent Bit 版: **@@FLUENTBIT_VERSION@@**
- 生成元 Yagura バージョン: `@@YAGURA_VERSION@@`

生成時点の値は同梱の `GENERATED.txt` にも記録されています。

## キットの内容

| ファイル | 役割 |
|---|---|
| `install.ps1` | サイレント導入(MSI 無人導入 → 設定配置 → サービス登録・遅延自動起動 → 起動確認)。**このキットでは宛先が設定済みのため引数不要** |
| `uninstall.ps1` | 撤去(サービス削除 + 設定削除。`-RemoveFluentBit` で MSI も削除) |
| `fluent-bit-yagura.conf` | 転送設定(宛先・ポート・チャネルは生成時に置換済み) |
| `winevt-severity.lua` | イベントログの Level → syslog severity 変換、Keywords(監査成功/失敗)の severity 反映、チャネル → facility 変換を行うフィルタ |
| `GENERATED.txt` | 生成時のメタデータ(生成日時・宛先・チャネル・版の来歴) |

## 導入手順

@@MSI_SECTION@@

```powershell
powershell -NoProfile -File .\install.ps1
```

`-YaguraHost` 等のパラメータは不要です(このキットには宛先が既に設定されています)。
導入が成功すると標準出力に `INSTALL_SUCCESS` が出て終了コード 0 で終わります。

## 運用上の注意

- **MSI を上書き更新した場合は `install.ps1` を再実行してください。** Fluent Bit の MSI は
  更新時にサービス定義を既定に戻すことがあるため、Yagura 向けの設定を指すサービス定義に
  戻す必要があります(`install.ps1` は再実行に対して安全です)
- **Yagura サーバの宛先(IP・ホスト名・ポート)を変更した場合は、管理 UI でキットを
  再生成し、配布し直してください。** このキットは生成時点の宛先を焼き込んだものであり、
  自動で追従しません
- Security チャネルを収集する場合は、機微情報を含み量も多いため、組織のポリシーで
  明示的に判断してから有効化してください(有効化は管理 UI 側で行います)

## 企業配布基盤での push

- **Intune (Win32 アプリ)**: キット一式 + MSI を `.intunewin` 化し、インストールコマンドを
  `powershell -NoProfile -File install.ps1`、アンインストールコマンドを
  `powershell -NoProfile -File uninstall.ps1 -RemoveFluentBit` にする。検出規則はサービス
  `fluent-bit` の存在または `C:\ProgramData\fluent-bit-yagura\fluent-bit-yagura.conf`
- **SCCM**: パッケージ/アプリケーションとして同じコマンドラインを指定する
- **GPO**: コンピューターのスタートアップスクリプトとして割り当てる(SYSTEM 実行のため
  管理者権限要件を満たす)

より詳しい手順(動作確認・設定の内容・検証済み環境)は、Yagura サーバのドキュメント
[Windows イベントログを Yagura へ転送する](../../docs/guides/forward-windows-eventlog.md)
を参照してください。
