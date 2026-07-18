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

## フェーズ 1: GCP KMS のセットアップ ✅ 完了（2026-07-15）

以下の構成で作成済み（Cloud Shell で実行）。**この名前・パスは以降のフェーズ 2・3 で共通で使う**。

| 項目 | 値 |
|---|---|
| プロジェクト ID / 番号 | `code-signing-502513` / `275116585205` |
| リージョン | `asia-northeast1`（東京。Cloud HSM 対応） |
| 鍵リング | `codesign` |
| 鍵 | `codesign-rsa4096`（RSA 4096・PKCS#1 v1.5・SHA256・**HSM 保護**・version 1 ENABLED） |
| 鍵パス | `projects/code-signing-502513/locations/asia-northeast1/keyRings/codesign/cryptoKeys/codesign-rsa4096` |

CSR は Cloud Shell で `libengine-pkcs11-openssl` + Google の `libkmsp11`（PKCS#11 ライブラリ）+ `openssl req -engine pkcs11` で生成し、attestation バンドル（ZIP）は Console（鍵 version の ⋮ → Verify attestation → Download Attestation Bundle）から取得して Sectigo の enrollment（Install on Existing HSM → Google Cloud KMS (Cloud HSM)）に提出済み。**鍵名だけ**を `pkcs11:object=codesign-rsa4096` で指定する（フルパスは CKA_ID の文字数制限で不可）。

> 鍵アルゴリズムは RSA を選んでいる。SignTool + Google Cloud KMS CNG provider は RSA のみ対応（EC 不可）で、jsign は RSA/EC いずれも可。両にらみのため RSA 4096 で統一した。

## フェーズ 2: WIF + リポジトリ別サービスアカウント + IAM + 監査ログ ✅ `gcloud` 部分は完了（2026-07-18）

> **実行済みの状態（2026-07-18 に `describe` / `get-iam-policy` で実機確認）**
>
> - プロバイダの `attributeCondition` = `(assertion.repository=='Yanai-Taketo/Yagura' || assertion.repository=='Open-DUMP-Viewer/Open-DUMP-Viewer') && assertion.environment=='release-signing'`
> - `attributeMapping` に `attribute.environment: assertion.environment` を含む
> - 鍵 `codesign-rsa4096` の IAM は両 SA に `roles/cloudkms.signerVerifier` **のみ**
> - `yagura-signer` は Yagura リポジトリ、`odv-signer` は ODV リポジトリからのみ借用可
>
> **未了: 監査ログの有効化（委任 4。下記）。** これが済むまで「事後の検知・帰属」は成立しない。

GitHub Actions の OIDC トークンを GCP が信頼させる設定。**長期の秘密鍵を GitHub Secrets に置かない**ための要（ADR-0014 委任 3）。**証明書の発行を待たずに実行できる**（署名する対象の証明書が無くても、認証基盤は先に作れる）。Cloud Shell で上から順に実行する。

```bash
gcloud config set project code-signing-502513
PROJECT_NUMBER=275116585205

# 2-1) Workload Identity Pool と GitHub OIDC プロバイダ
gcloud iam workload-identity-pools create github-pool \
  --location=global --display-name="GitHub Actions pool"

# 信頼条件(委任3)。2 段で絞る:
#   1) リポジトリ = Yagura と Open DUMP Viewer からのトークンだけ(fork を弾く)
#   2) Environment = 承認ゲート付きの release-signing で走るジョブだけ
#
# 2) が要る理由: リポジトリだけで絞ると「そのリポジトリの任意のブランチの任意の workflow」が
# 署名鍵を使えてしまう。決定3 の承認ゲートは release.yml の中に書かれた自主ルールにすぎず、
# GCP 側は承認の有無を見ないため、別の workflow を 1 つ足すだけでゲートを迂回して
# オーナーの実名で任意のバイナリに署名できる。2) を入れると承認ゲートが GCP の境界で強制される。
gcloud iam workload-identity-pools providers create-oidc github-provider \
  --location=global --workload-identity-pool=github-pool \
  --display-name="GitHub OIDC" \
  --issuer-uri="https://token.actions.githubusercontent.com" \
  --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository,attribute.ref=assertion.ref,attribute.environment=assertion.environment" \
  --attribute-condition="(assertion.repository=='Yanai-Taketo/Yagura' || assertion.repository=='Open-DUMP-Viewer/Open-DUMP-Viewer') && assertion.environment=='release-signing'"

# 2-2) リポジトリ別サービスアカウント(署名主体を分離。決定1 リスク3の緩和策)
gcloud iam service-accounts create yagura-signer --display-name="Yagura release signer"
gcloud iam service-accounts create odv-signer    --display-name="Open DUMP Viewer release signer"

# 2-3) 最小権限 IAM: 各 SA に「この鍵での署名/検証」だけを鍵リソース単位で付与(委任5)。
#      プロジェクト全体の権限は与えない。
#
# 注意: 2-2 の直後にこれを流すと "Service account ... does not exist" で失敗することがある
# (サービスアカウント作成の伝播遅延。2026-07-18 に実機で発生)。その場合は数十秒おいて
# 失敗した SA の分だけ再実行すればよい。冪等なので再実行して害はない。
for SA in yagura-signer odv-signer; do
  gcloud kms keys add-iam-policy-binding codesign-rsa4096 \
    --location=asia-northeast1 --keyring=codesign \
    --member="serviceAccount:${SA}@code-signing-502513.iam.gserviceaccount.com" \
    --role="roles/cloudkms.signerVerifier"
done

# 2-4) WIF から各 SA を借用できる紐付け(リポジトリ単位に限定)。
#      Yagura の CI は yagura-signer しか、ODV の CI は odv-signer しか借用できない。
gcloud iam service-accounts add-iam-policy-binding \
  yagura-signer@code-signing-502513.iam.gserviceaccount.com \
  --role="roles/iam.workloadIdentityUser" \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/github-pool/attribute.repository/Yanai-Taketo/Yagura"

gcloud iam service-accounts add-iam-policy-binding \
  odv-signer@code-signing-502513.iam.gserviceaccount.com \
  --role="roles/iam.workloadIdentityUser" \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/github-pool/attribute.repository/Open-DUMP-Viewer/Open-DUMP-Viewer"
```

**この信頼条件が成立する根拠（2026-07-18 に公式ドキュメントで確認）**:

- GitHub Actions の OIDC トークンには、ジョブが Environment を使うとき `environment` クレームが入る（[GitHub 公式](https://docs.github.com/en/actions/deployment/security-hardening-your-deployments/about-security-hardening-with-openid-connect)）
- GCP の `attribute-condition` は CEL であり、**true と評価されたときだけ資格情報を受理し、それ以外は拒否する**（[Google 公式](https://docs.cloud.google.com/iam/docs/workload-identity-federation)）。したがって Environment を使わないジョブは `environment` クレームを持たず、条件が満たされないため拒否される（fail-closed）

**Environment 名 `release-signing` は Yagura と Open DUMP Viewer で共通の規約になる。** プロバイダを 2 リポジトリで共用しており、信頼条件は両者に等しく効くため、署名するジョブは必ずこの名前の Environment を使う。ODV 側の署名を実装するときも同名の Environment を作ること。

**Environment は WIF を作る前に GitHub 側で作成しておく。** 存在しない Environment 名をジョブに書くとトークンが発行されず、署名も疎通確認も通らない。

**監査ログの有効化（委任 4。既定は無効。これが無いと署名記録が残らず、決定 1 の「事後の検知・帰属」が成立しない）**: Console の方が確実。
GCP コンソール → **IAM と管理 → 監査ログ** → 一覧から **「Cloud Key Management Service (KMS) API」** を選び、**「データ読み取り」「データ書き込み」にチェック → 保存**。以後、どの SA がいつ署名操作をしたかが Cloud Audit Logs に残る。

**署名操作のアラート（委任 13。事後追跡を能動検知へ）**: Cloud Logging で、`protoPayload.methodName` が `AsymmetricSign` 系かつ想定外の principal/時間帯のログにログベースの指標 + アラートを設定する（証明書運用が定常化してから設定でよい。フェーズ 3 完了後の hardening 項目）。

**この決定に紐づく GitHub 側の値**（フェーズ 3 で使う。Secrets ではなく公開してよい値なので、リポジトリ変数か workflow に直書きでよい）:
- WIF プロバイダ: `projects/275116585205/locations/global/workloadIdentityPools/github-pool/providers/github-provider`
- サービスアカウント（Yagura）: `yagura-signer@code-signing-502513.iam.gserviceaccount.com`

## フェーズ 3: リリースワークフローへの署名工程の組み込み（証明書発行後に実装・検証）

ADR-0014 決定 3 の構造を `release.yml` に実装する。**署名ステップは実際の証明書が無いと通し検証できない**ため、実装 PR は証明書受領後にマージする（現行の未署名リリース経路を壊さないよう、証明書が揃うまで main の署名は有効化しない）。以下は確定済みの設計。

### ジョブ構造（決定 3）

```
build (x64 / arm64)  … 未署名 MSI を artifact 化
  → 未署名 MSI に対して Full E2E（既存の smoke-x64 / smoke-arm64）
  → sign（GitHub Environment "release-signing" の承認ゲート = 人間の承認）
       · google-github-actions/auth で WIF から yagura-signer の短命トークンを取得
       · Yagura.Host.exe と Yagura.*.dll を deep signing → 署名済み publish 出力から MSI を再ビルド → MSI を署名
       · タイムスタンプは主+副 TSA でフォールバック（決定 9）
  → 署名済み MSI に対して Full E2E を再実行 + 署名/Subject/タイムスタンプ検証
  → 署名済み MSI の SHA256 を算出（未署名の値は使わない）
  → create-release（署名済み MSI + .sha256 のみ公開。全アーキ揃わなければ非公開）
  → 公開後検証（公開資産を取り直して署名・Subject・.sha256 を突合、証跡記録）
```

### 実装の要点

- **署名ツールは jsign**（決定 9 の TSA フォールバック要件に素直に合う。`--tsaurl <主> <副>` で複数指定できる）。`--storetype GOOGLECLOUD`、`--keystore <鍵リングパス>`、`--alias codesign-rsa4096`、`--storepass <WIF アクセストークン>`、`--certfile <Sectigo 発行の証明書チェーン>`。SignTool + Cloud KMS CNG は代替（Windows ランナー・単一 TSA）
- **認証**: `google-github-actions/auth`（**SHA ピン留め**: `@7c6bc770dae815cd3e89ee6cdf493a5fab2cc093` = v3.0.0）で `token_format: access_token` を取り、jsign の `--storepass` に渡す。長期鍵 JSON は置かない
- **deep signing（委任 2）**: MSI 内部の自前バイナリ（`Yagura.Host.exe`・`Yagura.*.dll`）を署名してから WiX で再パッケージし、最後に MSI を署名する。上流バイナリ（.NET ランタイム・MudBlazor）は署名しない。sign ジョブは署名済み publish 出力から `-p:SkipYaguraHostPublish=true -p:YaguraPublishDir=<署名済み出力>` で MSI を焼く
- **承認ゲート**: GitHub Environment `release-signing`（required reviewers = オーナー）で人間の承認を挟む（決定 3。(A) SignPath の手動承認に相当する統制を (C2) で再現）。WIF の紐付けを将来この Environment に絞ると更に堅い（委任 3 の hardening）
- **skip 条件**: WIF 設定が使えない実行（`pull_request`・fork）では sign ジョブを skip し、未署名ビルド + E2E の確認までで止める（Release は作らない）。決定 3
- **証明書チェーン**: Sectigo 発行の証明書（+中間証明書）をどこから供給するか（リポジトリ同梱の公開証明書 or Secrets）は実装 PR で確定。公開証明書自体は秘密ではない
- **全アクションを SHA ピン留め**（決定 3。`cla.yml` に前例）
- **退避弁・復旧手順・公開後検証・全アーキ all-or-nothing** は決定 3・8・9・委任 11 のとおり実装する

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
