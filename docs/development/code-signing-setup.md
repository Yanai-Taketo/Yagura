# コード署名セットアップ手順（Sectigo + Google Cloud KMS）

[ADR-0014](../adr/0014-code-signing.md) の実装に向けた運用手順。**証明書の発注・本人確認・アカウント作成・支払いはオーナー本人が行う**（AI エージェントは代行しない）。この文書は「オーナーがやること」と「実装で自動化すること」を分け、発注前に片づける確認事項（ADR-0014 委任 A/B/C）を実行可能な形にまとめる。

方式の確定事項（ADR-0014 決定 1、オーナー裁定 2026-07-15）:

- **CA**: Sectigo（正規リセラー経由）の OV コード署名証明書
- **申請主体**: **個人（自然人）名義**。証明書 Subject に本名が載る（屋号ルートは将来の再評価事項として保留）
- **鍵の保管**: Google Cloud KMS（Cloud HSM、FIPS 140-2 Level 3）。**USB トークンは使わない**
- **署名の実行**: GitHub Actions から Workload Identity Federation 経由。長期の秘密鍵 JSON は GitHub Secrets に置かない
- **証明書 1 枚を Open DUMP Viewer と共用**する（ADR-0014 決定 1 受け入れリスク 3）

> 調査の裏づけ（2026-07-15、deep-research 19 ソース検証済み）: GCP Cloud HSM の鍵はエクスポート不可のまま attestation ファイルで CA に証明でき、Sectigo・SSL.com とも公式に受理する（「USB トークン必須」説は反証済み）。SignTool + Google Cloud KMS CNG provider または jsign で署名でき、GitHub Actions で自動化できる。出典は本文末尾。

---

## フェーズ 0: 発注前の確認・準備（オーナー作業。委任 A/B/C）

### 委任 A — Sectigo リセラーへの事前確認（発注前・最重要）

**支払い前に必ず問い合わせる。** 個人名義 + GCP KMS 外部鍵の組み合わせが通ることを、実際の発注前に確認する。問い合わせ文面の例:

> 御社取扱いの Sectigo OV コードサイニング証明書について、発注前に 3 点確認させてください。
> 1. **個人（自然人）名義**で発行できますか。（法人・事業体ではなく個人としての申請）
> 2. 秘密鍵を **Google Cloud KMS（Cloud HSM）** で生成・保管し、その **attestation（鍵所在証明）と CSR を提出する方式**で発行できますか。（USB トークンの送付を受けずに済むか）
> 3. 個人名義での**本人確認に必要な書類**（政府発行 ID 等）と、**費用総額**（証明書 + 年数 + attestation 手数料の有無）、**発行までの日数**を教えてください。

**もし「個人名義では GCP KMS 外部鍵を使えない／USB トークンのみ」と回答された場合**: フォールバックとして **SSL.com の IV（Individual Validation）コード署名証明書**を検討する。SSL.com は個人 + Google Cloud HSM を公式に明示サポートしている（ただし GCP HSM の attestation に一回 **$500** の手数料がかかる点に注意）。この分岐は ADR-0014 決定 1「委任 A が不可だった場合の分岐」に対応する。

### 委任 B — MFA の有効化（発注前）

署名を発火させる権限が CI に載る構成のため、次の 2 つのアカウントで**多要素認証を有効化**しておく（ADR-0014 決定 6）:

- GitHub アカウント（`Yanai-Taketo`）
- Google Cloud アカウント（GCP プロジェクトの所有者）

### 委任 C — Open DUMP Viewer との共用設計（発注前・別リポジトリにまたがる）

証明書 1 枚・GCP KMS 鍵 1 つを Yagura と ODV で共用する。発注前に次を決める:

- GCP プロジェクト・KMS 鍵リング・鍵・WIF プールを**どちらの管理下に置くか**（オーナー個人の GCP プロジェクトに一元化するのが素直）
- **リポジトリごとに別のサービスアカウントを割り当てる**（Yagura 用・ODV 用）。証明書は共通でも署名操作の主体を分け、Cloud Audit Logs で「どちらのリポジトリが署名したか」を追跡できるようにする（ADR-0014 決定 1 受け入れリスク 3 の緩和策。ただしこれは**事後の帰属**であって予防ではない——同一証明書のため、侵害された側の署名は利用者から区別できない点は設計上の限界として受け入れ済み）
- ODV 側のリリースワークフロー改修は本リポジトリの範囲外だが、GCP 側の共用設計はここで揃える

### GCP の課金準備（オーナー作業）

- Google Cloud アカウント作成・請求先（カード）登録
- 想定ランニング費用: **Cloud HSM 鍵 月 $1〜2.5 程度 + 署名操作 $0.15/1 万署名**（Yagura のリリース頻度では署名操作費はほぼゼロ）

---

## フェーズ 1: GCP KMS のセットアップ（AI が手順を用意・オーナーが実行）

アカウントと課金が整ったら、以下を実行する。`gcloud` コマンドはオーナーの手元または Cloud Shell で実行する（AI は手順の作成・レビューを担当）。

1. **鍵リングと HSM 保護の署名鍵を作成**する
   - 保護レベル: `hsm`（Cloud HSM = FIPS 140-2 Level 3）
   - アルゴリズム: **RSA 4096（PKCS#1 v1.5・SHA256）**。理由: 署名を SignTool + Google Cloud KMS CNG provider で行う場合、**CNG provider は RSA のみ対応し EC 鍵は使えない**（jsign を使う場合は EC も可だが、両にらみのため RSA で統一する）
2. **attestation バンドル（ZIP）を取得**する（KMS の鍵詳細 → 証明書を検証 → 証明書バンドルをダウンロード）
3. **CSR を生成**する（OpenSSL + PKCS#11 で Cloud HSM の鍵にアクセス。Sectigo の公式手順に沿う）
4. CSR + attestation を Sectigo の enrollment（配信方法 = Install on Existing HSM → HSM 種別 = Google Cloud KMS (Cloud HSM)）に提出する（委任 A の回答に従う）

> 具体的な `gcloud` コマンド一式は、委任 A の回答（証明書の鍵仕様の指定を含む）を受けてから実装 PR で確定する。Sectigo が要求する鍵アルゴリズム・鍵長が RSA 4096 と異なる場合はそれに合わせる。

## フェーズ 2: Workload Identity Federation の設定（AI が実装）

GitHub Actions の OIDC トークンを GCP が信頼する設定。**長期の秘密鍵を GitHub Secrets に置かない**ための要。ADR-0014 委任事項 3 のとおり、**信頼条件を特定のリポジトリ・ref（タグ）・Environment に厳格に絞る**（fork や別 workflow からトークンを発行できないようにする）。あわせて:

- 最小権限 IAM（`roles/cloudkms.signerVerifier` 相当に限定。ADR-0014 委任 5）
- **Cloud KMS の Data Access 監査ログを明示的に有効化**する（既定は無効。ADR-0014 委任 4。これがないと署名記録が残らない）
- 想定外の principal・タイミングからの署名操作へのアラート（ADR-0014 委任 13）

## フェーズ 3: リリースワークフローへの署名工程の組み込み（AI が実装）

ADR-0014 決定 3 の構造（未署名で機能 E2E → GitHub Environment 承認ゲート → 署名 → 署名済みでフル E2E → 公開 → 公開後検証）を `release.yml` に実装する。署名済み MSI に対して SHA256 を算出し、署名アクション/ツールは SHA でピン留めする。詳細は ADR-0014 決定 3・委任事項を参照。

---

## 費用まとめ

| 項目 | 目安 | 頻度 | 支払い |
|---|---|---|---|
| Sectigo OV 証明書（個人名義・リセラー） | 委任 A で確認（~$220/年 前後の見込み） | 年 | オーナー |
| GCP Cloud HSM 鍵 | 月 $1〜2.5 程度 | 月 | オーナー |
| GCP 署名操作 | $0.15/1 万署名（ほぼゼロ） | 従量 | オーナー |
| （フォールバック時）SSL.com GCP HSM attestation | 一回 $500 | 初回のみ | オーナー |

証明書 1 枚で Yagura・Open DUMP Viewer の両方を署名するため、この費用で両プロジェクトを賄える。

## AI エージェントが行わないこと（安全境界）

次はオーナー本人が行う。AI は代行しない: 支払い情報の入力、アカウント作成、本人確認（政府発行 ID の提出等）、証明書の発注、GCP 請求先の登録。AI は手順書の作成・`gcloud`／ワークフローの実装・レビューを担当する。

## 出典（2026-07-15 確認）

- [Sectigo: OV Code Signing Validation for Organizations and Individuals](https://www.sectigo.com/knowledge-base/detail/OV-Code-Signing-Validation-for-Organizations-and-Individuals) — 個人発行可・O フィールドは法定名 or 登録済み DBA
- [Google Cloud KMS: Sign with SignTool and the CNG provider](https://docs.cloud.google.com/kms/docs/reference/cng-signtool) — HSM 鍵での Authenticode 署名手順・attestation
- [Certera: How to Use Google Cloud KMS with Sectigo Code Signing](https://certera.com/kb/how-to-use-google-cloud-kms-with-sectigo-code-signing-certificates/) — enrollment で GCP KMS を選択し CSR + attestation を提出
- [SSL.com: Implementing Code Signing with Google Cloud HSM](https://www.ssl.com/guide/implementing-code-signing-with-google-cloud-hsm/) / [Supported Cloud HSMs](https://www.ssl.com/guide/supported-cloud-hsms-document-signing-ev-code-signing/) — フォールバック候補・attestation $500
- [SSL.com: IV Code Signing](https://www.ssl.com/products/software-integrity/code-signing/iv/) — 個人発行・事業登録不要・本名表示
- 実装事例（法人名義）: [zenn.dev/inop](https://zenn.dev/inop/articles/7db6acf976c519)、[dev.to/katz](https://dev.to/katz/building-a-cost-effective-windows-code-signing-pipeline-sectigo-google-cloud-kms-on-github-2ghf)
