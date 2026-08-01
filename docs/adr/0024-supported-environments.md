# ADR-0024: 対応環境と推奨環境の定義 — v1.0 で表明するサポート行列

- 状態: proposed
- 日付: 2026-07-26
- 決定者: YANAI Taketo
- 関連: [ADR-0001](0001-project-founding.md)（目的・対象利用者・導入体験の原則）/ [ADR-0006](0006-v1-release-criteria.md)（v1.0 公開基準 1・3・4）/ [ADR-0009](0009-architecture-support.md)（アーキテクチャ対応。本 ADR は同じ枠組みを OS・DB・ブラウザへ広げるもの）/ [ADR-0004](0004-security-model.md) 決定 3（opt-in 強化 3 点）/ [ADR-0022](0022-viewer-https.md)（閲覧 HTTPS）/ [architecture.md](../design/architecture.md) §5.2・§9 M-6 / [database.md](../design/database.md) §5.3

## 文脈と課題

v1.0 は利用者に対する「本番利用を推奨できる」という宣言である（ADR-0006）。しかし現時点の Yagura は、**動作環境を利用者に一切表明していない**。README にあるのは ADR-0009 が定めた対応アーキテクチャ表（x64 = 対応 / ARM64 = 試験的）だけで、**OS のバージョン・SQL Server のバージョン・ブラウザ・ハードウェアの要件はどの文書にも存在しない**。

ADR-0009 委任事項 7 は「README のシステム要件表をアーキ別に更新する」と書いたが、実際にはシステム要件表そのものが存在せず、アーキ表だけが作られた。更新すべき土台が無かったためである。

これは ADR-0006 基準 3（利用者向け文書の完備——作者と独立した人間が文書だけを頼りに導入から運用まで到達できること）の穴である。導入判断の最初の一歩、「うちの Windows Server で動くのか」に答える文書が無い。

さらに問題は表明の欠落だけではない。**v0.x の間に実機で踏んだ環境は狭い**。「表明していない」だけでなく「検証していない」状態であり、v1.0 で何を約束するかを決めるには、まず外部の前提（.NET・Windows・SQL Server・ブラウザのサポート状況）を確定させる必要がある。

### 実機検証の現況（v0.x で実際に踏んだ環境）

| 環境 | 検証実績 | 出典 |
|---|---|---|
| Windows Server 2025（10.0.26100） | gMSA・ACL/監査・通知/無音化・起動警告・MSI アップグレードの各 lab | `installer/lab/` の各手順書 |
| Windows 11 / Windows 10 Pro クライアント | 開発機ベンチ・lab メンバー機 | `tools/Yagura.Bench/results/` |
| Windows Server 2022 | CI ランナー（当時の `windows-latest`）でのビルド・単体テスト・回帰ベンチ | `.github/workflows/ci.yml` |
| Windows 11 on Arm（ARM64） | ADR-0009 Phase 0/1 のスモーク | ADR-0009 |
| SQL Server 2022 Express | M7-2 実測・全 lab の保存先 | `tools/Yagura.Bench/results/2026-07-05-yagura-stg/` |
| **Windows Server 2019 / 2016** | **なし** | — |
| **SQL Server 2019 以前 / Express 以外のエディション** | **なし** | — |

現行 CI の `windows-latest` は Server 2025 を指すため、**Server 2022 は現在どのジョブでも踏まれていない**。

### 外部前提のライブ検証（2026-07-26 実施）

conventions.md の実体検証原則に従い、本 ADR の判断根拠となる外部サポート状況を当日ライブ確認した。

**(1) .NET 10 の Windows 対応**（[Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)、更新日 2026-06-02、確認日 2026-07-26）: Windows Server 系列（2012〜2025・Server Core・Nano Server）は **x64 のみ**、クライアント（Windows 11 / Windows 10 LTSC・Enterprise）は x64・x86・Arm64。ADR-0009 が記録した表と同一であることを再確認した。ただし同ページは **Windows Server 2012 / 2012 R2 について「Microsoft Visual C++ 2015-2019 再頒布可能パッケージ」を前提条件として明記**している。

**(2) .NET 10 のライフサイクル**（[.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)、確認日 2026-07-26）: .NET 10 は **LTS、サポート終了 2028-11-14**。

**(3) Schannel の TLS 1.3 対応**（[Protocols in TLS/SSL (Schannel SSP)](https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-)、更新日 2025-03-20、確認日 2026-07-26）: 該当箇所を引用する——「**TLS 1.3 is supported starting in Windows 11 and Windows Server 2022. Enabling TLS 1.3 on earlier versions of Windows is not a safe system configuration.**」。同ページの表でも Windows Server 2019・2016 の TLS 1.3 は Client/Server とも `Not supported`。

**(4) Blazor の対応ブラウザ**（[ASP.NET Core Blazor supported platforms](https://learn.microsoft.com/en-us/aspnet/core/blazor/supported-platforms?view=aspnetcore-10.0)、更新日 2026-07-22、確認日 2026-07-26）: Apple Safari・Google Chrome・Microsoft Edge・Mozilla Firefox の **Current（最新版）のみ**。Internet Explorer は対応表から外れている（ASP.NET Core 3.1 版の同ページには `Microsoft Internet Explorer | Not Supported` と明示されていた）。

**(5) Windows Server の同梱ブラウザ**（[What's New in Windows Server 2022](https://learn.microsoft.com/en-us/windows-server/get-started/whats-new-in-windows-server-2022)、更新日 2026-02-16、確認日 2026-07-26）: 該当箇所を引用する——「**Microsoft Edge is included with Windows Server 2022, replacing Internet Explorer.** (中略) It can be used with the **Server with Desktop Experience** installation options.」。すなわち **Server 2022 以降は Edge が同梱される**が、**それ以前の Server では同梱されず**（「replacing」の対象が Internet Explorer であることが裏返しの根拠）、**Desktop Experience 以外の導入形態（Server Core）では使えない**。Server 2019 / 2016 で Edge を別途導入できること自体は Microsoft Q&A 等で広く案内されているが、**「素の状態で管理 UI に到達できない」ことの実地確認は委任事項 1 項目③で行う**（本 ADR の調査は同梱有無の一次資料までで、未導入状態の実挙動には及んでいない）。

なお同ページは「**HTTPS and TLS 1.3 is now enabled by default on Windows Server 2022**」とも述べており、外部検証 (3) の Schannel の表と整合する。

**(6) OS・DB のライフサイクル**（Microsoft Lifecycle の各製品ページ、確認日 2026-07-26）:

| 製品 | メインストリーム終了 | 延長サポート終了 |
|---|---|---|
| [Windows Server 2016](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2016) | 2022-01-11 | **2027-01-12** |
| [Windows Server 2019](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2019) | 2024-01-09 | **2029-01-09** |
| [Windows Server 2022](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2022) | 2026-10-13 | **2031-10-14** |
| [Windows Server 2025](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2025) | 2029-11-13 | **2034-11-14** |
| [SQL Server 2016](https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2016) | 2021-07-13 | **2026-07-14（終了済み）** |
| [SQL Server 2017](https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2017) | 2022-10-11 | **2027-10-12** |
| [SQL Server 2019](https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2019) | 2025-02-28 | **2030-01-08** |
| [SQL Server 2022](https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2022) | 2028-01-11 | **2033-01-11** |
| [SQL Server 2025](https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2025) | 2031-01-06 | **2036-01-06** |

**SQL Server 2016 の延長サポートは本 ADR 起案の 12 日前（2026-07-14）に終了した**。以降は ESU 契約下でのみ更新が提供される。

**検証の限界**: Microsoft.Data.SqlClient が**接続先としてサポートする SQL Server のバージョン下限**を明示した公式ページは見つからなかった（README・Learn の名前空間紹介ページとも .NET 側の要件〔.NET 8.0+〕しか記載しない）。したがって本 ADR の SQL Server 下限は、ドライバ側の制約ではなく **Microsoft のライフサイクルと Yagura 自身の検証実績**を根拠に定める。

### 検証で判明した製品側の未確認事項

外部前提の確認と併せてコードを監査し、本 ADR の決定に影響する未確認事項を 3 点特定した。

- **TLS 1.3 の固定指定**: Yagura は TLS を使う 3 か所すべてで `SslProtocols.Tls12 | SslProtocols.Tls13` を固定している（syslog TLS 受信 = `src/Yagura.Ingestion/Tls/TlsSyslogListener.cs:245`、管理 HTTPS・閲覧 HTTPS = `src/Yagura.Host/Program.cs:359`）。**TLS 1.3 非対応の OS でこの指定が TLS 1.2 へ縮退するのか、ハンドシェイクまたは起動が失敗するのかは未検証**である
- **MSI に OS バージョンの起動条件が無い**: `installer/Package.wxs` には OS バージョンを判定する LaunchCondition が存在せず、対応外の OS にもインストールできてしまう
- **SQL Server 適合テストが CI で実行されていない可能性**: 適合テストは `Xunit.SkippableFact` による動的スキップを備えるが、GitHub ホストランナーの image には SQL Server インスタンスが同梱されない（`windows-2022` image の構成を確認。SQL OLEDB ドライバのみ。確認日 2026-07-26）。SQL Server 側の検証は実質的に lab の手動実施のみに依存している

## 検討した選択肢

### 対応 OS の下限をどこに置くか

- **(A) .NET 10 が対応する全 OS（Server 2012 以降）を対応と表明する**: 却下。理由は 3 つ——①検証していない環境を「対応」と呼ぶことになり、ADR-0009 決定 2 で定めた「設計原則は共通・実測保証は検証した環境にしかない」という自己拘束と矛盾する。②Server 2012 / 2012 R2 は VC++ 再頒布可能パッケージの別途導入が前提であり（外部検証 (1)）、ADR-0001 の**ゼロ設定ファーストラン**が成立しない。③Server 2016 は TLS 1.3 非対応かつ Edge が同梱されず、後述のとおり素の状態では管理 UI に到達できない
- **(B) 対応 = Server 2019 以上 / 推奨 = Server 2022 以上の 2 段（採用）**: Server 2019 は延長サポートが 2029-01-09 まで残り、中小企業の Windows 環境に現実に稼働している。一方 TLS 1.3 非対応という機能上の劣化が確実に存在する。この差を「対応だが推奨ではない」という 2 段で表現する
- **(C) 対応 = Server 2022 以上に統一する**: 却下。検証コストは最も小さく TLS 1.3 も揃うが、**延長サポートが 2029 年まで残る Server 2019 環境を丸ごと切ることになり、ADR-0001 が定めた対象利用者（中小企業の Windows 管理者）と正面から衝突する**。「Windows 管理者が既に持つ資産で導入できる」という価値提案は、稼働中の OS を切り捨てた瞬間に薄まる。再評価トリガに「Server 2019 の実機検証で中核機能に不成立が出た場合」を置き、そのときに本案へ倒す
- **(D) 下限を設けず「動作報告を募る」形にする**: 却下。v1.0 は「本番利用を推奨できる」宣言であり、推奨の範囲を利用者に決めさせるのは宣言の放棄にあたる

### ブラウザ要件を明記するか

- **(a) 「最新版の Edge / Chrome / Firefox / Safari」を要件として明記する（採用）**
- **(b) 書かない（現状維持）**: 却下。**管理 UI は loopback 固定**であり（ADR-0004・configuration.md §4。設定でも変更できない）、初回セットアップ・本番昇格・保持期間変更はサーバ機上のブラウザからしか行えない。Blazor は IE 非対応（外部検証 (4)）であり、かつ **Server 2019 / 2016 には Edge が同梱されない**（外部検証 (5)）ため、**追加ブラウザを入れていない素の状態では管理 UI に到達できない**。Server 2022 以降は Edge が同梱されるためこの問題は起きない。書かなければ「インストールは成功したのに設定画面が開けない」に着地する。これは ADR-0001 の導入体験の原則に直接反する

### ハードウェア要件をどう決めるか

- **(a) 実測に基づく算出式 + 検証済み構成の実測値で表現し、CPU・メモリの下限確定は M-6 に委ねる（採用）**
- **(b) 一般的な目安（例「2 コア / 4 GB 以上」）を書く**: 却下。architecture.md §5.2 が定めた「公称値は基準環境での絶対値で判定する」原則と衝突する。**根拠のない数値を要件表に書くと、それが後から公称値の既成事実になる**（ADR-0006 運用の「量的な床も自己拘束の内側に置く」に反する）。ディスクは M7-2 実測から算出式を導けるため今すぐ書けるが、CPU・メモリの下限は実測が無い

### 検証基盤をどう組むか

- **(a) 推奨環境は GitHub Actions に常設し、対応下限は単発検証する（採用）**: `windows-2022` ランナーは公開リポジトリで無料・無制限（ADR-0009 決定 5 が確認した前提と同じ）。**推奨環境の回帰がリリースごとに無料で効く**。一方 `windows-2019` ランナーは**退役済み**でランナー一覧に存在しない（確認日 2026-07-26）ため、Server 2019 は CI では踏めない。オーナーの lab には Proxmox の x64 機があり、Windows Server 2019 の評価版（180 日・ISO/VHD で配布継続中。確認日 2026-07-26）を載せれば**追加費用ゼロで単発検証できる**
- **(b) Server 2019 を含む多 OS の恒常 lab を組む**: 却下（現時点）。常時保持は保守コストが個人運営規模に見合わない。対応下限の表明が変わるときだけ再検証すれば足りる
- **(c) クラウド VM（Azure / AWS）で単発検証する**: 却下（第一選択としては）。ライセンス込みで評価版の期限・アクティベーション制約が無い利点はあるが、(a) が費用ゼロで同じ目的を達するため優位性がない。**Proxmox 機が使えない状況になった場合の代替として残す**

## 決定

**選択肢 (B) + (a) + (a) + (a)** を採用する。

### 決定 1: OS のサポート行列を「推奨 / 対応 / 試験的 / 対応外」の 4 区分で定義する

| 区分 | 対象 | 約束する水準 | 検証水準 |
|---|---|---|---|
| **推奨（Recommended）** | Windows Server 2022 / 2025（x64） | **全機能**。ADR-0004 決定 3 の opt-in 強化 3 点（TLS 受信・閲覧 HTTPS・AD 連携認証）を含む | `windows-2022`・`windows-latest`（= Server 2025）の CI 常設 + Server 2025 の実機 lab E2E |
| **対応（Supported）** | Windows Server 2019（x64） | 中核機能（受信・保存・閲覧・管理・スプール・昇格）。**TLS は OS が 1.3 を持たないため 1.2 まで** | 単発の実機検証（決定 5）。CI 回帰の対象外 |
| **対応（Supported）** | Windows 10 / 11 クライアント（x64。**ビルド 17763 以上**） | 中核機能（ADR-0009 決定 2 の x64 行を継承） | 開発機・lab メンバー機での実機使用 |
| **試験的（Experimental）** | Windows 11 on Arm 等のクライアント（ARM64） | ADR-0009 決定 2 の ARM64 行をそのまま継承（修正 SLA を約束しない） | ADR-0009 のスモーク水準 |
| **対応外** | Windows Server 2016 以前、Server Core / Nano Server、ビルド 17763 未満のクライアント、32-bit | — | — |

**Server 2016 以前を対応外とする根拠（3 点セット）**:

1. **ライフサイクル**: Server 2016 の延長サポートは 2027-01-12 に終了する。v1.0 のサポート期間（決定 7）と実質的に重ならない
2. **機能**: TLS 1.3 非対応（外部検証 (3)）。opt-in 強化の暗号強度が推奨環境と揃わない
3. **導入体験**: Edge が同梱されず（外部検証 (5)）、Blazor 非対応の Internet Explorer しか持たない状態で出荷されるため（外部検証 (4)）、**素の状態では loopback 固定の管理 UI に到達できない**。ADR-0001 の導入体験の原則が成立しない

Server Core・Nano Server を対応外とするのは、**管理 UI・閲覧 UI がブラウザを前提とする製品構造**（ADR-0003）と、GUI を持たない OS 構成が原理的に噛み合わないためである。Server 2022 で同梱された Edge も「**Server with Desktop Experience** の導入形態で使える」ものであり（外部検証 (5)）、Server Core はこの前提から外れる。リモートの閲覧 UI 経由での運用は理屈上可能だが、初回セットアップが loopback 固定である以上、素の Server Core では完結しない。検証もしていない。

**ADR-0009 との関係（supersession ではなく直交する軸の追加）**: ADR-0009 決定 2 は**アーキテクチャ**の軸（x64 = 対応 / ARM64 = 試験的）を定めた。本 ADR はそこに**OS バージョン**の軸を足すものであり、ADR-0009 の判断を反転させない——x64 は対応のまま、ARM64 は試験的のまま、対象環境の記述（ARM64 はクライアント OS 限定）も維持する。ただし本 ADR は ADR-0009 が粒度を持たなかった 2 点を**限定**する:

- ADR-0009 の x64 行「Windows Server 全般」を、**Server 2019 以上**に限定する（Server 2016 以前を対応外へ）
- ADR-0009 の x64 行「Windows 10/11 クライアント」に、**ビルド 17763 以上**の下限を与える（.NET 10 は Windows 10 1607 LTSC〔ビルド 14393〕も対応するが、Yagura は検証していないため対応と表明しない）

いずれも「既定を保ったまま適用条件を限定する」変更であり、docs/adr/README.md の判定の目安では amendment 相当だが、**独立した論点（OS バージョン）として本 ADR が引き受ける**。本 ADR が accepted になった時点で、ADR-0009 決定 2 の表の該当行に本 ADR への参照注記を加える（委任事項 9）。

**「対応」と「推奨」の差の意味**（ADR-0009 決定 2 の「サポート水準の定義」と同じ作法で明文化する）:

- **設計原則は全区分共通**: ADR-0001 の品質原則（ログを失わない・失った場合に必ず観測できる）は Server 2019 でも設計上の原則として適用される。区分によって「観測できない喪失を許容する」ことはしない
- **機能の差は 1 点に限定して明示する**: Server 2019 で劣化するのは **TLS 1.3 が使えない**ことだけである（OS の制約であり Yagura の実装都合ではない）。これ以外の機能差は設けない。もし単発検証で他の差が見つかった場合は、その差を本表に追記するか、対応区分そのものを見直す（再評価トリガ）
- **検証の裏付けが非対称**: 推奨環境は CI で継続検証する。対応環境は下限の表明が変わるときの単発検証にとどまる
- **不具合対応**: Server 2019 固有の不具合報告は受け付け、再現・修正を試みるが、修正の優先度は推奨環境の品質維持を優先して判断する

### 決定 2: SQL Server は「2019 以上を対応・2022 以上を推奨」とし、組み込み DB は全区分で同一とする

| 区分 | 対象 | 根拠 |
|---|---|---|
| **推奨** | SQL Server 2022 / 2025（Express・Standard・Enterprise） | 延長サポートが 2033-01-11 / 2036-01-06 まで残り、v1.0 のサポート期間を完全に覆う。2022 Express は Yagura が実測済みの唯一の SQL Server 構成 |
| **対応** | SQL Server 2019（同上） | 延長サポート 2030-01-08。v1.0 のサポート期間を覆う。**実機検証は未実施——委任事項 2 のゲートを通すまで README で表明しない** |
| **対応外** | SQL Server 2017 以前 | 2017 は 2027-10-12 に終了予定で v1.0 のサポート期間に収まらない。**2016 は 2026-07-14 に終了済み**（ESU 契約下の環境を「対応」と表明しない） |

- **エディションは問わない**が、**Express の適用限界は database.md §5.3 のとおり利用者向けに明示する**（DB 最大 10 GB・バッファプール 1,410 MB・4 コア）。ゼロ設定ファーストランの既定は SQLite であり、Express は「無償で本番昇格したい場合の選択肢」として位置づける
- **LocalDB は対応外**とする。常駐サービスの保存先として設計された製品ではなく（ユーザーセッションに紐づく起動モデル）、Yagura が仮想サービスアカウント / gMSA で動く前提と噛み合わない。検証もしていない
- **組み込み DB（SQLite）は全区分で同一**とする。ネイティブ資産はアーキ別に self-contained publish へ含まれる（ADR-0009 決定 3・Phase 0 で検証済み）ため、**DB 製品の別途導入も OS 側の前提も持たない**。OS バージョンによる挙動差の有無は委任事項 1 の単発検証で確認する（差が無いことを前提に置かない）

### 決定 3: ブラウザ要件を明記し、管理 UI の到達性の前提として位置づける

- **要件**: 最新版の Microsoft Edge / Google Chrome / Mozilla Firefox / Apple Safari のいずれか（Blazor の対応ブラウザに従う。外部検証 (4)）。**Internet Explorer は対応しない**
- **要件が掛かる場所を明示する**: 閲覧 UI（8514）は任意の端末のブラウザから開けるが、**管理 UI（8515）は loopback 固定のため、サーバ機自身に対応ブラウザが必要**である（管理リモート HTTPS を opt-in で有効にした場合〔ADR-0010〕はリモート端末のブラウザでよいが、**その設定自体を最初に行うのがサーバ機上の管理 UI である**——初回は必ずサーバ機のブラウザを通る）
- **推奨環境（Server 2022 / 2025）では Edge が同梱されるため追加作業は要らない**。対応環境（Server 2019）では**対応ブラウザの導入が導入手順の前提条件になる**ことを利用者向け文書に明記する（委任事項 3）

### 決定 4: ハードウェア要件は「算出式 + 検証済み構成の実測値」で表現し、CPU・メモリの下限確定は M-6 に委ねる

- **ディスクは算出式を今すぐ提示する**（M7-2 実測に基づく。database.md DB-1 が確定した「レコード単価 ≈ メッセージ長 + 約 95 B」を根拠とする）:

  `必要容量 ≒ (平均メッセージ長 + 95 B) × 秒間流量 × 保持期間の秒数 × 安全率 + スプール使用量上限 + 空き容量の下限`

  既定構成（保持 30 日・10 msg/s）での目安は **約 7.8 GB**（DB-1 の確定根拠と同じ計算）。ここにスプールの使用量上限（M-12）と、能動通知が警告を出す空き容量の下限 **1 GiB**（M-16）を積む
- **CPU・メモリの下限は本 ADR では確定しない**。architecture.md §9 **M-6（性能公称値と基準環境の定義）**が管轄する。本 ADR は M-6 に対し「**基準環境の定義は、決定 1 の推奨区分に含まれる構成から選ぶ**」という制約だけを追加する（推奨環境の外で公称値を測っても利用者向けの意味を持たないため。M-6 の行への反映は委任事項 8）
- **参考として実測済み構成の値は載せてよい**が、「要件」と明確に区別する。現在提示できる実測値は 8 論理コア / 7 GiB / SQL Server 2022 Express 同居の構成における **Express ≈ 4,500〜5,000 msg/s・SQLite ≈ 15,000 msg/s**（M7-2）

### 決定 5: 検証水準は「推奨環境は CI 常設・対応下限は単発」の 2 段とする

- **推奨環境**: `.github/workflows/` のジョブに **`windows-2022` を加える**（現行の `windows-latest` = Server 2025 と合わせて推奨 2 バージョンを常時踏む）。公開リポジトリの標準ランナーは無料・無制限のため追加費用は発生しない。対象ジョブの範囲（CI 全体か installer-e2e か release のみか）は委任事項 4 で確定する
- **対応下限（Server 2019）**: **オーナー lab の Proxmox（x64）上に Windows Server 2019 評価版の VM を立てて単発検証する**。`windows-2019` ランナーは退役済みで CI では踏めない。評価版は 180 日・10 日以内のオンライン認証が必要という制約があるが、単発検証には十分である。**Proxmox 機が使えない状況になった場合は、クラウド VM（ライセンス込み・数時間で数百円規模）を代替とする**
- **単発検証を再実施する契機**を機械的に定める: ①v1.0 リリース判定時、②対応下限の表明を変えるとき、③TLS・証明書・リスナ・インストーラのいずれかに変更が入ったリリース。それ以外では再実施しない
- **SQL Server の CI 検証**: GitHub ランナー image に SQL Server インスタンスは同梱されないため、適合テストは現在 CI で実行されていない。**推奨環境の CI ジョブで SQL Server Express をセットアップして適合テストを実走させることの要否**を委任事項 5 で判断する（v1.0 が SQL Server を既定 DB と位置づける以上、一度も CI で実行されない状態は基準 1 の証拠として弱い）

### 決定 6: MSI の起動条件で対応下限を機械的に強制する

利用者が対応外 OS へ導入して「動くはずのものが動かない」に着地するのを、文書ではなくインストーラで防ぐ。

- `installer/Package.wxs` に **OS ビルド番号による LaunchCondition を追加する**。**Windows Server 2019 と Windows 10 1809 はいずれもビルド 17763** であり、決定 1 の下限（Server 2019 以上 / クライアントは同世代以上）を**単一の条件式で表現できる**
- 判定に用いる MSI プロパティ・比較方法・拒否時のメッセージ文言は実装 PR で確証する（委任事項 6）。**本 ADR の調査は机上に留まる**——ADR-0009 が WiX `InstallerPlatform=arm64` の実ビルド挙動を机上調査に留めたのと同じ扱いとする
- **メッセージは「なぜ入れられないか」と「どうすればよいか」を含める**（例: 対応する OS の下限と、README のシステム要件表への導線）。単に拒否するだけの条件は追加しない

### 決定 7: サポート表明の期限を .NET 10 のライフサイクルに接続する

- Yagura は .NET 10（LTS・サポート終了 **2028-11-14**）に self-contained で依存する。**v1.0 系列のサポート表明はこの日付を超えない**
- SECURITY.md の「サポート対象バージョン」（ADR-0006 基準 5）に、この上限と根拠を記載する（委任事項 7）
- **.NET のメジャー更新は v1.0 系列の中では行わない**（後方互換性の凍結〔ADR-0006 基準 4〕と同じ扱い）。次の LTS への移行は次のメジャーバージョンで扱う

## 帰結

- **良くなること**:
  - 導入判断の最初の問い（「うちの環境で動くのか」）に文書で答えられるようになり、ADR-0006 基準 3 の穴が塞がる
  - 推奨環境（Server 2022）の回帰検証が**追加費用ゼロで CI に常設**される。現在 Server 2022 がどのジョブでも踏まれていない状態を同時に解消できる
  - 対応外 OS への誤導入をインストーラが止めるため、「入ったのに管理 UI が開けない」という最悪の導入体験を構造的に防げる
  - サポート表明の期限が外部のライフサイクルに接続され、根拠なく「いつまでも使える」と読まれることがなくなる
- **悪くなること（受け入れるトレードオフ）**:
  - **説明責任が増える**。「Server 2019 は対応だが TLS 1.3 が使えない」「Server Core は対応外」といった非直感的な制約を、README・インストーラ・ガイドで一貫して書き続ける必要がある（ADR-0009 が ARM64 で負ったのと同型の負債）
  - CI の実行時間とジョブ数が増える（`windows-2022` の追加分）
  - Server 2019 の単発検証は人手であり、リリースのたびに条件判定（決定 5 の再実施契機）を行う運用が要る
  - 対応区分を明示した結果、**現在 Server 2016 で試用している利用者がいた場合、その環境を明示的に切ることになる**（現時点でそのような報告は無いが、いないことの確認もできていない）
- **リスク**:
  - **Server 2019 の単発検証で中核機能に不成立が出る可能性がある**。特に TLS 1.3 固定指定（`Tls12 | Tls13`）が非対応 OS で例外になる場合、TLS 受信・閲覧 HTTPS・管理 HTTPS のいずれかが起動時に失敗しうる。その場合は実装側の是正（`SslProtocols` の指定方法の見直し）か、対応区分の縮小（選択肢 C への転換）かの判断が必要になる——**本 ADR が accepted でも、この検証を通過するまで README で「Server 2019 対応」を表明しない**（ADR-0009 決定 6 の Phase 0 ゲートと同じ構造）
  - 決定 4 が CPU・メモリの下限を M-6 に委ねるため、**v1.0 のシステム要件表は当面「ディスクは算出式・CPU/メモリは実測値の参考提示のみ」という不完全な形で公開される**。利用者にとっては「で、何コア要るのか」に答えが無い状態が続く
  - SQL Server は 2022 Express しか実測しておらず、**2019 および Express 以外のエディションは「ライフサイクル上の妥当性」だけを根拠に対応と表明する**ことになる。ドライバ側の下限が公式に確認できなかった（検証の限界）ことと合わせ、この表明は実測ではなく推論に立っている

## 先送りにする場合の再評価トリガ

- **Server 2019 の単発検証で中核機能の不成立が出た場合**: 是正の可否を判断し、是正できない場合は選択肢 (C)（対応 = Server 2022 以上）へ supersession で転換する
- **Windows Server 2022 のメインストリームサポートが終了した場合（2026-10-13。約 3 か月後）**: 推奨区分の表現を見直す。延長サポートは 2031-10-14 まで残るため区分自体の変更は要さない見込みだが、「推奨」の語が誤解を生まないかを点検する
- **Windows Server 2019 の延長サポート終了（2029-01-09）が近づいた場合**: 対応区分から外す。これは v1.0 系列のサポート期限（決定 7 の 2028-11-14）より後であり、実質的には次のメジャーバージョンの論点になる
- **SQL Server 2019 の延長サポート終了（2030-01-08）が近づいた場合**: 同上
- **Server Core での需要が一次情報として確認された場合**: 初回セットアップの loopback 制約（configuration.md §4）と併せて、GUI 無し環境でのセットアップ手段の要否を別 ADR で検討する
- **GitHub が `windows-2022` ランナーを退役させた場合**: 推奨環境の CI 常設が成立しなくなる。`windows-latest` の指す先が Server 2025 のみになるため、Server 2022 を単発検証側（決定 5 の後段）へ移す

## 委任事項の一覧（追跡用）

| # | 委任事項 | 委任先 | 内容 |
|---|---|---|---|
| 1 | **Server 2019 の単発実機検証**（README 表明の前提ゲート） | lab 手順書 + 実装 PR | Proxmox 上の評価版 VM で実施。**必須項目**: ①`Tls12｜Tls13` 固定指定の挙動（TLS 1.2 へ縮退するか / 例外か）を 3 か所すべて（syslog TLS 受信・管理 HTTPS・閲覧 HTTPS）で確認、②MSI 導入 → 受信 → 保存 → 閲覧 → 管理 UI 到達、③対応ブラウザ導入前の素の状態で管理 UI が開けないことの確認（決定 3 の根拠の実地確認）、④観測性カウンタの出力。実施記録を PR body に残す（conventions.md の実体検証記録の作法） |
| 2 | **SQL Server 2019 の実機検証** | lab 手順書 + 実装 PR | 決定 2 が推論で表明した下限の裏付け。接続・スキーマ作成・照合順序検査（`sys.fn_helpcollations()`）・昇格ウィザード・保持期間削除までを 1 通り |
| 3 | **README のシステム要件表の新設**（ADR-0009 委任事項 7 の土台） | README + operations.md | 決定 1〜4 の表を利用者向けの言葉で。**ブラウザ要件と、それが管理 UI の到達性に効くことを明記**。Server 2019 では対応ブラウザの導入が前提条件になることを導入手順に組み込む。Express の適用限界（database.md §5.3）への導線も張る |
| 4 | **CI への `windows-2022` 追加** | `.github/workflows/` + 実装 PR | 対象ジョブの範囲（ci.yml 全体 / installer-e2e.yml / release.yml のどれに掛けるか）と、実行時間への影響の実測記録 |
| 5 | **SQL Server 適合テストを CI で実走させることの要否判断** | 実装 PR | ランナー上への SQL Server Express セットアップの成立性・所要時間・維持コストを評価し、採否を記録する。採らない場合は「SQL Server の検証が lab 手動のみである」ことを ADR-0006 基準 1 の証拠提出時に明示する |
| 6 | **MSI の LaunchCondition 実装** | `installer/Package.wxs` + 実装 PR | ビルド番号 17763 を下限とする条件式の実装。使用する MSI プロパティと比較方法の実ビルドでの確証、拒否メッセージの文言（理由 + 対処の導線）、**対応外 OS での実挙動の確認**（決定 6） |
| 7 | **SECURITY.md のサポート対象バージョン記載** | SECURITY.md（ADR-0006 基準 5） | 決定 7 の上限（2028-11-14）と根拠（.NET 10 LTS のライフサイクル）を記載 |
| 8 | **M-6（基準環境の定義）への制約の反映** | architecture.md §9 | 決定 4 が課した「基準環境は推奨区分の構成から選ぶ」という制約を M-6 の行に追記する（番号表・本文・参照側の 3 点セット更新——homework.md の保守ルール準拠） |
| 9 | **ADR-0009 決定 2 の表への参照注記** | ADR-0009 + docs/adr/README.md | 本 ADR が accepted になった時点で、ADR-0009 決定 2 の x64 行（「Windows Server 全般」「Windows 10/11 クライアント」）に本 ADR による限定への参照注記を加え、ADR 一覧の状態欄にも注記する（docs/adr/README.md の部分 supersession の記述作法に準じた扱い。**status は accepted のまま維持する**——決定の反転ではないため） |
