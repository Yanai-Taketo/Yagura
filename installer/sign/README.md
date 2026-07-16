# コード署名(Sectigo + Google Cloud KMS)

[ADR-0014](../../docs/adr/0014-code-signing.md) の実装。リリース成果物(MSI と自前アセンブリ)を
GCP KMS の HSM 鍵で Authenticode 署名する。運用の全体像・発注手順は
[docs/development/code-signing-setup.md](../../docs/development/code-signing-setup.md) を参照。

## この配下

| ファイル | 役割 |
|---|---|
| `Invoke-YaguraSign.ps1` | jsign(GCP KMS)で自前バイナリ + MSI を署名し、Subject・タイムスタンプまで検証する |
| `codesign-chain.pem` | **証明書チェーン(leaf + 中間 CA)。公開情報なので同梱する。証明書受領後に追加する(未追加)** |

## 署名対象(ADR-0014 決定2)

- 署名する: `Yagura-<版>-<arch>.msi`、MSI 内部の `Yagura.Host.exe`・`Yagura.*.dll`(deep signing)
- 署名しない: .NET ランタイム(`coreclr.dll` 等。Microsoft 署名済み)、MudBlazor 等の第三者 DLL(上流の形のまま同梱)

deep signing は「自前アセンブリを署名 → 署名済み publish 出力から MSI を再ビルド → MSI を署名」の順で行う
(release.yml の `sign` ジョブ)。

## ピン留め(供給網保護。2026-07-17 確認)

| 依存 | 版 | 検証 |
|---|---|---|
| jsign | **7.5** | jar SHA256 = `602a51c3545a6dc4fb99bd2ea7152b26d1345916d0c93ddfbd5936cb735af91c`(release.yml で照合) |
| `google-github-actions/auth` | v3.0.0 | commit SHA `7c6bc770dae815cd3e89ee6cdf493a5fab2cc093` でピン |

TSA(タイムスタンプ)は主 `http://timestamp.sectigo.com` + 副 `http://timestamp.digicert.com` の
フォールバック(決定9)。全滅時は fail-closed。

## GCP リソース(フェーズ 2 で作成)

- 鍵リング: `projects/code-signing-502513/locations/asia-northeast1/keyRings/codesign`
- 鍵(エイリアス): `codesign-rsa4096`(RSA 4096・HSM)
- WIF プロバイダ: `projects/275116585205/locations/global/workloadIdentityPools/github-pool/providers/github-provider`
- サービスアカウント(Yagura): `yagura-signer@code-signing-502513.iam.gserviceaccount.com`

## GitHub の設定(署名を有効化するとき)

`release.yml` の `sign` ジョブは、リポジトリ変数 **`YAGURA_SIGNING` が `enabled` のとき**だけ動く
(未設定なら現行の未署名リリースのまま。証明書・WIF が揃うまで壊さないため)。

- リポジトリ変数(`vars`。公開してよい値):
  - `YAGURA_SIGNING = enabled`
  - `GCP_WIF_PROVIDER = projects/275116585205/.../providers/github-provider`
  - `GCP_SIGNER_SA = yagura-signer@code-signing-502513.iam.gserviceaccount.com`
  - `GCP_KEYRING = projects/code-signing-502513/locations/asia-northeast1/keyRings/codesign`
  - `GCP_KEY_ALIAS = codesign-rsa4096`
  - `SIGNER_SUBJECT = YANAI Taketo`(署名者 Subject の期待値。証明書の名前訂正後に確定)
- GitHub Environment **`release-signing`**(required reviewers = オーナー)= 承認ゲート(決定3)
- Secrets は不要(WIF で短命トークンを取得するため長期秘密を置かない)

## 通し検証(証明書 + WIF が揃ってから)

1. フェーズ 2 の `gcloud`(WIF/SA/IAM)を実行済みにする
2. `codesign-chain.pem` を追加(訂正版証明書 + 中間 CA)
3. 上記リポジトリ変数と Environment を設定
4. `gh workflow run release.yml -f publish_release=false` で**公開せず**署名までを試す
   (`create-release` は publish_release=false では走らないため、署名・署名検証まで確認できる)
5. 署名済み MSI の SmartScreen 実挙動を lab で確認(委任事項)

**未検証のまま本番リリースに使わないこと。** 現状 `codesign-chain.pem` 未追加・WIF 未確認のため、
`YAGURA_SIGNING` は未設定のまま(未署名リリースが継続)。
