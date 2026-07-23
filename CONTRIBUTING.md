# 貢献の手引き / Contributing

> **English**: Issues and pull requests are welcome in English or Japanese. Code and commit messages are in English (Conventional Commits; since we squash-merge, please give your PR a Conventional-Commits-style title). Documentation is Japanese-first. Fork → feature branch → PR; direct pushes to `main` are not accepted. Contributions require signing the Contributor License Agreement (a bot guides first-time contributors on the PR; corporate contributors also need the [Corporate CLA](docs/legal/cla-corporate.md)). Building requires .NET SDK 10.0.100 or later (any 10.0.x feature band; see `global.json` for the floor); building the MSI additionally requires accepting the WiX Open Source Maintenance Fee EULA (see below). See [docs/development/conventions.md](docs/development/conventions.md) for the full rules.

Yagura への関心をありがとうございます。Issue・PR を歓迎します。

規約の正本は [docs/development/conventions.md](docs/development/conventions.md) です。本ファイルは入口としての要約であり、詳細・最新の規約は必ず正本を参照してください（二重管理はしません）。

## 開発環境とビルド

[.NET SDK 10.0.100](https://dotnet.microsoft.com/download/dotnet/10.0) 以上（10.0.x 系列の任意の feature band。下限は `global.json` 参照）が必要です。

```
dotnet build Yagura.sln
dotnet test Yagura.sln
```

### MSI インストーラのビルドと WiX の EULA

MSI（WiX Toolset 7）のビルド手順は [installer/README.md](installer/README.md) を参照してください。

WiX v7 はビルドに Open Source Maintenance Fee（OSMF）の EULA 承諾を要求します。本リポジトリの `installer/Yagura.Installer.wixproj` は公式の Direct Acceptance 方式（`<AcceptEula>wix7</AcceptEula>`）で承諾済みです——**つまり、このプロジェクトをビルドする人は WiX の OSMF EULA に同意したことになります**。支払い義務は「年商 1 万ドル超の組織」にのみ課され（docs.firegiant.com/wix/osmf/、確認日 2026-07-06）、個人・非営利の貢献者には生じませんが、組織としてビルドする場合は該当の有無をご確認ください。

## 言語

- **Issue・PR・議論**: 日本語・英語のどちらでも構いません
- **コード・コミットメッセージ**: 英語（Conventional Commits 形式）
- **ドキュメント**: 日本語優先

## 変更の流れ

1. 大きな変更は、まず Issue で提案してください（手戻り防止のため）
2. すべての変更は feature ブランチ（`<type>/<description>`）+ Pull Request 経由です（`main` への直接 push は禁止）
3. マージは squash merge で行います。**PR のタイトルを Conventional Commits 形式にしてください**（squash 時の最終コミットメッセージになります。ブランチ内の個々のコミットは規約準拠でなくても構いません）
4. **CI green を確認してから merge します**

## コントリビューターライセンス契約（CLA）

本プロジェクトへ貢献するには、**コントリビューターライセンス契約（CLA）への署名が必要です**。CLA は著作権を移転せず、あなたが貢献の著作権を保持したまま、メンテナが本プロジェクトを Apache License 2.0 の下で継続的に配布できるよう、必要なライセンスを付与するものです。

- **個人**: [個人 CLA（Individual CLA）](docs/legal/cla-individual.md)。初めての Pull Request で、CLA ボットが未署名の場合に案内します。CLA 文書を読み、次の定型文を**その PR にコメント**することで署名できます:
  > `I have read the CLA Document and I hereby sign the CLA`
- **企業・団体に代わって貢献する場合**: 上記の個人 CLA に加えて、[企業 CLA（Corporate CLA）](docs/legal/cla-corporate.md)の帳票外手続きが必要です。オーナーへご連絡ください。

署名は本リポジトリ内の台帳（自己ホスト型 CLA Assistant Lite。外部サービスへ送信しません）に記録されます。

## PR の要件（要点）

正本は [conventions.md](docs/development/conventions.md)「PR の要件」です。特に見落としやすい点:

- **設計書の同梱**: 機能を追加・変更する PR は、対応する全体設計書（docs/design/）の更新を同じ PR に含めます。機能・状態の記述に影響する場合は、入口文書（README・SECURITY・CONTRIBUTING——「常に現在形」の層）の該当箇所も同じ PR で更新します
- **設定キーの追加・変更**: [configuration.md §8](docs/design/configuration.md) の設定スキーマ表の更新を同じ PR に含め、新キーは**反映方式**（即時 / リスナ再構成 / サービス再起動）と**不正時挙動**（起動失敗 / 既定値で継続 / 縮小側で継続）の両方を宣言します
- **外部依存の追加・更新**: その日の最新版をライブ検証した記録を PR body に 1 行残します——`Vendored: <pkg> <ver> (verified <cmd> = <ver>, YYYY-MM-DD)`
- **同等性の主張は推測で通しません**: 「X は Y と同等」「A で B を代用できる」型の記述は、公式ドキュメントの引用または実機確認を通してから書きます

## CI

- ビルド・テスト・依存パッケージの脆弱性スキャンが PR をブロックします
- **CI 設定（`.github/` 配下）への変更は特に厳格にレビューされます**。リリース成果物へのコード署名が CI から実行されるため、ワークフローの変更は署名の信頼性に直結します（[コード署名ポリシー](docs/code-signing-policy.md)）
- **コード署名は貢献の妨げになりません**: 署名はオーナー承認付きの本番リリースワークフローでのみ実行されます。**ローカルビルドや fork でのビルド・CI 実行は署名の影響を受けず、従来どおり動きます**（署名用の資格情報は fork には存在せず、必要もありません）
- コミット権を持つメンバー（現在はオーナーのみ）は GitHub アカウントの MFA 有効化が必須です。PR を送るだけの貢献に MFA 要件はありません
- **CI 回帰ベンチ**も実行されますが、blocking なのは**突合の成立**（「損失は必ずどれかのカウンタに計上される」の検証）のみで、スループット等の数値は情報記録です（CI ランナーの個体差のため。経緯と基準値の更新手続きは [conventions.md](docs/development/conventions.md)「CI 回帰ベンチの基準値更新」節）。基準値ファイルの変更は他の変更に紛れ込ませず、専用の PR で行います

## 設計文書（ADR・全体設計書）への提案

設計文書の変更にはペルソナレビュー制度（[docs/development/persona-review.md](docs/development/persona-review.md)）が適用されますが、**外部コントリビュータがレビューを実行する必要はありません**。レビューの実施はオーナー側の責務です。提案の PR を出していただければ、こちらでレビューを回して結果を PR 上でお返しします。

## AI エージェントとの協働について

本プロジェクトは、オーナー（YANAI Taketo）の承認を最終ゲートとして、AI エージェント（Claude）と協働で開発しています。コミットや PR にある `Co-Authored-By: Claude ...` の表記はその記録です。設計判断の経緯は原則として PR 上のコメントと ADR に残しています。

## 行動規範

思いやりを持って接してください。技術的な批判は歓迎しますが、人への攻撃は受け入れません。
