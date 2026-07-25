# フォワーダ MSI アップロードの受け入れ lab 検証手順 — ADR-0020 決定 5 lab ①〜④

> **未実施**（2026-07-24 作成）。本手順は [ADR-0020](../../docs/adr/0020-forwarder-msi-upload.md)
> 決定 5 が受け入れ条件とする lab 実機 4 項目（実装 = PR #431。Issue #283）を消化するためのもの。
> 実施したら結果を Issue #283 へコメントし、実測値（icacls 出力・イベントログ・監査記録）を
> security.md §5.1.1 へ反映する PR を起こす。本ファイル冒頭のこの注記も
> 「実施済み（日付・環境）」へ書き換えること。

**実施環境**: AD lab（`yagura.test`）。Yagura は **PR #431 マージ後のビルド**（または v0.5.1 以降の
リリース MSI）をインストール済みであること。項目①の gMSA 側・項目④には gMSA
（例 `YAGURA\gmsaYagura$`。[gmsa-service-account-lab-procedure.md](gmsa-service-account-lab-procedure.md)
の資産）と、**#426 の修正（PR #427。remember-property の AppSearch clobber）を含むインストーラ**が必要。
機能はホスト内の ACL・ファイル I/O 検証が主体のため DC 単体でも実施できるが、gMSA 切替を含む
項目④はメンバーサーバでの実施を推奨する（ADR-0010 委任 13 の「DC 同居構成の代表性」の教訓）。

## なぜ CI ではなく lab なのか

conventions.md「実環境依存の機能は lab 検証を受け入れ条件に含める」の適用。CI（ubuntu/Windows の
テストプロセス）では次が原理的に検証できない:

| 対象 | CI で検証できない理由 |
|---|---|
| `icacls` による ACE 付与・撤去・非波及 | CI のテストプロセスは管理者権限で走り、実サービスアカウント（仮想 SA / gMSA）の ACL 実効挙動を再現できない（SEC-13 と同型の権限非対称） |
| MSI の ProductVersion 読み取り | `msi.dll`（Windows Installer）と実 MSI ファイルが必要。単体テストは読み取り関数を偽実装で差し替えている |
| アカウント切替レールと手動 ACE の相互作用（項目④） | インストーラの deferred CA（`icacls /remove`）の実挙動はインストール実行時にしか観測できない |
| ACL 検出の周期監視（項目③） | `WindowsIdentity` のトークングループと実 ACL の突合が本番実行アカウントで走る必要がある |

## 実施の原則

- 各手順の実測出力（`icacls`・`Get-WinEvent`・`audit.jsonl` 抜粋）を**そのまま記録**する（要約しない）
- 期待と異なる挙動は**その場で判定せず記録して持ち帰る**（特に項目④は「どちらの挙動でも設計上の帰結が変わる」——後述）

---

## 準備（共通）

### P-1. 前提条件を構成する（fail-closed の確認を兼ねる）

まず**意図的に前提条件を欠いた状態**で機能を有効化し、fail-closed（1032）を確認する:

```powershell
# %ProgramData%\Yagura\yagura.json に追記（認証未構成のまま）
# "Admin": { "ForwarderKit": { "MsiUpload": { "Enabled": "true" } } }
Restart-Service Yagura   # → 起動失敗すること
Get-WinEvent -ProviderName Yagura -MaxEvents 5 | Format-List Id, Message
```

**合否**: サービスが起動せず、イベント ID **1032** に「欠けている条件の列挙」と
「`Admin:ForwarderKit:MsiUpload:Enabled` を false に戻す」誘導が含まれること。

次に正規の順序で構成する:

1. `/admin/auth-setup` でアプリ独自認証を有効化し、管理者アカウントを作成する
   （**ブレークグラス**——Windows 統合認証を主に使う場合もアプリ独自アカウントを 1 つ残す）
2. `Admin:Authentication:RequireForLoopback = true` を有効化する
3. `yagura.json` の `Admin:ForwarderKit:MsiUpload:Enabled` を `"true"` にして再起動する
   （現時点で本キーは手編集のみ。起動成功すること）

### P-2. 検証用 MSI を用意する

[packages.fluentbit.io](https://packages.fluentbit.io/) から検証済み版（`5.0.8`）の
`fluent-bit-5.0.8-win64.msi` と、**別版 1 つ**（置換・ハッシュ不一致フローの検証用。例
`fluent-bit-4.0.14-win64.msi`）を作業端末に取得しておく。

---

## 項目①: ACE 付与・撤去手順の実機確認（仮想 SA・gMSA 両構成）

### ①-1. 未開放状態の表出

配置フォルダが既定 ACL（サービスアカウント読み取りのみ）のまま `/admin/forwarder-kit` を開く。

**合否**: アップロード欄に「書き込み経路がまだ開放されていません」と、**実効実行アカウント名入り**の
`icacls` 付与コマンドが表示されること（仮想 SA 構成なら `NT SERVICE\Yagura`）。

### ①-2. 付与と開放の検出

```powershell
icacls "$env:ProgramData\Yagura\forwarder" /grant "NT SERVICE\Yagura:(OI)(CI)(M)"
icacls "$env:ProgramData\Yagura\forwarder"   # 出力を記録
```

画面を再読み込みする。**合否**: 「書き込み経路が開放されています」の常時表示に切り替わり、
ファイル選択・アップロードボタンが現れること。

### ①-3. 非波及の確認（付与 ACE が本フォルダ限定であること）

```powershell
icacls "$env:ProgramData\Yagura"          # データルート本体: (M) のまま変化なしを確認
icacls "$env:ProgramData\Yagura\audit"    # 監査領域: 変化なしを確認
icacls "$env:ProgramData\Yagura\forwarder\*" 2>$null   # 配下ファイルへの継承のみ
```

**合否**: 付与が `forwarder` 配下に閉じ、他領域の ACE が一切変化していないこと。

### ①-4. 撤去

```powershell
icacls "$env:ProgramData\Yagura\forwarder" /remove:g "NT SERVICE\Yagura"
icacls "$env:ProgramData\Yagura\forwarder" /grant "NT SERVICE\Yagura:(OI)(CI)(R)"
icacls "$env:ProgramData\Yagura\forwarder"   # 既定（R）へ戻ったことを記録
```

**合否**: 画面が未開放案内へ戻ること。**削除ボタン側も**未開放案内に従うこと
（配置済み MSI がある状態で撤去し、削除操作が I/O エラーではなく案内になることを確認）。

### ①-5. gMSA 構成での再実施

gMSA 構成（[gmsa-service-account-lab-procedure.md](gmsa-service-account-lab-procedure.md) で切替済み）
で ①-1〜①-4 を繰り返す。付与先は `YAGURA\gmsaYagura$`。

**合否**: 画面の付与コマンド案内が **gMSA 名で表示される**こと（実効実行アカウントからの導出——
ADR-0020 決定 2・security.md §5.2 の原則）。付与・検出・撤去が仮想 SA と同様に成立すること。

---

## 項目④: アカウント切替レールと手動付与 ACE の実測

**検証する仮説**: 仮想 SA へ手動付与した書き込み ACE が、gMSA への切替
（`msiexec ... YAGURA_SERVICE_ACCOUNT=...`）の除去レール（deferred CA の `icacls /remove`）で
**どう扱われるか**は未実測である（ADR-0020 決定 5 lab ④）。設計はどちらの結果でも安全側に
成立するが、**検出仕様（委任 7）の期待値を実測に合わせる**必要がある。

1. 仮想 SA 構成で `forwarder` へ手動 ACE（`(OI)(CI)(M)`）を付与し、`icacls` 出力を記録する
2. gMSA へ切替する（#426 修正済みインストーラで
   `msiexec /i Yagura-x64.msi /qn YAGURA_SERVICE_ACCOUNT="YAGURA\gmsaYagura$" ...`。
   手順詳細は gmsa-service-account-lab-procedure.md）
3. 切替後の `icacls "$env:ProgramData\Yagura\forwarder"` を記録し、次を判定する:
   - **仮想 SA の手動 ACE（M）が除去された** → 除去レールが手動 ACE も一掃する。残骸検出は
     空振り（安全側）で確定。security.md §5.1.1 と ADR-0020 委任 7 の記述を「切替レールが
     除去する」へ更新する
   - **残置された** → 想定どおり残骸になる。稼働中の周期監視（1033——gMSA 構成では旧 ACE は
     gMSA のトークンに該当しないため 1033 の対象外になる点に注意）と
     `ServiceAccountStartupInspector` の残置照合で運用者に見えるかを併せて確認し、見えない場合は
     検出仕様の拡張（委任 7）を実装課題として起票する
4. （任意）逆方向（gMSA → 仮想 SA）でも同じ観測を行う

---

## 項目②: 認証有効構成でのアップロード E2E

前提: P-1 構成済み・ACE 付与済み（①-2）・アプリ独自認証でログイン済み。

| ケース | 手順 | 合否基準 |
|---|---|---|
| A. 正常配置 | `fluent-bit-5.0.8-win64.msi` を選択 →「アップロードして内容を確認」 | 確認画面に版 `5.0.8`・SHA256・**公式配布 SHA256 と一致** が表示される |
| B. ステージング不可視 | ケース A の確認画面のまま `Get-ChildItem "$env:ProgramData\Yagura\forwarder"` | `.uploading-*.msi` が実在する一方、画面の検出結果は変化していない（生成にも現れない） |
| C. 確定と監査 | 「配置を確定」→ `audit.jsonl` とイベントログを確認 | 検出結果に反映。**2026** が記録され、`AuthenticatedPrincipal` に操作者名・`Detail` に `sha256=`・`connection=loopback` が入る |
| D. 生成突合 | MSI 同梱で ZIP を生成し `GENERATED.txt` を開く | `GENERATED.txt` の MSI SHA256 = 2026 の `sha256=` = 2005（生成監査）の `msiSha256` が一致する |
| E. 置換 + 二段階確認 | 別版（4.0.14）をアップロード | 旧/新 SHA256 が並記され、**置換確認 + 公式ハッシュ不一致確認の両チェック**を入れないと確定できない。確定後、フォルダに新版 1 つだけが残る（単一化）。2026 に `replacedSha256=` が入る |
| F. 中止の監査 | もう一度 stage し「中止（アップロードを破棄）」 | ステージングが消え、**3014**（`reason=cancelled-by-user`）が記録される |
| G. 削除 | 「配置済み MSI を削除」→ 確認パネル → 確定（Destructive ダイアログ） | **2027** に `deletedSha256=`（削除前 SHA256）が入り、フォルダから消える |
| H. 拒否系 | MSI でないファイル（例 .txt を .msi に改名）をアップロード | 「版（ProductVersion）を読み取れませんでした」で拒否。**3014**（`reason=product-version-unreadable`） |
| I. サイズ事前検査 | `fsutil file createnew big.msi 209715200`（200 MB）を選択 | **送信前に**画面でサイズ超過が表示される（ネットワーク送信が発生しない） |

イベントログ・監査の確認コマンド例:

```powershell
Get-WinEvent -ProviderName Yagura -MaxEvents 30 | Where-Object Id -in 2026,2027,3014 | Format-List Id, Message
Get-Content "$env:ProgramData\Yagura\audit\audit-$(Get-Date -Format yyyyMMdd).jsonl" -Tail 20
```

（任意）リモート HTTPS 構成（ADR-0012）がある場合はリモート経由でも 1 件アップロードし、
2026 の `connection=remote` を確認する。

---

## 項目③: ACL 検出の二系統が稼働中に通知に至ること

### ③-1. 乖離警告（1033）——稼働中検出

1. `Admin:ForwarderKit:MsiUpload:Enabled` を `"false"` に戻して再起動する（ACE は撤去済みの状態から開始）
2. **サービスを再起動せずに** `icacls ... /grant "NT SERVICE\Yagura:(OI)(CI)(M)"` で ACE を付与する
3. 周期監視（1 分間隔）を待つ:

```powershell
Get-WinEvent -ProviderName Yagura -MaxEvents 10 | Where-Object Id -eq 1033 | Format-List TimeCreated, Message
```

**合否**: **再起動を要さず** 2 分以内に 1033（警告）が出ること。本文に配置フォルダパスと
撤去の誘導が含まれること。15 分後に再表示されること（抑制窓）も 1 回確認する。

### ③-2. 乖離警告（1033）——起動時検出

ACE を付与したまま `Restart-Service Yagura`。**合否**: 起動直後（周期を待たず）に 1033 が出ること。

### ③-3. 開放継続の通知（1034）

1. ACE を撤去 → P-1 の正規構成（機能有効）へ戻し、ACE を付与し直す
2. **そのまま 24 時間以上放置**する（継続判定の仮値 = 24 時間。翌日に確認）

```powershell
Get-WinEvent -ProviderName Yagura -MaxEvents 10 | Where-Object Id -eq 1034 | Format-List TimeCreated, Message
```

**合否**: 24 時間経過後の周期で 1034（**情報レベル**）が出ること。本文に経過日数と
「常置運用なら確認のみで構わない」旨が含まれること。（7 日抑制窓の再表示確認は任意）

---

## 実施後の後始末と記録

1. 検証用 ACE を撤去し、既定（読み取りのみ）へ戻す（①-4 のコマンド）。検証用の配置 MSI・
   設定（`MsiUpload:Enabled` 等)も運用方針に合わせて整理する
2. 実測出力一式を **Issue #283 へコメント**する（各項目の合否 + `icacls`/イベントログ/監査の生出力）
3. security.md §5.1.1 への実測反映（項目④の結果次第で委任 7 の記述更新を含む）と、
   本ファイル冒頭注記の「実施済み」化を同一 PR で行う
4. 全項目合格なら、ADR-0020 決定 5 の受け入れ条件は CI 側（実装済み）と合わせて完了となる
   （Issue #283 のクローズ判断はオーナー。クローズ時に委任 8——効果測定の追跡 Issue——を起票する）
