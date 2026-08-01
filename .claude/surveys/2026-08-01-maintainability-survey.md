# v1.0 公開前 保守性・可読性 全体調査報告(2026-08-01)

> AI 向け内部資料。公式文書(docs/ 配下)ではない。実装フェーズの作業台帳として使う。
> 調査方法: 観点別サブエージェント 5 系統(コメント監査 / 巨大ファイル / 重複コード / 機械的一貫性 / テスト保守性)による並列走査 + 主要引用の抜き取り検証。
> 対象: src/ 約 57,000 行(322 ファイル)+ tests/ 約 45,000 行(191 ファイル)。

---

## 1. 判断基準 —「人間にとって保守性・可読性が高いコード」とは

確立された文献(Ousterhout『A Philosophy of Software Design』、McConnell『Code Complete』、.NET Framework Design Guidelines 等)に共通する要点を、本プロジェクトへの適用基準として整理した:

1. **コメントは「コードから読み取れない why」を書く**。what(次の行がやること)や出自(誰が・いつ・どの経緯で決めたか)は書かない。出自は git 履歴・ADR・PR が正であり、コメントに複製すると陳腐化して嘘になる(conventions.md の「依存バージョンをコメントに書かない」規約と同じ原理)
2. **変更の局所性**: 1 つの変更(設定キー追加・バグ修正)で読む/触るファイルが少ないほど良い。無関係な数百行をスクロールさせるファイルは、行数ではなく「読まされ方」が実害
3. **知識の単一箇所化(DRY の本質)**: 同じ*知識*が複数箇所にあると修正漏れが起きる。ただし**偶発的に形が似ているだけのコードを統合するのは逆に害**(将来の分岐が共通コードの if として蓄積する)
4. **規約は機械で強制する**: 人間の注意力に依存する規約は腐る。analyzer / CI / 同期検証テストで強制できるものはそちらへ寄せる
5. **テストは第二の仕様書**: 決定性(時刻・並行性)、失敗時の診断性(値が見えるアサーション)、フィクスチャの共有が保守性を決める

## 2. 総合評価(率直な第三者評価)

**構造・命名・テスト規律は同規模 OSS と比べても良好**。`#region` なし、`Console.WriteLine` 残骸なし、`Thread.Sleep` 0 件、インターフェース命名 100% 準拠、テスト命名は英語 SnakeCase で完全統一、bUnit 不採用理由の明文化や Conformance テスト基盤(契約ベース抽象基底 + 2 メソッド実装でプロバイダ追加可)は模範的。

一方、ご指摘の「生成 AI 特有の、入力プロンプト(背景)をそのまま書き足した箇所」は**定量的に裏付けられた**。これが本プロジェクト最大の可読性負債である:

- コメント行は src 全体の**約 33%(約 18,700 行)**
- コメント中の出典参照: **ADR 参照 1,160 件、「決定」1,297 件、Issue 参照 788 件、PR 参照 106 件、レビュー言及 約 120〜160 件**
- **ペルソナレビュアー名(田中・クリス・佐藤)が実コードに 26 件残存**
- 極端例: `AuditEventKind.cs` はコメント/コード比 **3.28**(コード 92 行に対しコメント 302 行)

第 2 の構造的負債は **Storage 層のプロバイダ間並行複製**。PostgreSQL / MySQL 対応が確定方針(CLAUDE.md 方針 1)である以上、現状のままでは 3 プロバイダ目の追加時に 1,000〜1,500 行級のコピーがもう 1 部増える。

## 3. 発見事項(優先度順)

### A. コメントの「出自語り」除去【最優先・効果最大・リスク最小】

分類と概算(src/ 対象):

| 分類 | 概算 | 扱い |
|---|---|---|
| a. 価値ある設計コメント(不変条件・落とし穴・既知の限界) | 数百件 | **残す**(ADR 番号 1 個の併記は可) |
| b. 出自語りの羅列(「ADR-XXXX 決定 N。Issue #NNN。PR #NNN レビュー指摘 N へのオーナー決定 2026-07-09」) | 1,000 件超 | 出典部分を削除・圧縮 |
| c. 変更履歴の語り(「以前は〜」「のち ADR-0016 決定 3 で撤去」「v1→v2→v3」) | 50 件程度 | 削除(git log / ADR が正) |
| d. コードの繰り返し | 十数件 | 削除 |
| e. レビュー対話の痕跡(「PR #163 レビュー指摘 1」「田中の指摘」) | 150 件前後 | 人物・PR 番号を削除、技術内容のみ残す |

代表例:
- `src/Yagura.Host/Configuration/ConfigurationEventIds.cs:132-139` — イベント ID **採番の経緯**(予約の衝突を PR #333 レビューで検出した顛末)が 8 行。実装ロジックと無関係
- `src/Yagura.Host/Program.cs:355-356` — 「ADR-0010 Phase 2 決定 4。田中の指摘。——ADR-0022 決定 1」と決定番号 2 つ + 個人名の連結
- `src/Yagura.Storage/Administration/Sqlite/SqliteAdminAccountStore.cs:17-24` — スキーマ v1→v2→v3 の変遷史を定数直上に記載
- `src/Yagura.Ingestion/Udp/UdpSyslogListener.cs:392-397` — **良い部分と悪い部分の混在例**: オーバーフロー時の実害説明(残す)+「PR #163 レビュー指摘 2」(削る)

密集ファイル上位(出典参照数 / コメント行/コード比): `Program.cs`(145 / 0.66)、`UiText.cs`(115 / 0.53)、`YaguraConfigurationLoader.cs`(86 / 0.33)、`ActiveNotificationMonitor.cs`(70 / 0.42)、`AdminAuthenticationExtensions.cs`(67 / 1.16)、`YaguraConfigurationOptions.cs`(52 / 1.33)、`AuditEventKind.cs`(50 / 3.28)、`AuditEventIds.cs`(44 / 1.71)。

また公開型の `<remarks>` が 60〜75 行に達する例が複数あり(`RetentionScheduler.cs`、`TcpSyslogListener.cs`、`IngestionMetrics.cs`、`IIngestionTlsAdminService.cs`、`LogStoreWriteGate.cs`)、architecture.md 等との**二重管理**になっている。設計解説は設計書へ、コード側は要点 + 参照 1 行に圧縮する。

**残すコメントの基準(コメント規約として conventions.md に追記する案)**:
1. コードから読み取れない「なぜ」(選択理由・トレードオフ・却下案の要点)のみ書く
2. 不変条件・並行性の落とし穴・既知の限界は書く(最も価値が高い)
3. 出典参照は `（ADR-0010 決定 3）` のような**識別子 1 個**まで。複数連結・PR 番号・日付・人物名・採番経緯・撤去済み機能の言及は書かない
4. tests/ も同基準(現状 ADR 参照 280 件・92 ファイル)

機械的に検出できる削除パターン(実装フェーズで正規表現走査に使う):
`PR #\d+ レビュー指摘` / `オーナー決定 \d{4}-` / `田中|クリス|佐藤` / `採番の経緯` / `以前は|旧実装|撤去した` / 1 文中の `ADR-\d{4}` 2 回以上。

### B. Storage 層の重複解消【3 プロバイダ化の露払い・優先度高】

`SqlServerLogStore.cs`(1,533 行)と `SqliteLogStore.cs`(1,165 行)は `ILogStore` 全メソッドが構造的に並行複製。`FindByIdAsync` / `CountAsync` / `QuerySystemEventsAsync` / `QuerySourceActivity*` / `QueryTopTalkers` / `WriteSystemEventAsync` / `DisposeAsync` はほぼ完全コピー。

**抽出候補(効果順)**:
1. 列マッピング(Reader→`LogRecord` 変換): 両プロバイダとも `DbDataReader` 実装なので、時刻パーサーだけ注入すれば 1 本化可能
2. WHERE 句組み立て + パラメータ追加(プレースホルダ記法だけ差し替え)
3. バッチ削除ループ(`while(true)` + バッチサイズ判定 — デリゲート注入で完全共通化可)
4. スキーマバージョン管理の骨格 — **LogStore × AdminAccountStore × 2 プロバイダで計 4 回複製**されており、投資対効果最大
5. 小物: `Normalize(username)` 完全一致 1 行メソッド 2 箇所、`ResolveCurrentAccountName()` 完全一致 2 箇所(`SqlServerFailureClassifier.cs:193` / `PromotionWizardService.cs:581`)

**統合してはいけない箇所(重要)**:
- カーソルページング述語(`SqlServerLogStore.cs:780-797` vs `SqliteLogStore.cs:459-469`): DB 最適化器の実測(DB-11)に基づく**意図的な方言差**。共通化すると性能劣化
- FailureClassifier のエラー番号対応表(DB 固有知識。「形」のみ共通化候補)
- SQL 文そのもの・接続系 API は各プロバイダに残す

効果見込み: 先に抽出すれば 3 プロバイダ目の新規実装を 1,000〜1,500 行 → **600〜700 行程度**に圧縮できる。

### C. Ingestion / Web の残存重複【中優先】

- **TCP/TLS リスナー**: Accept ループ・接続数管理・Start/Stop が**約 90 行の完全コピー**(`TcpSyslogListener.cs:143-254` ≒ `TlsSyslogListener.cs:97-192`)。接続処理デリゲート注入の共通ヘルパーで除去可。なお読み取りループ(`TcpFramedConnectionProcessor`)と dual-stack bind は既に共通化済みで模範的。UDP は受信モデルが異なるため統合対象外
- **証明書設定 3 画面**(`ViewerHttpsScreen` / `IngestionTlsScreen` / `AdminRemoteAccessScreen`): 証明書一覧パネル・`LoadCertificates`・`ApplyAsync` 末尾処理(保存→通知→Reload)がほぼ同型。共通コンポーネント化可。**ただしバッジの Error/Warning が画面ごとに意図的に逆転**(受信を止めない側に倒す製品仕様)しているため、ポリシーを必ずパラメータ化しテストで固定すること
- **管理系 Configure サービス 5 例**: 「正規化→検証→差分計算→no-op→保存→監査」の型が自覚的に反復されている。制御フローの骨格のみ共通化候補(低優先)。検証の可否判定(拒否 vs 警告)は意図的非対称のため各サービスに残す
- **見送り**: SetupWizard / PromotionWizard の基底クラス化(2 例のみで非対称性が大きく、偶発的類似の統合になる)

### D. 巨大ファイルの分割【低リスク組から着手】

リスクの低い順:

| 順位 | ファイル(行数) | 分割形態 | リスク |
|---|---|---|---|
| 1 | `SyslogParser.cs`(1,378) | partial: Rfc5424 / Rfc3164 / Shared(2 フォーマットは相互に呼び合わない) | 最低 |
| 2 | `UiText.cs`(2,284・定数 578 個) | partial: 画面単位(将来の resx 移行の下準備) | 最低 |
| 3 | `YaguraConfigurationLoader.cs`(2,300) | partial: 設定ドメイン単位(各 `Resolve*` は独立純関数) | 低 |
| 4 | `SqlServerLogStore.cs`(1,532) | partial: Schema / Write / Query / Retention(B と同時実施) | 低 |
| 5 | `AdminAuthenticationExtensions.cs`(768) | DI 登録 / 認可述語 / ミドルウェアの 3 ファイル化 | 低〜中 |
| 6 | `LogSearch.razor`(771) | static 純関数群(クエリ変換・フォーマット)のヘルパー抽出 | 低 |
| 7 | `ActiveNotificationMonitor.cs`(1,363) | partial: 監視カテゴリ単位(11 の `Evaluate*` は相互独立) | 中 |
| 8 | `Program.cs`(1,807・Main 実質 1,700 行) | 既存の `AddYagura*` DI 拡張パターンへ登録群を切り出し | 中 |
| 9 | `ForwarderKitScreen.razor`(958) | 子コンポーネント分割(キット生成 / MSI アップロードは別 ADR の別機能) | 中〜高 |
| 10 | `IngestionPipeline.cs`(1,058) | **原則現状維持**(並行不変条件のための意図的集約)。bind 再試行のみ抽出余地 | 高 |

### E. テスト基盤【構造的な改善余地】

1. **共有テスト支援プロジェクトが存在しない**(`Yagura.TestSupport` 相当なし・`IClassFixture` 利用 0 件)。これが以下のコピペの構造的原因:
   - `FindRepositoryRoot()` 完全一致コピー **8 ファイル**
   - 一時ディレクトリ/DB パス採番パターン **86 箇所・77 ファイル**(プレフィックス手打ち・後片付け個別実装)
   - `FakeLogStore` 4 ファイル個別定義(インターフェース変更で 4 箇所修正)、`FakeStatusReader` / `FakeAppAuthenticator` / `FakeForwarderMsiSource` 各 2 箇所重複
2. **時間依存の偏り**: `FakeTimeProvider` は Host.Tests に集中(24 ファイル)し、**Ingestion.Tests はほぼ未採用**。実時間ポーリング 30 件超・`Task.Delay` 60 件超。最悪パターンは「固定 1 秒待って何も起きないことを確認」する否定証明(`IngestionPipelineBindRetryTests.cs:82-85`)— フレークの温床
3. **アサーション診断性**: `Assert.True/False` 823 件中メッセージ付きは 48 件(6%)。`Sum`/`Count` を伴う複合条件から優先的に値表示型(`Assert.Equal`/`InRange`)へ
4. 巨大テストの partial 分割: `YaguraConfigurationLoaderTests.cs`(78 Fact・1,313 行)を設定ドメイン単位、`SyslogParserTests.cs` を RFC 単位
5. Conformance テスト基盤は**手を入れない**(設計良好・プロバイダ追加コスト最小化済み)

### F. 機械的な小粒案件

- `.editorconfig` は `:error` **0 件**・命名規則すべて `:suggestion` — 規約に強制力がない。上記コメント規約や命名を守らせるなら analyzer 重大度の引き上げ(段階的に `:warning`)を検討
- 空 catch(型指定なし・ログなし)**8 件**(`UdpSyslogListener.cs:192` ほか)。意図的な抑止なら「なぜ握りつぶすか」の 1 行(これは価値あるコメント)か、最低限例外型の明示を
- TODO/HACK 系 75 件、日付入りコメント 100 件超(→ A で一括処理)
- private フィールド `_` プレフィックス例外 4 件

## 4. 実装フェーズ計画案(段階分け)

各段階を独立 PR にし、squash merge 運用(conventions.md)に合わせる。

- **Phase 1: コメント浄化(A + F の一部)** — 効果最大・挙動変更ゼロ。①conventions.md にコメント規約を追記 → ②機械パターン走査で削除候補を列挙 → ③密集上位ファイルから順に適用(判断を伴うため機械置換一括ではなくファイル単位レビュー)。目安: コメント行 18,700 → 1 万行前後まで圧縮可能と推定
- **Phase 2: 低リスク分割(D の 1〜4)** — partial class 化のみ。ビルド成功 = 正しさが保証される機械的作業
- **Phase 3: Storage 共通化(B)** — 3 プロバイダ化の前提投資。Conformance テストが回帰の安全網
- **Phase 4: テスト基盤(E)** — `Yagura.TestSupport` 新設 → FindRepositoryRoot / 一時パス / Fake 集約 → 否定証明フレーク源の是正
- **Phase 5: 残存重複(C)と中リスク分割(D の 5〜9)** — 個別判断。`IngestionPipeline` は触らない

## 5. 参考数値一覧

| 指標 | 値 |
|---|---|
| src 総行数 / コメント行 | 57,315 / 約 18,754(33%) |
| ADR / 決定 / Issue / PR 参照(src コメント内) | 1,160 / 1,297 / 788 / 106 |
| ペルソナ名残存 | 26 件 |
| tests の ADR 参照 | 280 件(92/191 ファイル) |
| LogStore 並行複製 | 1,533 + 1,165 行 |
| TCP/TLS Accept ループ完全コピー | 約 90 行 |
| 一時パス採番の反復 | 86 箇所 / 77 ファイル |
| `Assert.True/False`(メッセージなし率) | 823 件(94%) |
| 空 catch / TODO 系 / `.editorconfig` :error | 8 / 75 / 0 件 |
