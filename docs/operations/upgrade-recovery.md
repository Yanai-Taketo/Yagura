# アップグレードに失敗した / 製品が消えた・起動しないとき

[運用ガイドの目次へ戻る](../operations.md)

MSI のアップグレードが `1920`（サービスを開始できませんでした）や `1603`（インストール中に致命的なエラー）で失敗した場合、および再インストールしても `Access to the path ... is denied` で起動しない場合の復旧手順。

> **1.0 以降の MSI で更新する場合、アップグレードの失敗で製品が消えることはない**（0.5.0 から 1.0 への更新を含む。[ADR-0023](../adr/0023-upgrade-availability.md) 決定 2）。旧製品の削除が新しい MSI のインストールのトランザクション内に入ったため、失敗時は全体がロールバックされて**旧版がそのまま残り、動き続ける**。本節は、それより前の MSI での更新で被害に遭った場合、および何らかの理由で状態が壊れた場合の手当てである。通常のアップグレードの手順は[アップグレードとアンインストール](upgrade-and-uninstall.md)を参照。

## まず確認する

1. 「プログラムと機能」に Yagura が残っているか
2. サービス `Yagura` が存在し、動いているか（`Get-Service Yagura`）
3. Windows イベントログ（アプリケーション）に Yagura のエラーが出ていないか

サービスが動いていれば**受信は継続している**。慌てて再インストールする前に、まずここを見ること。

## 製品が 1 つも残っていない場合

`%ProgramData%\Yagura` 配下の設定・データベース・スプールは**アンインストールでは消えない**。新しい MSI をそのままインストールすれば、既存のデータを引き継いで復帰する。

## 再インストールしても `Access to the path ... is denied` で起動しない場合

アップグレードが途中で失敗した際に、データルート配下のファイルのアクセス権が壊れている可能性がある（[Issue #467](https://github.com/Yanai-Taketo/Yagura/issues/467) 現象 B）。1.0 以降の MSI ではインストーラがこれを自動で戻す・是正するが、それより前の版で壊れた状態はそのまま残る。

管理者権限の PowerShell で、次を**上から順にそのまま**実行する:

```powershell
$Root = "$env:ProgramData\Yagura"
$Fwd  = Join-Path $Root 'forwarder'
$Svc  = 'NT SERVICE\Yagura'   # gMSA 運用中なら 'DOMAIN\gmsa名$' に置き換える

icacls "$Root" /reset /T /C
icacls "$Root" /inheritancelevel:r /grant "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "${Svc}:(OI)(CI)(M)"
icacls "$Fwd"  /inheritancelevel:r /grant "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "${Svc}:(OI)(CI)(R)"
```

そのうえで MSI を再インストールする。

> **`icacls /reset` は「そのフォルダに個別に付けたアクセス権をすべて捨てる」操作である。** 上のブロックが 3 行で 1 セットなのはこのためで、**2 行目・3 行目まで実行しないと権限が正しく戻らない**。
>
> とりわけ 3 行目を飛ばすと、フォワーダ配布フォルダ（`forwarder`）がデータルートの権限をそのまま引き継ぎ、**サービスアカウントに書き込み権限が付いたままになる**。ここは「管理者だけが書ける」設計であり（[ADR-0008](../adr/0008-forwarder-kit-generation.md) 設計条件 9）、`/reset` はその区別を壊す。
>
> 監査目的で独自の ACE を追加している場合、それも `/reset` で消える。実行前に `icacls "$env:ProgramData\Yagura" /save acl-backup.txt /T` で控えておくとよい。

## それでも起動しない場合

DB に接続できないことが原因かもしれない。その場合は[データベースに接続できないとき（縮退運転）](degraded-operation.md)を参照すること——1.0 以降は DB に接続できなくても起動して受信を続けるため、「起動しない」なら別の原因である。イベントログの Yagura のエラーを確認し、[Issues](https://github.com/Yanai-Taketo/Yagura/issues) へ報告してほしい。
