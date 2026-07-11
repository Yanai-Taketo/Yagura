namespace Yagura.Abstractions.Administration;

/// <summary>
/// 管理リモート HTTPS（ADR-0010 Phase 2 決定 4）用に選択可能な証明書を、Windows 証明書ストア
/// （<c>LocalMachine\My</c>）から列挙する read-only 契約（ADR-0012 決定 2）。
/// </summary>
/// <remarks>
/// <para>
/// 拇印の手入力・<c>yagura.json</c> 手編集を不要にするための「証明書ストア列挙 + 選択」UI
/// （ADR-0012 決定 1・2）の基盤。<b>副作用のない読み出し面</b>であり、書き込み系の
/// <see cref="IYaguraWriteService"/> は継承しない（ADR-0012 決定 3「列挙・検証は副作用なし」）。
/// 秘密鍵 ACL 付与は本契約では行わない（決定 3 は lab 実測で (b) = UI は読取検証と誘導のみ、に確定）。
/// </para>
/// <para>
/// Windows 実体（<c>Yagura.Host</c>）はストアを <c>OpenFlags.ReadOnly</c> で開く。契約を
/// <c>Yagura.Abstractions.Administration</c> に置くのは、呼び出し元が UI 層（Blazor 画面）であり
/// architecture.md §1.1「UI は Host アセンブリを参照できない／UI 用の抽象は Abstractions に置く」に
/// 従うため（<see cref="IAdminAuthenticationAdminService"/> と同一の層配置。ADR-0012 影響範囲）。
/// 単体テストは偽実装で決定的に検証し、実ストア接触は統合／E2E に限定する（ADR-0012 決定 5）。
/// </para>
/// <para>
/// <b>残存リスク（ADR-0012 決定 8）</b>: 既定（loopback 認証 opt-in 無効）では、本列挙 API は
/// 無認証で到達可能であり、機内の serverAuth 証明書インベントリ（サブジェクト CN・発行者・
/// 有効期限・拇印）を取得できる。列挙範囲を serverAuth EKU + 秘密鍵ありに最小化し、返却は
/// 表示に必要な最小フィールドに限ることで、無関係証明書のサブジェクト露出を抑える。
/// </para>
/// </remarks>
public interface IAdminCertificateStoreReader
{
    /// <summary>
    /// <c>LocalMachine\My</c> の証明書のうち、<b>serverAuth 拡張キー使用法（EKU
    /// <c>1.3.6.1.5.5.7.3.1</c>）を持ち、かつ秘密鍵を持つ</b>ものを列挙する。
    /// </summary>
    /// <remarks>
    /// serverAuth EKU 不適合・秘密鍵なしの証明書は、管理リモート HTTPS に使えないため
    /// <b>列挙から除外</b>する（用途違いのワンクリック誤選択を構造的に抑える——ADR-0012 決定 2）。
    /// 期限切れ・サービスアカウントが秘密鍵を読めないものは除外せず、警告フラグ
    /// （<see cref="AdminCertificateCandidate.IsExpired"/>・
    /// <see cref="AdminCertificateCandidate.IsPrivateKeyReadable"/>）付きで返す
    /// （「なぜこの証明書が使えないか」を UI で説明できるよう、無警告で選べる状態を作らない）。
    /// </remarks>
    /// <returns>選択候補の一覧（有効期限の新しい順 → 拇印順に安定ソート。0 件もあり得る）。</returns>
    IReadOnlyList<AdminCertificateCandidate> ListServerAuthCertificates();
}

/// <summary>
/// 管理リモート HTTPS の証明書選択 UI に表示する 1 証明書の最小メタ情報（ADR-0012 決定 2
/// 「返却フィールドは表示に必要な最小限」）。秘密鍵・エクスポート可能な秘密情報は含めない。
/// </summary>
/// <param name="Thumbprint">拇印（SHA-1。選択時にそのまま <c>Admin:Https:CertificateThumbprint</c> へ設定する値）。</param>
/// <param name="SubjectCommonName">サブジェクトのコモンネーム（CN）。取得できない場合はサブジェクト DN 全体。</param>
/// <param name="Issuer">発行者の識別名。</param>
/// <param name="NotBefore">有効期間の開始。</param>
/// <param name="NotAfter">有効期間の終了。</param>
/// <param name="IsExpired">現在時刻が有効期間外なら true（期限切れ／未来証明書。警告表示の対象）。</param>
/// <param name="IsPrivateKeyReadable">
/// サービスアカウント（実行中プロセスの identity）が当該秘密鍵を実際に読み取れるなら true。
/// false の場合、選択しても再起動後に HTTPS リスナが縮小継続する（秘密鍵の読取権限付与が必要）。
/// UI は false のとき <c>certlm.msc</c> での付与手順へ誘導する（ADR-0012 決定 3 = (b)）。
/// </param>
public sealed record AdminCertificateCandidate(
    string Thumbprint,
    string SubjectCommonName,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool IsExpired,
    bool IsPrivateKeyReadable);
