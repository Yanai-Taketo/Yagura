# セキュリティ設計

- 層: 全体設計書（常に現在形。[docs/README.md](../README.md) の 2 層構造に従う）
- 根拠 ADR: [ADR-0004](../adr/0004-security-model.md)（セキュリティモデル——本文書は全決定の詳細化）、[ADR-0002](../adr/0002-architecture-principles.md) 決定 6（挿入点）、[ADR-0003](../adr/0003-ui-policy.md)（UI 接続層の申し送り）
- 関連文書: [architecture.md](architecture.md)（§4.6 能動通知・§8 挿入点対応）、[configuration.md](configuration.md)（§1 縮小側原則・§4.2 リスナ構成・§6 HTTPS 証明書）、[ui.md](ui.md)（§4 リスナ帰属・§9 circuit メトリクス）
- 本文書の範囲: loopback 束縛の CI 回帰テスト仕様・UI 接続層（circuit）の防御・役割の粒度・監査記録・保存データの ACL 具体値・TLS 受信用証明書。次の事項は他文書の管轄とする:
  - 信頼ネットワーク成立チェックリスト・リバースプロキシ前置時の要件・DPAPI バックアップ手順・localhost 監査の限界などの利用者向け文書 → operations.md（ADR-0004 委任一覧のとおり）
  - 脆弱性報告窓口 → SECURITY.md（ADR-0005）
- **読み分けの凡例**: 「v0.1: 完動」= 既定構成（認証なし・平文受信）で最初から有効。**既定構成の利用者に関係するのは §1・§2.1・§2.2・§4・§5**。§2.3・§3 は認証 opt-in、§6 は TLS 受信 opt-in を有効化した場合にのみ関係する
- 数値の扱い: architecture.md と同じ運用。未確定は [§7 確定待ち一覧](#7-確定待ち一覧)（SEC-x）で追跡する

## 1. loopback 束縛の CI 回帰テスト仕様（v0.1 受け入れ条件）

ADR-0004 決定 2 の「一回きりの確認ではなく CI の回帰テストとして固定する」の仕様。実プロセスを起動する統合テストとして CI で常時実行し、**すべて合格することを v0.1 の受け入れ条件とする**:

| # | 検証項目 | 根拠 |
|---|---|---|
| L-1 | 管理リスナが `127.0.0.1` と `::1` のみで待ち受けている（外部インターフェースのアドレスに束縛されていない） | ADR-0004 決定 2 |
| L-2 | WebSocket（SignalR circuit）への昇格後も L-1 が維持される（昇格の過程で別リスナ・別ポートに逃げない） | ADR-0004 決定 2 |
| L-3a | 非 loopback のアドレスから管理ポートへ**到達できない**（LAN 側アドレスからの接続試行が確立しない）。**この拒否は OS のレベルで起こり、アプリからは観測されない——監査記録の対象ではない**（記録できない事象を記録の合格条件にしない） | ADR-0004 決定 2 |
| L-3b | **閲覧リスナに到達した管理系の要求**（管理系パスへのアクセス試行）が拒否され、**拒否が監査記録（§4）に残る**——ADR-0004 決定 7 の「非 localhost からの管理要求の拒否試行」のアプリ側検知点はここである。「管理系パス」の判定方式は**管理リスナのルート表からの機械的導出**を採用した（M6-2。Issue #52。実装は `ListenerPortGuardEndpointMetadata` によるエンドポイントメタデータ方式——一覧の保守は L-5 の許可リストと同じ規約に載る。ルート登録がそのまま真実源になる） | ADR-0004 決定 2・7 |
| L-4 | **設定がどう壊れていても管理リスナの loopback bind（127.0.0.1/::1）が失われない**（公開範囲・bind 系キーの不正値・未知キー・欠落を注入しても L-1 が成立する）。[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 2 以降、管理リスナは opt-in（`Admin:RemoteBinding:Enabled`）で**別ポート**への追加のリモート HTTPS bind を持ち得るが、これは loopback bind を**置き換えるものではなく追加**であり、L-4 が保証する対象（loopback bind の不動）はそのまま維持される。リモートバインド自体の fail-closed 不変条件（認証・HTTPS が両方構成されていない限り開かれない）は §2.5 で定義する（Phase 1 の loopback 認証 opt-in fail-closed——§2.4——と同じく、本表には新しい行を追加せず該当節に一元化する） | configuration.md §1 の不変条件 |
| L-5 | **閲覧リスナに登録される全 HTTP ルート・全ハブが許可リストと一致する**（不存在の走査ではなく、登録済みの全経路をアーキテクチャテストで列挙し、読み取り専用の許可リストと突合する——リスト外の経路が追加された PR はこのテストで落ちる） | ui.md §4 の不変条件 |

- **L-5 の覆域の限界を明示する（M6-4 で確定。Issue #54）**: 列挙方式は `EndpointDataSource`（`app.Services.GetServices<EndpointDataSource>()` から全登録済みエンドポイントを取得する）を採用し、実現可能性を実機確認した——**Blazor の Razor Components の `@page` ルート（例: `/`）と、Interactive Server が自動登録する SignalR ハブ関連の全ルート（`/_blazor` 本体・`/_blazor/negotiate`・`/_blazor/disconnect/`・`/_blazor/initializers/`・`/_framework/opaque-redirect`）は、いずれも列挙に現れることを実装時に確認した**（`Yagura.Web.Tests.ArchitectureTests.ViewerEndpointAllowlistTests` 参照。推測ではなく実行結果）。したがって「ハブへ到達するまでの経路」自体は本許可リストの検証対象に完全に含まれる。
  **一方、列挙に現れないのは「一度確立した circuit の上でやり取りされる個々の UI イベント」である**——Blazor Server は「UI 更新・イベント処理・JavaScript 呼び出しは WebSocket プロトコルを用いた SignalR 接続上で処理される」（Microsoft Learn "ASP.NET Core Blazor hosting models" の Blazor Server 節、確認日 2026-07-05）。ボタンクリック等のコンポーネントイベントハンドラ呼び出しは、`/_blazor` への接続確立後は単一の SignalR 接続上で多重化されるメッセージであり、ASP.NET Core のルーティング層（`EndpointDataSource`）には個々のハンドラ単位のエンドポイントとして現れない。**これが L-5 の覆域の限界そのものである**: 本許可リストは「circuit を確立するまでの経路」を機械的に検証できるが、「確立した circuit 上で何が呼び出せるか」は列挙の対象外である。
  この限界を埋めるため、**circuit 経由でサーバ状態を変更する経路が閲覧リスナに存在しないことは構造で担保する**——閲覧リスナ側のコンポーネント（`Yagura.Web.Components` 名前空間配下の Razor Components）から書き込み系サービスへ到達できない分離を、マーカーインターフェース `Yagura.Abstractions.Administration.IYaguraWriteService`（メンバーを持たない宣言専用のインターフェース）として実装設計で定め、`Yagura.Web.Tests.ArchitectureTests.ViewerComponentReferenceIsolationTests` が `[Inject]` プロパティ・コンストラクタ引数の型を標準リフレクションで走査し、`IYaguraWriteService` を実装する型への参照がないことを検査する。**M8-4（Issue #71）で書き込み系サービス群（設定ウィザード・本番昇格・circuit 管理の契約 = `ISetupWizardService` / `IPromotionWizardService` / `ICircuitManagementService` と実装）が追加され、この検査は実効化した**（実装が実在すること自体も `AdminScreenArchitectureTests` が検証し、空集合に対する空虚な真で green にならない構造にしてある）。
  **M8-4 の管理画面はこの覆域の限界の実例でもある**: 管理画面は Razor Components のページ（`Yagura.Web.Administration.Screens` 名前空間）であり、HTTP の直接到達は「エンドポイントへの Admin メタデータの機械的付与（`MapYaguraAdmin` が名前空間から導出）+ `ListenerPortGuardMiddleware`」が守るが、**閲覧リスナ経由で確立済みの circuit 上の対話的ナビゲーション（対話的ルーターは同一アセンブリ内の全 `@page` ルートを HTTP 要求なしに解決できる）はルーティング層に現れない**。この経路は circuit 層の二層目で塞ぐ——circuit 確立時に束縛したリスナ帰属（`YaguraCircuitContext`）を、全管理画面が必ず経由する共通レイアウト（`AdminScreenLayout`。経由の強制は `AdminScreenArchitectureTests` が機械検証）が描画前に検査し、帰属が管理リスナでなければ描画せず監査記録（ID 3001）に残す。帰属を判定できない場合も描画しない（fail-closed。`AdminScreenAccessPolicy`）。
  **列挙・参照検査のいずれも標準リフレクションのみで実現でき、外部ライブラリの追加は不要だった**（NetArchTest.Rules 等の参照関係検査ライブラリは、本検査の要件（属性付き型のプロパティ/コンストラクタ引数の型を集めてマーカーインターフェース実装を判定する）に対しては過剰であり、conventions.md の「依存を増やさない」判断規準により採用を見送った）。
  **静的アセット配信経路の扱い（M8-1 / Issue #68 で追加）**: MudBlazor 導入に伴い、閲覧リスナに同梱静的アセットの配信エンドポイント（`MapStaticAssets`。`_content/MudBlazor/` 配下）が加わった。これらも同じ `EndpointDataSource` 列挙に現れ、許可リスト突合の対象である——ただし固定リストではなく**制約付きパターンで突合する**（「既知 3 アセット（MudBlazor.min.css / .js / .js.map）とそのフィンガープリント・圧縮変種のみに一致し、HTTP メソッドは GET/HEAD に限られる」。フィンガープリントは MudBlazor の版更新のたびに変わるため、byte 固定のリストは維持できない——`ViewerEndpointAllowlistTests` のパターン定義コメント参照）。パターン外の静的アセット経路が追加された PR はこのテストで落ちる。**M8-2（Issue #69）で Yagura.Web 自前の静的アセット 2 ファイルが同型の制約付きパターンとして加わった**——共通コンポーネントの内部スタイル `yagura-components.css` と、ステール警告の自律監視 JS `stale-guard.js`（ui.md §5.2 が設計上確定した分離 JS モジュール例外。監視・警告表示はブラウザ内で完結し、サーバへの書き込み・外部通信を行わない）。制約は MudBlazor 側と同じ「既知 2 ファイル + フィンガープリント・圧縮変種のみ・GET/HEAD のみ」。また、.NET 10 の `MapStaticAssets` が build マニフェスト使用時に自動登録する「マニフェスト外ファイルの開発時フォールバック配信」（catch-all `{**path:file}`。GET/HEAD 限定・実在ファイル制約付き。publish 構成では登録されない——dotnet/aspnetcore release/10.0 ソースで確認、2026-07-06）は、設定キー `DisableStaticAssetNotFoundRuntimeFallback=true` で**製品として無効化した**——開発実行・E2E・publish のすべてで配信面が「マニフェスト記載のアセットのみ」に一致し、開発時だけ配信面が広がることを避ける（Yagura.Host/Program.cs）。
- **liveness エンドポイント `/health` の追加（Issue #126。2026-07-09 オーナー決定: 採用）**: 外形監視・LB のヘルスチェックからサーバの応答可否を確認する経路が閲覧リスナに無かったため、`/health` を許可リストへ 1 経路追加した。**認証なし・内部情報を一切持たない固定レスポンス限定**（200 + 固定文字列 `OK` のみ。バージョン・稼働時間・DB 接続状態・カウンタ等は一切含めず、DB にも触れない）——攻撃面最小化の方針（本許可リスト自体の存在理由）と外形監視の要望のトレードオフを、公開して問題のない情報範囲に絞ることで両立した。対応メソッドは **GET と HEAD のみ**（外形監視・LB ツールには帯域節約のため既定で HEAD を送るものがあるため。HEAD は HTTP セマンティクスどおり本文なし・ヘッダ同一——書き込みエンドポイントではない）。管理リスナ（8515）は既に loopback 限定であり、ネットワーク越しの外形監視という目的に対しては到達性が無いため、追加は閲覧リスナ側のみとした
- **閲覧 UI 認証の追加（ADR-0010 Phase 4 決定 7・委任事項 16 ①）**: 閲覧認証 opt-in（`Viewer:Authentication:Windows:Enabled=true`）有効時、閲覧ページ（`/`・`/search`・`/status`）と CSV エクスポート（`/search/export.csv`）に閲覧認可（`ViewerPolicy`。管理 ⊇ 閲覧）を機械的に付与する。**認証は「誰が到達できるか」を絞る機構であり、「どの経路が存在するか」を検証する L-5 と直交する——認証を理由に許可リストを緩めない**。認証関連で閲覧リスナに増えるルート（ログイン画面 `/login` は @page として常時登録・除外対象、副エンドポイント `/login/windows`・`/login/app`・`/logout` は opt-in 有効時のみ登録）は L-5 許可リスト（`ViewerEndpointAllowlistTests`）に明示追加済み（enabled 変種テストで「増えるのはログイン系のみ」を機械検証）。**監視用 `/health` は認証除外を維持**（非機微・固定文字列・GET/HEAD のみ・内部情報ゼロ——除外の継続条件。決定 7 ②）。
  - **`ViewerPolicy` の実効範囲（レビュー指摘への対応。田中・クリス）**: 閲覧ルートは管理リスナからも到達でき（ui.md §4）、`YaguraAdminListenerPort.Ports` にはリモート管理 HTTPS ポート（ADR-0010 Phase 2）も含まれる。**対象外にするのは管理リスナの loopback 束縛ポート経由のみ**（ローカル復旧/全アクセス経路として管理面の規則に委ねる）とし、**リモート管理 HTTPS 面・閲覧リスナ面はいずれも閲覧セッションを要求する**。これにより「`RemoteBinding` + 閲覧認証を同時有効化しても、リモート面で閲覧ログが無認証で読める」false sense of security を塞ぐ（`ViewerPolicyAuthorizationTests` が境界を固定）。判定は接続の実ローカルポートが loopback 管理ポート（`IsLoopbackAdminConnection`）か否かで行う。circuit 層ガード（`MainLayout`）も同じ根拠（`IsLoopbackListener`）で揃える。
- **ログ検索結果 CSV エクスポートの追加（Issue #157）**: 閲覧リスナに `/search/export.csv`（GET のみ）を追加し、L-5 許可リスト（`ViewerEndpointAllowlistTests`）に登録済み。書き込みを一切行わない読み取り専用の口であり、閲覧者が既に到達できるデータ（`ILogStore.QueryAsync` の対話的検索と同じ条件・同じ 10,000 件上限——ui.md §4）を CSV 形式で取得できるようにするのみで、**新しい権限境界・新しい閲覧範囲は生じない**（一覧射影の 200 文字切り詰めを回避して全文を返すが、全文自体は検索結果の詳細表示（`FindByIdAsync`）から既に個別取得可能——architecture.md §6）。CSV インジェクション対策（セル先頭 `=`・`+`・`-`・`@` への `'` 前置）は、エクスポートした CSV を Excel 等で開いた**閲覧者自身の端末上**での数式実行を防ぐものであり、Yagura サーバ自体への攻撃面を追加するものではない。
- **L-3b の覆域の限界を明示する（M6-2 で確定）**: ルート表からの機械的導出方式は、**閲覧リスナへの未登録パス要求（例: `/admin/xxx` のうち実際には登録されていないパス）を「管理系要求」として計上しない**——ルーティングが一致しない要求は通常の 404（`ListenerPortGuardMiddleware` を経由しない）であり、監査記録・拒否カウンタの対象にならない。**この覆域はオーナー決定で確定した（2026-07-05。PR #56 の決定記録）**: ADR-0004 決定 7 の「非 localhost からの管理要求の拒否試行」は「登録済み管理系エンドポイントへの到達試行」と解釈し、未登録パス要求は計上対象に含めない。代替案（「閲覧リスナへの未登録パス要求の全計上」方式——希釈は §4.4 の集約が受ける前提）は、将来この覆域では不足する運用実態が観測された場合の選択肢として記録に残す
- テストは将来のリスナ構成変更の退行も捕まえることが目的であり、**エンドポイントの追加・変更があった PR でも自動で全項目が再検証される**構造にする（許可リストの更新は明示の PR 差分として現れる）
- テスト環境の要件: L-3a の検証には非 loopback の送信元（実インターフェースのアドレス等）が必要であり、IPv6（`::1` 系）と併せて **CI 環境はこれらを検証できる構成を維持する**。スキップ条件は M6-3（Issue #53）のテスト実装で次のとおり確定した: **IPv6（`::1`）はスキップ条件を設けず常時検証する**（Windows の開発機・CI ランナーで IPv6 loopback が使えない構成は想定せず、使えない環境では黙ってスキップするのではなくテスト自体が失敗して顕在化させる）。**L-3a の非 loopback 送信元が存在しない環境ではローカル実行に限りスキップし、CI 上（環境変数 `CI` 設定時）ではスキップを失敗として扱う**（偽 green 防止。SQL Server 適合テストと同じ規約）

### 1.1 外向きネットワーク通信の台帳（[ADR-0007](../adr/0007-reverse-dns-display.md) で新設〔当初は閲覧層のみ〕。[ADR-0017](../adr/0017-email-notification.md) 委任 4 で製品全体へ拡張——2026-07-20。Issue #368）

L-1〜L-5 は受信面（bind 先・登録エンドポイント）の不変条件であり、**本製品が自発的に発する外向き通信を覆わない**。この面を暗黙にしないため、外向き通信を本節の台帳で管理する。**台帳のスコープは製品全体**とする（ADR-0017 委任 4 の裁定——閲覧層と Host 層で台帳を分けると「どこへ何が出ていくか」に 2 か所を見ないと答えられなくなり、台帳の存在意義が薄れる。Host 側相当欄の新設ではなく本表の一般化を選んだ）:

| # | 層 | 通信 | 宛先 | 停止手段 | 根拠 |
|---|---|---|---|---|---|
| 1 | 閲覧層 | 送信元 IP の逆引き（PTR）名前解決クエリ（対象はプライベート/サイトローカル系の帯域の IP のみ） | OS の名前解決機構の参照先——構成済み DNS サーバに加え、OS 既定のフォールバック（LLMNR・NetBIOS 等）を含む（ADR-0007 検証記録。いずれも OS の既存構成であり、本製品が新しい宛先を設定させることはない） | `Viewer:ReverseDns:Enabled`（既定オン。オフでクエリ 0） | ADR-0007 |
| 2 | ホスト | 能動通知のメール送信・管理画面からのテスト送信（SMTP。送信のたびに接続・認証・切断——常設接続なし。宛先ホスト名の名前解決を伴う） | 利用者設定の SMTP サーバ（`Notification:Email:Smtp:Host` / `Port`）。**利用者設定次第で管理セグメント外・公衆網上の外部サービス（M365 等）になり得る**——社内リレーを暗黙の前提にしない（ADR-0017 改訂 1 の明記要求。台帳は「どこへ何が出ていくか」に答える道具であり、暗黙の前提は将来の監査で誤答を生む） | `Notification:Email:Enabled`（**既定オフ = opt-in**。オフで送信 0——キュー内の未送信分も破棄される） | ADR-0017 |

- **台帳にない外向き通信を追加する PR は、本表の更新を同じ PR に含める**（「前例がある」で暗黙に増やさない。層を問わない——スコープの製品全体化に伴い、従来の「閲覧層に追加する PR」から拡大）。ただし L-5 の許可リストと異なり、**外向き通信は CI が実挙動で列挙・突合できない**——本台帳の実効性はレビュー規律と、下記の呼び出し集約 + 単体テストに依存する（非対称の明示）
- **保存先 DB への接続（SQL Server 等。`Storage:*`）は台帳の対象外とする**——外向きの「付随」通信ではなく、利用者が構成する保存基盤そのもの（syslog の受信と同じく製品の本体機能）であり、database.md が正面から扱う。台帳は「主目的の傍らで発生し、利用者が見落とし得る通信」を対象とする（この線引き自体を暗黙にしないため明記する）
- 「オフ時にクエリを発しない」「対象帯域外の IP にクエリを発しない」は、解決 API の呼び出しを解決サービス 1 点（`IReverseDnsLookup` の実装）に集約したうえで**単体テストとして固定済み**（ADR-0007 決定 4。`Yagura.Web.Tests` の `ReverseDnsResolverTests`——偽実装が「呼ばれないこと」を検証する）

## 2. UI 接続層（SignalR circuit）の防御

### 2.1 origin 検証（v0.1: 完動）

- 同一サイト以外からの circuit 確立を常時拒否する（ADR-0004 決定 6）。拒否は計測する（§4 の拒否試行と同じ経路）
- **実装参照（M8-4 / Issue #71）**: `CircuitGuardMiddleware`（`Yagura.Web.Circuits`）が `/_blazor` 配下（circuit の確立・維持経路）への要求の `Origin` ヘッダを要求先（スキーム + ホスト + ポート）と突合し、不一致は 403 で拒否する。ブラウザは cross-site のスクリプトが発する要求に本物の `Origin` を強制付与しスクリプトから偽装できないため、第三者サイトを踏み台にした circuit 確立はこの突合で遮断される。`Origin` を送らないクライアント（ブラウザ外のツール）は拒否しない（この検証の遮断対象は「閲覧者のブラウザの踏み台化」であり、ブラウザ外クライアントはその対象にならない）。拒否はカウンタ（`yagura.web.circuit.origin_rejected`）と監査記録（§4.3 の ID 3002）の両方に残る

### 2.2 circuit 数の上限（v0.1: 完動）

- **上限はリスナごとに設ける**（プロセス全体・両リスナ合算の単一上限にしない）。既定（管理リスナは loopback 限定）では同時利用者が実質 1 人のため小さな固定値でよく、設計の主対象は閲覧リスナ側である。**[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 2 でこの前提が変わる**——リモートバインド opt-in を有効化した環境では、管理リスナが「複数オペレーターの同時対応（障害対応時等）」という利用像を持ち得るため、管理リスナの上限（SEC-1）は Phase 2 でこの利用像を踏まえて見直した（§7 SEC-1 参照）
- **既定値の決め方**: 障害時の閲覧集中——NOC 等で同時多数が一斉に画面を開く利用特性——を入力とする（ADR-0004 決定 6）。circuit あたりのサーバ側メモリを実測し、ターゲット環境（1 台の Windows サーバ）の余裕から逆算する（[SEC-1](#7-確定待ち一覧)。小規模環境が既定値で案内画面に当たらないことを下限の条件とする）
- **到達時は既存を守り、新規を拒否する**: 障害対応中に掲示中のダッシュボード・調査中の検索画面が切れる害は、新規閲覧者が待たされる害より大きい。新規接続には「閲覧者数が上限に達しています」の静的な案内（circuit を要しないページ。**現在の閲覧者数・上限値と、管理者への連絡による解放の導線を含める**）を返し、拒否はカウンタに計上する
- **「既存を守る」は「生きている既存を守る」である**——放置されたタブが上限を占有し、障害時に駆けつけた対応者が弾かれるのは本設計の意図の逆である。これを 2 つの機構で防ぐ:
  - **無操作 circuit の回収**: 一定時間操作のない circuit を切断して枠を解放する。既定は長めとし、掲示用途（操作せず表示し続ける正当な利用）を殺さない値にする——「操作」の定義（掲示ダッシュボードの自動更新受信を操作と数えるか）を含めて [SEC-8](#7-確定待ち一覧) で確定する
  - **circuit の可視化と選択的切断**: **設定画面群（管理リスナ。ui.md §4 の画面構成に従う）内の circuit 管理**に circuit 一覧（接続元・確立時刻・最終活動時刻）を表示し、管理者が個別に切断できる（管理操作として監査対象。閲覧リスナのシステム状態画面には一覧を置かない——§3 の線引き）
- **管理リスナの上限到達時の復旧経路**: 管理リスナの無操作タイムアウトは短めに設定する（実質 1 人運用であり、放置された管理画面が枠を占有し続けると管理者自身がロックアウトされる）。それでも到達した場合の最終手段はサービス再起動ではなく無操作回収の経過待ちであり、回収時間の上限を SEC-8 で確定する
- 現在値（対上限比）・到達はゲージと能動通知で観測可能にする（ui.md §9 / architecture.md §4.6）
- **実装参照（M8-4 / Issue #71。SEC-1・SEC-8 はいずれも仮値のまま実装）**:
  - 台帳と判定: `CircuitRegistry` + circuit ごとの `YaguraCircuitHandler`（`Yagura.Web.Circuits`）。上限・回収の仮値は `CircuitGovernanceDefaults` に集約——**SEC-1 仮値: 閲覧 100 / 管理 5**、**SEC-8 仮値: 閲覧 8 時間 / 管理 30 分**（「操作」の定義も仮: circuit 上の inbound activity——UI イベント・JS interop 応答等——であり、サーバ発の表示更新の受信は数えない）。値が確定するまで設定キーは設けない（additive-only 規約により既定値変更の互換負債を先に負わないため。確定時に設定キー化を判断する）
  - 到達時挙動: 新規の画面表示（Razor Components ページへの GET）の時点で到達リスナの上限を判定し、circuit を要しない静的な案内（現在の閲覧者数・上限値・管理者への連絡による解放の導線を含む）を返す（`CircuitGuardMiddleware`）。**`/_blazor`（negotiate を含む）は上限判定の対象にしない**——確立済み circuit の再接続が同じ経路を通るため、そこで塞ぐと「既存を守る」を壊す（ページ表示を塞げば新規 circuit の発生源は絶たれる。ページを経由せず negotiate を直接叩く経路は残るが、その circuit も台帳・回収の統治下にある——v0.1 骨格の覆域）
  - 一覧・個別切断: 管理画面 `/admin/circuits`（`CircuitManagementService`。切断は監査対象 = §4.3 の ID 2004）。**切断・回収は協調方式**——ASP.NET Core は circuit をサーバ側から直接終了させる公開 API を持たない（`Circuit` 型の公開メンバーは `Id` のみ。.NET 10.0.9 実機確認・2026-07-06）ため、circuit 内の常駐コンポーネント（`CircuitGovernor`）が切断要求を受けて circuit を要しない案内ページへの全ページ遷移を行い、circuit を正常終了させる。購読者が未登録の circuit には切断が届かない限界があり、受理可否は操作結果として観測できる
  - 無操作回収: `CircuitIdleReclaimService`（1 分間隔の走査）。回収は管理「操作」ではなく統治機構の自動動作のため監査記録の対象とせず、カウンタ（`yagura.web.circuit.idle_reclaimed`）で観測する

### 2.3 認証状態の失効の反映（認証 opt-in 有効時）

ADR-0004 決定 6 の「失効は既存 circuit に反映する。ただし掲示用途を不意に切らない」の両立:

- **書き込み・管理操作は失効を即時反映する**: 権限剥奪・セッション無効化の後、既存 circuit 上の管理操作・設定変更は次の操作時点で拒否される（操作のたびに現在の認証状態で認可する——接続確立時の状態を信用し続けない）
- **読み取り専用の表示は即時切断せず、猶予期間の満了までに再認証へ誘導する**: 深夜の無人ダッシュボードが定期失効の瞬間に認証画面へ化ける事故（ADR-0004 の懸念）を避けつつ、**猶予は無期限にしない**（上限値は [SEC-6](#7-確定待ち一覧)。「上限を設けるか」ではなく「値をいくつにするか」が確定対象である——無期限を既定にすれば ADR-0004 決定 6 の「失効は既存 circuit に反映する」の例外条項を原則へ昇格させることになる）
- **対価を正確に記す**: 猶予中の circuit は生きており、失効済み利用者には**失効後に到着した新しいログも流れ続ける**（静止した残像ではなくライブ購読の継続である）。この重みを踏まえて SEC-6 の値を決める
- **猶予は証跡に残す**: 失効の反映時点で「継続を許容した circuit（利用者名・接続元・確立時刻）」を 3000 番台のセキュリティ事象（§4.3）として記録し、当該 circuit の終了（猶予満了・切断・全切断）も記録する——「失効から遮断までの間に誰が何を見得たか」に監査で答えられるようにする
- **即時の全切断を管理操作として提供する**: 資格情報の漏洩対応など「閲覧の継続自体を止めたい」局面では、管理者が全 circuit の即時切断 + 再認証要求を明示的に実行できる（監査記録の対象）
- 未認証で接続できる画面は最小化する（既定構成で未認証 circuit を持つのは loopback 限定の初期ウィザードのみ。ADR-0004 決定 6）
- **実装参照（SEC-6 猶予タイマー。Issue #267。猶予値 = 15 分・2026-07-17 オーナー裁定）**: `YaguraCircuitHandler` が circuit の再接続時に「認証あり → 認証なし」の遷移（失効の検知点）を捉え、閲覧リスナ帰属（`IsAdminListener == false`）の circuit に限り、`CircuitGovernanceDefaults.RevocationGracePeriod`（15 分）だけ表示を維持する。開始を監査 3010、終了（満了・切断・全切断・再認証）を 3011 に記録する。満了時は認証状態を無認証へ落とし circuit を協調切断する。**即時全切断（本節）は緊急全失効（ADR-0013 決定 2・監査 2013）の一部として `CircuitRegistry.RequestDisconnectAllAsync` が実装し、SEC-6 の猶予をバイパスする**（漏洩対応は猶予より強い）。
  - **権限昇格の防止（三重の防御。オーナー指示 2026-07-17）**: 猶予が「権限なく管理画面へ到達できる」穴にならないよう、①**管理セッション（`AdminSessionClaimType` を持つ principal）には猶予を与えず即時反映**する（SEC-6 は掲示用途＝閲覧専用のための機構）②猶予中に維持する状態は管理標識（`AdminSessionClaimType`）と全グループ SID（544 含む）を除去した**無害化 principal**（`IsAdminSessionAuthenticated`／`IsWindowsAdministrator` が構造的に不成立）③猶予対象は閲覧リスナ帰属のみで、その circuit は `AdminScreenAccessPolicy.Decide` が管理画面の描画自体を拒否する（既存）。これらは `CircuitRevocationGraceTests` が固定する

### 2.4 管理 UI 認証（opt-in。[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 1。v0.1: 未該当）

管理リスナ（8515）へ Windows 統合認証（Negotiate）・アプリ独自 ID/パスワード認証を追加する。**既定は現状維持**（認証なし・管理リスナは loopback 束縛のみ）。

- **共存**（[ADR-0013](../adr/0013-admin-winauth-session.md) が ADR-0010 決定 3 の共存セッションモデルを是正。Issue #252）: ログイン時のチャレンジは複数認証スキームで構成する——Windows 統合認証は `Negotiate` スキーム（選択式ログインの `/admin/login/windows` 経路でのみ発火）、アプリ独自認証は ID/パスワード POST。**認証成立後は方式に依らない単一の認証セッション Cookie（スキーム名は歴史的経緯で `YaguraAppAuth` を据え置き）に統一する**——Windows ログイン成功後も同じ Cookie を発行し以降の管理画面は Cookie で認証する（Negotiate はステートレスで後続要求に伝播せず、Cookie 化しないと 401 ループになる——#252 の根本原因）。**方式の区別は Cookie に焼き込む認証方式クレーム（`yagura:auth_method`）で保持**し、監査「誰が」欄はこのクレームから導出する（スキーム名に依存しない。決定 5。標識クレーム欠落は fail-closed）。**「セッション非共有」の実体は資格情報とバックオフ・レート制限状態の独立**であり、認証成立後のセッション担体まで別立てにすることは要求しない（各方式は自分の認証を経なければ Cookie を得られず相互波及は生じない）。**バックオフ・レート制限は方式ごとに独立**（Windows 統合認証は OS/AD のロックアウト・パスワードポリシーに依拠し本製品は関与しない。アプリ独自認証のバックオフ・レート制限は独立管理——[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 9。旧称「ロックアウト」は §2.4.1 の三層防御を指す）
- **Windows 認可（544）の失効反映と緊急全失効**（[ADR-0013](../adr/0013-admin-winauth-session.md) 決定 2）: Windows 由来 Cookie は 544 判定をログイン時に凍結するため、Windows 由来セッションは短い絶対寿命（仮値 1 時間・sliding 抑制・設定で短縮可。app 由来は 8 時間）で失効遅延を有界化する。緊急時の全セッション無効化は **Data Protection キーの破壊ではなくセッション世代番号（`yagura:session_gen`）のバンプ**で行う（キーローテーションは旧鍵を復号用に保持し既発行 Cookie を失効させないため）。各要求で現世代と fail-closed 照合し旧世代 Cookie を無効化する。世代番号はデータルート配下に永続化し（定常再起動では生存＝既発行セッション保持、緊急時のみバンプ＝全失効）、DC 非依存で実行できる（ログイン経路は殺さない）。§2.3「失効の即時反映」の Windows Cookie に対する適用外（反映は Cookie 寿命だけ遅延）はこのトレードオフとして明示受容する
- **認可**: 既定は `BUILTIN\Administrators`（well-known SID `S-1-5-32-544`）のグループ SID クレーム判定（`ClaimTypes.GroupSid`）。アプリ独自認証で作成したアカウントは常に「管理」役割のみを持つ（§3）。**AD グループへのマッピング拡張（SEC-9）は ADR-0010 Phase 4（2026-07-12）で実装済み**——`Admin:Authentication:Windows:AdminGroups` に指定したグループ（名/SID 両受理・SID 集合照合）を 544 判定に**加えて**認可する（§3 に指定形式・ネスト解決の確定内容。lab 実機検証は受け入れ条件）
- **loopback 認証 opt-in**（`Admin:Authentication:RequireForLoopback`。既定 `false`）: 有効化すると loopback アクセスにも認証を要求する。**fail-closed 不変条件**: 本キーが `true` かつ Windows 統合認証・アプリ独自認証のいずれも `false` の組み合わせは、起動時検証（`YaguraConfigurationLoader.Load`）が `ConfigurationValidationException` で起動を拒否する（configuration.md §1「起動失敗」分類。既存 L-4 系と対称）。管理画面（`/admin/auth-setup`）上でも同じ判定を先に行い、UI レベルで親切に拒否する（`AdminAuthenticationAdminService.ConfigureAsync`）
- **Kerberos-only opt-in**（`Admin:Authentication:Windows:KerberosOnly`。既定 `false`。NTLM 無効化）: `NegotiateOptions` 自体には NTLM を無効化する組み込みオプションが存在しない（dotnet/aspnetcore ソース確認。確認日 2026-07-10）。`Authorization: Negotiate <Base64>` ヘッダーのデコード結果に NTLM トークンの署名（ASCII `"NTLMSSP\0"`）が含まれるかを Negotiate ハンドラの手前のミドルウェアで検査し、検出した場合は 403 で拒否する方式を採用した（実装: `Yagura.Web.Administration.KerberosOnlyFilterMiddleware`）。**署名はトークン先頭だけでなく全体をスキャンする**——生の NTLM は署名がオフセット 0 に現れるが、SPNEGO（GSS-API）でラップされた NTLM は NTLM の mechToken（署名を含む）が非ゼロオフセットに埋め込まれるため、先頭一致だけでは SPNEGO ラップ NTLM を見逃す。全体スキャンは両方を一判定で遮断する保守的なヒューリスティックであり、正規の Kerberos AP-REQ・SPNEGO-Kerberos トークンが `"NTLMSSP\0"` を含むことはないため誤検知は生じない（**SPNEGO ラップ NTLM の実機提示形のライブ検証は完了**——AD/Kerberos lab で、SPNEGO(GSS-API) にラップされた NTLM が実提示形で遮断され、かつ正規 SPNEGO-Kerberos・Kerberos AP-REQ が誤遮断されないことを実機確認した〔Issue #228、2026-07-11〕。本ヒューリスティックは元より拒否を増やす方向のみで fail-safe）
- **circuit 認証状態の明示的な汲み直し**（決定 2・検証 3）: Microsoft Learn が示す公式パターン（`CircuitHandler.OnConnectionUpAsync` + `AuthenticationStateProvider.AuthenticationStateChanged`）を実装した（`Yagura.Web.Circuits.YaguraCircuitHandler.OnConnectionUpAsync` + `YaguraCircuitAuthenticationStateProvider`）。SignalR の再接続のたびに現在の `HttpContext.User` を汲み直し、既定の `FixedAuthenticationStateProvider`（接続確立時のスナップショットを固定として扱う）に頼らない。管理画面の描画可否判定（`AdminScreenLayout`）はこの汲み直された状態を毎回参照する
- **初期管理者アカウント**: アプリ独自認証を有効化する操作自体が、既に loopback 限定の管理 UI（認証なしの現状の管理リスナ）上（`/admin/auth-setup`）で行われる——物理到達性による信頼が既に効いている状態でオペレーターが対話的に設定する（ランダム生成した初期パスワードの別経路配布は行わない）
- **ログイン画面の振る舞い**（委任事項 9）: 選択式（`/admin/login`。Windows でサインイン → `/admin/login/windows`（Negotiate チャレンジ）/ アプリ独自 ID・パスワード → `/admin/login/app` への POST）。自動試行（両方式を暗黙に順に試す）は採らない
- **ユーザー列挙耐性・失敗試行対策**（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 3。旧ハードロックアウトを supersede）: 存在しないユーザー名でも実在アカウントの誤パスワードと同一の応答・同等のハッシュ検証コスト（ダミーハッシュに対する PBKDF2 検証）を払う。詳細は §2.4.1（三層防御）参照。CSRF 対策はログイン POST エンドポイントで `IAntiforgery.ValidateRequestAsync` を明示検証する
- **監査「誰が」欄の実効化（決定 6）**: サインイン成功（ID 2008）を起点に、管理操作の監査記録（2000 番台——設定変更 2001・昇格 2002/2003・circuit 切断 2004・フォワーダキット生成 2005・認証設定変更 2006・アカウント作成 2007）はすべて操作者の認証方式（`windows`/`app`）と認証済み利用者名を受け取り記録する（`AuditEvent.AuthenticationScheme`/`AuthenticatedPrincipal`。circuit 上の画面操作は circuit の現在の認証状態——`YaguraCircuitAuthenticationStateProvider`——から導出し、HTTP エンドポイントは `HttpContext.User` から導出する）。**射程の限定**（ADR-0010 決定 6）: 値が入るのは認証を経由した操作のみであり、既定（loopback 認証 opt-in 無効）の loopback 経由の操作は従来どおり接続元（`127.0.0.1`）のみが記録される
- **パスワードハッシュ**: `Microsoft.AspNetCore.Identity.PasswordHasher<TUser>`（`Microsoft.Extensions.Identity.Core` アセンブリ。`Microsoft.AspNetCore.App` 共有フレームワークに含まれ、追加の NuGet 依存なしに利用できることを実機ビルドで確認した）。ASP.NET Core Identity のフル導入（EF Core ストア）は採用していない
- **パスワード強度要件**（ADR-0011 決定 7）: 最小長 12 文字以上（[SEC-12](#7-確定待ち一覧)）+ 既知漏洩パスワード・頻出パターンのブロックリスト突合（必須。大文字小文字を区別しない）を `AppAdminAuthenticationService.SetAccountAsync` で強制する。文字種別（記号・大文字必須等）の複雑性ルールは課さない——予測可能なパターンへ誘導し実効強度をむしろ下げるため。ブロックリスト辞書は静的同梱（実行時ネットワーク取得なし）。同梱辞書の出典・ライセンス・加工内容は `src/Yagura.Host/Administration/AdminAuthentication/PasswordBlocklist/PROVENANCE.md` を参照
- **リモートバインドとの関係**: 本 Phase（Phase 1）自体はリモートバインドを解禁しない（管理リスナは引き続き loopback 束縛のみ）。認証を有効化して初めてリモートバインドを選べるようになる、という条件（決定 1）は **Phase 2 で実装した**（§2.5 参照）

#### 2.4.1 アプリ独自認証の失敗試行対策: 三層防御（[ADR-0011](../adr/0011-app-auth-failure-backoff.md)。旧ハードロックアウトの supersession）

ADR-0010 決定 3 が当初採用したハードロックアウト（一定回数の失敗で一定時間の完全拒否）は、単一アカウントモデル + Phase 2 のリモートバインド解禁の組み合わせで「唯一の正規管理者を締め出せる可用性攻撃の梃子」になり得た（M-1）。ADR-0011 はこれをバックオフ + IP レート制限 + グローバルトークンバケットの三層防御へ置き換える——いずれも正しいパスワードを完全拒否しない。

- **評価順序**（決定 2）: ①IP レート制限（送信元単位・最も安価・loopback 除外） → ②グローバルトークンバケット（プロセス全体・loopback 除外） → ③アカウント単位バックオフ + パスワード検証。①②で拒否された試行はパスワード検証まで到達せず、連続失敗回数 n も進めない（二重計上しない）
- **①IP レート制限**: 送信元 IP 単位の固定窓（仮値 60 秒 / 10 回。[SEC-12](#7-確定待ち一覧)）。超過時は待たせず即座に 429 + 有限 `Retry-After`（仮値上限 30 秒）で拒否する。**状態辞書のエビクション**（[Issue #233](https://github.com/Yanai-Taketo/Yagura/issues/233)。除去条件は PR #236 レビューで 2 段階に精緻化）: 送信元 IP は非実在ユーザー名と異なり攻撃者が制御できる次元（特に IPv6 は実質無制限のアドレス空間）であり、「状態空間の上限は運用者制御」という非実在ユーザー名の設計根拠（下記）が成立しない。このため `ActiveNotificationMonitor` の周期評価（仮値 1 分ごと。§4.6）が毎周期、窓が失効したエントリを掃き出す（`AdminAuthFailureDefense.SweepIdleIpRateLimitEntries`）。窓内で上限に達し続けているアクティブな送信元は対象外——上限超過による強制退避（LRU 等）は採用しない（退避された攻撃者がレート制限を回避できてしまうため）。除去条件は「**窓失効 かつ（能動的な拒否ストリーク（`DenyStreakStartAtUtc`。下記の 1019 エスカレーション判定の起点）を持たない、または staleness-cap = エスカレーション閾値の 2 倍（≒ 30 分）を超えて窓が凍結している）**」とする:
  - **拒否ストリーク保護**（PR #236 レビュー指摘 その 1）: 窓失効のみを条件にすると、毎窓の先頭で上限超のバーストを打ち残りをアイドルにするペース調整型の持続攻撃で、次のバーストの前にスイープがストリークごとエントリを消してしまい、1019 の能動通知が永久に発火しなくなる。このためストリーク進行中のエントリは窓失効後も保持する
  - **staleness-cap**（PR #236 レビュー指摘 その 2）: ストリーク保護だけでは、spoof IP から 1 窓内に上限超の試行を打ってストリークを立てそのまま放置する「撃ち逃げ」で、窓失効後もエントリが恒久ピン留めされ（`DenyStreakStartAtUtc` は新規アクセスによるロールオーバーが「上限未満の窓」を観測したときしか null に戻らないため、アクセスが二度と来なければ永久に非 null）、IPv6 アドレス空間でメモリ無制限増加が別経路で復活する。これを塞ぐため、ストリークを持つエントリでも `now - WindowStartAtUtc` が staleness-cap を超えたら除去可能にする。撃ち逃げ放置ピン（窓が凍結）は約 2×閾値 経過後に除去され、能動ペース攻撃（毎窓アクセスで窓が更新され staleness に到達しない）は保持される。「2×閾値」を使うのは、正当にエスカレーションするストリーク（閾値 = 15 分で 1019 発火）が確実に発火し終えた後にのみ staleness 除去が効くようにするため（放置ストリークも 15 分で 1 回はエスカレーションを出してから片付く）。**結果、辞書サイズは「現に進行中で通知対象の攻撃者数」で有界**であり、攻撃者が任意に膨らませられる恒久ピン留めは残らない

  IPv6 の `/64` 等プレフィックス単位のキー集約は見送った（レート制限のセマンティクスを変える度合いが大きく、共有プレフィックス下の無関係な送信元を巻き込みかねないため）——将来の実測でアイドルスイープだけでは不十分と判明した場合の追加検討課題として残す
- **②グローバルトークンバケット**: プロセス全体で単一の状態（定常補充 1 トークン/秒・バースト 20。[SEC-12](#7-確定待ち一覧)）。単一アカウントモデル固有のユーザー名探索攻撃（多数の候補名を多数の送信元 IP から試す）を、送信元分散に依らずプロセス全体の総試行量で頭打ちにする補完層
- **③アカウント単位バックオフ**: 連続失敗回数 n が猶予閾値 k（仮値 3 回）を超えると `delay = min(base * 2^(n-k), cap)`（仮値 base=1 秒・cap=30 秒）の遅延を試行ごとに適用する。**cap に達しても拒否はしない**——正しいパスワードであれば cap の待ち時間の後に必ずログインできる。キーは (アカウント正規化ユーザー名 × loopback/remote の別) の複合キー——送信元 IP はキーに含めない。n はログイン成功時に 0 へリセットし、無失敗が 30 分（仮値）継続したらアイドル減衰でも 0 へリセットする。**プロセス再起動で全状態が失われる**（インメモリ保持。正規の復旧手段ではなく攻撃者にも作用する副作用として明記する）
- **非実在ユーザー名には個別状態を持たせない**（決定 3）: 実在アカウントの集合は運用者が制御するため、状態を持つキー空間の上限が攻撃者の入力ではなく管理者の操作で決まる——追加の上限機構なしにメモリ枯渇 DoS を回避する。非実在ユーザー名は①②のみで絞る
- **loopback の扱い**（決定 4）: `127.0.0.1`・`::1` はいずれも①②の対象外（カウントも 429 拒否もしない）。loopback に作用する失敗試行対策は③バックオフのみ——「loopback は認証手段が全滅しても無条件の最終復旧経路」（ADR-0010 決定 1）を三層防御のどの層によっても否認しない。三層とも loopback の判定点は `Yagura.Web.Administration.AdminAuthenticationExtensions.IsLoopbackAdminConnection`（`IsUnauthenticatedLoopbackBypassAllowed` と同一の判定点）を共有する
- **応答種別・UI 文言の統一（決定 3 の非開示要件・列挙耐性の核心）**: ログイン失敗の HTTP 応答は、**実在アカウントのバックオフ待機中・非実在ユーザー名・誤パスワードのいずれでもバイト単位で同一**（`302 → /admin/login?error=1`・待機パラメータなし・同一の汎用 UI 文言）とする。バックオフ待機であることを応答種別・`Location` ヘッダ・UI 文言のいずれからも観測させない——`curl` の生ヘッダだけで実在/非実在を判別できる「計測不要の直接的な告白」経路（決定 3 が名指しで排除）を塞ぐ。**バックオフの効果はサーバ側の応答遅延（レイテンシ）としてのみ現れる**（`TryAuthenticateAsync` 内の `Task.Delay`）——これは決定 3 が Phase 1 で明示的に受け入れたタイミング非対称の範囲（計測して初めて分かる推論に留まる）。
  - **カウントダウン表示（決定 6）は IP レート制限/グローバルトークンバケット拒否の 429 応答（アクセス集中）に限る**——①②は送信元 IP 単位・プロセス全体の状態で判定し、ユーザー名の実在有無に依存しない（決定 4）ため、同一送信元からの実在/非実在の試行は同一の 429 + `Retry-After` + カウントダウンを受け、列挙シグナルにならない。**アカウント単位バックオフはカウントダウンを出さない**——決定 6 の当初意図（「壊れた／固まった、と誤解させない」）から一部外れるが、決定 3 が「決定 6 の文言は本決定 3 の非開示要件に従う」と明示的に決定 3 を優先させている。失敗理由・拒否層の別は監査記録にのみ残す（決定 9）
- **原子的な状態更新**（委任事項 1）: バックオフ状態・IP レート制限窓はいずれも `ConcurrentDictionary` の CAS ループで原子的に更新し、分散送信元からの同時失敗による lost update を防ぐ（PR #217 の DB 側原子的 UPDATE と同じ設計意図をインメモリで再現）
- **能動通知への昇格**（決定 6）: 同一アカウントでバックオフ cap 到達状態、または IP レート制限/グローバルバケット拒否状態が仮値 15 分（[SEC-12](#7-確定待ち一覧)）以上継続する場合、`ActiveNotificationMonitor`（§4.6）経由でイベントログの 1000 番台（ID 1019・警告）へ昇格する。通知本文には主因層（バックオフ/IP レート制限/グローバルバケット）・上位の送信元 IP を含める
- **実装**: `Yagura.Host.Administration.AdminAuthentication.AdminAuthFailureDefense`（状態保持・三層の判定）+ `AppAdminAuthenticationService.TryAuthenticateAsync`（評価順序の実装）+ `AdminAuthEndpoints`（HTTP 応答・監査記録）

### 2.5 管理リスナのリモートバインド + HTTPS（[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 2。opt-in。v0.1: 未該当）

Phase 1（§2.4）が整えた認証基盤の上に、管理リスナ（8515）のリモートバインドを解禁する。**既定は現状維持**（`Admin:RemoteBinding:Enabled = false`。loopback 束縛のみ）。

- **bind 構成**: リモートバインド opt-in を有効化すると、既存の loopback bind（`Admin:HttpPort`。平文 HTTP・両系統）を**置き換えずに残したまま**、別ポート（既定 8516。`Admin:Https:Port`）へ全インターフェース + HTTPS 必須の bind エントリを追加する（`Yagura.Host.ListenerBindPlan`）。同一ポートでの共存を避けた理由（OS の bind 制約・決定 4 の loopback HTTPS 対象外原則）は configuration.md §4.2 参照
- **fail-closed 不変条件（決定 1・4。既存 L-4 系の拡張——§1 参照）**: `Admin:RemoteBinding:Enabled = true` かつ、①認証（Windows 統合認証・アプリ独自認証のいずれも無効）または②HTTPS（`Admin:Https:Enabled = false`、または `Admin:Https:CertificateThumbprint` が未設定・不正形式）のいずれかが未構成の組み合わせは、起動時検証（`YaguraConfigurationLoader.Load`）が `ConfigurationValidationException`（イベント ID 1012。`ConfigurationEventIds.AdminRemoteBindingFailClosedStartupRejected`）で起動を拒否する。実プロセスを起動する E2E 回帰テストで固定（`AdminRemoteBindingRegressionTests`。既存 `AdminAuthenticationFailClosedRegressionTests` と同じ子プロセス起動パターン）。**管理画面（`/admin/auth-setup`）上でも同じ向きの判定を先に行う**——リモートバインドが有効な既存構成で認証方式を両方とも無効化する設定変更は、書き込み前に UI レベルで拒否する（`AdminAuthenticationAdminService.ConfigureAsync`）。Phase 1 の loopback 認証 opt-in の UI 拒否（§2.4）と対称の二段構えであり、誤設定で次回起動が起動時 fail-closed（1012）に拒否されて syslog 受信ごと止まる事故を、設定ファイルへ書き込む前に防ぐ
- **認証の適用範囲**: リモート経由の管理操作は（loopback 認証 opt-in の設定値に関わらず）常に認証必須とする。既存の `Admin:Authentication:RequireForLoopback`（既定 `false`）は loopback 経由の操作にのみ影響し、リモート経由の操作の認証要否には影響しない。**実装は接続元ポートで分岐する**——`AdminAuthenticationExtensions.AdminPolicyName` の認可判定（`RequireAssertion`）は、`AuthorizationHandlerContext.Resource`（既定で現在の `HttpContext` そのもの——`AuthorizationMiddleware` の既定動作。dotnet/aspnetcore ソース確認、確認日 2026-07-10）から接続の実ローカルポートを読み、**管理リスナの loopback 束縛ポート経由・かつ `RequireForLoopback = false` の場合に限り**未認証のまま許可する（`IsUnauthenticatedLoopbackBypassAllowed`）。リモート HTTPS ポート（別ポート）経由の接続はこの例外条件に一致しないため、常にフルの認可判定（Windows 管理者 or アプリ認証済み）を要求する。circuit（Blazor Interactive Server）経由の管理画面も同型の判定を独立に持つ——`YaguraCircuitContext.IsLoopbackListener`（接続が loopback 束縛ポート経由かを保持。`IsAdminListener` とは別の軸）を `AdminScreenAccessPolicy.IsAuthenticationSatisfied` が参照し、同じ「loopback かつ RequireForLoopback 無効なら無条件許可、それ以外は要認証」を circuit 層でも成立させる。**リスナ帰属は circuit 確立時のスナップショットに固定せず、SignalR の再接続のたびに現在の接続の実ローカルポートから汲み直す**（`YaguraCircuitHandler.OnConnectionUpAsync`。PR #224 レビュー指摘 #1 への対応）——認証状態の汲み直し（§2.4 の公式パターン）と同じ理由で、loopback で確立した circuit の再接続が別ポート（リモート HTTPS）の物理コネクションへ切り替わった場合に古い loopback 帰属（= 無認証許可側）が持ち越されることを防ぐ。再接続時に帰属を判定できない場合は不明（null）へ降格する（fail-closed——管理画面は描画されず、認証充足判定も認証必須側。復旧はページの再読み込み）。**`Admin:RemoteBinding:Enabled` を `RequireForLoopback = false` のまま有効化した典型構成で、この分岐が正しく機能することを実プロセスへの実 HTTP 要求で検証済み**（`AdminRemoteBindingRegressionTests.RemoteBinding_WithRequireForLoopbackFalse_LoopbackStaysUnauthenticated_ButRemoteRequiresAuthentication`）
- **証明書の参照方式（決定 4）**: Windows 証明書ストア（ローカルコンピューター・`My`）からの拇印参照——configuration.md §6（閲覧 UI の HTTPS）と同型の方式を採用する（`Yagura.Host.Administration.Https.AdminCertificateProvider`）。設定キーは閲覧 UI の HTTPS とは独立（`Admin:Https:*`。同一証明書の流用は両方のキーに指定することで実現し、暗黙の連動はしない）
- **秘密鍵アクセス権の付与**: 証明書が解決できた場合、サービスアカウント（`NT SERVICE\Yagura`）へ秘密鍵の読み取り権限のみを自動付与する（`Yagura.Host.Administration.Https.AdminCertificatePrivateKeyAccessGranter`）。**対応範囲は CNG（ソフトウェアキーストレージプロバイダー）ベースの秘密鍵に限る**——鍵コンテナファイル（`%ProgramData%\Microsoft\Crypto\Keys\<UniqueName>`）への ACE 追加という実装方式上、スマートカード・HSM・TPM 保護鍵等のファイルとして存在しない秘密鍵には対応できない（その場合は証明書スナップイン `certlm.msc` からの手動権限付与に誘導するエラーメッセージを返す——configuration.md §6 CF-D2 と同じフォールバック）。付与に成功した場合は監査記録の対象（イベント ID 2009。`AuditEventKind.AdminHttpsCertificatePrivateKeyAccessGranted`）。付与に失敗しても起動は妨げない（現在の実行アカウントが既に証明書へアクセスできる場合は動作を継続できるため——警告のみ）
#### 秘密鍵権限付与の実機検証結果（SEC-13 ①②。lab 2026-07-18。Windows Server 2025 10.0.26100 / Yagura 0.4.0 / インストーラ登録の `NT SERVICE\Yagura`）

自己署名証明書（`New-SelfSignedCertificate`。CNG ソフトウェアキーストレージプロバイダー）を `LocalMachine\My` へ導入し、`Admin:Https` + `Admin:RemoteBinding` を有効化して実測した。**上記の「付与に失敗しても起動は妨げない（警告のみ）」は、権限が既にある場合にのみ成り立つ**ことが判明した。

**① 秘密鍵ファイルの ACL（付与前。`icacls` 実出力）**——サービスアカウントの ACE は存在しない:

```
C:\ProgramData\Microsoft\Crypto\Keys\f83e5179c4c5591f738e27ee94fa1a8f_5b848d5b-29e7-411e-9e93-5f5124d06bbf
    CREATOR OWNER:(F)
    NT AUTHORITY\SYSTEM:(F)
    BUILTIN\Administrators:(F)
```

**② 自動付与は失敗する**。サービスアカウントは鍵ファイルの ACL を書き換える権限（WRITE_DAC）を持たないため、設計どおりの警告へ落ちる:

```
[admin-https-private-key-grant-failed] 管理リスナのリモート HTTPS 証明書の秘密鍵読み取り権限を NT SERVICE\Yagura へ
自動付与できませんでした（理由: 秘密鍵ファイル ... への ACL 付与に失敗しました: Attempted to perform an unauthorized
operation.）。... 証明書スナップイン（certlm.msc）から手動で権限を付与してください（configuration.md §6 CF-D2）。
```

**③ 権限が全く無い状態ではサービスが起動しない（不具合。要修正）**。上記の警告に到達する前に、鍵ファイルのパスを解決する処理が未処理例外となりプロセスが落ちる:

```
System.Security.Cryptography.CryptographicException: キー セットがありません。
   at System.Security.Cryptography.CngKey.Open(String keyName, CngProvider provider, CngKeyOpenOptions openOptions)
   at Yagura.Host.Administration.Https.AdminCertificatePrivateKeyAccessGranter.ResolveCngKeyFilePath(X509Certificate2 certificate)
```

`ResolveCngKeyFilePath` は**付与先のファイルパスを知るために秘密鍵を開く**が、その open 自体が付与対象の権限を要する（鶏と卵）。結果として「サービスアカウントに権限が無い」という本機構が想定する典型状況で、警告ではなく**起動失敗**になる。SCM からは 7000/7009（時間内に応答しない）として観測される。開発機の既存回帰テスト（`AdminRemoteBindingRegressionTests`）が通っていたのは、テストが管理者権限のプロセスで走り鍵を開けるためであり、**SEC-13 ① が「未確認」として挙げていた差分そのもの**である。

**④ 読み取り権限を別途付与すれば TLS は成立する**。`icacls <鍵ファイル> /grant "NT SERVICE\Yagura:(R)"` を適用すると:

```
NT SERVICE\Yagura:(R)
BUILTIN\Administrators:(F)
NT AUTHORITY\SYSTEM:(F)
```

サービスは即座に起動し 8516 を bind、TLS ハンドシェイクが成立する（`curl -k https://localhost:8516/admin` → `302`、`ssl_verify_result=0`）。すなわち**サービスアカウントによる秘密鍵の読み取り自体は成立する**（SEC-13 ① の本来の問い）。

**⑤ 監査 2009 は記録されない**。2009 は付与に**成功した**場合のみ発火するため、既定の ACL 構成では出力されない。「付与は監査対象」（決定 4）が実際に働くのは、サービスアカウントが鍵の ACL を書き換えられる構成に限られる。

**⑥ AD CS 発行証明書でも結論は同一（SEC-13 ③。2026-07-18 に AD CS を導入して実施）**。lab の DC へエンタープライズ ルート CA（`CN=yagura-test-CA`）を構成し、`WebServer` テンプレート + `Microsoft Software Key Storage Provider`（CNG）で証明書を発行して検証した。

- 発行証明書: `Subject = CN=WIN-H31KVKTHCKU.yagura.test` / `Issuer = CN=yagura-test-CA, DC=yagura, DC=test` / 鍵は **CNG（`RSACng`）**
- **鍵ファイルの既定 ACL は自己署名の場合と同一**（`CREATOR OWNER:(F) / NT AUTHORITY\SYSTEM:(F) / BUILTIN\Administrators:(F)`。サービスアカウントの ACE は無い）
- **③ と同じ未処理例外でサービスが起動しない**（`CngKey.Open` の `CryptographicException: キー セットがありません`）
- 読み取り権限を別途付与すると起動し、**TLS はチェーン検証込みで成立する**（`curl` を `-k` なしで `http=302` / `ssl_verify_result=0`——CA がドメインで信頼されているため）
- **自動付与は同じく失敗**（`[admin-https-private-key-grant-failed]` / `Attempted to perform an unauthorized operation.`）、**監査 2009 も同じく記録されない**

すなわち**本節の①〜⑤の結論は証明書の発行元に依存しない**——自己署名か AD CS 発行かは無関係で、決定要因は「鍵ファイルの既定 ACL にサービスアカウントの ACE が無いこと」である。AD CS を導入すれば解決する類の問題ではない。

- **最低 TLS バージョン**: TLS 1.2 以上を最低要件とし、TLS 1.3 を優先する。Kestrel の `HttpsConnectionAdapterOptions.SslProtocols` に `Tls12 | Tls13` を明示固定する（OS 既定の schannel ポリシーに暗黙に委ねない）
- **暗号スイートの個別制約は行わない**（委任事項 7 の結論）: .NET の `System.Net.Security.CipherSuitesPolicy` クラスは `[UnsupportedOSPlatform("windows")]`（Microsoft Learn API リファレンス、確認日 2026-07-10）——**Windows では利用できない**（Linux/OpenSSL 環境向けの API）。Yagura は Windows ネイティブ製品であり、Windows 上での暗号スイートの選定は OS（Schannel）のポリシー・グループポリシーに委ねる以外の手段が .NET から提供されていない。これは推測ではなく確認済みの API 制約であり、「実装しなかった」のではなく「.NET が Windows 向けに提供していない」ため対応不可能という限界を明示する（暗号スイートの統制が必要な環境は、OS レベルの TLS ポリシー（`Enable-TlsCipherSuite`/`Disable-TlsCipherSuite` 等の PowerShell コマンドレット・グループポリシー）で行う——利用者向けドキュメントへの申し送り。§8 SEC-D7）
- **証明書が解決できない場合（縮小継続）**: fail-closed 不変条件は「静的な設定の形」（拇印の形式・認証/HTTPS の有効フラグ）までしか検証できない。拇印が実際に証明書ストアで解決できるか（証明書の存在・秘密鍵アクセス可否・有効期間内か）は環境依存のため、**このリモート HTTPS bind エントリ 1 本のみを開かずに縮小継続する**（configuration.md §4.1「指定した bind 先が使用できない場合...そのリスナは開かずに縮小側で継続する」と同型の扱い。起動時警告イベント ID 1013。`ConfigurationEventIds.AdminHttpsCertificateUnavailableAtStartup`）——**管理リスナ全体・loopback 面は一切影響を受けない**（決定 4「loopback 経由の管理リスナは HTTPS の対象外のまま残る」の直接の帰結。RDP + loopback からの復旧操作は常に可能）
- **証明書期限切れ時の挙動（決定 4。configuration.md §6 の既存方針と同型）**: HTTPS リスナは停止し HTTP へは落とさない。実装は Kestrel の `ServerCertificateSelector`（TLS ハンドシェイクのたびに呼ばれる公式の拡張点）を用い、証明書の有効期間外（`NotBefore`/`NotAfter` の範囲外）を検知した以後の新規ハンドシェイクを拒否する（`null` を返す——TLS レベルでの拒否）。**リスナの bind 自体を解除する（ソケットを閉じる）実装ではない**——新規 TLS ハンドシェイクの受理のみを停止する点が「リスナを停止する」の実効的な意味である。loopback 面には一切影響しない。**失効（revocation）の確認は行わない**——判定は有効期間（`NotBefore`/`NotAfter`）のみであり、CRL/OCSP 照会は起動時・実行時とも実装しない（閲覧 UI 側の configuration.md §6 は「期限切れ・失効時」と両方を記すが、管理リスナ側の現時点の実装範囲は期限のみ。失効の即時反映が必要な場合はストアからの証明書削除——下記の周期監視が 1015 で検知する——が実効的な代替手段になる）
- **証明書の期限接近・稼働中異常の周期監視（決定 4「期限接近の事前警告」の実体。PR #224 レビュー指摘 #2・#3 への対応）**: `ActiveNotificationMonitor`（architecture.md §4.6 の能動通知。1 分周期・抑制窓 15 分）に管理リスナのリモート HTTPS 証明書の評価を追加した（`IAdminHttpsCertificateStatusProbe` / `StoreAdminHttpsCertificateStatusProbe`——周期ごとに証明書ストアを拇印で再照会する）。①有効期限が閾値（仮値 **30 日**。`ActiveNotificationConstants.AdminHttpsCertificateExpiryWarningWindow`）以内に接近したら **1014** で事前警告、②稼働中にストアから削除された・秘密鍵にアクセスできなくなった・期限切れへ遷移した（= `ServerCertificateSelector` が新規ハンドシェイクを拒否している状態）ら **1015** で継続警告する（§4.3）。プローブの結線はリモート HTTPS bind が実際に有効な場合のみ——起動時に証明書を解決できず縮小継続した構成は 1013 が報告済みで、再起動なしに bind が有効化されることもないため周期監視の対象外。**限界の明示**: 個々の TLS ハンドシェイク失敗イベントを Kestrel から拾う配線は持たない（`ServerCertificateSelector` の `null` 返却はハンドシェイク失敗として TLS レベルで処理され、アプリ層のフックが公式に提供されていない）——稼働中の証明書異常の検知は本周期監視（状態の検知。最大 1 分の遅延）が受け持つ
- **circuit 上限（SEC-1）の見直し**: §2.2 のとおり Phase 2 は「管理リスナ = 実質 1 人」の前提を崩す。仮値を 5 → **20** へ引き上げた（`Yagura.Web.Circuits.CircuitGovernanceDefaults.AdminCircuitLimit`）。依然として仮値であり、実測による確定は今後の課題として残る（§7 SEC-1）
- **認証負荷とログ受信の分離（Phase 2 受け入れ条件 (iv)）**: 管理リスナの認証処理（Negotiate ハンドシェイク・アプリ独自認証のパスワード検証等）と、UDP/TCP 受信パイプラインは別々の Kestrel エンドポイント・別々のスレッドプール作業単位で処理される——受信パイプライン（`Yagura.Ingestion`）は Kestrel と物理的に独立した `UdpClient`/`Socket` ベースの実装であり、HTTP 層（Kestrel・認証ミドルウェア）と CPU・スレッドプールを共有するが、受信処理自体は非同期 I/O 主体で HTTP 要求処理と直接の排他関係を持たない。この分離の負荷試験は `tools/Yagura.Bench` に追加した専用シナリオで検証する（architecture.md §5.2 の回帰ベンチ基盤を流用。詳細は同ツールのシナリオ一覧を参照）
- **NTLM 実質縮退期間のリスク受容**: ADR-0010 の帰結・Phase 2 受け入れ条件 (v) のとおり、Kerberos SPN の仮想サービスアカウントでの成立可否は Phase 3（未着手）まで未検証であり、**リモート解禁時点の Windows 統合認証は NTLM が事実上の常用経路になり得る**——NTLM の既知の弱点（リレー攻撃・pass-the-hash・相互認証の欠如）を受容したうえでの解禁である。緩和策は Kerberos-only モード（決定 2。§2.4）と HTTPS 必須（本節）。利用者向けドキュメントへの明記は §8 SEC-D7

## 3. 役割の粒度（認証 opt-in 有効時）

- **v1.0 までの役割は「閲覧」「管理」の 2 段のみ**とする（ADR-0004 決定 3 の最小構成をそのまま採用する）。閲覧 = ログ本体・自身の状態表示の読み取り、管理 = 管理操作全般（設定変更・昇格・保持期間変更・全切断等）+ **監査記録の読み取り**
- **監査記録の閲覧は「管理」役割・管理リスナ帰属とする**: 監査記録には利用者名・接続元・設定変更の差分要約が含まれ、既定（認証なし）の閲覧リスナから見えれば LAN 上の誰でも管理操作の履歴を偵察できる。閲覧リスナに置いてよい読み取りは「ログ本体・自身の状態表示（カウンタ・ゲージ・通知履歴）」に限る——この線引きは L-5 の許可リストの設計原則でもある
- 割当は **AD グループへのマッピング**で行う（グループ名を設定で指定する。個別ユーザーの列挙は管理対象を増やすため既定にしない）。**指定形式・ネスト解決は SEC-9 として ADR-0010 Phase 4 実装で確定した（2026-07-12）**:
  - **グループ名（`DOMAIN\Group`）と SID（`S-1-...`）の両形式を受理する**。名は起動時に `NTAccount.Translate` で SID へ解決してキャッシュする（SID 形式で与えられた指定は変換問い合わせを発さず正規化して受理——DC 非依存で解決できる指定を優先できる）。解決できない指定（存在しない名・DC 到達不能・タイプミス）は起動を止めず警告してスキップする（認可を付与しない安全側——§1 の縮小側原則と同じ向き）
  - **ネストグループは追加の LDAP 問い合わせを要しない**: 認可判定はトークンのグループ SID クレーム集合（`WindowsIdentity.Groups`——推移的グループを OS が展開済みで載せる）と、設定グループの解決済み SID 集合の交差で行う。照合は SID 文字列に一本化する（名の表記ゆれ・別名解決の非決定性を認可判定から排除する）
  - **役割の割当**: `Admin:Authentication:Windows:AdminGroups` → 「管理」役割（既定の `BUILTIN\Administrators`（544）判定に**加えて**認可。544 を置き換えない）。`Viewer:Authentication:Windows:ViewerGroups` → 「閲覧」役割、`Viewer:Authentication:Windows:AdminGroups` → 「管理」役割（管理 ⊇ 閲覧）。役割判定は Windows ログイン時に 1 回行い、認証成立後は単一 Cookie の役割クレーム（`admin_session`/`viewer_session`）で搬送する（ADR-0013 決定 5 の単一 Cookie モデルを閲覧へ共用——役割の失効は Cookie 寿命 + 緊急全失効で有界化。§2.3）
  - **単一 Cookie で 2 段役割を運ぶ判断の記録（能動的判断。クリスのレビュー指摘）**: ADR-0013 は方式 B（単一 Cookie）の分離論法を「役割が管理単一だから担体共通でも capability が同じ」と述べ、別 Cookie スキーム（方式 (c)）を「役割粒度が分化したら再評価」として先送りした。Phase 4 で「閲覧/管理」の 2 段役割が実効化したため、単一 Cookie は**異なる capability（`viewer_session` ≠ `admin_session`）を同一担体で運ぶ**。この分離は担体（Cookie スキーム）ではなく**クレームの正しさに全依存**する（ADR-0013 決定 5 の単一クレーム依存）。健全性は次で担保する: (i) 認可は標識クレームを正規根拠にし、欠落時は fail-closed（管理者/閲覧者へ昇格させない）、(ii) 閲覧セッションは `admin_session` を持たないため管理リスナで拒否される（`ViewerPolicyAuthorizationTests`・lab 実機で確認）。役割数が 3 段以上へ分化する、または一方の役割セッションだけを独立失効させたい要求が出た時点で方式 (c)（別 Cookie スキーム）を再評価する——ADR-0013「先送り」節の再評価トリガをこの判断で更新する。
- より細かい粒度（特定送信元のみ閲覧・検索のみ等）は v1.0 のスコープに含めない。**要望が反復した場合（目安: 独立した利用者から 3 件以上）に個別 ADR で再評価する**（役割の追加は認可判定の挿入点があれば増築できる——判定点を役割名のハードコードにしない実装要件のみ先に置く）

## 4. 管理操作の監査記録（v0.1: 完動）

### 4.1 対象と記録内容

- 対象は ADR-0004 決定 7 のとおり: **管理操作全般**（設定変更・再読み込み・DB 切替・昇格・保持期間変更・旧 DB ファイルの退避/削除・証明書の秘密鍵権限付与・circuit の個別/全切断・一時保管領域の手動操作等）と、**拒否された試行**（閲覧リスナに到達した管理系要求の拒否 = §1 L-3b、認証失敗、origin 検証拒否）
- **他文書が「監査記録に乗せる」と定めた項目の受け皿**（逆引き。網羅性の検証点）:
  - **手動移行の実施記録の投入**（database.md §6.2——製品外で実施された移行の事実・件数・実施者を証跡へ取り込む経路。記録手段は**手動移行手順の作成（database.md DB-D1・operations.md 起筆時）までに**本節と合同で確定する——手順書が記録手段より先に必要になるため）
  - **インストーラ由来の記録の転記**（configuration.md §4.3 の FW 規則一覧・オプトアウト選択、**本文書 §5** の ACL 適用——初回起動時にイベントログへ転記し、採番は 2000 番台の管理操作区画に含める）
- 記録内容: 誰が（認証有効時は利用者名、無効時は接続元）・いつ（UTC）・何を・変更前後の要約。**秘密情報（パスワード・提示 SQL の原文）は記録しない**（configuration.md §2・database.md §5.2 で確定済みの各規則に従う）
- **対象外の明示（手編集 + サービス再起動。Issue #306。2026-07-18 オーナー裁定）**: 設定ファイル（`yagura.json`）の手編集を**サービス再起動**で反映した変更は、アプリの監査記録には残らない——アプリの操作面（ウィザード = 2001・再読み込み操作 = 2016）を経ない変更であり、前回稼働時の設定を記録した永続物も持たないため、起動時に「何が変わったか」を検出する基準が存在しない（起動時に出るのは現在の内容に対する検証警告・未知キー警告のみ）。この経路の統制は**データルートの ACL（§5）と、構成する場合は OS のファイル監査（SACL）に委ねる既存特性として受容する**（限界の明示——ADR-0004 決定 7 の流儀）。手編集でも**再読み込み操作（UI /admin/reload・SCM カスタム制御コード）を経れば 2016 に証跡が残る**（configuration.md §3）——証跡を残したい運用では再起動ではなく再読み込みを推奨経路とする。軽量補完として**起動時の設定差分照合（Issue #329）**を実装済み: 最後に適用した生 options のスナップショット（データルート直下 `last-applied-configuration.json`。保存契機は起動完了時・ウィザード保存 = 2001・再読み込み反映 = 2016）と起動時点の設定ファイルを比較し、差分があれば「前回稼働時から設定ファイルが変更された状態で起動した」を監査 2019 に 1 件記録する（変更キー名のみ——2016 と同粒度。configuration.md §3）。ただしこれは「**何が**変わったか」の補完であり「**いつ・誰が**」は特定できず、スナップショット自体もサービスアカウント書き込み可（手編集できる者は消せる）——本項の受容（統制は ACL・SACL）を変えるものではなく、事故調査のための運用証跡である（一次の耐タンパ線はイベントログ併記 = §4.2）

### 4.2 出力先・保持・改変耐性の水準

- **アプリ記録の実体は、メタデータ領域と同じくホスト管轄の専用ローカルファイル**とする（architecture.md §4.3 と同じ理由——DB 障害中・provider 切替中にこそ記録が必要であり、DB に置かない。ACL は §5 の保護対象に含める）
- **Windows イベントログへ併記する**（ADR-0004 決定 7。消去が痕跡を残し、既存のログ転送・監視レールに乗る）
- **改変耐性の到達水準を明示する**: アプリ記録は、サービスアカウント権限での改変・削除に対する耐性を**構成次第でしか持たない**——監査記録の領域を分離し、サービスアカウントへ追記系のみ（既存データの変更・削除を許さない）の ACE を与える構成の成立可否を実機検証する（[SEC-3](#7-確定待ち一覧) に含める。保持期間超過分の削除・ローテーションの権限分離も同時に検証する）。**成立しない場合、侵害されたサービスアカウントはアプリ記録を改竄できる——その場合の一次の耐タンパ線はイベントログ併記である**（消去自体が痕跡を残す。ADR-0004 決定 7 の限界の明示と同じ流儀で、この限界も隠さない）
- 保持期間はログ本体・システムイベントと独立に設定でき、**既定はログ本体より長くする**（証跡はログより長く問われる。**既定 365 日**——2026-07-05 オーナー決定。[SEC-2](#7-確定待ち一覧) 確定値。設定キー `Audit:RetentionDays`——configuration.md §8。ログ本体の既定 30 日（database.md DB-1）より長い制約を満たす）
- **削除・ローテーションの実装（Issue #261。2026-07-16）**:
  - **日次ローテーション**: 追記先は事象発生日（UTC）ごとのファイル `audit-yyyyMMdd.jsonl`（`FileAuditRecorder.GetFileNameFor`）。方式にサイズローテーション（rename 世代交代）ではなく日付分割を選んだ理由は、①**既存内容の書き換え・rename・切り詰めが一切不要**であり、上記の追記専用 ACE 構成（SEC-3）と両立する（rename は既存ファイルへの DELETE 権限を要する）②削除の単位（ファイル = 日）が保持期間の単位（日数）と一致する、の 2 点。導入前の単一ファイル `audit.jsonl` は追記されなくなり、最終書き込みから保持期間が経過した時点で削除対象になる
  - **保持期間削除**: `AuditRetentionScheduler` が起動時 1 回 + ログ本体と同じ実行時刻（`Retention:ExecutionTimeOfDay`）に日次実行し、**最終書き込み時刻（UTC）が cutoff（基準時刻 − 保持日数）より古いファイル**を削除する（最終書き込み = ファイル内の最新事象の時刻であり、この条件を満たすファイルの中身はすべて期限切れ——旧単一ファイルにも同じ規則で安全に適用できる）。削除はファイル単位で冪等のため、ログ本体側のようなキャッチアップ機構は持たない（起動時実行が兼ねる）
  - **削除の証跡**: 1 件以上削除した実行を監査事象 2015（§4.3）として記録する（0 件の実行は記録しない——毎日のノイズ行で監査記録を埋めない）
  - **追記専用 ACE 構成（SEC-3）との関係**: 削除権限の分離が適用された環境では、本スケジューラの削除は `UnauthorizedAccessException` で失敗する。これは分離が機能している正しい状態であり得るため、エラーではなく**警告**として「分離構成では削除を ACL 管理側の運用手順で行う」旨を明示する（発火は日次実行時のみ）。この運用手順は operations.md 起筆時の申し送り対象とする
- **監査記録の書き込み不能は管理操作自体を妨げない**（ADR-0004 決定 7）。記録の失敗はそれ自体を観測可能にする: アプリ記録が書けなければイベントログへ、イベントログも書けなければゲージ・状態画面（architecture.md §4.6 の独立チャネル）へ。**この多段の限界も明示する**: ①アプリ記録とイベントログは同一ディスクの満杯で同時に失陥し得る、②第 3 段（ゲージ・状態画面）は在室者前提であり、無人時間帯の実効通知にはならない（ui.md §5.2 の役割分担のとおり）。**記録失敗中に実行された管理操作の記録内容はメモリ内に保持し、チャネル復旧後に書き戻す**（保持の上限と、上限超過時の縮退——件数のみ保持等——は [SEC-10](#7-確定待ち一覧)）。**この保持にも限界がある**: 保持中のプロセス終了（クラッシュ）で当該事象は失われる。記録失敗の開始は第 2・第 3 チャネルで観測済み（開始時刻が残る）であるため、復旧後の書き戻し時に「監査記録が欠落し得る期間」を証跡として残す（architecture.md §4.4 のクラッシュ限界と同じ流儀——期間記録の詳細は SEC-10 に含める）
- **保持・書き戻しの実装（Issue #269。SEC-10）**: `Yagura.Host/Observability/Auditing/ResilientAuditRecorder.cs`。`FileAuditRecorder` をラップするデコレータ（IAuditRecorder。デコレータ鎖は SEC-4 集約 → 本 SEC-10 → 実書き込みの順で、SEC-10 は「アプリ記録ファイルへ確実に残ったか」を内側 `TryRecord` の戻り値で判定するため実書き込みの直上に置く）。**アプリ記録ファイルへの書き込みが失敗した事象**（クエリ可能な正本に載らなかった事象）をメモリ内キューに保持し、①次の新規事象の書き込み成功、または②周期スキャン（仮値 30 秒。新規事象が来なくても復旧を検知）で、発生日（UTC）のファイルへ古い順に書き戻す。書き戻した事象は `Detail` に**遅延記録の印**を付す（「いつ発生し・いつ書かれたか」を取り違えないため。発生日時は原事象のまま保つ）。全件書き戻し後に復旧サマリ（`AuditChannelRecovered`=3013。§4.3）を 1 件記録し、「欠落し得た期間・書き戻し件数・縮退破棄件数」を証跡に残す。**保持上限（仮値 1000 件。`AuditResilienceDefaults`。SEC-10 確定待ち・設定キーは未公開）**を超えたら古い側を残して新しい到来事象を破棄し（障害起点の保全）、破棄件数はライブ計器 `yagura.web.audit.buffer_dropped` にも計上する（復旧サマリが書けないままクラッシュしても件数が観測に残るよう二重化）。**第 3 チャネル（状態画面の可視化）の要否の判断（Issue #269）**: 本実装のライブ計器 2 種（`audit.write_failed`・`audit.buffer_dropped`）と復旧サマリ事象（3013）で観測性を確保し、専用の状態画面ウィジェットは今は追加しない（§4.2 が第 3 段に位置づける「ゲージ・状態画面」の枠に沿う。計器を出しておくことで将来の状態画面がそれを読める）

### 4.3 Windows イベントログのイベント ID 採番方針（早期固定）

イベント ID は外部の監視設定（イベントログ転送・フィルタ）から参照されるため、**採番方針を実装前に固定し、意味の互換を凍結対象とする**:

- イベントソース名は `Yagura` とする
- 採番の区画と**イベントレベルの割当方針**（レベルだけで最低限の監視が組めるようにする——「ソース Yagura の警告以上を通知」が成立すること）:
  - **1000 番台 = 運用警告**（architecture.md §4.6 の能動通知——スプール系・縮退・証明書期限接近等）。レベルは警告（機能停止を伴う事象はエラー）
  - **2000 番台 = 管理操作の監査**（実行された操作・インストーラ由来の転記）。レベルは情報
  - **3000 番台 = 拒否・セキュリティ事象**（拒否試行・認証失敗・origin 拒否・失効猶予の記録）。レベルは警告
- **一度公開した ID の意味とレベルは変えない**（additive-only。ID の追加は MINOR で可、意味の変更・転用は行わない——監視側のフィルタを黙って壊さない）。ID 一覧の初版（**レベル列を含む**）は実装 PR で本節に記録する（[SEC-5](#7-確定待ち一覧)）。この互換規則は database.md §7 の凍結対象一覧に登録済みである
  - **例外記録（v0.1 期の訂正。issue #237）**: 上記の凍結は原則だが、v0.1 の間は ID 契約を対外的に凍結する前提には至っていない。3003（Windows 統合認証の拒否）は当初「握手失敗」と「認証成功だが管理者権限なし」の両方を含めていたが、名（握手失敗）と実（認証成功）の乖離で運用者が Kind だけで切り分けられなかったため、issue #237 で「認証成功後の認可拒否」を **3008（`AdminAuthorizationDenied`）へ分離し、3003 を握手失敗のみへ narrow した**。これは公開済み ID の意味変更に当たる意図的な例外であり、オーナー承認のうえ本記録を残す。v1.0 での ID 凍結以降は同種の意味変更を行わない
- **凍結（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 9 で追加）**: 機構の supersession 等により発火しなくなった ID は、意味・レベルを変えずに「（本機構の採用以降は発火しない）」注記を「意味」列へ追記して**凍結**扱いとする（ID の削除・転用はしない）。additive-only 規約が想定していなかった「ID が発火しなくなるケース」を明示的に扱う初の実例が 3005（下表参照）

#### イベント ID 一覧（SEC-5 初版。M6-2 実装 PR で記録。イベントログ本文の表示名列は 2026-07-06 イベントログ日本語化で追記。1000 番台は Issue #149（M4-6）実装 PR で記録）

| ID | 区画 | レベル | メール通知 | イベントログ本文の表示名 | 意味 |
|---|---|---|---|---|---|
| 1001 | 1000 番台（運用警告） | 警告 | 対象（警告） | スプールなし縮退運転で起動 | スプール領域を開けなかったため、スプールなしで受信のみ続行する縮退運転での起動（architecture.md §1.2）。M3-2 で実装済みだったが、本 ID の遡及割当は Issue #149（1000 番台を初めて配線した PR）で行った |
| 1002 | 1000 番台（運用警告） | 警告 | 対象（警告） | スプール使用量が上限に接近 | スプール使用量が上限接近の閾値（仮値比率 0.8。architecture.md §9 M-16）に達した（architecture.md §4.6）。`ActiveNotificationMonitor` による周期監視（Issue #149） |
| 1003 | 1000 番台（運用警告） | 警告 | 対象（警告） | スプール使用量が上限に到達 | スプール使用量が上限（比率 1.0）に到達し、以降の退避対象を破棄している（architecture.md §3.2.3・§4.6）。`ActiveNotificationMonitor` による周期監視（Issue #149） |
| 1004 | 1000 番台（運用警告） | 警告 | 対象（警告） | スプールへの退避が継続 | スプールへの退避が一定時間（仮値 5 分。M-16）以上継続している——持続的な速度不足の兆候（architecture.md §3.2.2・§5.3・§4.6）。`ActiveNotificationMonitor` による周期監視（Issue #149） |
| 1005 | 1000 番台（運用警告） | 警告 | 対象（警告） | スプールへの書き込みに失敗 | スプールへの書き込みがリトライ後も失敗し、当該レコードを破棄した（architecture.md §3.2.1・§4.6）。発生箇所（`PersistenceWriter.EvacuateSingleRecordAsync`）からの即時通知（Issue #149） |
| 1006 | 1000 番台（運用警告） | 警告 | 対象（警告） | 監視対象ボリュームの空き容量が減少 | 監視対象ボリューム（データルート + スプール有効時はスプール置き場所。同一ボリュームは重複排除）の空き容量が閾値（仮値 1 GiB。M-16）を下回った（architecture.md §4.6。database.md §3・§5.3）。本文にどのボリューム（ルート。例: `C:\`）かを含む。抑制窓はボリューム単位で独立。`ActiveNotificationMonitor` による周期監視（Issue #149。DriveInfo による監視。スプール置き場所の追加は PR #188 レビュー対応——PR 公開前のため additive-only 規約に抵触しない） |
| 1007 | 1000 番台（運用警告） | 警告 | 対象（警告） | SQL Server Express の DB 容量が上限に接近 | SQL Server Express の DB サイズが上限（10 GB）に接近した（database.md §5.3。閾値は仮値比率 0.8。M-16）。`ActiveNotificationMonitor` による周期監視（Issue #149） |
| 1008 | 1000 番台（運用警告） | エラー | 対象（エラー） | 能動通知の周期評価に失敗 | 能動通知の周期評価中に未捕捉例外が発生した。監視ループ自体は継続し次周期で再試行する（監視自身が無警告で恒久停止する経路を残さない——PR #188 レビュー対応）。レベルがエラーなのは「その周期の監視が実行できなかった = 部分的な機能停止を伴う事象」のため（本節のレベル割当方針） |
| 1009 | 1000 番台（運用警告） | エラー | 対象（エラー） | スプールの定期自己検証に失敗 | スプールの定期自己検証（architecture.md §3.2.5。Issue #152）が失敗した——合成レコードの投入自体に失敗した（書込失敗・上限到達）、または投入した合成レコードが期待時間（仮値 10 分。architecture.md §9 M-16）以内に drain へ合流判定されず、かつ同じ期間内に drain の進捗（消化済みセグメント削除の累積カウンタ `DiskSpool.DeletedSegmentsTotal` の増分）も観測されなかった。**タイムアウトかつ drain 進捗なしの場合に限定**——未消化バックログの滞留（持続的な速度不足。architecture.md §3.2.2 が正常状態と明記する運用状態）であれば drain の進捗が観測されるはずであり、その場合は本 ID ではなく 1010 として通知する（バックログ起因の判別。Issue #202。PR #200 レビュー対応の解消）。障害時専用経路（スプール退避 → drain）の平常時検証が完了していない状態のためレベルはエラー（本節のレベル割当方針「機能停止を伴う事象」）。`ActiveNotificationMonitor` による周期監視（Issue #152・#202） |
| 1010 | 1000 番台（運用警告） | 警告 | 対象（警告） | スプールの定期自己検証がタイムアウト（未消化バックログの滞留） | スプールの定期自己検証（architecture.md §3.2.5）がタイムアウトしたが、同じ期待時間（仮値 10 分）内に drain の進捗（消化済みセグメント削除の累積カウンタの増分——追記と混ざらない単調増加のカウンタであり、追記速度が消化速度を上回る高負荷下でも進捗を取りこぼさない）を観測しており、経路は生きていて未消化バックログの滞留（保存先の持続的な速度不足。architecture.md §3.2.2 が正常な運用状態と明記——1004 が並行発火している可能性が高い）に起因すると判定した（Issue #202。PR #200 レビュー対応の解消）。レベルは 1004（スプールへの退避が継続）と同じ「機能停止を伴わない、対応が必要な運用状態の継続」区分のため警告。本文には現在のスプール使用率を含む。進捗が途絶えれば同一のタイムアウト状態でも 1009 へ切り替わり、**一度 1009 へ切り替わった後は当該マーカーの追跡が終わる（次のマーカーが投入される）まで本 ID へ戻らない**（ラッチ。1009/1010 が同一の根本原因に対して交互に再発火する振動を防ぐ。判定を先送りし続けて実障害の検知が沈黙しないための設計と併せて architecture.md §3.2.5 参照）。additive-only で 1009 の次に採番。`ActiveNotificationMonitor` による周期監視（Issue #202） |
| 2001 | 2000 番台（管理操作の監査） | 情報 | — | 設定変更を適用 | 設定変更の適用（初期セットアップウィザードによる設定ファイル生成を含む。M8-4）。記録内容は操作者の接続元・UTC 時刻・変更キーと反映方式の要約（秘密情報キーは値を載せない） |
| 2002 | 2000 番台（管理操作の監査） | 情報 | — | 本番昇格の接続検証を実施 | 本番昇格の準備フェーズにおける SQL Server 接続検証の実施（database.md §6.1。M8-4）。管理者資格情報を「使用した」事実と成否のみを記録し、資格情報そのものは記録しない（configuration.md §5） |
| 2003 | 2000 番台（管理操作の監査） | 情報 | — | 本番昇格を実行 | 本番昇格の切替実行（database.md §6.1。M8-4）。provider の切替と旧 DB 処分の選択を記録する。接続文字列は記録しない |
| 2004 | 2000 番台（管理操作の監査） | 情報 | — | circuit を切断 | circuit の個別切断（§2.2。M8-4）。対象 circuit の識別子と操作者の接続元を記録する |
| 2005 | 2000 番台（管理操作の監査） | 情報 | — | フォワーダ配布キットを生成 | フォワーダ配布キットの生成（[ADR-0008](../adr/0008-forwarder-kit-generation.md) 設計条件 6・9）。記録内容は生成日時・宛先（ホスト・ポート）・収集チャネル・MSI 同梱の有無（`msiBundled`）。MSI 同梱時は `msiVersion`・`msiSha256`・`officialHashMatch`（公式配布 SHA256 との照合結果）・`versionMismatchAcknowledged`（版不一致の二段階確認の事実）を追加で記録する。秘密情報は含まない。**ID・レベルの意味は変えていない**——記録項目の拡張は additive-only 規約（値の追加であり ID の意味転用ではない） |
| 3001 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 閲覧リスナへの管理操作を拒否 | 閲覧リスナに到達した管理系要求の拒否（§1 L-3b）。管理系エンドポイントのルート表に一致した要求が、管理リスナ以外のポートに到達した場合に記録する。記録内容は§4.1 のとおり（接続元・UTC 時刻・試行パス・到達したリスナのポート）。**「管理系パス」の判定は管理リスナのルート表からの機械的導出方式を採用しており、ルート表に一致しない未登録パスへの要求（例: 閲覧リスナへの `/admin/xxx` の未登録パス）はこの ID の対象にならない（通常の 404）——覆域の限界は §1 L-3b のとおり**。M8-4 以降は、確立済み circuit 上の対話的ナビゲーションによる管理画面への到達試行（circuit 層ガードの拒否——§1 L-5 の覆域の限界を埋める二層目）も同一の事象種別としてこの ID に記録する |
| 3002 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | circuit 確立要求の origin 検証で拒否 | 同一サイト以外からの circuit 確立試行の拒否（origin 検証。§2.1。M8-4）。記録内容は接続元・UTC 時刻・試行パス・到達したリスナのポート・提示された Origin 値（初動解析の手がかりであり秘密情報ではない） |
| 2006 | 2000 番台（管理操作の監査） | 情報 | — | 管理 UI 認証設定を変更 | 管理 UI 認証設定の変更（[ADR-0010](../adr/0010-admin-ui-authentication.md) 決定 1・3。Windows 統合認証/アプリ独自認証の有効化・Kerberos-only・loopback 認証 opt-in の切替。§2.4）。記録内容は変更後の各フラグ値 |
| 2007 | 2000 番台（管理操作の監査） | 情報 | — | 管理者アカウントを作成/変更 | アプリ独自認証の管理者アカウントの作成・パスワード変更（ADR-0010 決定 3）。記録内容はユーザー名のみ（パスワードは記録しない） |
| 2008 | 2000 番台（管理操作の監査） | 情報 | — | 管理 UI へサインイン | 管理 UI へのサインイン成功（Windows 統合認証・アプリ独自認証の両方。ADR-0010 決定 6「誰が」欄の実効化の起点。§2.4）。記録内容は認証方式（`windows`/`app`）・認証済み利用者名・接続元 |
| 3003 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | Windows 統合認証のハンドシェイクに失敗 | Windows 統合認証（Negotiate）の**プロトコルレベルの握手失敗・拒否**（ADR-0010 決定 6。§2.4）。`NegotiateEvents.OnAuthenticationFailed`（トークン不正・SPN 不一致等——認証が成立していない）・Kerberos-only モードでの NTLM 拒否をこの ID に記録する（記録内容の `Detail` で理由を区別する——利用者応答では区別しない）。**「認証は成立したが管理者権限がない」拒否は 3008（`AdminAuthorizationDenied`）に分離した（issue #237。握手失敗と認証成功後の認可拒否を Kind だけで切り分け可能にするため）** |
| 3004 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | アプリ独自認証のログインに失敗 | アプリ独自認証のログイン失敗（ADR-0010 決定 6）。試行されたユーザー名を保持する（§4.4 の集約規約のとおり利用者名の次元を保持） |
| 3005 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 管理者アカウントをロックアウト | アプリ独自認証アカウントのロックアウト発生（ADR-0010 決定 6。連続失敗試行が閾値に達した）。**凍結（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 9。三層防御の採用以降は発火しない）**——後継事象は 3006・3007。意味・レベルは変更しない |
| 3006 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | アプリ独自認証のバックオフが上限に到達 | アプリ独自認証のアカウント単位バックオフが cap（上限遅延）に到達した（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 3・9。§2.4.1）。記録内容は送信元 IP・アカウントキー（アカウント正規化ユーザー名 × loopback/remote の別）・現在の連続失敗回数 n・算出された待機時間。通常の失敗ログイン記録（3004）に加えて記録する追加のセキュリティ事象 |
| 3007 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | アプリ独自認証のログイン試行をレート制限で拒否 | IP レート制限またはグローバルトークンバケットによる拒否（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 2・4・5.1・9。§2.4.1）。記録内容は送信元 IP・拒否理由の別（IP レート制限/グローバルトークンバケット涸渇のいずれか。`Detail` で区別——利用者応答では区別しない）。グローバルトークンバケットの場合はプロセス全体の事象である旨も含める |
| 3008 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 認証成功後に管理者権限がなくアクセスを拒否 | 認証は成立したが管理者権限がないための認可拒否（Windows 統合認証で認証成立・`BUILTIN\Administrators` 非所属等。ADR-0010 決定 5・6。issue #237）。握手失敗（3003）とは別事象として分離し、運用者が Kind だけで両者を切り分けられるようにする。記録内容は認証方式（`windows`）・認証済み利用者名・接続元。**現時点の記録点は `/admin/login/windows` エンドポイント経由の拒否のみ**——認可ポリシー（`AdminPolicyName` の `RequireAssertion`）経由で権限不足拒否になる他経路の記録は今後の課題として issue #237 に残す |
| 1011 | 1000 番台（運用警告） | エラー | — | 管理 UI 認証の fail-closed 設定を拒否して起動を中止 | loopback 認証 opt-in（`Admin:Authentication:RequireForLoopback`）が有効なのに認証方式（Windows 統合認証・アプリ独自認証）が一つも構成されていない設定の起動時拒否（ADR-0010 決定 1・委任事項 5。§2.4）。「なぜ起動しないか・何を直せばよいか」を例外メッセージに含める。採番の経緯: 1009 = Issue #152、1010 = PR #211 が使用のため 1011 |
| 1012 | 1000 番台（運用警告） | エラー | — | 管理リスナのリモートバインドの fail-closed 設定を拒否して起動を中止 | 管理リスナのリモートバインド（`Admin:RemoteBinding:Enabled`）が有効なのに、認証（Windows 統合認証・アプリ独自認証のいずれも無効）または HTTPS（`Admin:Https:Enabled = false`、または拇印が未設定・不正形式）のいずれかが未構成の設定の起動時拒否（ADR-0010 Phase 2 決定 1・4。§2.5）。「なぜ起動しないか・何を直せばよいか」を例外メッセージに含める |
| 1013 | 1000 番台（運用警告） | 警告 | — | 管理リスナのリモート HTTPS 証明書が解決できず縮小継続 | 拇印は静的検証を通過したが、実際の証明書ストア参照が失敗した（証明書が見つからない・秘密鍵にアクセスできない・既に期限切れ）ため、リモート HTTPS の bind エントリのみを開かずに縮小継続した（ADR-0010 Phase 2 決定 4。§2.5）。loopback 経由の管理リスナは影響を受けないため、他の 1000 番台の「機能停止を伴う事象」（エラー）より軽い「警告」とする |
| 2009 | 2000 番台（管理操作の監査） | 情報 | — | 管理 UI HTTPS 証明書の秘密鍵アクセス権を付与 | 管理リスナのリモート HTTPS 証明書の秘密鍵読み取り権限をサービスアカウントへ付与した（ADR-0010 Phase 2 決定 4。§2.5）。記録内容は証明書拇印・付与先アカウント名。秘密鍵そのものは記録しない |
| 1014 | 1000 番台（運用警告） | 警告 | 対象（警告） | 管理 UI リモート HTTPS 証明書の期限が接近 | 管理リスナのリモート HTTPS 証明書の有効期限が閾値（仮値 30 日。`ActiveNotificationConstants.AdminHttpsCertificateExpiryWarningWindow`）以内に接近した（ADR-0010 Phase 2 決定 4 の「期限接近の事前警告」の実体。§2.5）。`ActiveNotificationMonitor` の周期監視（1 分間隔・抑制窓 15 分） |
| 1015 | 1000 番台（運用警告） | 警告 | 対象（警告） | 管理 UI リモート HTTPS 証明書が稼働中に使用不能 | 管理リスナのリモート HTTPS 証明書が稼働中に使用できなくなった——証明書ストアからの削除・秘密鍵アクセス不能・有効期限切れへの遷移（期限切れ中は新規 TLS ハンドシェイクが拒否されている状態。§2.5）。起動時の解決失敗（1013）とは別の、稼働中の周期監視による検知。レベルは 1013 と同じ判断（リモート HTTPS 面のみの縮退で loopback・受信は無影響）で警告 |
| 1016 | 1000 番台（運用警告） | 警告 | — | TLS 受信証明書が解決できず縮小継続 | TLS 受信（`Ingestion:Tls:Enabled`。RFC 5425。opt-in）が有効なのに、実際の証明書ストア参照が失敗した（拇印が未設定・不正形式・証明書が見つからない・秘密鍵にアクセスできない）ため、TLS 受信の bind エントリのみを開かずに縮小継続した（§6。Issue #137）。平文 UDP/TCP 受信は一切影響を受けない（ADR-0004 決定 3）。1013 と同型の扱い |
| 2010 | 2000 番台（管理操作の監査） | 情報 | — | TLS 受信証明書の秘密鍵アクセス権を付与 | TLS 受信証明書の秘密鍵読み取り権限をサービスアカウントへ付与した（§6。Issue #137）。記録内容は証明書拇印・付与先アカウント名。秘密鍵そのものは記録しない。2009 と同型（起動時の自動操作） |
| 1017 | 1000 番台（運用警告） | 警告 | 対象（警告） | TLS 受信証明書の期限が接近 | TLS 受信証明書の有効期限が閾値（仮値 30 日。管理 UI HTTPS 証明書（1014）と同じ `AdminHttpsCertificateExpiryWarningWindow` を流用）以内に接近した（§6）。`ActiveNotificationMonitor` の周期監視（1 分間隔・抑制窓 15 分） |
| 1018 | 1000 番台（運用警告） | 警告 | 対象（警告） | TLS 受信証明書が稼働中に使用不能 | TLS 受信証明書が稼働中に使用できなくなった——証明書ストアからの削除・秘密鍵アクセス不能・有効期限切れへの遷移（§6）。**管理 UI HTTPS（1015）との非対称に注意**: TLS 受信は「止めない」設計のため、この状態でも新規 TLS ハンドシェイクを拒否しない——本通知は状態の可視化のみを目的とし、リスナの挙動を変えない |
| 1019 | 1000 番台（運用警告） | 警告 | 対象（警告） | アプリ独自認証の三層防御が昇格閾値以上継続 | アプリ独自認証の三層防御（バックオフ・IP レート制限・グローバルトークンバケット）のいずれかが昇格閾値（仮値 15 分）以上継続して発動している（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 6・§2.4.1。`AdminAuthFailureDefenseEscalated`——実装済み）。**訂正（2026-07-18）**: 本表の 1019 行は一時期「設定読み取り失敗による起動失敗（#312 予約・未実装）」と記載されていたが、1019 は本事象が実装済みで使用中であり予約側が誤り——読み取り失敗イベントは 1024 へ再採番した（additive-only 規約。PR #333） |
| 1024 | 1000 番台（運用警告） | エラー | — | 設定ファイルを読み取れず起動失敗 | `yagura.json` をファイル全体として解釈できず（構文エラー・文字化け・重複キー）、起動に失敗した（configuration.md §1。Issue #312。当初予約の 1019 が実装済み ID と衝突していたため 1024 へ再採番）。**キー単位の縮退に分解できないため「何が既定へ落ちたか」を提示できず、可視化された縮退の系に載せられないことが起動失敗を選ぶ根拠である**。記録内容は対象ファイルのフルパスと解析に失敗した位置（1 始まりへ変換した行番号 + 補助としてバイト位置）。**該当行の内容・周辺の抜粋は記録しない**（設定ファイルは資格情報を含みうる。§2）。閲覧 UI も起動しないため本イベントが唯一の通知経路になる——サービス死活の外形監視を運用側で用意することを利用者向けに案内する（configuration.md §10 CF-D8） |
| 2011 | 2000 番台（管理操作の監査） | 情報 | — | 管理リスナのリモートバインド設定を変更 | 管理リスナのリモートバインド（`Admin:RemoteBinding:Enabled`）の有効化・無効化（[ADR-0012](../adr/0012-admin-https-cert-ui.md) 決定 7。§2.5）。「機の公開」という最重要のセキュリティ状態遷移を認証設定変更（2006）に畳み込まず独立 ID で記録する。記録内容は変更後の値・操作者の接続元・認証方式・認証済み利用者名（未認証の loopback 操作では接続元のみ） |
| 2012 | 2000 番台（管理操作の監査） | 情報 | — | 管理 UI リモート HTTPS の証明書設定を変更 | 管理リスナのリモート HTTPS 設定（`Admin:Https:Enabled`・`Admin:Https:CertificateThumbprint`・`Admin:Https:Port`）の変更（ADR-0012 決定 7。§2.5）。記録内容は変更キーと新値（拇印は証明書の公開識別子であり秘密ではないため値を残す。秘密鍵そのものは扱わない）・操作者（2011 と同じ）。リモートバインドの切替（2011）と同一操作で両方が変わった場合は 2 件記録する |
| 2013 | 2000 番台（管理操作の監査） | 情報 | — | 認証セッションを緊急全失効 | 認証セッションの緊急全失効（[ADR-0013](../adr/0013-admin-winauth-session.md) 決定 2。§2.4）。セッション世代番号をバンプして発行済みの全認証セッション Cookie を即時無効化した。記録内容は無効化後の世代番号（＝無効化した母集団の識別）・操作者（2011 と同じ。認証方式・認証済み利用者名） |
| 2014 | 2000 番台（管理操作の監査） | 情報 | — | 閲覧 UI へサインイン | 閲覧リスナ（8514）へのサインイン成功（[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 4 決定 7・§3）。管理リスナのサインイン成功（2008）と区別する。記録内容は認証方式（`windows`/`app`）・役割（`role=viewer`/`role=admin`）・認証済み利用者名・接続元。`SignInAsync`（認証セッション発行）完了時点で発火する（ADR-0013 決定 3 の「成功」の意味づけを閲覧経路にも適用） |
| 2015 | 2000 番台（管理操作の監査） | 情報 | — | 保持期間を超過した監査記録ファイルを削除 | 監査記録の保持期間削除の実行（§4.2 SEC-2。既定 365 日。Issue #261）。1 件以上削除した実行のみ記録し、記録内容は削除ファイル数・保持日数・cutoff（UTC）・削除したファイル名（上限 20 件で打ち切り注記）。**証跡の削除自体を証跡に残す**——イベントログ併記により、監査ファイル側が後日消されても削除の事実がイベントログに残る（ADR-0004 決定 7「消去が痕跡を残す」と同じ向き）。システムの定時実行のため実行者欄は持たない |
| 2016 | 2000 番台（管理操作の監査） | 情報 | — | 設定を再読み込み | 設定ファイルのライブ再読み込みの実行（configuration.md §3。CF-4 層1。Issue #262）。UI（/admin/reload）経由・SCM カスタム制御コード（CF-5。`sc control Yagura 128`）経由が合流する。記録内容は変更キー・適用キー・再起動待ちキーの要約（前後値は含めない——2001 と同じ粒度で秘密情報の混入を構造的に避ける）。変更なしの実行は記録しない。未反映の残存は 1020（警告）、検証失敗による拒否は 1021（警告）が別途知らせる |
| 1020 | 1000 番台（運用警告） | 警告 | — | 設定の再読み込みで未反映の項目が残存 | 再読み込みされた変更のうち反映にサービス再起動（または層2 のリスナ再構成）を要するキーが未反映のまま残っている（configuration.md §3「未反映のまま残る項目の明示」。再起動まで再読み込みのたびに累積して報告される）。Issue #262 |
| 1021 | 1000 番台（運用警告） | 警告 | — | 設定の再読み込みを拒否（旧設定で継続） | 再読み込み対象の設定に「起動失敗」分類の不正値（受信ポート不正等。configuration.md §1）があり、適用を拒否した。**実行中の構成は変更前のまま継続する**——起動時の fail-fast と異なる意図的な非対称（稼働中は「受信を止めない」を優先。Issue #262） |
| 1022 | 1000 番台（運用警告） | 警告 | 対象（警告） | 受信リスナの一部が開けず縮小継続 | 起動時に受信リスナの一部（または全部）が環境要因（ポート競合・NIC のアドレス未確立等）で bind できず、開けたリスナのみで縮小継続している（configuration.md §4.1。Issue #291——#141 原子的起動の環境要因についての反転。2026-07-16 オーナー裁定）。開けなかったリスナは CF-6 の定期再試行（仮値 30 秒）が受信再開を試み続け、再開時は受信断区間 `downtime.listener-bind-retry` が記録される |
| 1023 | 1000 番台（運用警告） | 警告 | — | ファイアウォール規則の不一致 | リスナの実ポートと Yagura 名前空間のファイアウォール規則の不一致（欠落・ポート変更後の取り残し。CF-2。configuration.md §4.3。Issue #265）。起動時とリスナ再構成の適用時に検出する。ファイアウォールでの drop はアプリのカウンタにも OS 統計にも現れない観測の死角のため、警告で「沈黙して受信できないだけ」を防ぐ。インストール時に規則作成をオプトアウトした環境（Yagura 名前空間の規則 0 件 + 記録あり）では発火しない |
| 2017 | 2000 番台（管理操作の監査） | 情報 | — | インストール記録を転記 | インストール記録（`firewall-rules.ini` = 作成された規則一覧・オプトアウト選択）の初回起動時のイベントログ転記（§4.1「インストーラ由来の記録の転記」・configuration.md §4.3。Issue #265）。「なぜこのサーバには規則がないのか」に証跡で答える。転記は 1 回のみ（マーカーファイルで判定） |
| 2018 | 2000 番台（管理操作の監査） | 情報 | — | 蓄積ログを移行 | 蓄積ログ移行（SQLite → SQL Server。database.md §6.2。DB-5。Issue #266）の実行。記録内容は検証の合否・移行元件数・累計移行件数・移行先範囲内件数。移行由来レコードの機械可読な識別はシステムイベント `migration.import`（移行範囲 + 件数）が担う |
| 2019 | 2000 番台（管理操作の監査） | 情報 | — | 前回稼働時から設定ファイルが変更された状態で起動 | 起動時の設定差分照合（§4.1・configuration.md §3。Issue #329）。前回適用スナップショット（`last-applied-configuration.json`）と起動時点の設定ファイルに差分があるときに 1 件記録する。記録内容は変更キー名のみ（2016 と同粒度——前後値・秘密値は含めない）。比較対象は `ConfigurationChangePlanner` 登録キーのみ（未知キーは既存の未知キー警告が担う）。初回起動・スナップショット欠損/破損時は照合せず記録しない（情報ログのみ）。「いつ・誰が」変更したかは特定できない——手編集 + 再起動経路（§4.1 の対象外受容）への軽量補完であり、統制は ACL・SACL に委ねたまま |
| 2020 | 2000 番台（管理操作の監査） | 情報 | — | TLS 受信の証明書設定を変更 | TLS 受信の設定（`Ingestion:Tls:Enabled`・`Ingestion:Tls:Port`・`Ingestion:Tls:CertificateThumbprint`）の変更（[ADR-0019](../adr/0019-ingestion-tls-cert-ui.md) 決定 5。§6。Issue #349）。管理リモート HTTPS 版の 2012 と対になる TLS 受信版で、記録内容も同型——変更キーと新値（拇印は証明書の公開識別子であり秘密ではないため値を残す）・操作者（2011/2012 と同じ命名空間付き利用者名・認証方式）。`Port` を対象に含めるのは到達面の変更であり監査価値が高いため（2012 も 3 キーすべてを対象としている）。`Ingestion:Tls:BindAddress` は UI の対象外（ADR-0019 決定 1）のため本 ID の記録対象にも含まれない——手編集による変更は 2019（起動時の設定差分照合）が拾う |
| 3009 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | Windows 認証成功後に閲覧/管理グループ非所属でアクセスを拒否 | 閲覧リスナで Windows 統合認証は成立したが、設定された閲覧（`Viewer:...:ViewerGroups`）・管理（`Viewer:...:AdminGroups`）いずれの AD グループにも非所属のための認可拒否（[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 4 決定 7・SEC-9・§3）。管理側の 3008（`AdminAuthorizationDenied`。544/管理グループ非該当）と対をなす閲覧側事象。記録内容は認証方式（`windows`）・認証済み利用者名・接続元 |
| 3010 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 失効後の閲覧継続を猶予として許容 | 認証失効後の閲覧 circuit の継続を猶予として許容した（SEC-6。§2.3。Issue #267）。記録内容は継続を許容した circuit の利用者名・接続元・確立時刻・猶予満了予定——「失効から遮断までに誰が何を見得たか」に監査で答える起点。管理セッションは猶予対象外（§2.3 の三重防御）のため本事象は発火しない |
| 3011 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 猶予中の circuit が終了 | 猶予中だった閲覧 circuit の終了（SEC-6。§2.3。Issue #267）。`Detail` に終了の別（猶予満了 / 切断 / 全切断）を記録し、3010 と対で「見得た期間」を閉じる |
| 3012 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 拒否試行を集約記録 | 同一送信元・同一種別の拒否が短時間に反復した場合の集約サマリ（§4.4 SEC-4。Issue #268）。集約対象の個別事象（3002/3003/3004/3006/3008/3009）が閾値・窓を満たしたときに個別記録から切り替わり、静穏窓の経過で 1 件記録される。記録内容は集約対象種別・回数（全件——集約中も件数を失わない）・期間・試行された利用者名の集合（§4.4「どのアカウントが狙われたか」の次元）・最初と最後の試行のフル詳細 |
| 1025 | 1000 番台（運用警告） | 警告 | **対象外（構造的）** | メール通知の送信に失敗 | メール通知（[ADR-0017](../adr/0017-email-notification.md) 決定 5）の送信が再試行（1 通あたり 1 回・仮値 5 分後）後も失敗し、当該通知を破棄した。一部宛先のみが受理されなかった場合（委任 7）も本 ID で警告する（メッセージとしては送信成功のため再送はしない——受理済み宛先への二重送信を作らない）。抑制窓 15 分付き。**本 ID をメール通知の対象にしないのは、送信失敗をメールで通知しようとするループを定義レベルで排除するため**（実装規律ではなく allowlist の定義で担保し、テストが固定する） |
| 1026 | 1000 番台（運用警告） | 警告 | **対象外（構造的）** | メール通知が流量制御により抑制された | 同一イベント ID の再送間隔（仮値 60 分）または全体流量上限（仮値 10 通/時。エラーレベルは対象外）により、メール通知を送信しなかった（ADR-0017 決定 6）。抑制が発生している状態が続く間、イベントログへは 1 回だけ出す——継続的な可視化は管理画面の常設カード（決定 5）が担う。1025 と同じ理由で本 ID もメール通知の対象にしない |
| 1027 | 1000 番台（運用警告） | 警告 | 対象（警告） | 登録済み送信元からの受信が途絶 | ウォッチリスト（[ADR-0018](../adr/0018-source-silence-detection.md) 決定 1）に登録した送信元からの受信が、当該エントリの閾値を超えて途絶した（決定 3。Issue #351）。判定は `ActiveNotificationMonitor` の周期評価（1 分間隔）。**発火は状態遷移で 1 回**（途絶中フラグのラッチ）+ **エントリ別の抑制窓**（仮値 15 分。[CF-9](configuration.md#9-確定待ち一覧)）で律速する——粒度がエントリ別であることが既存のトリガ別抑制窓との違いで、装置 A の発火が装置 B の初報を飲まない。**窓に飲まれた途絶が窓明け時点でも継続していれば遅延発火する**（恒久に失わない）。記録内容は送信元アドレス・`Label`・最終受信時刻（壁時計表示）・経過時間・サーバ側受信経路の状態（ログ本文は含めない）。**全受信リスナが受信不能な間は判定を保留する**（真因がサーバ側なのに運用者を装置側の調査へ誘導しない） |
| 1028 | 1000 番台（運用警告） | 警告 | 対象（警告） | 複数の送信元が一斉に途絶 | 同一評価周期に閾値件数（仮値 5 件。CF-9）以上が途絶へ遷移した場合、個別警告ではなく 1 件の集約警告にする（ADR-0018 決定 3）。集約スイッチ障害等で 50 台が同時に黙ったとき、個別 50 件（メール通知有効時は 50 通）は診断情報としても劣化しているため。Detail には件数・エントリ一覧に加え、「サーバ側受信経路の状態を先に確認」する誘導と、**起動後の再アーム起点の一斉発火かの別**を含める（同一閾値で再アームされたエントリ群は起動から閾値経過後に同時発火し得る——独立障害の寄せ集めを「共通上流障害」と誤誘導しない）。**集約時も各エントリの途絶フラグ・抑制窓は個別警告時と同様に更新する** |
| 1029 | 1000 番台（運用警告） | **情報** | — | 途絶していた送信元からの受信が再開 | 途絶警告（1027）を出したエントリからの受信が再開した（ADR-0018 決定 3）。**能動通知はしない**（復帰は対応を要する事象ではない——Issue #132 の前例）が、途絶警告（始端）と対で**「ログが欠けていた期間」の終端を証跡に残す**（外部送信元のログ欠落期間は監査上の関心事であり、サーバ内部の自己回復とは監査対象性が異なる）。集約せず個別に記録する——集約警告（始端 1 件）と復帰記録（終端 N 件）の非対称は既知として受容する（終端は各装置が別々のタイミングで復帰するのが常であり、集約の意味が薄い）。**1000 番台に情報レベルを置く初例**: 管理操作ではないため 2000 番台に属さず、事象の性質は運用側であるため本区画に置く |
| 2021 | 2000 番台（管理操作の監査） | 情報 | — | メール通知の設定を変更 | メール通知設定（`Notification:Email:*` の 8 キー）の変更（ADR-0017 決定 4。Issue #350）。記録内容は**変更キーと新値** + 操作者。`Smtp:Password` は「変更した」事実のみで値は残さない。宛先（`To`）・接続先（`Smtp:Host` / `Port`）の値を残すのは、これらが「通知がどこへ向かうか」= **流出経路そのものの定義**であり、キー名粒度（2016）では事後に追えないため——値を残す前例は 2012（証明書拇印・ポートの新値）にある。手編集 + 再読み込み経路は従来どおり 2016 に合流し、手編集 + 再起動は 2019 が変更キー名を残す |
| 2022 | 2000 番台（管理操作の監査） | 情報 | — | メール通知のテスト送信を実行 | 管理画面からのテスト送信（ADR-0017 決定 8。Issue #350）。記録内容は接続先（ホスト・ポート・暗号化指定）・差出人・宛先・認証の有無・**「保存済み資格情報を使用したか」の別**・成否・操作者。資格情報の値は記録しない。「状態を変えない操作」だが監査対象とするのは、①資格情報の使用 + 外向き実トラフィックを伴う点で 2002（本番昇格の接続検証）と同型であり、②**未保存の値で任意のホスト・ポートへ接続を試せる操作は内部ネットワークの到達性探査に転用し得る**ため。**失敗も記録する**（成功のみの記録では探査が証跡に現れない） |
| 2023 | 2000 番台（管理操作の監査） | 情報 | — | 途絶検知のウォッチリストを変更 | 送信元の途絶検知のウォッチリスト変更（[ADR-0018](../adr/0018-source-silence-detection.md) 決定 5。Issue #351）。記録内容は**追加・削除・変更されたエントリのアドレスと表示名** + 総数 + 操作者——ウォッチリストは**検知範囲そのものの定義**であり、「管理権限を得た攻撃者が証跡遮断の前にエントリを外す」を事後に再構成できる粒度で残す（キー名粒度の 2016 では答えられない。値は秘密情報ではない。値を残す前例は 2012・2021）。手編集 + 再読み込み経路は従来どおり 2016 に合流する（手編集は監査対象外——§4.1） |
| 3013 | 3000 番台（拒否・セキュリティ事象） | 警告 | — | 監査チャネル復旧・障害中事象を書き戻し | 監査チャネル（アプリ記録ファイル）の書き込みが失敗していた期間の事象をメモリ内に保持し、復旧後にファイルへ書き戻し切った時に 1 件記録する（§4.2 SEC-10。Issue #269）。記録内容は「監査記録が欠落し得た期間（障害開始〜復旧の UTC 窓）」・書き戻し件数・保持上限超過で縮退破棄した件数——「証跡が欠けた可能性のある期間」自体を証跡に残す。レベルは警告（監査の欠落可能性を既定の監視で拾える） |

アプリ独自認証のバックオフ cap 到達・IP レート制限/グローバルトークンバケット拒否は [ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 9 で 3006・3007 として実装済み。アプリ独自認証の三層防御の能動通知への昇格は 1019（`AdminAuthFailureDefenseEscalated`）として実装済み（決定 6）。失効後の閲覧継続の猶予・猶予終了は SEC-6 で 3010・3011 として（Issue #267）、拒否試行の集約記録は §4.4 SEC-4 で 3012 として（Issue #268）、監査チャネル復旧・書き戻し完了は §4.2 SEC-10 で 3013 として（Issue #269）実装済み。以後、他の 3000 番台事象を実装する際は 3014 以降を（3008 = ADR-0010 の認証成功後の認可拒否、3009 = ADR-0010 Phase 4 の閲覧認証成功後の認可拒否、3010・3011 = SEC-6 の失効猶予、3012 = SEC-4 の拒否試行集約、3013 = SEC-10 の監査チャネル復旧として実装済み）、2000 番台の管理操作は 2021 以降を（管理リスナのリモートバインド設定変更・リモート HTTPS 証明書設定変更は [ADR-0012](../adr/0012-admin-https-cert-ui.md) 決定 7 で 2011・2012 として、認証セッションの緊急全失効は [ADR-0013](../adr/0013-admin-winauth-session.md) 決定 2 で 2013 として、閲覧 UI へのサインイン成功は ADR-0010 Phase 4 決定 7 で 2014 として、監査記録の保持期間削除は SEC-2 で 2015 として、設定再読み込みは CF-4 で 2016 として、インストール記録の転記は CF-2 で 2017 として、蓄積ログ移行は DB-5 で 2018 として、起動時の設定差分照合は Issue #329 で 2019 として、TLS 受信の証明書設定変更は [ADR-0019](../adr/0019-ingestion-tls-cert-ui.md) 決定 5 で 2020 として実装済み）を実装する際は 2021 以降を、1000 番台の運用警告（circuit 上限の到達等。自己検証の失敗は Issue #152 で 1009 として、そのバックログ起因の区別は PR #211 で 1010 として、管理 UI 認証の fail-closed 拒否は ADR-0010 Phase 1 で 1011 として、管理リスナのリモートバインドの fail-closed 拒否・証明書解決失敗・証明書期限接近・稼働中の証明書使用不能は ADR-0010 Phase 2 で 1012〜1015 として、TLS 受信証明書の起動時解決失敗・秘密鍵アクセス権付与・期限接近・稼働中使用不能は Issue #137 で 1016〜1018・2010 として、アプリ独自認証の三層防御の能動通知への昇格は ADR-0011 で 1019 として、再読み込みの未反映残存・拒否・縮小継続・FW 規則不一致は Issue #262/#291/#265 で 1020〜1023 として、設定読み取り失敗による起動失敗は Issue #312 で 1024 として、メール通知の送信失敗・流量制御による抑制は [ADR-0017](../adr/0017-email-notification.md) で 1025・1026 として、送信元の途絶検知・一斉集約・復帰は [ADR-0018](../adr/0018-source-silence-detection.md) で 1027〜1029 として実装済み）を実装する際は 1030 以降を additive-only で追加し、本表を更新する。2000 番台はメール通知の設定変更・テスト送信が ADR-0017 で 2021・2022 として、途絶検知のウォッチリスト変更が ADR-0018 で 2023 として実装済みのため 2024 以降を使う。

**「メール通知」列について（2026-07-19。[ADR-0017](../adr/0017-email-notification.md) 決定 6）**: メール通知（opt-in）の対象となるイベント ID を本表で定義する。**既定は「対象外」（`—`）であり、新しい ID を追加する PR は本列を必ず埋める**——「メールに乗せるか」を明示的に判断させるための更新義務である。`対象（警告）` / `対象（エラー）` の別は流量制御の挙動を決める（エラーは全体流量上限〔仮値 10 通/時〕の対象外。ただし ID 粒度の再送間隔〔仮値 60 分〕は等しく適用されるため無制限ではない）。この重大度は**本表が正本**であり、発火点で使われている `LogLevel` からは読み取らない——発火点のログレベルを変える PR が流量制御の挙動を黙って変えることを避けるため。実装は `EmailNotificationAllowlist`（`Yagura.Host.Observability.ActiveNotification.Email`）で、本表との一致はテストで固定する。`対象外（構造的）` はメール通知チャネル自身が発する ID（1025・1026）につく印で、**送信失敗をメールで通知しようとするループを定義レベルで排除している**ことを示す（allowlist に載せた瞬間にテストが落ちる）。

**イベントログ本文の表示名について（2026-07-06）**: Windows イベントログの本文に埋め込まれる `{Kind}` 相当部分は、従来 `AuditEventKind` の英語 enum 名（`ViewerListenerAdminRequestRejected` 等）がそのまま漏れていたため、運用者が読む日本語の短い説明（本表の「イベントログ本文の表示名」列）に置き換えた（`Yagura.Host.Observability.Auditing.AuditEventDescriptions`。ID・レベルの意味は変えていないため additive-only 規約には抵触しない）。アプリ記録ファイル（`audit.jsonl`）側の `Kind` フィールドは、外部ツール（grep・jq 等）による機械的解析対象として英語 enum 名のまま維持する。

**イベントレベルの配線の補足（M8-4）**: Windows イベントログの `EventLog` プロバイダの既定フィルタは警告以上のため、2000 番台（情報）が既定でイベントログに届くよう、監査カテゴリ（`Yagura.Host.Observability.Auditing`）に限り情報レベルまで通すフィルタを Host が構成する（Yagura.Host/Program.cs）。「ソース Yagura の警告以上を通知」という最小監視構成は 1000/3000 番台で従来どおり成立する。

### 4.4 拒否試行の流量制御（証跡の希釈・ディスク圧迫対策）

- 同一の送信元・同一の事象種別による拒否が短時間に反復する場合、**個別記録から集約記録へ自動で切り替える**（ADR-0004 決定 7 の委任。スキャン・設定ミスの反復が証跡を埋め尽くし、本来見るべき単発の拒否を希釈することを防ぐ）
- **集約しても初動解析の手がかりを失わない**ことを内容の要件とする:
  - 集約記録は「期間・送信元・事象種別・回数」に加え、**期間内の最初と最後の試行のフル詳細**（§4.1 と同粒度——対象エンドポイント・試行された利用者名等）を保持する
  - **事象種別が変われば（同一送信元でも）個別記録する**。認証失敗の集約では試行された利用者名の次元を保持する（「どのアカウントが狙われたか」を件数に畳まない）
  - **静穏窓の経過後は個別記録へ復帰する**（一度集約に入った送信元の、後日の単発の試行が集約に吸われ続けない）
- 切替閾値・窓・静穏判定は [SEC-4](#7-確定待ち一覧)。確認の判定基準は「単発・新種の事象が希釈されないこと」とする。**集約中も件数は失われない**（architecture.md の「破棄は必ず計上」と同じ思想）
- **アプリ独自認証の三層防御（§2.4.1）はレイヤーの別を集約単位の次元として保持する**（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 9）: バックオフ（3006）・レート制限拒否（3007）は事象種別（`AuditEventKind`）自体が異なるため、本節の集約規約（「事象種別が変われば個別記録する」）が自然に層を混同しない。3007 は IP レート制限（送信元単位）とグローバルトークンバケット涸渇（プロセス全体・単一の事象）の両方を含むため、**グローバルトークンバケットの事象を「送信元ごと」の集約単位に載せない**（「プロセス全体で 1 つ」の事象を送信元別に按分すると実態と乖離する）。実装（下記）は 3007 を集約対象から一律に除外し個別記録のまま残すことでこれを満たす
- **実装（Issue #268）**: `Yagura.Host/Observability/Auditing/AggregatingAuditRecorder.cs`。`IAuditRecorder` のデコレータとして内側 `FileAuditRecorder` の前段に挟まり（DI では本クラスを `IAuditRecorder` として登録、全呼び出し側に透過）、集約キー `(Kind, RemoteAddress)` 単位で状態を持つ。集約対象種別は `AuditAggregationDefaults.AggregatedKinds`（3002/3003/3004/3006/3008/3009。3007 グローバルバケットと凍結 ID 3005 を除外）。窓内に閾値到達で集約モードへ入り、以降は個別記録を止めて回数・利用者名集合・最初/最後の事象を蓄積、静穏窓の経過（周期スキャン + 停止時フラッシュ）でサマリ（`RejectionAggregated`=3012）を 1 件出してキーを破棄し個別記録へ復帰する。閾値・窓・静穏・スキャン間隔は `AuditAggregationDefaults`（仮値。SEC-4 確定待ち。確定まで設定キーは設けない）

## 5. 保存データの ACL 具体値（v0.1: 完動）

ADR-0004 決定 5「サービスアカウントと Administrators のみ」の具体化。対象はデータルート配下の全域（設定ファイル・組み込み DB・スプール・メタデータ領域・監査記録。configuration.md §2）:

- **ACE 構成（意図）**: `SYSTEM` = フルコントロール / `Administrators` = フルコントロール / サービスの仮想サービスアカウント = 変更（読み書き・作成・削除。フルコントロールは与えない——ACL 自体の変更権限を実行時アカウントに持たせない）。**継承を無効化し、上位フォルダからの ACE を持ち込まない**。`Users` / `Authenticated Users` の ACE は置かない
- **監査記録の領域の分離は「削除権限の分離」までとする（実機検証で確定。2026-07-18）**: 追記系のみを許す構成（§4.2）のうち、**真の追記専用（既存データの上書きを許さない）は成立しない**が、**削除権限の分離は成立する**。詳細は下記「監査記録領域の ACE（SEC-3 実機検証の結果）」
- 適用は**インストーラが行い、サービスは起動時に検証する**: 期待と異なる ACL を検出した場合、自動修復はせず（管理者の意図的な変更と事故を区別できないため）、能動通知で警告する。**警告には期待される ACE 構成と確認手順（operations.md——[§8](#8-利用者向けドキュメントへの申し送り) SEC-D2）への参照を含める**——深夜に警告だけを見た利用者が次の一手に辿り着けるようにする

#### 実機で確定した ACE 表現（2026-07-18。Windows Server 2025 10.0.26100 / Yagura 0.4.0 / 仮想サービスアカウント `NT SERVICE\Yagura`）

インストーラが適用する既定（データルート配下。継承で伝播）の `icacls` 出力:

```
C:\ProgramData\Yagura\audit NT AUTHORITY\SYSTEM:(I)(OI)(CI)(F)
                            BUILTIN\Administrators:(I)(OI)(CI)(F)
                            NT SERVICE\Yagura:(I)(OI)(CI)(M)
```

**仮想サービスアカウントの指定形式は `NT SERVICE\Yagura`** で `icacls /grant` にそのまま渡せる（SID への事前解決は不要）。継承フラグは `(OI)(CI)` で親から伝播し、ファイル側には `(I)` 付きで現れる。

#### 監査記録領域の ACE（SEC-3 実機検証の結果）

**仮説 1「追記専用 ACE」は成立しない**。ファイルに対し `AD`（FILE_APPEND_DATA）のみを与え `WD`（FILE_WRITE_DATA）を与えない構成にすると、**監査記録の書き込み自体が失敗する**:

```
icacls <audit> /grant "NT SERVICE\Yagura:(CI)(RD,RA,REA,X,WD,RC,S)"
icacls <audit> /grant "NT SERVICE\Yagura:(OI)(IO)(RA,REA,AD,RC,S)"
→ ファイル側 ACL: NT SERVICE\Yagura:(I)(Rc,S,AD,REA,RA)   ← AD あり・WD なし（真の追記専用）
```

この構成で監査事象が発生すると:

```
[audit-file-write-failed] 監査記録ファイル C:\ProgramData\Yagura\audit\audit-20260718.jsonl への書き込みに失敗しました。
System.UnauthorizedAccessException: Access to the path '...' is denied.
   at Microsoft.Win32.SafeHandles.SafeFileHandle.CreateFile(String fullPath, FileMode mode, FileAccess access, ...)
```

すなわち **.NET の `FileMode.Append` は FILE_APPEND_DATA だけでは成立せず、FILE_WRITE_DATA を要求する**（§4.2 の検証ポイントが「実機で確認するまで確定しない」としていた点の答え）。`WD` を与えれば書き込みは成立するが、`WD` はファイルに継承されると既存データの上書きを意味するため、**「既存データの変更を許さない」という当初の狙いは ACL では表現できない**。

したがって **§4.2 の「成立しない場合、一次の耐タンパ線はイベントログ併記である」が確定した到達水準**である。なお本検証中、ACL 拒否により監査チャネルが機能しない間の事象は失われず、復旧後に `[deferred-writeback]` 注記付きでイベントログへ書き戻されることも実機確認した（§4.2 の多重化が実際に働いた）。

**仮説 2「削除（ローテーション）権限の分離」は成立する**。サービスアカウントから削除権限を落とした構成では、保持期間超過ファイルの削除が設計どおり拒否され、警告に留まる:

```
[audit-retention-acl-denied] 保持期間を超過した監査記録ファイル C:\ProgramData\Yagura\audit\audit-20200101.jsonl を ACL のため削除できません。
System.UnauthorizedAccessException: at System.IO.FileSystem.DeleteFile(String fullPath)
   at Yagura.Host.Observability.Auditing.AuditRetentionScheduler.DeleteExpiredOnceAsync(...)
```

対照として ACL を既定へ戻すと同ファイルは削除され、監査記録に 2015 が残る:

```
{"Kind":"AuditRetentionApplied","EventId":2015,"Detail":"deleted=1 retentionDays=365 cutoffUtc=... files=audit-20200101.jsonl"}
```

**実装上の必須事項（実機検証で判明）**: 制限 ACE を組む場合、**`S`（SYNCHRONIZE）を必ず含める**こと。`S` を欠くと Win32 `CreateFile` がハンドルを開けず、書き込み以前にディレクトリ列挙が失敗する:

```
[audit-retention-enumerate-failed] 監査記録ディレクトリ C:\ProgramData\Yagura\audit の列挙に失敗しました。
System.UnauthorizedAccessException: at System.IO.Enumeration.FileSystemEnumerator`1.CreateDirectoryHandle(...)
```

**採用する構成**: 監査記録ディレクトリに削除権限の分離のみを適用する場合の ACE は次のとおり（`icacls` が正規化した形。`(OI)(CI)` で伝播）:

```
C:\ProgramData\Yagura\audit NT SERVICE\Yagura:(OI)(CI)(RX,WD,AD)
                            BUILTIN\Administrators:(OI)(CI)(F)
                            NT AUTHORITY\SYSTEM:(OI)(CI)(F)
```

この構成では書き込み・ローテーション（日次ファイル新規作成）は動作し、期限切れファイルの削除のみが拒否される（= 削除は ACL 管理側の運用手順の責務になる。operations.md 起筆時の申し送り対象——§4.2）。**分離は既定にしない**（既定は上記のデータルート既定 `(M)`）。適用は運用者の選択とする。

### 5.1 フォワーダ MSI 配置フォルダの ACL（[ADR-0008](../adr/0008-forwarder-kit-generation.md) 設計条件 9・委任 #7）

MSI オプトイン同梱のため、管理者が Fluent Bit の MSI を手動配置するフォルダ（データルート配下 `forwarder`。`%ProgramData%\Yagura\forwarder\`）は、§5 の ACL 方針とは**異なる専用の ACE 構成**を持つ——`Administrators` のみ書き込み可とする（ADR-0008 設計条件 9）。ここに書ける者は「全端末に配る MSI を差し替えられる者」であり、供給網上防御価値が最も高い書き込み対象になるため、データルート本体の既定（サービスアカウントに Modify を与える§5）よりも一段絞る。

- **インストーラ（WiX）が `forwarder` フォルダの作成・ACL 設定を行う**（Issue #171 で実装。`installer/Package.wxs` の `ForwarderFolder` コンポーネント）。DataRootFolder（§5）と同じ native `PermissionEx`（`MsiLockPermissionsEx`。SDDL でセキュリティ記述子を直接指定）方式を使うが、**データルートの ACL をそのまま継承しない**——継承すると `NT SERVICE\Yagura`（仮想サービスアカウント）に Modify（書き込み）権限が残ってしまい、設計条件 9 の意図（Administrators のみ書き込み可）と乖離する（#171 の発見経緯）
- **ACE 構成（実装値）**: `D:P`（継承無効化）+ `SYSTEM` = フルコントロール + `Administrators` = フルコントロール + サービスアカウント（`NT SERVICE\Yagura` の SID）= **読み取りのみ**（`FILE_GENERIC_READ` = `0x120089`。書き込み・削除・ACL 変更のいずれも与えない）。データルート本体の ACE 構成（§5。サービスアカウント = Modify `0x1301bf`）との差分はサービスアカウントの権限のみ
- **フォルダは常に作成する**（存在時のみ適用、ではない）: DataRootFolder と同じ挙動に揃え、管理者が生成 UI の案内（配置パス表示）を見た時点で、既に正しい ACL のフォルダが存在する状態にする
- **サービス動作への影響なし（実機確認済み・2026-07-09）**: `SystemForwarderMsiSource`（`src/Yagura.Web/ForwarderKit/SystemForwarderMsiSource.cs`）はフォルダの列挙・ファイルの SHA256 計算・MSI 版取得（`MsiGetFileVersion`）のみを行う読み取り専用の実装であり、書き込みは一切行わない。実機で MSI インストール → `Administrators` 権限でファイル配置 → `NT SERVICE\Yagura` で稼働する Yagura.Host の `/admin/forwarder-kit` へアクセスし、配置した MSI が「未検出」にならず正しく検出されることを確認した
- **アップグレード・アンインストール時の挙動（実機確認済み・2026-07-09）**: `icacls` で `forwarder` フォルダのみ `NT SERVICE\Yagura:(OI)(CI)(R)`（データルート本体は `(OI)(CI)(M)` のまま）になることを確認。アンインストール時、フォルダが空（MSI 未配置）なら DataRootFolder と同じ挙動で削除される。MSI 配置済み（非空）の場合は削除されず**保持**され、ACL（Administrators のみ書き込み可）もファイルシステム側の属性としてそのまま残る。MajorUpgrade（既定 Schedule = `afterInstallValidate`。旧製品削除 → 新製品インストール）でも、新製品インストール時に `ForwarderFolder` コンポーネントの `PermissionEx` が再実行されるため ACL は再適用される
- **免責**: Yagura は配置された MSI の取得元・真正性・脆弱性対応の責任を負わない。Yagura が行うのは、管理者が配置したファイルを生成 ZIP に梱包し、その来歴（ファイル名・版・SHA256）を記録することのみである。この免責は生成 README・`GENERATED.txt` にも明記する（ADR-0008 設計条件 9）

## 6. TLS 受信（RFC 5425）用証明書（TLS 受信 opt-in 有効時）

configuration.md §6 の射程宣言が委任した項（PR #16 クリスの指摘で homework 化）:

- **参照方式は Web UI の HTTPS と同型**とする: Windows 証明書ストア（ローカルコンピューター）からの選択・秘密鍵の読み取り権限のみをウィザードが付与（付与は監査対象）。ただし**設定キーは HTTPS と独立**とし、同一証明書の流用は「同じ証明書を両方のキーに指定する」ことで実現する（暗黙の連動をしない——片方の変更が他方へ波及して見えない事故を避ける）
- **期限切れ・失効時、TLS 受信リスナは停止しない**——期限切れの証明書のまま提示を継続し、強い警告（期限接近の事前警告 + 期限切れ中の継続警告。architecture.md §4.6 の経路）で可視化する
  - 理由: サーバ側で停止すれば**確実な受信断（= ログの喪失）**になる。継続すれば、送信側の証明書検証ポリシー次第で受信が続く可能性が残る。「ログを失わない」原則（ADR-0001）を通信の真正性より優先する判断である。**この理由の前提（期限切れを受け入れる送信側設定が実在すること）は代表的な送信実装の実機検証で確認する**（[SEC-11](#7-確定待ち一覧)。推測を既定の根拠のままにしない）
  - **期限切れ中の実害を観測可能にする**: 送信側が検証拒否した場合、サーバから見えるのは **TLS ハンドシェイクの失敗**である——これを送信元別のカウンタとして計上し（architecture.md §4.1 のカウンタ体系に「TLS ハンドシェイク失敗」を追加。同文書にも記載）、**期限切れ中のハンドシェイク失敗の増加は期限切れ警告と関連づけて能動通知・状態画面に出す**。送信元の脱落は ui.md の送信元別受信状況（無音化検出。UI-4）でも確認できる——「止めない」判断の成否を運用者が観測できて初めてこの既定は成立する
  - **再評価トリガ**: 期限切れ中にほぼ全送信元のハンドシェイク失敗が続く観測が得られた場合（= 「継続の方が損失が小さい」が成立していない環境）、既定の向きを再評価する
  - **HTTPS（閲覧）との非対称を明示する**: HTTPS は停止・HTTP へ落とさない（configuration.md §6）のに対し、TLS 受信は継続する。閲覧の停止は人間が代替手段（サーバ上での操作）を持つが、受信の停止はその瞬間のログの喪失そのものであり、取り返しがつかない——この違いが挙動の違いの根拠である
  - 平文受信（UDP/TCP 514）を有効にしている構成では、TLS の障害は平文経路に影響しない（共存可。ADR-0004 決定 3）

### 6.1 実装状況（Issue #137。オーナー採用決定 2026-07-10）

- **サーバ認証のみを実装**（相互 TLS = クライアント証明書によるフォワーダ側の送信元認証は対象外——オーナー決定 2026-07-10）。相互 TLS は将来の別判断として本節に記録する: 需要が確認された場合、`SslServerAuthenticationOptions.ClientCertificateRequired` を有効化し、許可するクライアント証明書の判定（拇印一覧・発行 CA 等）を別途設計する必要がある。現時点では要望が無く、実装しない
- **RFC 5425 の octet-counting 強制**: `Yagura.Ingestion.Tcp.TcpFrameDecoderOptions.RequireOctetCounting` を新設し、TLS 受信リスナはこれを常に `true` で構成する——接続の最初のバイトが数字でなければ（non-transparent-framing と判別されれば）即座に再同期不能な破損として切断する。PR #169 の再同期バイト数上限・フレーミング進捗タイムアウト（§4.5 の A+B 天井）はそのまま流用する（重複実装しない）
- **TLS ハンドシェイクの完了猶予（未認証 DoS の遮断。PR #225 レビュー指摘 High）**: `TlsSyslogListenerOptions.HandshakeTimeout`（仮値 15 秒）を新設し、`AuthenticateAsServerAsync` を `CancellationTokenSource.CancelAfter` でリンクしたトークンで囲む。ClientHello を送らない（または途中で黙る）接続がハンドシェイク段階のまま同時接続枠（`MaxConcurrentConnections`。既定 256）を占有し続ける slowloris 型の未認証資源枯渇を防ぐ——アイドル・フレーミング進捗タイムアウトはいずれも**ハンドシェイク成功後**の読み取りループにしか効かないため、ハンドシェイク段階専用の天井が要る（TLS 固有の攻撃面。平文 TCP には存在しない）。猶予超過は TLS ハンドシェイク失敗カウンタ（下記）で計上し、ログはタイムアウトである旨を区別できる文言にする。決定的テスト（短い猶予での無言接続の回収 + 正常接続が巻き添えにならないこと）を `Yagura.Ingestion.Tests` に追加した
- **証明書ロード・秘密鍵アクセス権付与・期限監視の共有**: TLS 受信は `Yagura.Host.Administration.Https.AdminCertificateProvider.Load` / `AdminCertificatePrivateKeyAccessGranter.TryGrantReadAccess`（いずれも管理リスナのリモート HTTPS——§2.5——と共通の実装）をそのまま呼ぶ。設定キーは独立（`Ingestion:Tls:*`）——同一証明書の流用は両方のキーに指定することで実現し、暗黙の連動はしない
- **管理 UI HTTPS との非対称の実装**: 管理 UI HTTPS の Kestrel `ServerCertificateSelector` は期限切れで `null` を返しハンドシェイクを拒否するが、TLS 受信（`Yagura.Ingestion.Tls.TlsSyslogListener`）は期限を一切検査せず、起動時に解決した証明書をハンドシェイクのたびにそのまま提示し続ける——期限切れ・失効時も「止めない」（本節上部の確定方針）をコードで担保する
- **TLS ハンドシェイク失敗カウンタ**: `yagura.ingestion.tcp.tls_handshake_failure`（architecture.md §4.1.1。送信元アドレスをタグに持つ——本カウンタ体系で唯一のタグ付き計器。理由は同表参照）。`TlsSyslogListener` がハンドシェイク失敗（`AuthenticationException`/`IOException`/`OperationCanceledException`、およびハンドシェイク猶予超過）のたびに計上する。**タグの distinct 値は上限（既定 256。`IngestionMetrics.MaxTlsHandshakeFailureSourceCardinality`）で有界化し、超過分は `(other)` へ畳む**——送信元アドレスは認証成立前に計上される攻撃者制御の次元であり、無制限のタグ基数が集約 exporter のメモリを圧迫するのを防ぐ。上限到達（＝「ほぼ全送信元が失敗」の観測。本節の再評価トリガに相当）は overflow バケット `(other)` の増加として現れるため、送信元別の脱落確認の目的は損なわれない
- **イベント ID**: 起動時に証明書が解決できず縮小継続 = 1016、秘密鍵アクセス権付与 = 2010、期限接近の事前警告 = 1017、稼働中の使用不能 = 1018（§4.3 参照。いずれも additive-only で新設）
- **縮小継続経路の自動テスト（PR #225 レビュー指摘 Medium）**: 証明書ストア解決失敗時の縮小継続（1016）は Program.cs の起動時経路であるため、実プロセスを起動する E2E 回帰テスト（`Yagura.E2E.Tests.IngestionTlsDegradedStartupE2ETests`。拇印は正しい形式だがストアに存在しない値を指定し、①UDP/TCP 受信は通常起動、②1016 警告が出力、③TLS 受信リスナは起動しない、④プロセスは中止しない、を確認）で固定した。管理リスナのリモート HTTPS の同型シナリオ（1013）と異なり、**ストアへ実証明書を導入しない**——「解決できない」ことそのものを検証するため
- **フォワーダキットの TLS 送信は見送り（オーナー決定 2026-07-11。再評価トリガあり）**: 当初はキット側にも TLS 送信対応（`install.ps1 -Mode tls` 等）を同一 PR で含める予定だったが、実機検証（2026-07-11）で **Fluent Bit 5.0.8 の `out_syslog` は TLS 有効時も RFC 6587 octet-counting フレーミングを実装しない**（LF 区切りのまま。`-Mode tcp` の既知の制約と同一原因）ことが判明した。Yagura の TLS 受信は RFC 5425 準拠で octet-counting を厳格に強制するため、キットから TLS 送信を提供すると「選ぶと TLS ハンドシェイクは成立するが後続メッセージが全て拒否され、しかも Fluent Bit は送達確認を待たない実装のため送信側は無音で失う」という危険な組み合わせを生む。**したがってキット側の TLS 送信（`-Mode tls`・生成 UI の TLS 選択肢）は実装せず、UDP/TCP のみに留める**。Fluent Bit が octet-counting に対応した時点でキットへ TLS 送信を再導入する（**再評価トリガ**）。TLS 暗号化転送が必要な環境は、octet-counting に対応した送信実装（rsyslog・syslog-ng の RFC 5425 モード等）を使う（利用者ガイドで案内）。この判断の実機検証根拠は [SEC-11](#7-確定待ち一覧)、送信側の非互換の詳細は [docs/guides/forward-windows-eventlog.md](../guides/forward-windows-eventlog.md) §「TLS を使う」を参照
- **相互運用検証（SEC-11）の実施範囲**: [§7](#7-確定待ち一覧) 参照

## 7. 確定待ち一覧

architecture.md §9 と同じ運用。番号は SEC-x。

| # | 項目 | 決め方 | 反映先 |
|---|---|---|---|
| SEC-1 | circuit 上限の既定値（閲覧リスナ・管理リスナ別）。**M8-4 で仮値（閲覧 100 / 管理 5）のまま実装済み。ADR-0010 Phase 2（決定 8 受け入れ条件 (iii)）でリモート解禁後の複数オペレーター同時対応を踏まえ管理を 5 → 20 へ引き上げ（`CircuitGovernanceDefaults`）——依然として仮値。確定は引き続き本項** | circuit あたりのサーバ側メモリの実測 × ターゲット環境の余裕 + 障害時の同時閲覧数の想定。小規模環境が既定値に当たらない下限条件つき | §2.2・§2.5 |
| SEC-2 | ✅ 確定 → 監査記録の保持期間の既定値は **365 日**（ログ本体の既定 30 日（DB-1）より長い制約を満たす）。2026-07-05 オーナー決定。**削除・ローテーション機構は実装済み**（Issue #261。日次ローテーション + 期限切れファイル削除 + 削除の証跡 2015——§4.2 の実装記述参照。設定キー `Audit:RetentionDays`） | —（確定済み・実装済み） | §4.2 |
| SEC-3 | ACL の具体 ACE 構成（icacls 出力の形で記録）。**監査記録領域の追記専用 ACE の成立可否・ローテーション権限の分離を含む** | **確定（lab 実機検証済み 2026-07-18。Windows Server 2025 / Yagura 0.4.0）**——①仮想サービスアカウントは `NT SERVICE\Yagura` 形式でそのまま指定でき、継承は `(OI)(CI)` で伝播する ②**追記専用 ACE は成立しない**（.NET の `FileMode.Append` が FILE_WRITE_DATA を要求するため、`AD` のみでは監査書き込みが `UnauthorizedAccessException` で失敗する）→ §4.2 の「一次の耐タンパ線はイベントログ併記」が到達水準として確定 ③**削除権限の分離は成立する**（`[audit-retention-acl-denied]` 警告に留まり記録は保持。既定にはせず運用者の選択とする） ④制限 ACE には `S`（SYNCHRONIZE）が必須（欠くとディレクトリ列挙から失敗する）。icacls 出力は §5 に記録済み | §4.2・§5 |
| SEC-4 | 拒否試行の集約切替の閾値・窓・静穏判定。**機構は実装済み**（Issue #268。`AggregatingAuditRecorder` = `IAuditRecorder` デコレータ。閾値・窓・静穏は `AuditAggregationDefaults` の仮値——閾値 10・窓 1 分・静穏 5 分。確定まで設定キーは設けない）。確定は引き続き本項 | 仮値で実装し、実トラフィックで確認。判定基準は「単発・新種の事象が希釈されないこと」 | §4.4 |
| SEC-5 | イベント ID 一覧の初版（**レベル列を含む**） | 実装 PR で採番し本文書 §4.3 に記録（以後 additive-only。3001 = M6-2 実装済み。2001〜2004・3002 = M8-4 実装済み。2005 = ADR-0008 実装済み。1009 = Issue #152 実装済み。1010 = PR #211。2006〜2008・3003〜3005・1011 = ADR-0010 Phase 1 実装済み） | §4.3 |
| SEC-6 | ✅ **確定・実装済み（Issue #267。2026-07-17）** → 猶予の上限値 = **15 分**（オーナー裁定。CF-3 の無操作タイムアウトと同じ時間感覚）。猶予タイマーは §2.3 の実装参照のとおり（監査 3010/3011・満了時の協調切断・緊急全失効によるバイパス）。**権限昇格防止の三重の防御**（管理セッションは猶予対象外・無害化 principal・帰属による描画拒否）を実装しテストで固定 | —（確定・実装済み。掲示用途の実運用での見直しは値のみ） | §2.3 |
| SEC-7 | ~~L-5 の実現方式——閲覧リスナの全ルート・全ハブの列挙と許可リスト突合、および circuit 経路の構造分離（DI 分離等）の参照検査の実現可能性~~ → **M6-4（Issue #54）で両方とも実現方式を確定・実装済み。列挙は `EndpointDataSource` から全登録済みエンドポイントを取得する方式（Razor Components の `@page` ルート・Interactive Server の SignalR ハブ関連ルートいずれも列挙に現れることを実機確認済み）。参照検査はマーカーインターフェース `IYaguraWriteService`（`Yagura.Abstractions.Administration`）+ 標準リフレクションによる `[Inject]` プロパティ・コンストラクタ引数の走査（外部ライブラリの追加なし）。覆域の限界（circuit 確立後の個々の UI イベントハンドラ呼び出しは列挙に現れないこと）は §1 L-5 に確定として追記済み**。~~L-3b の「管理系パス」判定の実現方式（ルート表導出 / 未登録パス全計上）を含む~~ → **M6-2（Issue #52）でルート表導出方式を確定・実装済み。覆域（未登録パス要求は計上対象に含めない）もオーナー決定で確定（2026-07-05。PR #56 の決定記録・§1 L-3b）** | 実装設計で検証（不可の部分は検証方式を再設計し、覆域の限界を §1 に追記） | §1 |
| SEC-8 | 無操作 circuit の回収（「操作」の定義・タイムアウト値。閲覧は掲示用途を殺さない長め・管理は短め）。**M8-4 で仮値（閲覧 8 時間 / 管理 30 分。「操作」= inbound activity）のまま実装済み——確定は引き続き本項** | 仮値で実装し、掲示運用と両立することを確認して確定 | §2.2 |
| SEC-9 | ~~AD グループの指定形式（名前 / SID）とネストグループ解決の有無~~ → **ADR-0010 Phase 4（2026-07-12）で確定・実装済み**。名（`DOMAIN\Group`）と SID の両形式を受理し、起動時に SID へ解決してキャッシュ（解決不能な指定は警告してスキップ＝認可を付与しない安全側）。ネストは `WindowsIdentity.Groups`（推移的グループ SID・OS 展開済み）で解決し追加 LDAP 不要。照合はトークンのグループ SID 集合と設定 SID 集合の交差。役割: `Admin:...:AdminGroups`→管理（544 に加算）、`Viewer:...:ViewerGroups`→閲覧、`Viewer:...:AdminGroups`→管理（§3）。**lab 実機確認済み（2026-07-18。yagura.test ドメイン / Yagura 0.4.0）**——①**名前形式 + ネストグループ**: `Viewer:...:ViewerGroups = YAGURA\YaguraViewers` に対し、その入れ子グループ `YaguraNestedViewers` にのみ所属する利用者が `200` + 「役割: 閲覧」を得ることを確認（推移的解決が実際に働く） ②**SID 形式**: `Viewer:...:AdminGroups` に SID 直指定したグループへ利用者を追加すると「役割: 管理（閲覧を含む）」へ昇格することを確認 ③**非所属者の拒否**: いずれのグループにも非所属かつ `BUILTIN\Administrators` 非該当の利用者は `403` で拒否され、監査 3009 が記録されることを確認（`{"Kind":"ViewerAuthorizationDenied","EventId":3009,"AttemptedPath":"/login/windows","ReachedListenerPort":8514,"Detail":"authenticated-but-not-in-viewer-or-admin-group","AuthenticatedPrincipal":"YAGURA\\yagura-none1"}`） ④**解決不能指定**: 存在しないグループ名を混在させても他の指定は正しく機能し、認可も付与されない（安全側で正しい）——**ただし警告 `[sec9-group-unresolved]` は運用者に届かない**（下記の限界） | §3 |
| SEC-9-a（新規。上記の派生） | **解決不能なグループ指定の警告が運用者に届かない**（lab 実機確認 2026-07-18）。`WindowsSecurityGroupResolver.ResolveToSids` は `[sec9-group-unresolved]` を `LogWarning` で出力するが、その出力先は `Program.cs` の **bootstrap ロガー**（`bootstrapLoggerFactory`。ホストの EventLog プロバイダ構築前）であり、Windows イベントログへ到達しない。実測: 存在しない `YAGURA\NoSuchGroup12345` を設定して起動しても、イベントログ全体（Yagura プロバイダ 400 件）に当該警告は **0 件**。他の起動時警告（`Yagura.Host.Administration.Https` / `.ViewerAuth` 等）は到達しているため、ロガーの差に起因する。ファイルログの出力先も存在しない。**帰結**: グループ名のタイプミスは「そのグループの所属者が黙って認可されない」形で現れ、運用者は原因に辿り着く手がかりを得られない（fail-closed ではあるが不可視） | 警告の出力先を実ロガーへ移すか、起動完了後に再出力する | §3 |
| SEC-10 | 記録失敗中の監査事象のメモリ内保持の上限・超過時の縮退。**機構は実装済み**（Issue #269。`ResilientAuditRecorder` = `FileAuditRecorder` をラップするデコレータ。保持上限＝仮値 1000 件・復旧スキャン間隔＝仮値 30 秒は `AuditResilienceDefaults`。縮退は古い側を残し新しい到来を破棄・破棄件数は復旧サマリ 3013 とライブ計器 `audit.buffer_dropped` に計上。確定まで設定キーは設けない）。確定は引き続き本項 | 実装設計で確定（＝上限・縮退方針は実装済み。上限値の妥当性は実運用の障害時挙動で確認） | §4.2 |
| SEC-11 | ✅ 部分確定（2026-07-11 実機検証。Issue #137） → **openssl s_client（標準的な X.509 検証を行う代表実装として使用）で確認**: ①期限内証明書に対する TLS ハンドシェイク + octet-counting フレーミング送信 → Yagura 側で正常受信・解析・保存されることを確認（`/search/export.csv` で到達確認）。②**既に期限切れの証明書を Yagura 側に構成しても TLS 受信リスナは起動・接続受理を継続する**（「止めない」設計の実機確認）。③期限切れ証明書に対し `openssl s_client -verify_return_error` で厳格検証を行うと、クライアント側は `certificate has expired` でハンドシェイクを拒否し、**Yagura 側はこれを TLS ハンドシェイク失敗として記録する**（フレーミング失敗とは別経路であることをログで確認——`Yagura.Ingestion.Tls.TlsSyslogListener` の警告ログ「ハンドシェイクに失敗しました」+ 内部例外「受信した証明書の期限が切れています」）。④能動通知の周期監視（`ActiveNotificationMonitor`）が実プロセスで実際に動作し、有効期限接近（30 日以内の証明書。EventId 1017）・既に期限切れ（EventId 1018）の両方を正しい文言で発火することを確認。**Fluent Bit（`out_syslog`。本製品の推奨フォワーダ）は本検証の対象外とせざるを得なかった**——TLS ハンドシェイク自体は成立するが、`out_syslog` が RFC 6587 octet-counting フレーミングを実装しない（TLS 有効時も）ため、そもそも通常のメッセージ配送が成立せず、期限切れ時の挙動差分を観測する前提が満たせなかった（詳細は §6.1・ADR-0008 改訂履歴 3）。**rsyslog・syslog-ng・NXLog は未検証**——Linux 環境（rsyslog・syslog-ng）または追加の Windows 実行環境構築（NXLog）を要し、本 PR の実行環境（Windows 単体）では実施できなかった。正直に申し送る——検証の再開時は、これら 3 実装のいずれかが実際に octet-counting を送出し、期限切れ証明書に対して検証拒否する（または継続受理する）挙動を実機で確認すること | 残る 3 実装（rsyslog/syslog-ng/NXLog）の実機検証は Linux/追加環境を用意できるタイミングで実施 | §6 |
| SEC-12 | アプリ独自認証のバックオフ・レート制限仮値（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 10 で、旧「ロックアウト閾値・期間」から三層防御の仮値表へ全面差替え）。**`AdminAuthenticationDefaults` に仮値のまま実装済み**: バックオフ猶予閾値 k=3 回・基数 base=1 秒・上限 cap=30 秒・n のアイドル減衰窓=30 分／IP レート制限窓=60 秒・上限=10 回（loopback 除外）／グローバルトークンバケット 定常 1 トークン/秒・バースト 20（loopback 除外）／レート制限層の `Retry-After` 上限=30 秒／能動通知への昇格閾値=15 分／パスワード最小長=12 文字（確定値）——確定は引き続き本項。**NAT/VPN 出口配下の複数オペレータでの IP レート制限仮値の実測は未実施**（ADR-0011 決定 10 が実装 PR での一度の実測を求めた項目。開発機単体では複数オペレータ・複数拠点の同時アクセスを再現できないため、実運用フィードバックでの確定に申し送る） | 仮値で実装し、実利用（誤入力の反復頻度・NAT 配下からの正規ログイン試行が 429 に抵触した報告等）を踏まえ確定する | §2.4.1 |
| SEC-13 | 管理リスナのリモート HTTPS 証明書の秘密鍵権限付与（ADR-0010 Phase 2 決定 4）の実機確認 | **確定（①②③すべて実施。lab 実機検証 2026-07-18。Windows Server 2025 / Yagura 0.4.0 / インストーラ登録の `NT SERVICE\Yagura`）**。詳細は §2.5「秘密鍵権限付与の実機検証結果」。要点: **自動付与は失敗する**（サービスアカウントは鍵ファイルの ACL を書き換える権限を持たないため）。**権限が全く無い状態では `CngKey.Open` が未処理例外となりサービスが起動しない**（不具合。Issue #345）。**読み取り権限を別途付与すれば TLS は成立する**（③では AD CS の信頼チェーン検証込みで成立）。**監査 2009 は記録されない**（付与成功時のみ発火するため）。**③ AD CS 発行証明書（エンタープライズ ルート CA + `WebServer` テンプレート + CNG KSP）でも①②と完全に同一の結果**——本項の結論は**証明書の発行元に依存せず**、決定要因は鍵ファイルの既定 ACL にサービスアカウントの ACE が無いことである | §2.5 |

## 8. 利用者向けドキュメントへの申し送り

architecture.md §10 と同じ運用。

| # | 内容 | 出典 |
|---|---|---|
| SEC-D1 | 認証失効の既定挙動（**猶予中は新着ログの表示も継続すること・猶予は記録されること**・上限があること）と、漏洩対応時の即時全切断の手順 | §2.3 |
| SEC-D2 | ACL 検証警告を受けたときの確認・修復手順（警告本文から参照される） | §5 |
| SEC-D3 | TLS 受信証明書の期限切れ時の挙動（受信は止めない・送信側の検証ポリシー次第で接続が拒否され得る・**期限切れ中は送信元別受信状況とハンドシェイク失敗で脱落を確認する**）と期限管理の推奨 | §6 |
| SEC-D4 | イベント ID の一覧と監視設定の作り方（**レベルだけで組める最小構成——「ソース Yagura の警告以上を通知」——を含む**） | §4.3 |
| SEC-D5 | opt-in 強化の有効化手順一式（HTTPS が AD 認証・リモート管理の前提であること、グループマッピングの設定を含む） | §2.3・§3・configuration.md §6 |
| SEC-D6 | 管理 UI 認証（ADR-0010）: ①Windows 統合認証運用時もアプリ独自アカウントを最低 1 つ保持するブレークグラス推奨、②loopback 認証 opt-in 有効時の最終復旧経路（設定ファイル手編集。CF-5 確定までは事実上サービス再起動 = syslog 受信断を伴うこと・手編集の実行可能者は ACL を満たす者に限られること）、③非ドメイン機でも Windows 統合認証（NTLM フォールバック）が使えること、④Kerberos-only モード有効時は NTLM フォールバックが成立しないこと。**（[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 決定 6 で追記）** アプリ独自認証の三層防御について: ⑤バックオフ・レート制限は仕様であり、正しいパスワードであれば待てば必ずログインできること（一般向けの言葉で説明する——例:「5 回連続で間違えると徐々に待ち時間が延びて最大 30 秒になる」）、⑥単一アカウントモデルでは同僚の誤入力が自分のログイン遅延として波及し得ること（共有アカウントモデルの構造的な帰結）、⑦攻撃が継続している間は待ち時間が常に cap 近辺になり得ること（「常に速く通る」ではなく「詰みはしない」が正確な射程であること）、⑧アプリ独自認証を有効化した時点で追加設定なしにバックオフ・レート制限の両方が既定で有効になること、⑨**バックオフ・レート制限の待ちを解消する目的でサービスを再起動しないこと**（再起動は syslog 受信断を招くだけで、待てば cap で必ず抜けられる）、⑩能動通知の届け先が既定でイベントログ（ソース `Yagura`・1000 番台・警告レベル。EventId 1019）であること | §2.4・§2.4.1・[ADR-0010](../adr/0010-admin-ui-authentication.md) 委任事項 11・[ADR-0011](../adr/0011-app-auth-failure-backoff.md) 委任事項 8 |
| SEC-D7 | 管理リスナのリモートバインド（ADR-0010 Phase 2）: ①リモート解禁時点の Windows 統合認証は Kerberos SPN が未検証のため NTLM が事実上の常用経路になり得ること（NTLM の既知の弱点——リレー攻撃・pass-the-hash・相互認証の欠如——を明示）、②有効化に要するファイアウォール規則の手動作成手順（規則名の名前空間・ポート——既定 8516——を含む。インストーラは自動作成しない）、③証明書取得・証明書ストアへの取り込み手順（configuration.md §6 CF-D2 と同型。ひとり情シス向けの最短経路を含む）、④暗号スイートの個別制約は製品側で提供しないこと（Windows では .NET の `CipherSuitesPolicy` が使えないため——OS レベルの TLS ポリシー・グループポリシーでの統制が必要な場合の案内先）、⑤証明書の秘密鍵アクセス権の自動付与が対応する秘密鍵の種別（CNG ソフトウェアキーストレージプロバイダーのみ）と、対応外（スマートカード・HSM・TPM 保護鍵）の場合の手動付与手順 | §2.5・[ADR-0010](../adr/0010-admin-ui-authentication.md) Phase 2 |
