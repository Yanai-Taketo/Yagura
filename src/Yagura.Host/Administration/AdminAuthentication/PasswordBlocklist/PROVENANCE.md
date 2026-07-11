# パスワードブロックリストの供給網（ADR-0011 決定 7・委任事項 5）

- **同梱ファイル**: `blocklist.txt.gz`（gzip 圧縮済みテキスト。1 行 1 パスワード・小文字正規化・重複排除・辞書順ソート済み）
- **収録件数**: 97,746 件（下限の目安「上位数万〜数十万語級」を満たす。ADR-0011 決定 7・10）
- **出典**: [danielmiessler/SecLists](https://github.com/danielmiessler/SecLists) リポジトリの
  `Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt`
  （英国 National Cyber Security Centre が公開した既知漏洩パスワードの頻出リストを基にした
  データセット。実漏洩データの頻度分析であり、人工的なパターン生成ではない）
- **取得日**: 2026-07-11（本 PR 実装時に取得したスナップショット。以後の更新はソフトウェア更新でのみ行う——
  実行時にネットワーク経由で取得しない。ADR-0011 決定 7）
- **ライセンス**: SecLists リポジトリ全体は MIT License（`SOURCE-LICENSE.txt` に同梱の全文。
  Copyright (c) 2018 Daniel Miessler）。本製品への同梱・再配布は同ライセンスの条件（著作権表示・
  ライセンス全文の保持）の範囲内で行う——`SOURCE-LICENSE.txt` を本フォルダに同梱することで満たす
- **加工内容**（本 PR で実施。可逆的な正規化のみ）:
  1. 各行を trim し、空行を除去
  2. 小文字化（`ToLowerInvariant`）——本製品のパスワード突合が大文字小文字を区別しない設計
     （`AdminPasswordBlocklist.IsBlocked`）と整合させるため、辞書側もあらかじめ正規化した
  3. 小文字化後の重複排除・辞書順ソート
  4. gzip 圧縮（`CompressionLevel.Optimal`）してアセンブリへ埋め込む——展開後 895KB 相当のテキストを
     260KB 程度に圧縮し、アセンブリサイズへの影響を抑える
- **更新方針**: 辞書の陳腐化はソフトウェア更新の周期で受容する（ADR-0011 決定 7 が明示した
  トレードオフ）。更新する場合は同じ手順（取得 → 正規化 → 重複排除 → gzip 圧縮）を踏み、本ファイルの
  取得日・件数を更新すること
