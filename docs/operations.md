# 運用ガイド（利用者向け）

現在形の設計書（[design/](design/)）が「どう作るか」を定めるのに対し、運用ガイドは**導入・運用する人向け**の手順・注意をまとめる。本書はその目次であり、各トピックの本文は [operations/](operations/) 配下の文書に置く。ここに無い運用トピック（本番昇格の手順、逆引き参照ゾーンの整備、TLS 受信の証明書運用など）は各機能の実装に合わせて追記していく。

> **申し送りの受け皿**: 本書は複数の ADR が「利用者向けドキュメント（operations.md 系）へ申し送る」と定めた事項の受け皿である。認証に関する申し送りは [ADR-0010](adr/0010-admin-ui-authentication.md) 委任事項 11・[ADR-0013](adr/0013-admin-winauth-session.md) 委任事項 8。

## 目次

| 文書 | 扱う内容 |
|---|---|
| [監視と警告への対処（イベント ID 一覧）](operations/monitoring.md) | 監視の組み方（イベントログ・サービスの死活・メール通知）、状態画面とカウンタの読み方、運用警告（1000 番台）の一覧と警告ごとの対処、監査・拒否のイベント ID |
| [認証（管理 UI / 閲覧 UI）の運用](operations/authentication.md) | 認証の既定（オフ）、閲覧 UI・管理 UI の認証の有効化と AD グループ、セッションの寿命、緊急全失効、ブレークグラス、NTLM 縮退、認証まわりの監査イベント |
| [閲覧 UI の HTTPS（8514）を有効化する](operations/viewer-https.md) | 環境別の証明書の入手指針、自己署名 + GPO ルート配布の手順、検証、撤去・棚卸し、更新、証明書事故のとき、外形監視の見直し |
| [データベースに接続できないとき（縮退運転）](operations/degraded-operation.md) | 保存先に接続できない間に動くもの・止まるもの、無損失でいられる時間の上限（スプールの容量）、管理画面に入れなくなる構成と復旧 |
| [アップグレードとアンインストール](operations/upgrade-and-uninstall.md) | 通常のアップグレード手順（0.5.0 から 1.0 への更新を含む）、事前のデータの退避、0.4.0 以前・OS ビルド 17763 未満・gMSA 構成での注意、更新後の確認、アンインストールで消えるもの・残るもの |
| [アップグレードに失敗した / 製品が消えた・起動しないとき](operations/upgrade-recovery.md) | MSI のアップグレード失敗（`1920` / `1603`）や、データルートのアクセス権が壊れて起動しない状態からの復旧手順 |
| [対応環境を維持する](operations/supported-environments.md) | 閉域環境でのブラウザ更新、対応下限の環境の扱い、サポート期限、対応外の OS、Windows Server 2019 の送信元から TLS 受信へ送るとき |
| [昇格後に旧データベースファイルを処分する](operations/promotion-old-db.md) | SQLite から SQL Server へ本番昇格した後の、旧ファイルの退避・削除 |
| [ファイアウォール規則を自分で足す](operations/firewall.md) | TLS 受信・管理画面のリモートアクセスの受信許可規則の追加・削除と、起動時の規則の突合の警告（イベント ID 1023） |
| [バックアップ・復旧・サーバの移設と交換](operations/backup-and-migration.md) | バックアップの対象と置き場所・アクセス権、サービス停止の要否、暗号化された設定値（DPAPI）がサーバに束縛されることと入れ直し、同じサーバへの復旧、別のサーバへの移設、SQL Server の交換 |

## 節番号での参照の読み替え

文書・リリースノート・製品の警告文などで「運用ガイド §14」のように節番号で参照している箇所は、次の表で対応する文書の見出しへ読み替える。

| 節番号 | 対応する見出し | 文書 |
|---|---|---|
| §1 | [認証は既定でオフ（信頼ネットワーク前提 + opt-in 強化）](operations/authentication.md#認証は既定でオフ信頼ネットワーク前提--opt-in-強化) | authentication.md |
| §2 | [閲覧 UI 認証（8514）を有効化する](operations/authentication.md#閲覧-ui-認証8514を有効化する) | authentication.md |
| §3 | [管理 UI 認証と AD グループ（SEC-9）](operations/authentication.md#管理-ui-認証と-ad-グループsec-9) | authentication.md |
| §4 | [セッションが切れたときの挙動](operations/authentication.md#セッションが切れたときの挙動) | authentication.md |
| §5 | [緊急全失効（退職者・漏洩疑い時）](operations/authentication.md#緊急全失効退職者漏洩疑い時) | authentication.md |
| §6 | [ブレークグラス（非常口）の推奨](operations/authentication.md#ブレークグラス非常口の推奨) | authentication.md |
| §7 | [NTLM 縮退・非ドメイン機](operations/authentication.md#ntlm-縮退非ドメイン機) | authentication.md |
| §8 | [監査（誰がアクセスしたか）](operations/authentication.md#監査誰がアクセスしたか) | authentication.md |
| §9 | [閲覧 UI の HTTPS（8514）を有効化する（CF-D2）](operations/viewer-https.md) | viewer-https.md |
| §9.1 | [主経路: 自己署名 + GPO ルート配布（所要: 半日を見込む）](operations/viewer-https.md#主経路-自己署名--gpo-ルート配布所要-半日を見込む) | viewer-https.md |
| §9.2 | [検証](operations/viewer-https.md#検証) | viewer-https.md |
| §9.3 | [撤去・棚卸し（配布の対）](operations/viewer-https.md#撤去棚卸し配布の対) | viewer-https.md |
| §9.4 | [更新（期限が近づいたら）](operations/viewer-https.md#更新期限が近づいたら) | viewer-https.md |
| §9.5 | [証明書事故のとき（期限切れ・削除・秘密鍵の権限喪失）](operations/viewer-https.md#証明書事故のとき期限切れ削除秘密鍵の権限喪失) | viewer-https.md |
| §9.6 | [外形監視をしている場合](operations/viewer-https.md#外形監視をしている場合) | viewer-https.md |
| §10 | [データベースに接続できないとき（縮退運転）](operations/degraded-operation.md) | degraded-operation.md |
| §10.1 | [「再起動できるようになった」は「直った」ではない](operations/degraded-operation.md#再起動できるようになったは直ったではない) | degraded-operation.md |
| §10.2 | [管理画面に入れなくなる構成](operations/degraded-operation.md#管理画面に入れなくなる構成) | degraded-operation.md |
| §10.3 | [関連イベント ID](operations/degraded-operation.md#関連イベント-id) | degraded-operation.md |
| §11 | [アップグレードに失敗した / 製品が消えた・起動しないとき](operations/upgrade-recovery.md) | upgrade-recovery.md |
| §11.1 | [まず確認する](operations/upgrade-recovery.md#まず確認する) | upgrade-recovery.md |
| §11.2 | [製品が 1 つも残っていない場合](operations/upgrade-recovery.md#製品が-1-つも残っていない場合) | upgrade-recovery.md |
| §11.3 | [再インストールしても `Access to the path ... is denied` で起動しない場合](operations/upgrade-recovery.md#再インストールしても-access-to-the-path--is-denied-で起動しない場合) | upgrade-recovery.md |
| §11.4 | [それでも起動しない場合](operations/upgrade-recovery.md#それでも起動しない場合) | upgrade-recovery.md |
| §12 | [対応環境を維持する（時間が経ってから効く話）](operations/supported-environments.md) | supported-environments.md |
| §12.1 | [閉域環境でのブラウザ更新](operations/supported-environments.md#閉域環境でのブラウザ更新) | supported-environments.md |
| §12.2 | [対応下限の環境は「二重に劣後」する](operations/supported-environments.md#対応下限の環境は二重に劣後する) | supported-environments.md |
| §12.3 | [Yagura のサポートは対応 OS より先に切れる](operations/supported-environments.md#yagura-のサポートは対応-os-より先に切れる) | supported-environments.md |
| §12.4 | [対応外の OS で動かしている場合](operations/supported-environments.md#対応外の-os-で動かしている場合) | supported-environments.md |
| §12.5 | [Windows Server 2019 の送信元から TLS 受信へ送るとき](operations/supported-environments.md#windows-server-2019-の送信元から-tls-受信へ送るとき) | supported-environments.md |
| §13 | [昇格後に旧データベースファイルを処分する](operations/promotion-old-db.md) | promotion-old-db.md |
| §13.1 | [なぜ手動なのか](operations/promotion-old-db.md#なぜ手動なのか) | promotion-old-db.md |
| §13.2 | [手順](operations/promotion-old-db.md#手順) | promotion-old-db.md |
| §13.3 | [退避したファイルを後で読みたい場合](operations/promotion-old-db.md#退避したファイルを後で読みたい場合) | promotion-old-db.md |
| §14 | [ファイアウォール規則を自分で足す](operations/firewall.md) | firewall.md |
| §14.1 | [規則を追加する](operations/firewall.md#規則を追加する) | firewall.md |
| §14.2 | [ポートを変えたとき・無効に戻したとき・アンインストールしたとき](operations/firewall.md#ポートを変えたとき無効に戻したときアンインストールしたとき) | firewall.md |
| §14.3 | [規則の突合と警告（イベント ID 1023）](operations/firewall.md#規則の突合と警告イベント-id-1023) | firewall.md |
