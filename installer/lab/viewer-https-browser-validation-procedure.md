# lab 手順書: 閲覧 UI HTTPS のブラウザ実機検証（ADR-0022 委任 4・委任 3 の一部）

- 対象: [ADR-0022](../../docs/adr/0022-viewer-https.md) 決定 4（SAN 助言検査）・決定 6（旧 URL 断絶）
- 位置づけ: **SAN 検査の警告文言の確定ゲート**。「SAN に短名があればブラウザ警告なしになる」という
  主張は本手順の通過までは断定形を避けてある（`UiText.ViewerHttpsSan*` は「〜見込みです」表記。
  [operations.md](../../docs/operations.md) §9.2 も同様）。CI では原理的に検証できない
  （実ブラウザ + GPO 配布ルートが要る——conventions.md「実環境依存の機能は lab 検証を受け入れ条件に含める」）
- 前提環境: AD DC（GPO 配布可能）+ ドメイン参加の Windows クライアント（Chrome・Edge・Firefox を導入）+
  Yagura サーバ（ドメイン参加。閲覧 HTTPS 有効化済み——構築は operations.md §9.1 の手順どおり。
  SAN = FQDN + 短名の両方を含む自己署名 + GPO ルート配布）

## 測定項目

各項目は「ブラウザ／URL／表示（警告の有無と種類）／スクリーンショット取得」を記録する。

| # | 測定 | 期待（未検証の仮説——実測が正） |
|---|---|---|
| M1 | Chrome/Edge で `https://<FQDN>:8514/` | 警告なし（鍵アイコン） |
| M2 | Chrome/Edge で `https://<短名>:8514/`（単一ラベル dNSName の扱い） | 警告なし（SAN に短名を含むため）——**本測定が警告文言確定の本丸** |
| M3 | Firefox 既定設定で M1/M2 | 警告あり得る（Firefox は既定で Windows 証明書ストアを見ない）。`security.enterprise_roots.enabled = true`（または GPO の Certificates ポリシー）での再測定も行い、案内文言の要否を判定 |
| M4 | `https://<IPアドレス>:8514/` | 証明書名の不一致警告（検査対象外の明記どおり） |
| M5 | 旧 URL `http://<サーバ名>:8514/`（HTTPS 有効化後） | ブラウザの実表示を記録（接続リセット等）。**Chrome/Edge の自動 https:// 再試行（HTTPS アップグレード）が非既定ポートで働き成功するかを含める**（成功するなら決定 6 の断絶の深刻度評価が下がる——ADR-0022 委任 3） |
| M6 | 稼働中の期限切れ遷移（短寿命の検証用証明書で再現） | 新規アクセスがハンドシェイク失敗になること・その際のブラウザ表示・イベント 1037 の発火 |
| M7 | GPO 未反映端末からの M1 | ルート未信頼の警告（operations.md §9.1「反映待ちの間は警告が残るのが正常」の裏取り） |

## 通過後の反映（実装側 TODO）

1. M1〜M4 の結果に基づき、`UiText.ViewerHttpsSanSatisfiedFormat` 等の「〜見込みです」を実測に即した表現へ確定する（公的 CA 証明書〔短名 SAN 構造的不可〕への警告抑制/文言分岐——委任 4 ②——の要否もここで判定）
2. M3 の結果に基づき、operations.md §9.2 に Firefox の注意（enterprise roots）を追記するか判定する
3. M5 の結果を ADR-0022 の帰結（旧 URL 断絶の深刻度）に改訂履歴として記録する
4. 記録一式（スクリーンショット・判定）は本ファイルの改訂として追記する（gold-standard 測定の前例——`gmsa-service-account-lab-procedure.md`——と同じ形式）
