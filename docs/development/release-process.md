# リリース手順

Yagura のリリース（署名付き MSI を GitHub Release で公開すること）の手順と規則を、この文書 1 本にまとめる。**リリースの手順と規則の正はこの文書**であり、ほかの文書は次の役割だけを持つ。

| 文書 | 役割 |
|---|---|
| [installer/sign/README.md](../../installer/sign/README.md) | 署名基盤の参照資料（署名対象・ピン留め・GCP リソース・リポジトリ変数と Environment の値） |
| [code-signing-setup.md](code-signing-setup.md) | 署名基盤を構築したときの記録（発注・GCP・WIF） |
| [docs/code-signing-policy.md](../code-signing-policy.md) | 利用者向けの署名ポリシー（署名者の期待値・検証手順） |
| [.github/workflows/release.yml](../../.github/workflows/release.yml) | リリースの実装。この文書の記述は release.yml の実装に合わせる |

**凡例**: 「**未検証**」と書いた手順は、release.yml の実装と GitHub・gh の公式文書から組み立てたもので、まだ実際に通したことがない。使ったら結果をこの文書に反映する。

---

## 1. 規則

- **バージョンの単一の正は [Directory.Build.props](../../Directory.Build.props) の `<YaguraVersion>`**（ADR-0014 決定 7）。アセンブリと MSI（`installer/Package.wxs` の `Version="$(var.YaguraVersion)"`）がともにこれを参照するため、git タグ・MSI・アセンブリの三者は構造的に一致する。版を上げるときは chore(release) PR でこの値だけを変える（`Package.wxs` に版を直接書かない）。release.yml はタグと本値の一致を検証し、不一致なら失敗する
- **バージョンタグを付け直さない（再 push 禁止）**。タグは push した時点でリポジトリ上に公開される。Release の作成前に止まった場合も、原則として同じタグを消して付け直さず、次のパッチ版で出す（例外の判断は §10）
- **公開するのは署名済み MSI（x64・ARM64）とその `.sha256` だけ**。全アーキの署名済み MSI が揃わなければ公開しない（release.yml が fail-closed で強制する）
- **未署名の MSI を公開しない**。例外は ADR-0014 決定 8 の退避弁だけで、その手順は release.yml にまだない（§10）
- **リリース資産には `.sha256` を必ず添付する**（release.yml が自動で作る）
- **MSI はビルドごとに再現しない**ため、サイズ・SHA256 の byte-exact 値を文書に書かない。「約 N MB」と書き、Release 資産の `.sha256` を正とする

## 2. 役割と前提

### 2.1 誰が何をするか

| 操作 | 担当 |
|---|---|
| リリース準備の確認・chore(release) PR の作成 | オーナー（AI エージェントが代行してよい） |
| chore(release) PR のマージ | オーナー |
| タグの作成と push | オーナー |
| 署名の承認（Environment `release-signing`） | オーナー |
| リポジトリ変数・Environment・ruleset の変更 | オーナー |
| 公開後の確認と記録 | オーナー（AI エージェントが補助してよい） |

タグの push で起動した run は、再実行しても起動者（タグを push した人）の権限で動く（GitHub 公式文書）。

### 2.2 タグを push する前に確かめる設定

- **リポジトリ変数 `YAGURA_SIGNING` が `enabled` であること**。`enabled` でないと release.yml は署名系のジョブ（`sign`・`smoke-signed-*`）を飛ばし、`create-release` は**未署名の MSI を公開する**。確認は Settings → Secrets and variables → Actions → Variables、または `gh variable list`
- 署名に使うほかのリポジトリ変数（`GCP_WIF_PROVIDER`・`GCP_SIGNER_SA`・`GCP_KEYRING`・`GCP_KEY_ALIAS`・`SIGNER_SUBJECT`）が [installer/sign/README.md](../../installer/sign/README.md)「GitHub の設定」の値どおりであること
- **Environment `release-signing`** の required reviewers にオーナーが入っていること。**「Prevent self-reviews」を有効にしない**——有効にすると、run を起動した本人（タグを push したオーナー）は承認できず、単一メンテナ体制では誰も承認できなくなる（GitHub 公式文書）。Environment の名前は変えない（WIF の信頼条件がこの名前を要求しており、変えるとトークンが発行されず署名できない。installer/sign/README.md）
- **証明書が有効期限内であること**。期待値と期限は [code-signing-policy.md](../code-signing-policy.md)「署名者の期待値」。期限が近い場合は先に更新する（更新手順はこの文書では扱っていない。§11）
- **`v*` タグの保護**（ADR-0014 委任 12）: タグの作成・移動・削除を ruleset で制限しているかを Settings → Rules → Rulesets で確かめる
- オーナーの GitHub・Google Cloud アカウントで MFA が有効であること（ADR-0014 決定 6）

## 3. リリース準備（当日の確認）

タグを push する日に行う。結果は chore(release) PR の body に残す（§4）。

### 3.1 外部依存

- 未マージの Dependabot PR と、Security タブの依存アラートを確認する。脆弱性の修正は chore(release) PR とは別の PR で先にマージする（依存を上げる PR の記録の仕方は [conventions.md](conventions.md)「PR の要件」の `Vendored:` 行）
- NuGet の脆弱性監査は release.yml の build ジョブの restore でも走り、NU1900〜1904 をエラーにする。前日に main の CI が緑でも、当日に新しい脆弱性情報が公表されればタグ後のビルドで止まる
- 署名工程のピン留め（jsign・`google-github-actions/auth`。[installer/sign/README.md](../../installer/sign/README.md)「ピン留め」）について、上流で脆弱性が告知されていないかを確認する。版を上げる場合は別の PR にする

### 3.2 .NET の最新パッチ

同梱される .NET ランタイムの版は、どこにも固定していない。次の 2 段で決まる。

1. release.yml の `actions/setup-dotnet` は [global.json](../../global.json)（`10.0.100` + `rollForward: latestFeature`）を読み、10.0 系で最も新しい SDK を導入する（[setup-dotnet の README](https://github.com/actions/setup-dotnet#using-the-global-json-file-input)）
2. self-contained の publish は、その SDK が持つ最新パッチのランタイムを収める（[.NET 公式文書「Self-contained deployment runtime roll forward」](https://learn.microsoft.com/dotnet/core/deploying/runtime-patch-selection)）。Yagura 側に `RuntimeFrameworkVersion` の指定はない

したがって当日のビルドは、その時点の最新パッチを収めるはずである。準備では次を行う。

- .NET 10 の最新パッチを公式のリリース情報で確認し、版を控える（[.NET 10 のダウンロードページ](https://dotnet.microsoft.com/download/dotnet/10.0)。機械可読な一覧は [dotnet/core の releases.json](https://github.com/dotnet/core/blob/main/release-notes/10.0/releases.json) の `latest-runtime`）
- 公開後に、実際に収められた版がこれと一致することを確かめる（§8.2 の 5）

### 3.3 Fluent Bit の検証済み版

- 現在の検証済み版は `ForwarderKitConstraints.VerifiedFluentBitVersion`（利用者ガイドとの一致は CI が検知する）
- 当日の最新版と CVE の有無を、公式の配布ページ（[Fluent Bit Windows downloads](https://docs.fluentbit.io/manual/installation/downloads/windows)）とリリース情報で確認する
- 上げるか据え置くかを決める。上げる場合は [conventions.md](conventions.md)「フォワーダ配布キット（Fluent Bit）の版運用」の受け入れ条件（実機スモーク E2E と `Vendored:` 行）を満たす別の PR を先にマージする。据え置く場合はその判断を chore(release) PR の body に 1 行残す

### 3.4 入口文書とリリースノートの雛形

- README・SECURITY.md の版表記とサポート表が、このリリースの状態と合っていること（[conventions.md](conventions.md)「PR の要件」の入口文書の規約）。直す場合は chore(release) PR とは別の PR で先にマージする
- **リリースノートは release.yml の雛形から生成される固定文**で、リリースごとの変更点を書く欄はない（ほかに入るのは、署名が有効なときの署名の節だけ）。雛形の文言（版番号を含む注意書きなど）がこのリリースに合っているかを読み、直す場合は release.yml を変える PR を**タグより前に**マージする。タグの run も再実行も、タグが指すコミットの release.yml を使うため、タグの後で直しても反映されない
- release.yml を変える PR では、その PR 上で release.yml 自体が build と smoke まで走る（署名と公開はしない）

## 4. chore(release) PR

- ブランチ名は `chore/release-vX.Y.Z`、PR タイトルは `chore(release): vX.Y.Z`
- **変更は `Directory.Build.props` の `<YaguraVersion>` の 1 行だけ**にする（入口文書・雛形の修正は §3 のとおり先に済ませる）
- body に書くこと: 前回のリリースからの主な変更の要約、§3.2 で控えた .NET の最新パッチ、§3.3 の Fluent Bit 版の判断
- `Directory.Build.props` を変えるため、必須チェックの CI（`build-and-test`）に加えて Installer E2E（`installer-e2e.yml`）も走る。両方が緑になってから squash merge する

## 5. main の CI が緑であることを確かめる

- タグを付けるのは、chore(release) PR をマージした後の **main の先頭のコミット**とする。マージの後にほかの PR が入っていれば、それも含めてリリースされる
- そのコミットで走った CI が success であることを確かめる:

  ```bash
  git fetch origin
  SHA=$(git rev-parse origin/main)
  gh run list --workflow ci.yml --commit "$SHA"
  gh run list --workflow codeql.yml --commit "$SHA"
  ```

- そのコミットの `<YaguraVersion>` が、これから付けるタグと一致することを確かめる（一致しないと build ジョブが失敗し、押したタグが無駄になる）:

  ```bash
  git show "$SHA:Directory.Build.props" | grep '<YaguraVersion>'
  ```

## 6. タグの作成と push（オーナー）

```bash
git tag -a vX.Y.Z -m "Yagura vX.Y.Z" "$SHA"
git push origin vX.Y.Z
```

- タグ名は `v` + `<YaguraVersion>` と完全に一致させる
- 注釈付きタグにする（作成者と日時がタグに残る）
- **タグは 1 つずつ名前を指定して push する**。`git push --tags` などで 4 つ以上のタグを同時に push すると push イベントが作られず、release.yml が起動しない（GitHub 公式文書）
- GitHub の Releases 画面から手で Release やタグを作らない。release.yml の `create-release` は、Release がまだなければノート付きで作成し、既にあれば資産を上書きするだけでノートは書かないため、手で作った Release があると生成したノートが載らない

## 7. release.yml の流れと署名の承認

### 7.1 ジョブの流れ

```
build (x64 / arm64)
    版の解決（タグと <YaguraVersion> の照合）・依存の脆弱性監査・未署名 MSI と .sha256 の作成
  → smoke-x64 / smoke-arm64
    未署名 MSI の Full E2E（ARM64 は windows-11-arm の実機ランナー。スプール発動 → drain の 1 サイクルも通す）
  → sign (x64 / arm64)                    ★ Environment release-signing の承認待ち
    自前アセンブリ（Yagura.Host.exe・Yagura.*.dll）の署名 → 署名済みの出力から MSI を再ビルド
    → MSI の署名 → 署名後の MSI で .sha256 を算出
  → smoke-signed-x64 / smoke-signed-arm64
    署名済み MSI の Full E2E
  → create-release
    署名済み MSI の検証（2 アーキ揃っていること・署名 Valid・署名者 Subject・タイムスタンプ。1 つでも欠ければ公開しない）
    → リリースノートの生成 → GitHub Release の作成 → 公開資産を取り直しての検証
```

- `sign` と `smoke-signed-*` は、`YAGURA_SIGNING = enabled` かつ（タグの push または手動 dispatch）のときだけ動く。`sign` の matrix は `fail-fast: true` で、片方のアーキが失敗すると、もう片方は打ち切られる
- `create-release` は、タグの push では常に動き、手動 dispatch では入力 `publish_release` が `true` のときだけ動く
- 所要時間の目安: v0.5.0 のときは承認待ちを含めて約 11 分だった

**手動 dispatch での予行**: `gh workflow run release.yml -f publish_release=false` で、公開せずに build から smoke-signed までを通せる（`--ref` を付けないので既定ブランチの main で動く。署名が有効なら承認を求められる）。この経路は、署名基盤を本番のリリースに使う前の通し検証で通している（[installer/sign/README.md](../../installer/sign/README.md)「通し検証」の手順 7）。

- **署名基盤の設定を変えたとき（証明書の更新・WIF や IAM の変更など）は、本番のリリースの前にこの予行で署名を試す**
- **予行に `publish_release=true` を付けない**。§9.3 のタグからの dispatch とは `--ref` の有無しか違わないため取り違えやすいが、ブランチから `publish_release=true` で動かすと、タグの push（§5・§6 の確認）を経ずに Release が作られる（§9.5）
- **予行で署名した MSI は配布物ではない**。いまの `<YaguraVersion>`（多くは公開済みの版）を名乗り、中身は main の先頭のコードで、本物の証明書で正しく署名されている。しかも run の artifact（`release-msi-signed-x64`・`release-msi-signed-arm64`）は、保持期間（既定 90 日）のあいだ、GitHub にサインインした誰でも取得できる（公開リポジトリのため。GitHub 公式文書）。そこで次のようにする
  - 予行が済んだら、署名済み MSI の artifact を消す。run のページの「Artifacts」で、`release-msi-signed-*` の横のごみ箱のアイコンを押す（GitHub 公式文書「Removing workflow artifacts」）。gh では次のとおり（未検証）:

    ```bash
    gh api repos/{owner}/{repo}/actions/runs/<run-id>/artifacts --jq '.artifacts[] | {id, name}'
    gh api -X DELETE repos/{owner}/{repo}/actions/artifacts/<release-msi-signed-* の id>
    ```

  - リリースに合わせて予行する場合は、版を上げた chore(release) PR のマージの後、タグを付ける前に行う。名乗る版と中身が、これから公開するものと同じコミットになる（それでも MSI はビルドごとに再現しないため、公開資産とは別のファイルであり、artifact は上のとおり消す）

### 7.2 承認の前に確かめること

承認の要求は、`sign` の前のジョブ（build・smoke）がすべて成功してから run のページに出る。承認する前に次を確かめる。

1. ワークフローが `Release`（`.github/workflows/release.yml`）で、起動者が自分であること。イベントと ref が次のどちらかであること。**身に覚えのない承認要求は却下する**——署名鍵を使えるのは Environment `release-signing` を通ったジョブだけなので、この承認が最後の関門になる
   - タグの push で起動した run: イベントが push、ref がいま push したタグ
   - タグからの dispatch で起動した run（§9.3）: イベントが workflow_dispatch、ref がタグ `vX.Y.Z`（`refs/tags/vX.Y.Z`）。ref が main などのブランチなら公開してはいけない run なので、承認しない（§9.5）。例外は、§7.1 の予行（`publish_release=false`）を自分で起動したときだけである
2. run のコミットが §5 で確かめたコミットと一致すること
3. build ジョブの「Resolve release version」のログに `resolved version = X.Y.Z (source: git tag)` と出ていること。ブランチから動いた run では `(source: Directory.Build.props (workflow_dispatch))` と出る（release.yml の実装）
4. build・smoke-x64・smoke-arm64 がすべて success であること

### 7.3 承認の操作

run のページで「Review deployments」→ `release-signing` を選ぶ →「Approve and deploy」。v0.5.0 では、1 回の承認で `sign (x64)` と `sign (arm64)` の両方が動き出した。

「Reject」を選ぶと run は失敗し、何も公開されない。承認の要求は最長 30 日待つ（GitHub 公式文書）。

## 8. 公開後の確認と記録

### 8.1 release.yml が自動で行う確認

`create-release` の「Post-publish verification」が、公開された資産を `gh release download` で取り直し、各 MSI について次を確かめる。1 つでも外れると失敗する。

- Authenticode 署名が `Valid` であること
- 署名者の Subject に `SIGNER_SUBJECT` が含まれること
- `.sha256` が公開されており、その値が MSI の実体の SHA256 と一致すること

成功すると、各 MSI について `OK <ファイル名>: Subject='…' Thumbprint=… NotAfter=…` をログに出す。タイムスタンプの有無は、この段ではなく公開前の検証（Verify signed artifacts）で確かめている。

### 8.2 手で行う確認

1. **Release のページ**: タイトルが `Yagura vX.Y.Z`、資産が 4 つ（`Yagura-X.Y.Z-x64.msi`・`Yagura-X.Y.Z-arm64.msi` と、それぞれの `.sha256`）、下書きやプレリリースではなく、Latest になっていること
2. **公開後検証のログ**: 上の `OK …` の行が 2 つあり、Thumbprint が [code-signing-policy.md](../code-signing-policy.md) の期待値と一致すること
3. **別の Windows 端末での検証**: 公開資産を取り直し、release.yml の公開後検証と同じ判定にタイムスタンプの確認を加えて行う。

   ```powershell
   $v = 'X.Y.Z'
   gh release download "v$v" --repo Yanai-Taketo/Yagura --pattern '*.msi' --pattern '*.sha256' --dir .\verify
   Get-ChildItem .\verify\*.msi | ForEach-Object {
     $sig = Get-AuthenticodeSignature -LiteralPath $_.FullName
     $expected = ((Get-Content -LiteralPath "$($_.FullName).sha256" -Raw) -split '\s+')[0]
     $actual = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
     [pscustomobject]@{
       Name        = $_.Name
       Status      = $sig.Status
       Subject     = $sig.SignerCertificate.Subject
       Thumbprint  = $sig.SignerCertificate.Thumbprint
       TimeStamped = ($null -ne $sig.TimeStamperCertificate)
       Sha256Match = ($actual -eq $expected)
     }
   } | Format-List
   ```

   期待値: `Status` が `Valid`、`Subject` と `Thumbprint` が code-signing-policy.md の期待値どおり、`TimeStamped` と `Sha256Match` が `True`

   上の PowerShell が出すのはタイムスタンプの有無だけで、時刻は出さない（`TimeStamperCertificate` は TSA の証明書であり、時刻ではない）。**タイムスタンプの時刻**は osslsigncode で読む。出力の `Countersignatures:` の下の `Timestamp time:` が時刻である（osslsigncode 2.8 で、公開済みの v0.5.0 の MSI に対して確認。Debian・Ubuntu では `apt install osslsigncode`）。

   ```bash
   osslsigncode verify -in Yagura-X.Y.Z-x64.msi | grep 'Timestamp time'
   osslsigncode verify -in Yagura-X.Y.Z-arm64.msi | grep 'Timestamp time'
   ```

4. **リリースノート**: 表示された本文を読み、ダウンロード表のファイル名と版、署名の節の署名者名、リンク先、雛形の文言がこのリリースに合っていることを確かめる。直すときは `gh release edit vX.Y.Z --notes-file <ファイル>` で書き換える（再実行してもノートは更新されない。§9.1）
5. **同梱 .NET の版**: 公開された x64 MSI を検証用の端末に導入し、収められたランタイムの版が §3.2 で控えた最新パッチと一致することを確かめる。

   ```powershell
   (Get-Content 'C:\Program Files\Yagura\Yagura.Host.runtimeconfig.json' -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
   ```

   `Microsoft.NETCore.App` の `version` を見る（インストール先を既定から変えた場合はパスを読み替える）

### 8.3 記録

次の値を、リリースを追跡する Issue（なければ chore(release) PR）にコメントで残す。

- release.yml の run の URL
- 各 MSI の SHA256（`.sha256` の値）と、証明書の Thumbprint・NotAfter、タイムスタンプの時刻（§8.2 の 3）
- §8.2 の 3・5 の結果（同梱 .NET の版を含む）と、Fluent Bit の検証済み版

Actions の run・ログ・artifact は保持期間（公開リポジトリでは最長 90 日）を過ぎると消えるため、run の中だけに残しておくと後から辿れない。どのリリースをどの証明書で署名したか、いつ署名したかの記録は、証明書が失効したときに影響範囲を告知するのにも要る（ADR-0014 決定 3 は証明書の thumbprint と署名日時の記録を求めている）。公開資産を削除して配布を止めた後（§9.4）は、時刻を取り直す手段が残らない。

## 9. 途中で止まったときの復旧

### 9.1 前提になる事実

GitHub 公式文書で確かめた事実（2026-11-16 確認）:

- run の再実行は、**最初の実行から 30 日以内**に限られる。1 つの run を再実行できるのは最大 50 回
- 再実行は、元の run と**同じコミット（`GITHUB_SHA`）と同じ ref（`GITHUB_REF`）**を使う。つまり、タグが指すコミットのコードと release.yml で動く
- 失敗したジョブだけ、または特定のジョブだけを再実行すると、それに依存するジョブも再実行される。再実行しないジョブは前回の出力をそのまま使う
- 承認の要求は最長 30 日待つ。run 全体は 35 日で打ち切られる

release.yml の実装から読み取れること:

- `create-release` は、Release がまだなければ `gh release create` でノート付きで作り、既にあれば `gh release upload --clobber` で資産を上書きするだけでノートは変えない
- `gh release create` は、資産付きのとき「下書きの作成 → 資産のアップロード → 公開」を別々の API 呼び出しで行う（`gh release create --help`）。アップロードや公開の呼び出しが失敗したときは gh が下書きを消すが、ランナーのタイムアウトやキャンセルで gh 自体が止まると、下書きの Release が残る（gh 2.89 のソース `pkg/cmd/release/create/create.go` の `cleanupDraftRelease`）
- `gh release view`・`upload`・`download`・`delete` は、タグ名で公開済みの Release だけでなく**下書きも見つける**（gh 2.89 のソース `pkg/cmd/release/shared/fetch.go` の `FetchRelease`）。そのため下書きが残っていると、`create-release` は「既にある」と判断して下書きへ資産を上げるだけで、**公開もノートの更新もしない**。続く公開後検証も下書きから資産を取り直して成功しうるため、run は緑で終わっても何も公開されていないことがある
- `gh release create` に `--verify-tag` を付けていない。そのため、指定したタグがまだリポジトリになければ、gh が既定ブランチのその時点の最新に軽量タグを自動で作る（`gh release create --help`）

### 9.2 まず「公開されたか」を見分ける

- `create-release` の「Create or update GitHub Release」まで進んでいない: **何も公開されていない**（タグは push 済み、Release はない）
- 「Create or update GitHub Release」で失敗した: Releases 画面（または `gh release list`）で、Release や下書きが残っていないかを確かめる。**下書きが残っていたら、再実行の前に消す**（未検証）。下書きを残したまま再実行すると、run は緑で終わっても何も公開されない（§9.1）

  ```bash
  gh release delete vX.Y.Z --yes
  ```

  `--cleanup-tag` は付けない（付けるとタグも消える。§1）
- 「Post-publish verification」で失敗した: **公開済み**。利用者が既に取得している可能性がある

### 9.3 公開前に止まった場合

| 状況 | 対処 |
|---|---|
| 承認待ちのまま（承認者が不在） | 戻ってから承認すれば、そのまま続く（承認の要求は最長 30 日待つ） |
| 承認を却下した / `sign` が失敗した（WIF・KMS・TSA の障害など）/ smoke の失敗がランナー側の一時的な原因 | 原因が解消してから、同じ run を再実行する（下の手順。未検証）。再実行した `sign` も Environment を参照するため、承認を求める |
| smoke・smoke-signed の失敗が製品や MSI の不具合 | 再実行では直らない（同じコミットを使うため）。main で直し、**次のパッチ版**として §4 からやり直す。押したタグは欠番として残す |
| release.yml 自体の不具合 | 同上。再実行もタグからの dispatch も、タグが指すコミットの release.yml を使う |
| 最初の実行から 30 日を過ぎた | 再実行はできない。下の「タグからの dispatch」を使う |

**同じ run の再実行（未検証）**

- **既定: 全ジョブの再実行**。何も公開されていないので、build から作り直しても失うものはない。

  ```bash
  gh run rerun <run-id>
  ```

  画面からは run のページの「Re-run all jobs」。

- **代替: 失敗したジョブだけの再実行**。成功済みのジョブの出力は引き継がれるが、前の試行で上げた artifact を後続のジョブが取得できるか、`sign` を再実行したときに同じ名前の artifact を上げ直せるかは確かめていない。

  ```bash
  gh run rerun <run-id> --failed
  ```

**タグからの dispatch（未検証）**

```bash
gh workflow run release.yml --ref vX.Y.Z -f publish_release=true
```

- **`--ref vX.Y.Z`（push 済みのタグ）を必ず付ける**。付けないと既定ブランチの main で動き、公開まで進んでしまう（§9.5）
- `GITHUB_REF` がタグになるため、build の版の照合はタグと `<YaguraVersion>` で行われる。`sign` は dispatch でも動き、`create-release` は `publish_release=true` で動く
- Environment の「Deployment branches and tags」の判定も `GITHUB_REF` に対して行われるため、タグの push の run と同じ結果になる
- 30 日を過ぎて再実行できない場合のほか、新しい run で最初からやり直したい場合にも使える

### 9.4 公開後に止まった場合

**公開後検証が失敗した**: まずログで、どの確認が外れたかを見る。

- 資産の取得の失敗など、一時的な原因の場合（未検証）: `create-release` だけを再実行する。Release が既にあるため、run の artifact を `--clobber` で上げ直してから、公開後検証をやり直す。

  ```bash
  gh run view <run-id> --json jobs --jq '.jobs[] | {name, databaseId}'
  gh run rerun --job <create-release の databaseId>
  ```

  リポジトリで Release の不変化（immutable releases）を有効にしている場合、公開後は資産を差し替えられないため、この経路は使えない

- 署名が `Valid` でない・Subject や SHA256 が一致しない状態が続く場合: 公開された資産が正しくない可能性がある。**先に配布を止める**（資産を削除する、または `gh release edit vX.Y.Z --draft=true` で下書きに戻す。どちらも未検証）。タグは付け直さず、原因を直して**次のパッチ版**で出す。利用者が取得した可能性がある期間と影響は、次のリリースノートと、必要なら Security Advisory で告知する

**リリースノートだけが誤っている**: `gh release edit vX.Y.Z --notes-file <ファイル>` で直す（§8.2 の 4）。

### 9.5 してはいけないこと

- タグを消して、同じ名前で付け直す（§1）
- **`publish_release=true` の dispatch をブランチから実行する**（`--ref` を付けない、または `--ref main` などを付ける）。`publish_release=true` の dispatch は、必ず `--ref vX.Y.Z`（push 済みのタグ）で行う。ブランチから動かすと、版はブランチの `<YaguraVersion>` から取られてタグとの照合が行われず、`sign` も `create-release` も動く。その結果、次のいずれかになる
  - タグ `vX.Y.Z` が既に別のコミットを指している場合: Release はそのタグに付くが、資産はブランチの先頭からビルドされる。ソースのタグと署名済み MSI が食い違う Release になる
  - その版のタグがまだない場合: `gh release create` が既定ブランチの最新に軽量タグを自動で作り（§9.1）、§5 の CI の確認と §6 の注釈付きタグを経ずに公開される
  - その版の Release が既にある場合: 公開済みの資産が、ブランチの先頭からビルドした MSI で上書きされる（`gh release upload --clobber`。§9.1）

  dispatch を起動するのも承認するのもオーナー自身なので、§7.2 の確認でも気づきにくい。コマンドを打つ前に `--ref` を確かめる
- `YAGURA_SIGNING` を `enabled` 以外にして、未署名のまま公開する（ADR-0014 決定 8 の条件を満たさない。§10）
- 署名済みと未署名が混在する Release を手で作る

## 10. 失敗したときの判断

判断はオーナーが行う。

| 状態 | 既定の判断 |
|---|---|
| 何も公開されていない・原因は一時的 | 同じ run を再実行する（§9.3）。下書きの Release が残っていれば先に消す（§9.2） |
| 何も公開されていない・原因はコミット側（製品・MSI・release.yml） | main で直し、次のパッチ版で出す。押したタグは欠番 |
| 公開済み・資産は正しい（検証側の一時的な失敗） | `create-release` を再実行する（§9.4）か、§8.2 の手の確認で検証を完了して記録する |
| 公開済み・資産が疑わしい | 配布を止め、次のパッチ版で出す（§9.4） |
| タグを誤ったコミットに付けた（版の照合で build が失敗した等）で、Release は作られていない | 既定は欠番にして次の版で出す。Release も資産も作られていないことを確かめたうえでタグを消して付け直すかはオーナーが判断し、その理由を記録する |
| 署名基盤が使えない（承認者の長期不在を含む）が、Critical な脆弱性の修正を出さなければならない | ADR-0014 決定 8 の退避弁にあたる。**release.yml には退避弁の入力がまだない**。`YAGURA_SIGNING` を `enabled` 以外にすれば未署名で公開する経路になるが、決定 8 が求める「リリースノート冒頭での未署名の明示」は入らない。手順は未整備（§11） |

## 11. この文書がまだ扱っていないこと

- 証明書の年次更新（現行の証明書の期限は [code-signing-policy.md](../code-signing-policy.md) に記載）。更新すると `installer/sign/codesign-chain.pem`・リポジトリ変数 `SIGNER_SUBJECT`・code-signing-policy.md の期待値が変わる
- 署名基盤が使えないときの緊急リリース（ADR-0014 決定 8 の退避弁）と、承認ゲートと緊急対応の両立（ADR-0014 委任 10）
- KMS の署名操作の監視とアラート（ADR-0014 委任 13）
- release.yml の改善（release.yml を変える後続の PR の候補。それまではこの文書の手順と §9.5 で防ぐ）
  - `gh release create` に `--verify-tag` を付け、タグがないときに Release と軽量タグを自動で作らないようにする
  - `--verify-tag` だけでは、タグや Release が既にある場合のブランチからの dispatch（§9.5）を防げない。`create-release` の条件に、dispatch でも ref がタグ（`refs/tags/v`）であることを加える
  - 公開後検証のログに、各 MSI のタイムスタンプの時刻を出す（いまは Thumbprint・NotAfter だけで、時刻は §8.2 の 3 で手で読んでいる。ADR-0014 決定 3・決定 9）

## 出典（2026-11-16 確認）

- GitHub Docs: [Re-running workflows and jobs](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)（30 日・50 回・同じ `GITHUB_SHA`/`GITHUB_REF`・依存ジョブの再実行・起動者の権限）
- GitHub Docs: [Actions limits](https://docs.github.com/en/actions/reference/limits)（承認待ち 30 日・run 35 日）
- GitHub Docs: [Deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)（required reviewers・Prevent self-reviews・Deployment branches and tags）
- GitHub Docs: [Reviewing deployments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments)（承認・却下の操作）
- GitHub Docs: [Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)（4 つ以上のタグの同時 push ではイベントが作られない・workflow_dispatch のタグ指定）
- GitHub Docs: Actions の成果物とログの保持期間（既定 90 日。公開リポジトリは 1〜90 日で設定）
- GitHub Docs: [Downloading workflow artifacts](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)（サインインしていて読み取り権限があれば artifact を取得できる）・[Removing workflow artifacts](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/remove-workflow-artifacts)（run のページからの削除）・REST API の「List workflow run artifacts」「Delete an artifact」
- gh 2.89: `gh release create --help`・`gh release delete --help`・`gh run rerun --help`・`gh workflow run --help`・`gh release edit --help`
- gh 2.89 のソース（[cli/cli v2.89.0](https://github.com/cli/cli/tree/v2.89.0/pkg/cmd/release)）: `pkg/cmd/release/shared/fetch.go` の `FetchRelease`（下書きもタグ名で見つける。`view`・`upload`・`download`・`delete`・`edit` が使う）、`pkg/cmd/release/create/create.go` の `cleanupDraftRelease`（アップロードや公開が失敗したときに下書きを消す）
- osslsigncode 2.8: 公開済みの `Yagura-0.5.0-arm64.msi` に `osslsigncode verify -in` を実行し、`Countersignatures:` の下に `Timestamp time:` が出ることを確認した
- [actions/setup-dotnet README](https://github.com/actions/setup-dotnet#using-the-global-json-file-input)（`rollForward` の `latest*` の扱い）
- .NET 公式文書: [Self-contained deployment runtime roll forward](https://learn.microsoft.com/dotnet/core/deploying/runtime-patch-selection)
- self-contained で publish した出力の `runtimeconfig.json` が `includedFrameworks` に版を持つことは、`dotnet publish -r win-x64 --self-contained true` で確認した
