# Fluent Bit 配布キット

Windows 端末・サーバのイベントログを Fluent Bit(Apache-2.0)経由で Yagura へ
syslog(RFC 5424 / UDP・TCP(平文)・TLS(暗号化。syslog over TLS。RFC 5425)、
`install.ps1 -Mode` で選択)転送するための、サイレント導入可能な配布キット。
**`-Mode tls` は現時点の Fluent Bit(`out_syslog`)では Yagura へメッセージを配送できない
既知の非互換がある**(2026-07-11 実機検証。詳細は
[利用者ガイド](../../docs/guides/forward-windows-eventlog.md)の「TLS を使う」参照)。

**収集対象端末のアーキテクチャ**: Fluent Bit は Windows x64(`win64`)・ARM64(`winarm64`)の
MSI を公式提供している(ADR-0009 決定7)。`install.ps1` は導入先端末のアーキを自動判定し、
同じフォルダに置かれた win64 / ARM64 いずれの MSI からも該当するものだけを選ぶため、
両アーキ混在のフォルダをそのまま混在端末群への配布に使える。Windows x86(32bit)は
Yagura 本体・本キットとも対象外(ADR-0009 決定1)。詳細は
[Windows イベントログを Yagura へ転送する](../../docs/guides/forward-windows-eventlog.md)
の「対応アーキテクチャ」を参照。

| ファイル | 役割 |
|---|---|
| `install.ps1` | サイレント導入(MSI 無人導入 → 設定配置 → サービス登録・遅延自動起動 → 起動確認) |
| `uninstall.ps1` | 撤去(サービス削除 + 設定削除。`-RemoveFluentBit` で MSI も削除) |
| `fluent-bit-yagura.conf` | 転送設定テンプレート(導入時に宛先等を自動置換) |
| `winevt-severity.lua` | イベントログの Level → syslog severity 変換、Keywords(監査成功/失敗)の severity 反映、チャネル → facility 変換を行うフィルタ |

使い方・企業配布基盤(Intune / SCCM / GPO)での push 手順・検証済み環境は
**[Windows イベントログを Yagura へ転送する](../../docs/guides/forward-windows-eventlog.md)** を参照。

`README.generated.md` は本キットのファイルではなく、Yagura 管理 UI(`/admin/forwarder-kit`)が
宛先設定済みキットを生成する際に使う README テンプレート(ADR-0008)。
