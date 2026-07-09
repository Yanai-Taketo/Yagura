# ADR-0009: アーキテクチャ対応拡張 — x64 に加えて ARM64・x86 を採用

- 状態: proposed
- 日付: 2026-07-09
- 決定者: YANAI Taketo
- 関連: [Issue #123](https://github.com/Yanai-Taketo/Yagura/issues/123)（起票・現状調査）/ [ADR-0001](0001-project-founding.md)（目的——手軽な導入）/ [ADR-0002](0002-architecture-principles.md)（アーキテクチャ原則）/ [ADR-0005](0005-oss-packaging.md)（README 等入口文書）/ [ADR-0008](0008-forwarder-kit-generation.md)（フォワーダ配布キット・Fluent Bit 検証済み版の運用）/ `Directory.Build.props`・`Directory.Packages.props`・`installer/Yagura.Installer.wixproj`・`installer/Package.wxs`・`.github/workflows/ci.yml`

## 文脈と課題

Yagura は現在 **x64 のみ**をビルド・配布対象としている（`installer/Yagura.Installer.wixproj` の `InstallerPlatform=x64` 固定・`dotnet publish -r win-x64 --self-contained true` ハードコード、CI は `windows-latest`＝x64 のみ）。Issue #123 は「古い Windows Server（32-bit を含む）や Windows on ARM でも手軽に syslog 集約を試せるようにする」ことを動機に、ARM64・x86 への対応拡張を提起した。オーナーは 2026-07-09、**x64 に加えて ARM64・x86 の両方を採用する**方針を決定した。本 ADR はこの決定を記録し、各アーキテクチャの位置づけ・検証水準・実現方針・段階導入計画を定める。

Issue #123 の実ファイル調査（`550475b` 時点）によれば、コード層の障壁は低い（`Directory.Build.props` に `RuntimeIdentifier`/`PlatformTarget` の指定がなく、publish 時の `-r` で決まる）。一方、実質の足かせは MSI/WiX・CI/リリースパイプライン・ネイティブ依存（SQLite・SqlClient）のアーキ別解決・フォワーダ配布キットの Fluent Bit アーキ対応にある。Issue はまた「arm64 / x86 の self-contained publish 実機検証は未完」と明記しており（検証環境の SDK feature band 不一致が理由）、本 ADR の起案時点でもこの実機検証は行っていない。

### 前提の見直し: 「古い Windows Server（32-bit）」は .NET 10 の対象外である

conventions.md の実体検証原則に従い、.NET 10 の Windows サポート行列を当日ライブ確認した（[Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)、更新日 2026-06-02、確認日 2026-07-09）。該当表を引用する:

| Operating System | .NET 10 (Architectures) |
|---|---|
| Windows 11 (26H1, 25H2, 24H2, 23H2 Ent/Edu) | x64, x86, Arm64 |
| Windows 10 (21H2, 1809, 1607 LTSC/Enterprise) | x64, x86, Arm64 |
| **Windows Server**（2025, 23H2, 2022, 2019, 2016, 2012 R2, 2012） | **x64 のみ** |
| Windows Server Core（同上） | **x64 のみ** |
| Nano Server | **x64 のみ** |

つまり **.NET 10 は Windows Server 系列（Server Core・Nano Server を含む）を x64 でしか公式サポートしない**。ARM64・x86 は Windows 11/10 の**クライアント SKU**（LTSC/Enterprise を含む）に限られる。加えて次の 2 点をライブ確認した:

- **Windows Server の ARM64 版は現時点で一般提供（GA）されていない**。Windows Server 2025 の ARM64 は Insider Preview チャネルのみで、オンプレミス向け GA の発表はない（Microsoft Q&A「Windows Server on Arm64」・TechCommunity のインサイダー議論、確認日 2026-07-09）
- **Windows Server の 32-bit（x86）版は Windows Server 2008 が最後**であり、後継の Server 2008 R2（2009 年）以降は 64-bit のみ。Server 2008 自体のサポートは 2011 年に終了している（endoflife.date、確認日 2026-07-09）

したがって、Issue #123 が動機として挙げた「古い Windows Server（32-bit を含む）」への ARM64/x86 対応は、**.NET 10 自体がその組み合わせをサポートしない**ため技術的に成立しない。本 ADR はこの事実を踏まえ、ARM64・x86 の対象環境を「Windows Server」ではなく「**Windows 11/10 のクライアント OS**」と明確に再定義する（下記「決定 2」）。この訂正は Issue 起票時点の想定を修正するものであり、批判的レビュー（クリス視点）に照らして起案文書に明記する。

## 検討した選択肢

### 対応アーキテクチャの範囲

- **(A) ARM64 のみ採用**: Issue #123 は「開発機が既に ARM64 のため優先度・実現性ともに高い」と評価していた。x86 は 32-bit Windows Server の EOL 進行を理由に見送る案。却下（オーナー決定）——x86 の対象を「レガシー Server」ではなく「レガシー・省リソースなクライアント PC での試用」に再定義すれば、実現コストに見合う価値が残ると判断
- **(B) x64 + ARM64 + x86 の 3 アーキ採用（採用案）**: オーナー決定どおり全採用。ただし 3 アーキを**同格**（同一 SLA・同一検証水準）とはしない——x64 を主、ARM64・x86 を副とする非対称な位置づけにする（決定 2）
- **(C) 全アーキを同格の実機 E2E 保証で扱う**: 却下。Windows Server の ARM64 が GA されていない現状で ARM64 の実機 lab を Server 環境に対して回す意味がない（GA 前に投資しても検証対象自体が存在しない）。x86 についても現行 Windows Server に対象がないため同様。コスト（lab 環境・CI 時間・保守範囲）に見合わない

### x86 の対象環境の再定義

- **(a) 「32-bit Windows Server」を目標に据え続ける**: 却下——上記のとおり .NET 10 が非対応であり、目標自体が技術的に不成立
- **(b) 「Windows 10/11 の x86 クライアント + ごく小規模な試用・検証用途」に限定（採用）**: 現実に存在し .NET 10 がサポートする組み合わせに対象を合わせる。既定の運用規模（大量ログ・長期保持）は明確に非推奨とする

### ARM64 の対象環境の再定義

- **(a) 「Windows Server on ARM」を目標に据える**: 却下（現時点）——GA 製品が存在しないため実機検証自体ができない。GA 後に再評価する（再評価トリガ参照）
- **(b) 「Windows 11 on Arm 等のクライアント OS」に対象を限定（採用）**: 開発機が実際に ARM64（Windows 11）であることとも整合し、実現性が高い

### CI のアーキ別実行基盤

- **(a) GitHub ホスト `windows-11-arm` ランナーを使う（採用）**: Yagura リポジトリは public（`Yanai-Taketo/Yagura`、確認済み）であり、public リポジトリの標準ホストランナー（`windows-11-arm` 含む）は無料枠で利用できる（[GitHub-hosted runners reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)、確認日 2026-07-09）。x86 は専用ランナーを要さない——x64 ランナー上で `dotnet publish -r win-x86` のクロス publish、および WOW64 経由の実行スモークが可能（x64 Windows は x86 バイナリをネイティブに実行できる）
- **(b) セルフホストランナーを構築する**: 却下——個人運営規模（conventions.md のブランチ保護運用と同じ前提）で保守コストに見合わない。GitHub ホストランナーで足りる

## 決定

**選択肢 B（x64・ARM64・x86 の 3 アーキ採用）+ x86 対象環境の再定義 (b) + ARM64 対象環境の再定義 (b) + CI は GitHub ホストランナー**を採用する。

### 決定 1: 対応アーキテクチャは x64・ARM64・x86 の 3 種

`win-x64` / `win-arm64` / `win-x86` はいずれも .NET のポータブル RID として有効である（[.NET Runtime Identifier (RID) catalog](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog) の「Windows RIDs」節に 3 つとも明記。確認日 2026-07-09）。ARM（32-bit）は対象外とする——.NET が Windows 32-bit ARM ランタイムを提供しないため（Issue #123 の調査どおり。RID カタログにも `win-arm` の記載はない）。

### 決定 2: 3 アーキは非対称な位置づけとする

| アーキ | 対象環境 | 想定利用者・用途 | 検証水準 |
|---|---|---|---|
| **x64** | Windows Server 全般 + Windows 10/11 クライアント | 本番運用の主対象（現行どおり） | 実機 lab E2E を維持（installer-e2e.yml・M9-3 lab 相当） |
| **ARM64** | **Windows 11 on Arm 等のクライアント OS**（Windows Server の ARM64 は GA されていないため対象外。再評価トリガ参照） | Arm デバイスでの試用・開発機での動作確認・小規模エッジ用途 | ビルド成立 + 起動・受信・閲覧までのスモークテスト（`windows-11-arm` ホストランナー上、または実機）。実機 lab E2E は当面 x64 のみとし、需要が確認できた段階で引き上げを再評価する |
| **x86** | **Windows 10/11 の x86 クライアント SKU**（現行 Windows Server に x86 版は存在しないため Server 運用は対象外と明記する） | レガシー PC・省リソース環境でのごく小規模な試用・検証用途 | ビルド成立 + スモークテスト（x64 ランナー上のクロス publish + WOW64 実行）。実機 lab E2E は対象外（x64 lab 環境で代替検証しない） |

x64 を「主」、ARM64・x86 を「副」とする理由: (1) Windows Server 上の本番運用が Yagura の中核想定利用者（ADR-0001）であり x64 以外では実現できない、(2) ARM64・x86 の対象がクライアント OS の試用・小規模用途に限られる以上、実機 Server lab を割く投資対効果が薄い、(3) 実機検証コストを「主」に集中させることで、x64 の品質を落とさずに対応範囲を広げられる。

### 決定 3: ネイティブ依存の対応状況（実体検証）

- **Microsoft.Data.SqlClient 7.0.2**: 公式リリースノートに `.NET 8.0+ (Windows x86, Windows x64, Windows ARM, Windows ARM64, Linux, macOS)` と明記されている（[dotnet/SqlClient releases](https://github.com/dotnet/SqlClient/releases)、確認日 2026-07-09）。ネイティブ SNI（`Microsoft.Data.SqlClient.SNI.runtime`）は x64・x86・ARM64（および ARM）向けの `Microsoft.Data.SqlClient.SNI.{arch}.dll` を同梱する（`dotnet/core` issue #6749 の記述で確認）。**3 アーキとも対応が明記されている**
- **Microsoft.Data.Sqlite 10.0.9 / SQLitePCLRaw.lib.e_sqlite3 3.50.3**: 公式ドキュメントに RID を列挙する単一ページは見つからなかった（検証の限界として明記）。ただし複数の Microsoft Q&A スレッドが `runtimes\win-x86\native\e_sqlite3.dll`・`runtimes\win-arm64\native\e_sqlite3.dll` を当然存在するパスとして扱っており（確認日 2026-07-09）、パッケージが win-x64/win-x86/win-arm64 のネイティブ資産を同梱している状況証拠は得られた。**しかし `dotnet publish -r win-arm64 --self-contained true` / `-r win-x86 --self-contained true` を実行し、restore/publish がネイティブ資産を実際に解決できることの実機検証は、本 ADR の起案時点で未実施**（Issue #123 と同じ理由——検証環境の SDK feature band 不一致）。この検証を実装着手前のゲート条件とする（決定 6・委任事項 1）

### 決定 4: MSI/WiX のアーキ別ビルド方針

- `installer/Yagura.Installer.wixproj` の `InstallerPlatform` と `dotnet publish -r`・`--self-contained` を MSBuild プロパティでパラメータ化する（例: `-p:YaguraArch=arm64` で `InstallerPlatform=arm64`・`-r win-arm64` を連動させる）。WiX の `InstallerPlatform` は x86/x64/arm64 の 3 値を公式にサポートする（v4/v5 系のドキュメント記述。実ビルドでの確証は委任事項 2 で固定する）
- `installer/Package.wxs` の `StandardDirectory Id="ProgramFiles64Folder"` は x64・ARM64 では現行どおり（ARM64 でも 64-bit 版 Program Files を使う）。**x86 では `ProgramFilesFolder`（32-bit Program Files）への分岐が必要**（Issue #123 の指摘どおり）
- リリース成果物の命名にアーキを機械可読に含める（例 `Yagura-0.3.0-x64.msi` / `Yagura-0.3.0-arm64.msi` / `Yagura-0.3.0-x86.msi`）。既存の x64 単体配布との後方互換のため、当面 x64 は無サフィックス名も維持するかは実装 PR で確定する（委任事項 2）

### 決定 5: CI/リリースパイプラインの拡張方針

- 現行 `ci.yml`（PR ごとの単一ビルド・単体テスト・回帰ベンチ）は **x64 のまま維持**する（本 ADR は CI の主目的である「マージ前の品質ゲート」を多アーキ化しない——回帰ベンチの基準値は x64 CI ランナーで確定済みであり、他アーキに同じ基準を持ち込む根拠がない）
- **リリース用ワークフローを新設**し、タグ発行時に x64・ARM64・x86 の RID マトリクスで publish + MSI ビルドを行う。ARM64 のビルド自体は x64 ランナー上でのクロス publish で足りるが、**起動・受信・閲覧のスモークテストは実アーキ上で行う**——ARM64 は `windows-11-arm` ホストランナー（public リポジトリで無料）、x86 は x64 ランナー上での WOW64 実行で代替する
- `installer-e2e.yml` 相当の重い E2E（実機同等シナリオ）は当面 x64 のみに残す（決定 2 の検証水準表と整合）

### 決定 6: 段階導入計画

Issue #123 の未検証事項（ネイティブ依存の実機 publish 検証）を踏まえ、次の順で進める。各フェーズはゲートを通過してから次へ進む:

1. **Phase 0（ゲート）**: SDK feature band を揃えた環境で `dotnet publish -r win-arm64 --self-contained true` / `-r win-x86 --self-contained true` を実行し、SQLite ネイティブ資産（`e_sqlite3.dll`）と SqlClient ネイティブ資産（`Microsoft.Data.SqlClient.SNI.{arch}.dll`）が publish 出力に含まれ、起動・DB 接続まで成功することを確認する。**失敗した場合は本 ADR の計画自体を見直す**（再評価トリガ参照）
2. **Phase 1（ARM64 先行）**: MSI/WiX のパラメータ化・リリースワークフローのマトリクス化を ARM64 から着手する（開発機が ARM64 であり実機動作確認がしやすいため。Issue #123 の評価どおり）。`windows-11-arm` ランナーでのスモーク確立を含む
3. **Phase 2（x86）**: Phase 1 の枠組み（パラメータ化済みの wixproj・リリースワークフロー）を x86 へ展開する。`ProgramFilesFolder` 分岐・x86 向け推奨既定値（決定 7）を確定する
4. **Phase 3（フォワーダキット拡張）**: ADR-0008 のキット生成が前提とする Fluent Bit MSI 検出パターン（`fluent-bit-*-win64.msi`）を ARM64・x86 へ拡張する（決定 8）。Yagura サーバ本体のアーキ対応とは独立に進行可能なため、Phase 1/2 と並行できる
5. **Phase 4（ドキュメント・入口文書）**: README のシステム要件表・ダウンロード導線をアーキ別に更新する（ADR-0005 の入口文書更新原則）

### 決定 7: x86 の制約と推奨既定値

32-bit プロセスはユーザーモード仮想アドレス空間が既定で 2GB に制限される（`IMAGE_FILE_LARGE_ADDRESS_AWARE` フラグを立てても最大 4GB。.NET ランタイムが自動でこのフラグを立てるかは実装 PR で確認する）。Yagura は受信バッファ・スプール・SQLite の in-process 動作を持つため、x86 でのメモリ上限は大量ログ保持・長期スプールに直接影響する。本 ADR の時点では具体的な既定値（スプールサイズ上限・保持日数の推奨値）を確定しない——**実装 PR で x86 実機の負荷試験を行い、推奨既定値と警告文言（インストーラ・configuration.md）を確定すること**を委任事項とする（委任事項 3）。ドキュメント上は「x86 は小規模・試用目的。大量ログの本番運用には x64 を推奨する」という位置づけを明記する。

### 決定 8: フォワーダ配布キット（Fluent Bit）のアーキ対応

Fluent Bit は公式に Windows 32-bit（`win32`）・64-bit（`win64`）・ARM64（`winarm64`）の EXE・ZIP・MSI をすべて提供している（v5.0.8 時点。[Fluent Bit Windows downloads](https://docs.fluentbit.io/manual/installation/downloads/windows)、確認日 2026-07-09）。ADR-0008 のキット生成機能が前提とする MSI 検出パターン `fluent-bit-*-win64.msi`（設計条件 9）は win64 のみを対象としており、ARM64/x86 の送信端末向けにキットを生成するには、検出パターン・生成 UI・利用者ガイドをアーキ対応に拡張する必要がある。これは ADR-0008 の amendment または別途委任事項として扱う（委任事項 4）——本 ADR は「Fluent Bit 側の公式提供状況」の実体検証までを担い、キット生成側の実装方針の確定は委任する。

## 帰結

- **良くなること**: Windows on Arm・レガシー x86 PC でも Yagura を試用できるようになり、ADR-0001 の「手軽な導入」目標に資する。x64 の本番運用品質（実機 lab・CI 回帰ベンチ）は変更せず維持される
- **悪くなること（受け入れるトレードオフ）**:
  - CI/リリースパイプラインが複雑化する（RID マトリクス・アーキ別成果物）。保守対象が 3 倍になる
  - サポート行列の説明責任が増える——「ARM64/x86 は Windows Server 非対応」という非直感的な制約を README・インストーラ・ガイドで一貫して明記し続ける必要がある（誤解による問い合わせ・誤設置の温床になりうる）
  - x86 は大量ログ運用に不向きであるという注意書きの保守が必要になる（決定 7）
  - フォワーダキットのアーキ対応（決定 8）は本体のアーキ対応と別の実装負担であり、ADR-0008 の変更半径をさらに広げる
- **リスク**:
  - Phase 0 のネイティブ依存実機検証（SQLite の win-arm64/win-x86 資産解決）が失敗した場合、ARM64・x86 対応の実現可能性そのものが揺らぐ。本 ADR はこの検証を「ゲート」として明記しているが、**検証未了のまま本 ADR が accepted になる**——起案時点の技術的検証と実装着手時の検証に時間差があることを明記しておく
  - Windows Server の ARM64 が将来 GA された場合、「ARM64 はクライアント限定」という決定 2 の前提が変わる。放置すると古い ADR の記述と実態が乖離する（再評価トリガで対処）
  - WiX `InstallerPlatform=arm64`/`x86` の実ビルド挙動（`ProgramFiles64Folder` 分岐・`Wix4UtilCA_X64` などアーキ依存の内部参照名の扱い）は本 ADR では机上調査に留まる。実装 PR で ICE 検証・実ビルドを通すまで未確定要素が残る

## 先送りにする場合の再評価トリガ

- **Windows Server が ARM64 版を一般提供（GA）した場合**: ARM64 の対象環境を「クライアント限定」から「Server 込み」へ拡大するか、実機 lab E2E を ARM64 にも適用するかを再評価する
- **Phase 0 のネイティブ依存実機検証（SQLite・SqlClient の win-arm64/win-x86 資産解決）が失敗した場合**: 本 ADR の段階導入計画（決定 6）を見直す。失敗が SQLite 側の構造的な制約であれば、当該アーキでの DB provider 既定を SQLite から別の選択肢へ変更する検討も含める
- **x86 の試用フィードバックが一定期間（次回 v0.x リリース準備時を目安）ないか、需要が実質的にないと判明した場合**: x86 サポートの縮小・撤回（決定 1 の反転）を検討する。これは既定を反転させる判断のため、その場合は supersession とする（docs/adr/README.md の判定基準どおり）
- **ARM64/x86 のいずれかで CI・リリースパイプラインの保守コストが継続的に本体開発を圧迫すると判断された場合**: 段階導入計画の該当フェーズを凍結し、次リリースへ先送りするかを再評価する

## 委任事項の一覧(追跡用)

| # | 委任事項 | 委任先 | 内容 |
|---|---|---|---|
| 1 | ネイティブ依存の実機 publish 検証（Phase 0 ゲート） | 実装 PR | SDK feature band を揃えた環境で `dotnet publish -r win-arm64 / win-x86 --self-contained true` を実行し、SQLite・SqlClient のネイティブ資産解決と起動・DB 接続の成功を確認・記録する |
| 2 | WiX/wixproj のアーキ別パラメータ化の実装細目 | 実装 PR | `InstallerPlatform`・publish RID の連動、`ProgramFiles64Folder`/`ProgramFilesFolder` 分岐、成果物命名規則（x64 の後方互換命名の要否を含む）、ICE 検証・実ビルドでの確証 |
| 3 | x86 向け推奨既定値・警告文言の確定 | 実装 PR + configuration.md | x86 実機の負荷試験に基づくスプール上限・保持日数の推奨値、インストーラ・ドキュメントでの警告文言 |
| 4 | フォワーダキットの Fluent Bit 検出パターンのアーキ拡張 | ADR-0008 の amendment または別 ADR + 実装 PR | `fluent-bit-*-win64.msi` 検出パターンを ARM64（`winarm64`）・x86（`win32`）へ拡張する設計・実装。利用者ガイド・生成 README テンプレートとの整合を含む |
| 5 | ARM64 実機 lab 検証の受け入れ条件の具体化 | configuration.md / operations.md 相当 | Phase 1 完了時点でのスモークテスト項目・合否基準を明文化する |
| 6 | CI リリースワークフローの新設 | `.github/workflows/` + 実装 PR | RID マトリクスビルド・`windows-11-arm` ランナーの実導入・成果物アップロード・SHA256 添付（conventions.md のリリース規約に準拠） |
| 7 | README・入口文書のアーキ対応記述 | README 等入口文書（ADR-0005） | システム要件表・ダウンロード導線をアーキ別に整理し、「ARM64/x86 は Windows Server 非対応・クライアント OS 限定」を明記する |
