# ADR-0014: リリース成果物のコード署名 — SignPath Foundation の証明書による Authenticode 署名

- 状態: proposed
- 日付: 2026-07-14
- 決定者: YANAI Taketo
- 関連: [ADR-0006](0006-v1-release-criteria.md)（基準 5「リリースの完全性」の実現手段を確定する）、[ADR-0009](0009-architecture-support.md)（委任事項 8「コード署名方針」を閉じる）、[ADR-0005](0005-oss-packaging.md)（入口文書の体裁）、[ADR-0004](0004-security-model.md)（セキュリティモデル）、[SECURITY.md](../../SECURITY.md)、[conventions.md](../development/conventions.md)（リリース）

## 文脈と課題

### 現在地

Yagura のリリース資産（`Yagura-<版>-x64.msi` / `Yagura-<版>-arm64.msi`）は **Authenticode 署名されていない**。利用者は MSI を実行すると SmartScreen の「Windows によって PC が保護されました」警告に遭い、「発行元: 不明」と表示される。回避には「詳細情報 → 実行」の 2 クリックが要る。

改ざん検知の手段としては `.sha256` チェックサムを添付しているが、これは**配布経路が信頼できることを前提にした完全性チェック**であって、真正性（誰が作ったか）を証明しない。チェックサムと成果物が同じ GitHub Release に並んでいる以上、Release を掌握した攻撃者は両方を差し替えられる。

### 決定を要求している既存の合意

コード署名は新規の思いつきではなく、すでに 2 つの ADR が宿題として登録している。

- **ADR-0006 基準 5「リリースの完全性 — コード署名を含む」**: v1.0 公開の要件として「リリース資産（インストーラ）に **Authenticode コード署名（タイムスタンプ付き）**が付与されている」ことを要求し、証拠として「リリースノートから署名の検証手順（signtool verify 等）と `.sha256` が辿れること」を求めている。同 ADR は「証明書の入手・管理はリリース準備の課題として別途扱う」と明記して手段の決定を先送りした
- **ADR-0009 委任事項 8**: 「リリース成果物のコード署名（Authenticode）方針 / 別 ADR または ADR-0005 の amendment。署名の要否・証明書の入手と鍵管理・リリースワークフローへの組み込み。アーキ数に依存しない独立論点だが、導入時は全アーキ成果物への適用を設計に含める」

**本 ADR はこの 2 つを閉じる。** ADR-0006 は「署名する」という要否をすでに決めているため、本 ADR が決めるのは**手段**（どの証明書を、どう入手し、どう鍵を守り、どうワークフローへ組み込むか）である。

### 課題の核心 — OSS 個人プロジェクトは証明書を買えない

コード署名証明書は、**法的主体（legal entity）に対してのみ発行される**。OSS プロジェクトそれ自体は法人格を持たないため、CA から見た受取人になれない。さらに 2 つの制約が個人開発者を締め出している。

1. **秘密鍵のハードウェア保管が必須**: CA/Browser Forum の Code Signing Baseline Requirements により、**2023-06-01 以降、EV・非 EV を問わずコード署名証明書の鍵ペアは FIPS 140-2 Level 2 または Common Criteria EAL 4+ 以上のハードウェア暗号モジュール（HSM／ハードウェアトークン）で生成・保管することが必須**となった（[CA/Browser Forum Code Signing Requirements](https://cabforum.org/working-groups/code-signing/requirements/)、確認日 2026-07-14）。ソフトウェア鍵での運用は不可能になり、個人が負担する初期費用・運用手間が上がった
2. **CI との相性の悪さ**: ハードウェアトークンは物理的に 1 か所にしか挿さらない。GitHub Actions のようなクラウド CI から署名するには、クラウド HSM か署名サービスを別途調達する必要がある。「手元のマシンで手動署名してアップロード」はビルドの再現性・来歴（provenance）を壊し、ADR-0006 が求める「検証可能な完全性」の趣旨に反する

つまり Yagura が署名を実現するには「**個人が法人格なしに、公的に信頼される証明書へアクセスでき、かつ CI から自動で署名できる**」経路が要る。この 3 条件を同時に満たす選択肢は多くない。

## 検討した選択肢

### (A) SignPath Foundation の無償 OSS 証明書 + SignPath.io の署名基盤 【採用】

[SignPath Foundation](https://signpath.org/) は OSS プロジェクトに無償のコード署名を提供する非営利団体。証明書は **SignPath Foundation 名義で発行され、それを OSS プロジェクトに使わせる**（[terms](https://signpath.org/terms.html)、確認日 2026-07-14）。プロジェクトが法人格を持つ必要がない。署名は SignPath.io のクラウド HSM 上で行われ、GitHub Actions から `signpath/github-action-submit-signing-request` で要求する。

- 利点:
  - **費用ゼロ**。個人 OSS の継続性（ADR-0001 の運営原則）と両立する
  - **鍵を一切持たない**。秘密鍵は SignPath.io の HSM にあり、Yagura のリポジトリにも CI にも鍵が存在しない。漏洩させる鍵がないことが最大の防御になる（ADR-0004 の最小権限の思想と同型）
  - **来歴の技術的強制**: SignPath は署名要求のたびに origin verification を行い、「その成果物が、指定されたリポジトリのソースから、GitHub ホストランナー上の実際のワークフロー実行で生成されたこと」を検証する。この検証はビルドスクリプトの自己申告ではなく **GitHub 側が提供するメタデータ**に基づく。署名は「Yagura が作った」だけでなく「**このコミットから CI が自動生成した**」ことの証明になり、ADR-0006 が求める完全性の水準を、単なる Authenticode 署名より高い位置で満たす
  - リリースごとに**人間の手動承認**を要求する（terms「Every release needs manual approval for signing」）。CI が単独でリリース資産を署名・公開できない構造は、リポジトリ侵害時の被害を限定する
- 欠点・リスク:
  - **審査に通る保証がない**（後述「受け入れるリスク」）。terms は実行可能プログラムについて「a certain verifiable reputation」を要求すると明記する
  - **第三者への依存**が増える。SignPath Foundation がサブスクリプションを停止・証明書を失効させれば、以後のリリースは署名できなくなる（terms は「pause or terminate the subscription without prior notice」「revoke the certificate effective immediately or retroactively」の権利を留保している）
  - **発行元表示は "SignPath Foundation"** になる。Yagura の名前は証明書の Subject に出ない。利用者から見た発行元は Yagura ではなく SignPath Foundation である
  - terms が課す運用制約（コード署名ポリシーの掲示、全メンバーの MFA、メタデータの統一、役割分担の明文化）を恒久的に守る義務を負う

### (B) Azure Artifact Signing（旧 Trusted Signing）【却下】

Microsoft のマネージド署名サービス。月額約 $10 と安価で、鍵は Azure 側の HSM に置かれ、CI 連携も公式アクションがある。個人開発者（self-employed）にも 2026 年時点で開放されており、旧来の「3 年の事業履歴」要件は個人には課されない。

- **却下理由（実体検証済み）**: **提供地域が限定されており、日本の個人開発者は対象外**。Microsoft Learn の [Quickstart: Set up Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) は次のように明記する（確認日 2026-07-14、原文）:

  > For Public Trust certificates, Artifact Signing is currently available to organizations in the USA, Canada, the European Union, and the United Kingdom, as well as individual developers in the USA and Canada.

  Yagura のオーナーは日本の個人であり、組織（米国・カナダ・EU・英国）にも個人開発者（米国・カナダ）にも該当しない。技術的な優劣ではなく**資格要件で門前払いになる**ため、現時点では選択肢にならない。

  なお Azure のリージョンとしては Japan East が Artifact Signing をサポートしており（同ページのリージョン表）、リージョンの話と**証明書発行の資格地域の話は別**である点に注意する。この 2 つを混同すると「日本でも使える」と誤読しかねない。
- **再評価トリガ**: 提供地域に日本が追加された場合（後述）

### (C) 商用 CA から OV コード署名証明書を購入【却下】

DigiCert・Sectigo 等から個人名義（Individual validation）で OV 証明書を購入する。発行元表示に「YANAI Taketo」等、実名が出る。

- **却下理由**:
  1. **費用が継続的に発生する**（証明書年額 + ハードウェアトークンまたはクラウド HSM の費用）。個人が無報酬で運営する OSS に対して、リリースを続ける限り毎年かかるコストを構造として抱え込むことになる。ADR-0006 の supersession 歯止め条件（82 行）が想定した「年間費用が継続不能な水準」への接近を、無償の代替（(A)）を検討しないまま自ら選ぶ理由がない
  2. **実名・住所の公開**: OV 証明書の Subject には検証済みの個人名が入る。個人 OSS 開発者にとって、これは無視できないプライバシー上の帰結である
  3. **CI 連携に追加調達が要る**: 前述のとおりハードウェアトークンは GitHub Actions から使えない。クラウド HSM（Azure Key Vault + 対応 CA 等）を組めば可能だが、これは (A) が無償で提供する構造を有償で自作することに等しい
- ただし **(A) が却下された場合の第一の代替**として残す（後述の再評価トリガ）

### (D) Sigstore / cosign による署名【却下 — 問題を解決しない】

キーレス署名。OIDC で身元を証明し、証明書は短命。透明性ログ（Rekor）に記録される。ADR-0006 の 82 行が「無償・低コストの代替」の例として名指ししている。

- **却下理由（実体検証済み）**: **Windows の Authenticode 検証を満たさない**。Authenticode は Microsoft Trusted Root Program に参加する CA が発行した証明書チェーンを要求するが、Sigstore の CA（Fulcio）はこのプログラムに参加していない。したがって Sigstore のみで署名した Windows バイナリは、依然として SmartScreen で「発行元不明」と表示される（確認日 2026-07-14）。

  Sigstore は「配布物の来歴を検証可能にする」目的には有効だが、**ADR-0006 基準 5 が解決しようとしている問題（SmartScreen 警告への対処）そのものを解決しない**。ADR-0006 が代替候補として挙げた項目だが、実体を確認した結果、代替になっていないことがここで判明した。これは ADR-0006 の歯止め条件①「代替の検討記録を残すこと」に対する記録である
- **併用の可能性は残す**: GitHub Actions の artifact attestation（SLSA provenance）との併用は Authenticode 署名と排他ではない。ただし本 ADR のスコープ外とし、委任事項として登録する

### (E) 署名しない（現状維持）【却下】

- **却下理由**: ADR-0006 基準 5 が v1.0 の公開要件として署名を明示的に要求している。署名しないことを選ぶには、ADR-0006 の supersession が必要であり、その場合は同 ADR の歯止め条件（82 行）により「**v1.0 の延期を対等な選択肢として併記・比較する**」ことと「README・リリースノートへの署名なしの明示」が義務づけられる。無償の実現手段（(A)）が存在し、まだ試してもいない段階でこの道を選ぶ理由はない

## 決定

### 決定 1: SignPath Foundation の無償 OSS 証明書を採用し、SignPath.io を署名基盤とする

選択肢 (A) を採用する。決め手は次の 3 点である。

1. **資格要件で門前払いにならない唯一の現実的経路**（(B) は地域要件、(C) は費用とプライバシー、(D) は技術的に問題を解決しない）
2. **Yagura が秘密鍵を一切保持しない**。鍵管理という最も事故が起きやすい領域を、構造的に自分の手から外せる
3. **origin verification が「CI が当該コミットからビルドした」ことを技術的に強制する**。これは単に警告を消す以上の価値であり、ADR-0006 が「リリースの完全性」という名で求めていたものの本質に近い

**証明書の発行元は SignPath Foundation であり、Yagura ではない**。この事実を隠さず、入口文書と署名ポリシーに明記する（決定 5）。

### 決定 2: 署名対象は「自前バイナリのみ」とし、上流バイナリは署名しない

SignPath Foundation の terms「Sign your own binaries only」に従い、署名対象を次のとおり限定する。

| 対象 | 署名 | 根拠 |
|---|---|---|
| `Yagura-<版>-<arch>.msi`（インストーラ本体） | **する** | Yagura のビルドスクリプトが生成する自前成果物。利用者が実行する入口であり、SmartScreen が評価する対象 |
| `Yagura.Host.exe`・`Yagura.*.dll`（Yagura が書いたアセンブリ） | **する** | 自前ソースからのビルド成果物。MSI 内部の deep signing |
| .NET ランタイム一式（self-contained publish に同梱される `coreclr.dll` 等） | **しない** | Microsoft が既に署名している上流バイナリ。terms は自プロジェクトのソースから作っていないものへの署名を禁じている |
| MudBlazor 等の第三者アセンブリ（NOTICE に掲載） | **しない** | 同上（上流 OSS の成果物）。terms は「未署名の上流バイナリを署名済みパッケージに同梱すること」を明示的に許可している |

**「MSI は署名されているが、中の第三者 DLL の一部は未署名」という状態を正とする。** これは terms が想定・許容する構成であり、隠すべき欠陥ではない。

### 決定 3: リリースワークフローは「未署名で機能を検証し、署名済みで署名を検証し、署名済みのみを公開する」

`release.yml` を次の構造へ改める。

```
build (x64 / arm64)
  → 未署名 MSI を GitHub artifact としてアップロード
  → [既存] Full E2E スモーク（x64 / ARM64 実機）を未署名 MSI に対して実施
  → SignPath へ署名要求（submit-signing-request）
  → 【人間による手動承認】
  → 署名済み MSI を取得
  → 署名検証ゲート（Authenticode 検証 + 署名済み MSI のインストール〜起動スモーク）
  → SHA256 を【署名済み MSI に対して】算出
  → GitHub Release を公開（署名済み MSI + その .sha256 のみ）
```

判断の内訳:

- **機能 E2E は未署名 MSI に対して行う**。署名は手動承認を挟むため、E2E の前段に置くと「承認待ちで CI が止まったまま、機能の破損がまだ判明していない」状態が生まれる。壊れた MSI に人間が承認印を押す事故を避けるため、**機能検証を承認より前に置く**
- **ただし「テストしたものと出荷するものが違う」問題を放置しない**。署名後に**署名検証 + インストール〜起動のスモーク**を必ず通す（署名は PE / MSI の構造を変更するため、「署名したら壊れた」は理論上ありうる）。フル E2E の再実行はしない——署名が機能を壊す経路は限られており、コストに見合わないと判断する。この判断が誤りであった場合（署名済み MSI 固有の不具合が実際に出た場合）は、フル E2E の再実行へ格上げする
- **SHA256 は必ず署名済み MSI に対して算出する**。署名はファイルを変更するため、未署名 MSI のチェックサムを公開すると利用者の検証が必ず失敗する。現行 `release.yml` はビルドジョブ内でチェックサムを算出しており、**この移設は必須の変更**である
- **未署名 MSI を GitHub Release へ公開しない**。署名要求が失敗・却下された場合はリリースを中断する（署名済みと未署名が混在する Release を作らない）

### 決定 4: 署名は全アーキ（x64・ARM64）の成果物に等しく適用する

ADR-0009 委任事項 8 の「導入時は全アーキ成果物への適用を設計に含める」に従う。ARM64 の検証水準が「試験的」であること（ADR-0009 決定 2）と、**署名の有無は別の軸**である。試験的だから署名しない、という扱いはしない——署名は「誰が作ったか」の証明であって「どれだけテストしたか」の証明ではなく、両者を混同すると利用者に誤ったメッセージを送る。

### 決定 5: コード署名ポリシーを公開し、入口文書に検証手順を書く

terms「Specify a code signing policy」は掲示内容を具体的に指定している。次を新設・更新する。

- **`docs/code-signing-policy.md`（新設）**: 見出しに「コード署名ポリシー」を用いる。必須の記載事項は terms が定めるとおり:
  - 定型文言「Free code signing provided by SignPath.io, certificate by SignPath Foundation」（terms が原文での掲示を指定しているため、**この 1 文は英語のまま掲載する**。日本語の説明文を併記する）
  - チームの役割（Authors / Reviewers / Approvers）とそのメンバー
  - プライバシーポリシー（後述）
- **README.md**: 「コード署名ポリシー」への導線をインストール節に置く（terms は「home page and download/release pages」での掲示を求める）
- **SECURITY.md**: 署名の検証手順（`signtool verify` / `Get-AuthenticodeSignature`）を追加する。ADR-0006 基準 5 の「証拠」要件が求める内容である
- **リリースノート**: 署名済みである旨と検証手順への導線を定型文に追加する（現行の「アーキテクチャ対応」表と同じ枠組みで運用する）

**プライバシーポリシーの記載**: Yagura はテレメトリを持たず、利用者が設定した宛先以外へ情報を送信しない（SECURITY.md の既定構成の姿勢と一致する）。terms が用意する定型文「This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it」を採用する。ただし**逆引き（PTR）の DNS 問い合わせ（ADR-0007）とフォワーダ配布キットの取得は、利用者が明示的に有効化・実行する操作である**旨を日本語で補足し、定型文の「unless specifically requested by the user」の内実を利用者に対して具体的に示す。

### 決定 6: チームの役割を「単一メンテナ + 外部貢献は必ずレビュー」として明文化する

terms「Assign code signing roles」への回答。Yagura は現在オーナー 1 名の体制であり、これを偽らずそのまま宣言する。

| 役割 | 現在の担当 | 意味 |
|---|---|---|
| Authors | YANAI Taketo（リポジトリオーナー） | 追加レビューなしにソースを変更できる者 |
| Reviewers | YANAI Taketo | 外部からの PR は必ずレビューを経てマージされる（`main` は PR 必須 + 承認 1 件必須のブランチ保護下にある） |
| Approvers | YANAI Taketo | 署名要求を承認する者 |

- **単一メンテナであることの帰結を隠さない**: 役割分離が形式的にしか成立していない（同一人物が全役割を兼ねる）。これは体制の実態であり、コントリビュータが増えた時点で役割を分ける。ポリシーページにこの事実をそのまま書く
- **全メンバーの MFA は terms の必須要件**。オーナーの GitHub・SignPath アカウントで MFA を有効化していることを申請前に確認する。将来コミット権を持つメンバーを迎える場合、MFA の有効化を権限付与の条件とする

### 決定 7: 製品メタデータを一元化し、CI で一致を強制する

terms「SignPath configuration requirements」は、署名対象バイナリに**製品名・製品バージョンのメタデータが設定され、file metadata restrictions で強制されている**ことを要求する。

現状、`Directory.Build.props` にも各 `.csproj` にも `Product` / `Company` / `Version` / `Copyright` が**一切設定されていない**。結果として生成されるアセンブリは SDK 既定値（`AssemblyCompany` = アセンブリ名、`AssemblyFileVersion` = `1.0.0.0`）を持ち、バージョンの正は `installer/Package.wxs` の `Version` **のみ**である。この状態では SignPath のメタデータ制約を満たせない。

- `Directory.Build.props` に製品メタデータを追加する（`Product` = `Yagura`、`Company` = `Yagura Project`（MSI の `Manufacturer` と一致させる）、`Copyright`（NOTICE と一致させる）、`Version` 系）
- **バージョンの単一の正（single source of truth）を決め、`Package.wxs` の `Version` とアセンブリのバージョンが食い違ったら CI で失敗させる**。現行 `release.yml` は「git タグ vs `Package.wxs`」の一致検証を既に持っている（74-99 行）。この検証を「タグ vs `Package.wxs` vs アセンブリ」の三者一致へ拡張する
- 正をどちらのファイルに置くか（`Directory.Build.props` から `Package.wxs` へ流し込むか、その逆か）は実装上の選択であり、**委任事項 1** とする

### 決定 8: SignPath が使えなくなった場合の退避経路をあらかじめ決めておく

第三者依存を受け入れる以上、切れたときの手順を決めずに依存しない。次の順で退避する。

1. **申請が却下された場合**: 却下理由が「評判不足」であれば、**整備した内容（ポリシーページ・メタデータ・ワークフローの署名対応）はそのまま維持し、署名ステップだけを無効化して**リリースを継続する。実績（利用者・ダウンロード・被参照）を積んでから再申請する。この場合、**v1.0 の公開は ADR-0006 基準 5 を満たせないため延期する**（署名なしでの v1.0 公開は ADR-0006 の supersession を要する別の決定であり、本 ADR では選ばない）
2. **サブスクリプションが停止・証明書が失効した場合**: 選択肢 (C)（商用 CA の OV 証明書 + クラウド HSM）へ移行する。決定 3 のワークフロー構造（署名を独立ジョブとして分離し、署名済み成果物のみを公開する）は署名基盤を差し替えても維持できるよう設計する——**署名基盤への依存をワークフローの 1 ジョブに閉じ込めることを設計要件とする**
3. **失効が遡及適用された場合**（terms は「retroactively」の可能性を明記）: 既公開の署名済み MSI が「失効した証明書で署名されたもの」になる。タイムスタンプ付き署名は証明書の**期限切れ**には耐えるが、**失効**には耐えない場合がある。この事象が起きた際は、SECURITY.md でその事実と影響範囲を告知する（隠さない）

## 帰結

### 良くなること

- 利用者が MSI を実行したときの SmartScreen 警告が軽減され、発行元が「不明」ではなくなる。ADR-0001 が掲げる「Windows 管理者が既に持つスキルだけで導入できる」という思想において、**警告を無視する手順を利用者に教えなくて済む**ことの価値は大きい（「警告を無視して進む」習慣を育てない、という `configuration.md` §6 の自己署名証明書に関する判断と同じ原則）
- リリース資産の真正性が、配布経路への信頼に依存せず検証可能になる
- origin verification により「このバイナリは、このコミットから、この CI が作った」ことが技術的に強制される。リポジトリ侵害・CI 侵害に対する防御が 1 段深くなる
- 秘密鍵を保持しないため、鍵の漏洩・紛失・トークンの物理管理という問題群がそもそも発生しない
- ADR-0006 基準 5 と ADR-0009 委任事項 8 が閉じる

### 悪くなること・トレードオフ

- **リリースが完全自動でなくなる**（署名の手動承認が挟まる）。タグを push したら Release ができる、という現在の運用は失われる。ただしこれは terms の必須要件であると同時に、**リリースという不可逆な行為に人間の確認を差し込むこと自体が防御である**とも解釈でき、素直に受け入れる
- **リリース所要時間が延びる**。承認待ちが人間の応答速度に律速される。緊急のセキュリティ修正リリースでも承認が要る（SECURITY.md の SLA と矛盾しないか、実装時に確認する——**委任事項 5**）
- **第三者への依存が 1 つ増える**（SignPath Foundation・SignPath.io）。可用性・継続性は Yagura の管理外にある
- **発行元表示が Yagura ではなく SignPath Foundation になる**。利用者から見て「誰が作ったか」の答えが一段間接的になる。これはポリシーページで説明して補う
- terms が課す運用制約を恒久的に守る義務を負う（ポリシー掲示・MFA・メタデータ統一・役割分担）。将来 terms が変わればそれに追従する必要がある
- CI のワークフローが複雑になる（ジョブ数の増加、artifact の受け渡し、承認待ちのタイムアウト設計）

### 受け入れるリスク

- **審査で却下されるリスクを、率直に、承知の上で受け入れる。** SignPath Foundation の terms は「For executable programs that may be downloaded and executed based on our signature, we require a certain verifiable reputation」と明記する。2026-07-14 時点の Yagura は **star 0・fork 0・リポジトリ作成から 11 日・リリース資産のダウンロード数は各 0〜4 件**であり、この要件に照らして強い立場にはない。技術要件（OSI ライセンス・リリース済み・GitHub ホストランナーでのビルド・アンインストール提供）はすべて満たせる見込みだが、**評判要件は満たしているとは言えない**
  - この事実を隠して申請しない。申請時に体制（単一メンテナ・新しいプロジェクトであること）をそのまま申告する
  - 却下された場合の扱いは決定 8-1 のとおり。**整備作業（ポリシー・メタデータ・ワークフロー）は却下されても無駄にならない**——これらは (C) へ移行する場合にも、再申請する場合にも、そのまま使える
- **SmartScreen の警告が「消える」とは約束できない。** SmartScreen の評価は証明書ごとの評判に基づく。SignPath Foundation の証明書は多くの OSS プロジェクトが共用しているため既に評判が蓄積していると**期待される**が、これは Yagura として実測していない。したがって:
  - 入口文書に「SmartScreen 警告が出なくなる」とは書かない。「署名され、発行元が検証可能である」という事実のみを書く
  - **署名済み MSI での SmartScreen の実挙動を lab で検証し、その結果を記録する**（conventions.md「実環境依存の機能は lab 検証を受け入れ条件に含める」——単体テスト・CI では原理的に検証できない領域である）。**委任事項 4**

## 先送りにする場合の再評価トリガ

| 先送りする事項 | 再評価トリガ |
|---|---|
| **Azure Artifact Signing（選択肢 B）の採用** | 提供地域に**日本の個人開発者**が追加されたとき。Microsoft の提供地域は拡大方針が公言されているため、**各 Yagura リリース準備時に [Quickstart の Prerequisites](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) の地域記述を確認する**（フォワーダの Fluent Bit 版確認と同じ、リリース準備時の当日ライブ確認に相乗りさせる）。日本が追加された場合、(A) からの乗り換えを検討する——ただし乗り換えは「無償 → 有償」「発行元が SignPath Foundation → 実名」というトレードオフを伴うため、自動的な移行は決めない |
| **商用 CA の OV 証明書（選択肢 C）の採用** | SignPath Foundation の申請が却下され、かつ再申請の見込みが立たないとき。または SignPath のサブスクリプションが停止されたとき（決定 8-2） |
| **Sigstore / GitHub artifact attestation（SLSA provenance）の併用** | Authenticode 署名の導入が完了した後。Authenticode とは排他ではなく、来歴の第 2 の証明として価値があるが、**現時点で解決すべき問題（SmartScreen 警告）を解決しないため後回しにする**。v1.0 公開基準（ADR-0006）の見直し時に再評価する |
| **SBOM の生成** | SignPath Foundation の terms が「We reserve the right to enforce the implementation of additional best practices in the future, such as the generation of SBOM files」と将来の要求を予告している。terms が実際に SBOM を必須化したとき、または v1.0 公開基準の見直し時 |

## 委任事項（実装 PR で決めること）

| # | 事項 | 決める場所 | 内容 |
|---|---|---|---|
| 1 | バージョンの単一の正をどこに置くか | 実装 PR | `Directory.Build.props` と `installer/Package.wxs` のどちらを正とし、他方へどう流すか。三者（git タグ・MSI・アセンブリ）の一致を CI でどう強制するか（決定 7） |
| 2 | SignPath の Artifact Configuration の具体 | 実装 PR | MSI 内部のどのファイルを deep signing の対象に含め、どれを除外するか（決定 2 の表を SignPath の設定へ落とす）。file metadata restrictions の具体値 |
| 3 | 署名要求のタイムアウト設計 | 実装 PR | `wait-for-completion-timeout-in-seconds` の値（既定 600 秒 = 10 分）。手動承認が挟まる以上、既定値では短すぎる可能性が高い。承認待ちで CI が失敗した場合の再実行手順を含めて決める |
| 4 | 署名済み MSI の SmartScreen 実挙動の lab 検証 | 実装 PR（lab 検証） | 実機（クリーンな Windows）で署名済み MSI をダウンロード・実行し、SmartScreen の表示を記録する。「発行元: SignPath Foundation」の表示・警告の有無。結果を入口文書の記述の根拠とする |
| 5 | 緊急セキュリティリリースと手動承認の両立 | 実装 PR | SECURITY.md の対応 SLA と、署名の手動承認による遅延が矛盾しないかを確認し、必要なら SECURITY.md を更新する |
| 6 | 署名ステップの独立性の担保 | 実装 PR | 決定 8-2 の「署名基盤への依存をワークフローの 1 ジョブに閉じ込める」を、`release.yml` の構造としてどう実現するか |
