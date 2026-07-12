# ADR-0013: 管理 UI 認証の共存セッションモデル — 認証成立後の単一 Cookie セッションと Windows 認可の失効反映

- 状態: proposed
- 日付: 2026-07-12
- 決定者: YANAI Taketo
- 関連: [ADR-0010](0010-admin-ui-authentication.md)（管理 UI 認証。本 ADR は**決定 3 の「セッション・トークンを方式間で共有しない」共存セッションモデルを部分 supersession** する〔ADR-0010 状態行・決定 3・委任事項 9・Phase 1 受け入れ条件 (iv) に注記済み〕。決定 2〔circuit 認証状態の汲み直し〕・決定 5〔認可〕・決定 6〔監査「誰が」欄〕・委任事項 6/9/11 の実装解釈を確定する）/ [ADR-0011](0011-app-auth-failure-backoff.md)（失敗試行対策の三層防御。本 ADR が「分離の実体」と呼ぶ独立性の一方の柱）/ [ADR-0012](0012-admin-https-cert-ui.md)（管理リモート HTTPS。本欠陥は Windows 認証 + リモートバインドにも波及するため、本 ADR の是正がリモート面の Windows 認証も回復させる）/ [ADR-0003](0003-ui-policy.md)（Blazor Interactive Server 単一モード）/ [ADR-0004](0004-security-model.md) 決定 2・3（信頼ネットワーク前提 + opt-in 強化）/ [docs/design/security.md](../design/security.md) §2.3（失効の即時反映）・§2.4（共存の実装）・§5（データルート ACL）/ Issue #252（起案元の P0）/ PR #253（5 ペルソナレビューをこの PR 上に保存）

## 文脈と課題

[ADR-0010](0010-admin-ui-authentication.md) 決定 3 は、管理 UI の 2 認証方式（Windows 統合認証 = Negotiate、アプリ独自 ID/パスワード）の共存を「ASP.NET Core の複数認証スキームとして構成し、**セッション・トークンは方式間で共有しない**」と定め、選択式ログイン画面の実装を委任事項 9 へ委ねた。

この「セッション非共有」を額面どおり実装した Phase 1 の初期実装（v0.3.0〜v0.3.2）は、**Windows 統合認証でログインしても管理 UI に入れない P0 回帰**を生んでいた（Issue #252。lab 実機 v0.3.2 で発見）。症状: `Admin:Authentication:Windows:Enabled=true` + `Admin:Authentication:RequireForLoopback=true` の構成で、正しい資格情報を入れても認証ダイアログが繰り返し戻り `/admin` へ到達できない。監査には `AdminLoginSucceeded`（2008・`scheme=windows`）が記録され、**認証は成立するが identity が後続の認可判定に伝播しない**。

### 根本原因（機序）

1. 選択式ログインの Windows 経路 `/admin/login/windows` は、Negotiate ハンドシェイク成功を監査記録して `/admin` へリダイレクトするだけで、**認証セッションを一切発行していなかった**（`SignInAsync` を呼ばない）。
2. Negotiate は**接続単位・ステートレス**であり Cookie を発行しない。既定認証スキーム（`DefaultAuthenticateScheme`）はアプリ独自認証の Cookie スキーム（`YaguraAppAuth`）を指すが、**そのスキームはアプリ独自認証有効時のみ登録**される。
3. したがって Windows 認証のみの構成では、管理画面の認可属性（`AuthorizeAttribute(AdminPolicyName)`）が未登録の既定スキームで `HttpContext.User` を解決 → 匿名 → `AdminPolicyName` 拒否 → 唯一登録された Negotiate へフォールバックして 401 チャレンジ → ハンドシェイク完了 → また匿名で拒否、の**無限ループ**になる。
4. `/admin/login/windows` だけが動くのは、そのエンドポイントが明示的に `AuthenticationSchemes = Negotiate` を指定しているため。この明示指定は他の管理画面にはない。

「セッション非共有」を「Cookie を方式ごとに別立てにする（Windows は Cookie を持たない）」と過剰解釈したことが設計上の一因である。**Negotiate はステートレスゆえ、Cookie を持たない Windows セッションは後続要求に伝播しえない**——共存セッションモデルの再設計を要する。既定（無認証・`RequireForLoopback=false`）では発現しないため既定インストールは無事だが、loopback 認証 opt-in を選んだ環境、および [ADR-0012](0012-admin-https-cert-ui.md) のリモートバインド（HTTPS）経由の Windows 認証で管理 UI が使用不能になる。

### 本 ADR が確定すべき論点

再設計は「認証成立後のセッションをどう持つか」に加え、その選択が波及する次を同時に確定する必要がある（PR #253 の 5 ペルソナレビューが提起）: (i) Windows 認可（544 SID）の失効反映、(ii) 監査「成功」の意味づけ、(iii) Windows ログインの副作用化に伴う CSRF/セッション固定、(iv) 方式弁別の単一クレーム依存、(v) Cookie 暗号鍵（Data Protection）という新しい保存時秘密、(vi) Cookie 属性とリモート平文拒否への依存。

## 検討した選択肢（認証成立後のセッション担体）

### (a) 方式 A: 透過 SSO（全管理エンドポイントで Negotiate 自動チャレンジ・Cookie なし）

Windows 認証有効時、全管理エンドポイントの認証・チャレンジスキームに Negotiate を含め、未認証要求をブラウザの透過認証（ドメイン参加端末はダイアログなし、非参加端末はダイアログ）で解決する。

- 利点: クリック不要の古典的 Windows 統合認証 UX。**各要求で 544 を原理的に再評価できる**（認可の鮮度が最も高い——後述の失効反映問題を構造的に持たない）。
- 欠点: ①[ADR-0010](0010-admin-ui-authentication.md) 決定 3・委任事項 9 の「選択式ログイン画面」を Windows 経路で実質バイパスする。②両方式併用時に、アプリ資格情報で入りたい利用者にも Windows ダイアログが出る。③認証成立後の identity を circuit（Blazor Interactive Server）境界へ Negotiate のまま伝播させる必要が残る——[ADR-0010](0010-admin-ui-authentication.md) 検証 3 が公式の既知課題として言及した「`WindowsPrincipal` は元のリクエストに紐づく OS ハンドルであり circuit 生存期間中の再利用が保証されない」脆い経路。
- **却下**（利点の「544 ライブ再評価」は魅力だが、選択式ログインの破壊と circuit 伝播の脆さが上回る。ただし失効反映の観点で退けきれない側面は決定 2 で正面から扱う）。

### (a') 方式 A の下位変種: 「A + 認証成立点での Cookie 変換」

透過チャレンジで認証したうえで、成立点で `SignInAsync` により Cookie へ変換する。circuit 伝播の脆さは回避できる（クリスの指摘: **circuit 伝播は「Cookie 担体か Negotiate 担体か」の軸であって「A か B か」の軸ではない**）。しかし Cookie 化した瞬間に方式 B と同じ 544 失効反映の問題を負い、かつ透過チャレンジの副作用（login CSRF・併用時ダイアログ）は残る。**却下**（B に対する優位が「選択式を捨てる」ことだけになり、対価に見合わない）。

### (b) 方式 B: 認証成立後の単一 Cookie セッション（採用）

Windows ログインの成功後も、アプリ独自認証と同一の認証セッション Cookie を発行する。Negotiate ハンドシェイクは選択式ログインの Windows 経路（`/admin/login/windows`）でのみ発火させ、他の管理画面へは波及させない。詳細は決定 1。

### (c) 方式 B の変種: Windows 専用の別 Cookie スキーム

「方式ごとに独立」を Cookie 名前空間まで徹底し、Windows 由来セッションとアプリ由来セッションを別スキーム・別 Data Protection 目的で持つ。

- 利点: 分離が最も強い。**一方の方式のセッションだけを独立に無効化できる**（アプリ資格情報漏洩時に app セッションだけ強制失効、が可能）。方式弁別がスキーム型そのものになり単一クレーム依存にならない（クリスの指摘の核心）。
- 欠点: 認可判定・監査・circuit 認証状態の各所で 2 系統の Cookie を場合分けする実装コスト。
- **却下するが恒久却下にしない**（クリスの指摘を受けた修正）: 現時点では役割が「管理」単一（[ADR-0010](0010-admin-ui-authentication.md) 決定 5）であり、独立失効の需要が具体化していないため実装コストに見合わない。**再評価トリガ**を「先送り」節に明記する（独立失効要件の顕在化 or 役割粒度の分化）。

## 決定

### 決定 1. 認証成立後の単一 Cookie セッション + Negotiate の閉じ込め（方式 B）

- **Windows ログイン成功後も、アプリ独自認証と同一の認証セッション Cookie を発行する**。`/admin/login/windows` は、Negotiate 認証済みの `WindowsIdentity` に対する `BUILTIN\Administrators`（544 SID）判定に合格した時点で `context.SignInAsync` により Cookie を発行する（決定 4 の CSRF/確認ステップを伴う）。以降の管理画面要求はこの Cookie（= 既定認証スキーム）で認証される。
- **Negotiate ハンドシェイクは選択式ログインの Windows 経路（`/admin/login/windows`）でのみ発火させる**。認証セッション Cookie スキームを既定の認証**かつ**チャレンジスキームに固定し、未認証の管理要求は選択式ログイン画面（`/admin/login`）へ誘導する（Negotiate の自動チャレンジに落とさない）。これにより [ADR-0010](0010-admin-ui-authentication.md) 委任事項 9 の「選択式」を意図どおり完成させる（方式 A を退けた根拠の実体化）。
- **認証セッション Cookie スキームは、Windows 統合認証・アプリ独自認証のいずれか一方でも有効なら登録する**（従来はアプリ独自認証有効時のみ登録し、これが #252 の直接原因）。
- **circuit（Blazor Interactive Server）への identity 伝播も Cookie 経由で成立する**。[ADR-0010](0010-admin-ui-authentication.md) 検証 3 が既知課題とした「`WindowsPrincipal` の OS ハンドル依存」の脆い経路を、認証成立後は Cookie に一本化することで回避する——これが Cookie 担体を選んだ技術的利点。決定 2（circuit 認証状態の汲み直し）はこの Cookie 認証状態を毎回参照する。
- **スキーム名の中立化**: 現行の Cookie スキーム定数 `YaguraAppAuth` は Windows 由来セッションも運ぶため「アプリ独自認証専用」を含意する名称は誤読を招く（リサの指摘）。中立名（例 `YaguraAdminAuth`）への改称を実装時に行い、`security.md` §2.4 の「専用 Cookie スキーム」の限定語も是正する。改称は additive-only の設定スキーマに影響しない内部実装（Cookie 名の変更は全既存セッションの再ログインを 1 回要するのみ）。

### 決定 2. Windows 認可（544 SID）の失効反映 — 短寿命 + §2.3 例外の明記 + 緊急全失効（オーナー決定 2026-07-12）

方式 B は 544 判定をログイン時 1 回に確定し Cookie へ焼き込む。Cookie サインイン後の principal は `WindowsIdentity` ではなく `ClaimsIdentity` であり、`IsWindowsAdministrator` の型ゲートは Cookie 搬送要求で成立しない——**以降の認可は 544 を再検証しない**。結果、AD 側で `BUILTIN\Administrators` から除名しても Cookie 失効まで管理権限が残る（田中・鈴木・クリスが独立に指摘）。これは [ADR-0010](0010-admin-ui-authentication.md) 決定 2・検証 3 および `security.md` §2.3「操作のたびに現在の認証状態で認可する／失効を即時反映する」に対する**後退**である。**この後退は方式 B に内在するトレードオフとして明示的に受容し**、次で有界化する:

- **Windows 由来 Cookie の寿命をアプリ由来 Cookie と分け、大幅に短くする**。**絶対寿命上限**を設け（仮値: 1 時間。sliding は抑制——絶対上限を超えて延長させない）、失効窓を運用上許容できる長さに抑える。アプリ独自認証の Cookie は従来どおり（`SlidingExpiration`・8 時間）——アプリアカウントの資格情報は Yagura 自身が管理し、その無効化はアプリ側で即時に効く（発行済みセッションの扱いは下記の緊急全失効で共通に覆う）。確定値は `security.md` の確定待ち一覧に SEC 番号を採番して管理する（仮値で実装 → 実測で確定、の既存運用に倣う）。
- **`security.md` §2.3「即時反映」の Windows Cookie に対する適用外を正直に線引きする**。「Windows 統合認証の権限剥奪の反映は、Windows 由来 Cookie の絶対寿命（最大 1 時間・仮値）だけ遅延する」ことを設計書・利用者向けドキュメント（[ADR-0010](0010-admin-ui-authentication.md) 委任事項 11）に明記する。§2.3 の即時反映が完全に効くのはアプリ独自認証および「操作ごとに Cookie を読み直す」範囲であり、Cookie の主張（544 合格）そのものの陳腐化は寿命でしか縮まらない。
- **緊急時の全セッション無効化を提供する**。Data Protection キーリングのローテーション（またはセッション世代番号のバンプ）により、**発行済みの全認証セッション Cookie を即時無効化**する管理操作を用意する。これは (a) 管理者権限を緊急剥奪したい場合の Windows Cookie 失効手段、(b) アプリ資格情報漏洩時の発行済み app セッション失効手段、を兼ねる（[ADR-0011](0011-app-auth-failure-backoff.md) のバックオフ/レート制限は**ログイン経路**しか守らず発行済みセッションには効かない——田中の指摘への構造的回答）。操作自体は監査対象（決定 6）。
- **periodic 544 再検証（`OnValidatePrincipal` での AD グループ再問い合わせ）は採らない**。鮮度は上がるが、**能動セッションを DC 到達性に結合**させ、[ADR-0010](0010-admin-ui-authentication.md) が繰り返し守ってきた「DC 障害時も loopback で復旧できる」レジリエンス（決定 1・鈴木の一貫した観点）と衝突する（DC 障害で能動セッションが道連れに切れうる）。信頼ネットワーク前提・単一管理役割の製品にとって短寿命 + 緊急全失効で足りると判断する。要件が変われば「先送り」節のトリガで再評価する。

### 決定 3. 監査「成功」の意味づけの是正

今回の P0 を最も惑わせたのは、**認証は成功と記録（2008）されるのに利用者は入れない**状態だった（鈴木の指摘）。「成功」の定義がハンドシェイク完了に置かれていたことが観測性の罠になった。

- **`AdminLoginSucceeded`（2008）は、Negotiate ハンドシェイク完了時点ではなく `SignInAsync`（認証セッション発行）完了時点で発火させる**——「成功」＝「後続要求で使えるセッションが確立した」に一致させる。
- **「Negotiate は成功したが 544 判定不合格・またはセッション未発行/未伝播」を表す独立の監査事象を 3000 番台に新設する**（`security.md` §4.3 の採番方針で additive-only に確定）。将来また identity 伝播に欠陥が入っても、同じ「監査は成功・現場は 401 ループ」の罠がこの事象で可視化される。[ADR-0010](0010-admin-ui-authentication.md) 委任事項 6 に追記。

### 決定 4. Windows ログインの副作用化に対する CSRF / セッション固定対策

方式 B では `/admin/login/windows`（GET）が `SignInAsync` で Cookie を発行する**副作用**を持つ。Negotiate は透過認証のため、攻撃者ページが被害者ブラウザに当該 GET を強制すれば、被害者の Windows 資格情報で自動認証 → Cookie 植え付け（login CSRF / セッション固定の変種）が起こり得る（田中・クリスの指摘）。発行 Cookie が `SameSite=Strict` である点は緩和になるが、**Negotiate チャレンジ発火自体は SameSite で守られない**。

- **Negotiate 成立後、`SignInAsync` の前に antiforgery トークン付きの明示的な確認ステップ（POST）を挟む**。Cookie を発行する副作用を、利用者が意図した操作（確認ステップの POST）にのみ結び付ける。`/admin/login/app`・`/admin/logout` が既に `IAntiforgery.ValidateRequestAsync` で保護されているのと同水準に揃える。
- **セッション固定対策**: `SignInAsync` は未認証訪問時の既存 Cookie を確実に置換する（framework 既定で概ね担保されるが、明示的にテスト固定する）。
- login CSRF の脅威評価を lab 検証項目に含める。

### 決定 5. 方式弁別の単一クレーム依存の fail-closed

共通 Cookie にすると、Windows 由来セッションでも Cookie スキームの型を持つため、認可・監査の方式区別（`scheme=windows`/`scheme=app`）が **Cookie に焼き込む認証方式クレームの正しさに全依存**する（クリスの指摘: スキーム分離ならスキーム自体が構造的弁別子だった）。

- **認証方式クレーム（`scheme`）と、認可の根拠となる「管理セッション」クレームを Cookie に明示的に載せる**。監査「誰が」欄は**単一の関数（`AuditActorResolver`）で必ず `scheme + 主体名` を出す**——どこか 1 経路でも name 単独描画が残ると `DOMAIN\user` とアプリ名の衝突（[ADR-0010](0010-admin-ui-authentication.md) 決定 3・6）が再燃するため、「アプリ名 `DOMAIN\user`」でも区別できることをテスト固定する。
- **クレーム欠落時は fail-closed**: 認証方式クレーム・管理セッションクレームのいずれかを欠く Cookie は、管理者として認可しない（クレーム喪失で管理者昇格しない・監査で `scheme` を偽装/喪失できない）。
- **認可ポリシー `AdminPolicyName` は「管理セッションクレームを持つ Cookie」を正規の判定根拠にする**。`IsWindowsAdministrator`（`WindowsIdentity` 型 + 544 クレーム）はログイン時の Cookie 発行判定（`/admin/login/windows`）でのみ使い、Cookie 搬送後の判定では管理セッションクレームで通す——「アプリ認証は無効なのにアプリ認証済み(`IsAppAuthenticated`)と判定される」意味論の捻れ（リサ・田中の指摘）を、判定関数名・クレーム設計の是正で解消する。

### 決定 6. Cookie は自己完結型 + Data Protection キーリングの永続化・ACL

- **認証セッション Cookie は自己完結型**（Data Protection で暗号化・**サーバ側チケットストアを持たない**）とする。8515（管理リスナ）の認証負荷が受信データ経路（DB）に一切触れないことを構造で保証する（鈴木の「ロス 0 に波及しうる」懸念への回答）。
- **Data Protection キーリングの永続化先を明示し、`security.md` §5 の ACL 対象に含める**。キーがプロセス揮発だと**サービス再起動のたびに全 Cookie が無効化 = 全利用者再ログイン**になり、本製品の最終復旧が「手編集 → サービス再起動」（[ADR-0010](0010-admin-ui-authentication.md) 決定 1・委任事項 15）である以上、定常再起動でも毎回ログアウトが起きると運用ノイズになる（鈴木の指摘）。キーリングをデータルート配下に永続化し（仮想サービスアカウント `NT SERVICE\Yagura` が読み書きでき、`Users`/`Authenticated Users` の ACE を持たない `security.md` §5 の ACE 構成に載せる）、再起動をまたいでセッションが生存する設計にする。キーリングは新しい「保存時の秘密」であり、ACL の実機確認（`icacls`）を受け入れ条件に含める。
- なお決定 2 の緊急全失効は、この永続キーリングの**意図的なローテーション/破棄**として実装する（定常再起動では生存、緊急時のみ全失効、という非対称を作る）。

### 決定 7. Cookie 属性とリモート平文拒否（fail-closed）への依存の明示

- Cookie 属性は `HttpOnly=true`・`SameSite=Strict` を維持する。`SecurePolicy` は既定（`SameAsRequest`）を維持する——loopback HTTP 面（[ADR-0010](0010-admin-ui-authentication.md) 決定 4 で HTTPS 対象外）で認証を成立させるための意図的判断。
- **この `SameAsRequest` の安全性は「リモートバインドは HTTPS 必須で fail-closed」（[ADR-0010](0010-admin-ui-authentication.md) 決定 1・4、`security.md` §1 L-4 系）に load-bearing に依存する**ことを明示する。Cookie は host スコープで port を区別しないため、fail-closed が回帰すると Windows 由来 Cookie が平文で流れうる——この因果を本 ADR と `security.md` §2.5 に相互参照で記録する（田中の指摘）。`__Host-` プレフィックス（`Secure` 必須）が loopback HTTP と両立しないため使えないトレードオフも明記する。

## 帰結

- 良くなること:
  - **#252 の P0 が解消し、Windows 統合認証 + loopback 認証 opt-in の構成で管理 UI に入れるようになる**。[ADR-0012](0012-admin-https-cert-ui.md) のリモート HTTPS + Windows 認証も同時に回復する。
  - 選択式ログイン（[ADR-0010](0010-admin-ui-authentication.md) 委任事項 9）が意図どおり完成する。Negotiate チャレンジは Windows 経路に閉じ込められ、画面遷移のたびに資格情報ダイアログが出る挙動を避けられる。
  - 認証成立後を Cookie に一本化することで、circuit 境界への Negotiate identity 伝播という脆い経路（検証 3）を回避し、セッション模型が方式間で統一される。
  - 監査「成功」の意味づけ是正（決定 3）により、今回のクラスの障害（監査は成功・現場は入れない）が将来可視化される。
- 悪くなること（受け入れるトレードオフ）:
  - **方式 B は Windows 認可（544）のライブ失効反映を犠牲にする**（決定 2）。管理者権限の剥奪は Windows 由来 Cookie の絶対寿命（最大 1 時間・仮値）だけ遅延する。短寿命 + 緊急全失効で有界化するが、`security.md` §2.3「即時反映」の完全な達成は Windows 経路では放棄する。
  - **Data Protection キーリングという新しい「保存時の秘密」を持ち込む**（決定 6）。永続化先・ACL・キーローテーション運用が新たな管理対象になる。
  - 認証方式 2 系統の共存の複雑さ（[ADR-0010](0010-admin-ui-authentication.md) の恒常コスト）に、方式弁別の単一クレーム依存（決定 5 で fail-closed 化）が加わる。
  - `SecurePolicy=SameAsRequest` の安全性がリモート平文拒否（fail-closed）に load-bearing に依存する（決定 7）。
- 受け入れるリスク: Negotiate + Blazor Interactive Server の identity 伝播、Windows 由来 Cookie の circuit 稼働中の失効挙動は原理上 CI で検証できず lab 検証に依存する（受け入れ条件で担保）。

## 先送りにする場合の再評価トリガ

- **別 Cookie スキーム変種（選択肢 (c)）**: 次のいずれかで再評価する——①**発行済みセッションの独立失効要件が顕在化**した場合（例: 一方の方式のセッションだけを即時失効させたい運用要求）、②`security.md` §3 の役割粒度が「閲覧/管理」2 段を超えて分化した場合（「担体を共通化しても capability が同じだから安全」という方式 B の分離論法は役割単一を前提にする——田中の条件付き不変条件の指摘）。
- **periodic 544 再検証（決定 2 で不採用）**: 能動セッションを DC 到達性に結合してでも失効鮮度を上げるべき要件（例: 高機微環境での即時剥奪要求）が独立利用者から反復した場合に再評価する。その際は DC 障害時のレジリエンス（[ADR-0010](0010-admin-ui-authentication.md) 決定 1）との両立方法を同時に設計する。
- **Windows 由来 Cookie 寿命の仮値（1 時間）**: 実運用の失効反映の体感と「開きっぱなしで切れる」不便（佐藤・鈴木）の実測で SEC 番号として確定する。設定可能にするか固定かも実装 PR で判断する。

## 受け入れ条件（[conventions.md](../development/conventions.md) の実環境依存機能の lab 検証原則）

lab 実機で 1 周し、記録を実装 PR body に残す（Negotiate + Blazor Interactive Server の identity 伝播は CI で原理的に検証不可）:

1. **「Windows 統合認証のみ + `RequireForLoopback=true`」で ログイン → `/admin` 到達（200）→ 対話的操作（circuit 確立後も認証維持）** まで通る。
2. **[ADR-0012](0012-admin-https-cert-ui.md) リモートバインド + Windows 認証**の経路でも同様に通る（loopback だけにしない——佐藤の指摘。P0 が波及する面を実機で確認）。
3. **セッション中に対象ユーザーを `BUILTIN\Administrators` から除外 → 次の管理操作が有界時間内（Windows Cookie 絶対寿命以内）に拒否される**。緊急全失効（決定 2）で即時失効できることも確認する。
4. **DC 到達不能時の `/admin/login/windows` 挙動**（ハングせずタイムアウト・明確な失敗・監査/イベントログ・UI フィードバック）——[ADR-0010](0010-admin-ui-authentication.md) 委任事項 13 を方式 B 経路へ再スコープ（鈴木の指摘: 544 グループ所属解決が DC 参照を要してハングし得る、はハンドシェイクのタイムアウトとは別軸）。
5. **Windows 由来 Cookie が circuit 稼働中（SignalR 常時接続下）に絶対寿命で失効するケース**の挙動（切れた瞬間の画面表示・入力保全・再ログイン後の遷移先）——`SlidingExpiration` が SignalR 下で更新されず「開きっぱなしで失効」する可能性の実機確認（佐藤・鈴木）。
6. **サービス再起動後に既発行 Cookie が生存する**（Data Protection キーリング永続化。決定 6）。**緊急全失効操作で全 Cookie が無効化される**。
7. データルート配下の Data Protection キーリングの ACL が `security.md` §5 の ACE 構成に一致する（`icacls` 確認）。

HTTP 層の回帰テスト（既存 `AdminAuthenticationFailClosedRegressionTests` が「プロセス起動までしか検証していなかった」統合ギャップを塞ぐ）:

- (i) Windows 単独構成で Windows ログイン後に `/admin` が 200 を返す（Cookie が実発行される）。
- (ii) その Cookie が `scheme=windows` の認証方式クレームを持つ。
- (iii) 監査（2008・および決定 3 の 3000 番台）が `scheme=windows` を維持する。
- (iv) Cookie 搬送要求の認可が `WindowsIdentity` 型に依存せず「管理セッションクレーム」で通る（決定 5）。
- (v) **負方向**: `/admin/login/windows` を経ない直接の `/admin` 要求は `/admin/login`（選択式）へ誘導され、Negotiate 自動チャレンジに落ちない（方式 A の再混入を防ぐガード）。
- (vi) 認証方式クレーム/管理セッションクレームを欠く Cookie は管理者として認可されない（決定 5 の fail-closed）。

**HTTP 回帰テストの被覆境界を線引きする**（クリスの指摘）: ドメイン非参加 CI では Negotiate ハンドシェイクの実成功を模擬せざるを得ないため、「未登録既定スキームによる 401 ループ」という**本質の end-to-end 再現は lab（受け入れ条件 1・2）に落ちる**。回帰テストが模擬する範囲（Cookie 発行後の認可・クレーム・監査・負方向誘導）と、lab が担う範囲（実 Negotiate → Cookie 発行 → circuit 伝播）を実装 PR に明記する。

## 委任事項の一覧（追跡用）

| # | 委任事項 | 委任先 | 内容 |
|---|---|---|---|
| 1 | 単一 Cookie セッションの実装（決定 1） | 実装 PR + `security.md` §2.4 | `/admin/login/windows` の `SignInAsync` 化、Cookie スキームを「いずれかの認証方式が有効なら登録」、既定認証**かつ**チャレンジスキームの Cookie 固定、スキーム名の中立化（`YaguraAdminAuth` 等）。§2.4 の「共存」記述（無効化される文「セッション・トークンは方式間で共有しない」・「専用 Cookie スキーム」の限定語）を本 ADR に合わせて是正 |
| 2 | Windows 由来 Cookie の寿命分離と緊急全失効（決定 2） | 実装 PR + `security.md` §2.3/§7 確定待ち | Windows Cookie の絶対寿命上限（仮値 1h・sliding 抑制）を SEC 番号で採番、緊急全失効（DP キーローテーション）の管理操作、§2.3「即時反映」の Windows Cookie 適用外の明記 |
| 3 | 監査「成功」の意味づけ是正（決定 3） | 実装 PR + `security.md` §4.3 | 2008 を `SignInAsync` 時点へ移動、「Negotiate 成功だが 544 不合格/セッション未発行」の 3000 番台事象を additive-only で採番 |
| 4 | Windows ログインの CSRF/セッション固定対策（決定 4） | 実装 PR | Negotiate 成立後の antiforgery 付き確認 POST、`SignInAsync` の既存 Cookie 置換のテスト固定、login CSRF の lab 脅威評価 |
| 5 | 方式弁別の単一クレーム fail-closed（決定 5） | 実装 PR | 認証方式クレーム・管理セッションクレームの設計、`AuditActorResolver` の単一関数化、クレーム欠落時 fail-closed、`AdminPolicyName` の判定根拠の是正、`WindowsIdentity` 型ゲートを login 時のみに限定 |
| 6 | Data Protection キーリングの永続化・ACL（決定 6） | 実装 PR + `security.md` §5 | 自己完結型 Cookie（サーバ側ストアなし）、キーリングのデータルート配下永続化と ACE 構成、`icacls` 実機確認、緊急全失効との整合 |
| 7 | Cookie 属性と fail-closed 依存の明記（決定 7） | 実装 PR + `security.md` §2.5 | `SecurePolicy=SameAsRequest` の安全性がリモート平文拒否に load-bearing であることの相互参照、`__Host-` 不採用のトレードオフ明記 |
| 8 | 利用者向けドキュメント（[ADR-0010](0010-admin-ui-authentication.md) 委任事項 11 に合流） | 利用者向けドキュメント | ①セッション切れ時の挙動（絶対寿命・入力保全・再ログイン後の遷移先・選択式ログインは有効な方式のみ表示——佐藤）、②Windows 権限剥奪の反映遅延 = Cookie 寿命、③今回の P0 実例と結びつけたブレークグラス推奨の強調（この opt-in 構成は認証不具合で管理 UI ごと入れなくなり得る） |

## 却下した代替案

- **方式 A（透過 SSO）/ A + Cookie 変換**: 検討した選択肢のとおり。選択式ログインの破壊・circuit 伝播の脆さ・login CSRF を理由に却下（544 ライブ再評価の利点は決定 2 で別途扱う）。
- **方式 B の変種（別 Cookie スキーム）**: 現時点の役割単一・独立失効需要の不在を理由に却下するが、「先送り」節に再評価トリガを残す（恒久却下にしない）。
- **periodic 544 再検証**: DC 到達性への結合がレジリエンスと衝突するため決定 2 で不採用（再評価トリガあり）。
