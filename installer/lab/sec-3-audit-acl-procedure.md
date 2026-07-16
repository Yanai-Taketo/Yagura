# SEC-3 実機検証手順 — 監査記録領域の追記専用 ACE と削除権限の分離

security.md §4.2・§5 / SEC-3（確定待ち一覧）/ Issue #261 の lab 検証手順。
**実施環境**: Yagura サービスがインストール済みの lab（仮想サービスアカウント `NT SERVICE\Yagura` が存在すること。開発機の単体では仮想サービスアカウントの指定形式を検証できない）。

## 検証する仮説

1. **追記専用 ACE の成立可否**: 監査記録ディレクトリ（`%ProgramData%\Yagura\audit`）に対し、サービスアカウントへ「新規ファイル作成 + 既存ファイルへの追記」のみを許し「既存データの変更・削除」を許さない ACE 構成が Windows の ACL で表現でき、かつ Yagura の監査書き込み（`FileMode.Append`）が動作し続けるか
2. **削除（ローテーション）権限の分離**: 上記構成でサービス自身の保持期間削除（`AuditRetentionScheduler`）が `UnauthorizedAccessException` で拒否され、警告ログ（`[audit-retention-acl-denied]`）が出ること（= 分離構成では削除が運用手順側の責務になることの実機確認）

## 手順

### 1. 現状 ACL の記録（変更前ベースライン）

```powershell
icacls "$env:ProgramData\Yagura\audit"
```

出力を記録する（security.md §5 への記録用。継承状態の確認）。

### 2. 追記専用 ACE の適用

```powershell
# 継承を切り、明示 ACE のみにする（/inheritance:r）
icacls "$env:ProgramData\Yagura\audit" /inheritance:r

# 管理者・SYSTEM はフルコントロール（ACL 管理・削除の運用主体）
icacls "$env:ProgramData\Yagura\audit" /grant "BUILTIN\Administrators:(OI)(CI)F"
icacls "$env:ProgramData\Yagura\audit" /grant "NT AUTHORITY\SYSTEM:(OI)(CI)F"

# サービスアカウントには「読み取り + ファイル作成 + 追記」のみ
# RD=ディレクトリ列挙 / X=走査 / RA/REA=属性読取 / WD=ファイル作成(ディレクトリに対する
# FILE_ADD_FILE) / AD=追記(ファイルに継承されると FILE_APPEND_DATA)
icacls "$env:ProgramData\Yagura\audit" /grant "NT SERVICE\Yagura:(OI)(CI)(RD,RA,REA,X,WD,AD,RC)"
```

> **検証ポイント（仮説 1 の核心）**: `WD`（FILE_WRITE_DATA）はディレクトリでは「ファイル作成」、ファイルへ継承されると「既存データの上書き」を意味する二重性がある。`FileMode.Append` は FILE_APPEND_DATA のみで成立するはずだが、.NET の `FileStream` が要求するアクセス右の実挙動（GENERIC_WRITE を要求しないか）は**実機で確認するまで確定しない**。追記が失敗する場合は、ディレクトリ用 ACE（ファイル作成）とファイル継承用 ACE（追記のみ）を `(CI)` / `(OI)(IO)` で分離する第 2 案を試す:
> ```powershell
> icacls "$env:ProgramData\Yagura\audit" /remove "NT SERVICE\Yagura"
> icacls "$env:ProgramData\Yagura\audit" /grant "NT SERVICE\Yagura:(CI)(RD,RA,REA,X,WD,RC)"
> icacls "$env:ProgramData\Yagura\audit" /grant "NT SERVICE\Yagura:(OI)(IO)(RA,REA,AD,RC)"
> ```

### 3. 動作確認

1. **追記が生きていること**: 管理 UI で監査対象の操作（設定変更等）を 1 回行い、当日の `audit-yyyyMMdd.jsonl` に行が増えること・イベントログに併記されることを確認
2. **改変・削除が拒否されること**: `psexec -i -u "NT SERVICE\Yagura"` は仮想サービスアカウントでは使えないため、確認はサービス経由で行う——
   - 期限切れ相当のダミーファイルを置く: `New-Item "$env:ProgramData\Yagura\audit\audit-20200101.jsonl"; (Get-Item ...).LastWriteTimeUtc = "2020-01-02"`（管理者権限で実施）
   - サービスを再起動し（起動時に保持期間削除が 1 回走る）、①ダミーファイルが**残っている**こと ②イベントログ/ログに `[audit-retention-acl-denied]` 警告が出ることを確認
3. **分離なし構成（既定）との対比**: ACL を手順 1 のベースラインへ戻し（`icacls ... /reset` + 必要なら継承再有効化）、サービス再起動でダミーファイルが**削除され**、イベントログに 2015（保持期間削除の実行）が記録されることを確認

### 4. 結果の記録

- 最終的に成立した ACE 構成の `icacls` 出力全文を security.md §5 へ記録する（SEC-3 の確定）
- 成立しなかった場合（追記と作成を分離できない等）は、その事実と「一次の耐タンパ線はイベントログ併記」（security.md §4.2 の既存の限界明示）を security.md へ記録して SEC-3 を閉じる
- 分離構成を採用可能とする場合、期限切れファイルの手動削除手順を operations.md（起筆時）へ申し送る

## 備考

- 本手順は ACL を変更するため、**lab 環境でのみ実施**する（実運用環境の ACL を直接いじらない）
- 削除機構側（Issue #261）は分離構成を前提に設計済み: 日次ファイル分割により既存ファイルへの書き換え・rename が不要で、ACL 拒否時は日次 1 回の警告に留まる（反復ノイズなし）
