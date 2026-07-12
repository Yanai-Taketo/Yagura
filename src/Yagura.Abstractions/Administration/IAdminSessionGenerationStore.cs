namespace Yagura.Abstractions.Administration;

/// <summary>
/// 認証セッションの「世代番号」を保持する契約（ADR-0013 決定 2）。
/// </summary>
/// <remarks>
/// <para>
/// 緊急時の全セッション無効化を、Data Protection キーの破壊（非可逆・レース・復旧困難）ではなく
/// <b>世代番号のバンプ</b>で実現するための状態源。認証セッション Cookie には発行時点の世代番号が
/// クレーム（<c>yagura:session_gen</c>）として焼き込まれ、各要求で <see cref="CurrentGeneration"/> と
/// fail-closed で照合される（旧世代の Cookie は無効）。
/// </para>
/// <para>
/// <b>永続化と非対称</b>: 世代番号はデータルート配下に永続化する（security.md §5 の ACL 対象）。
/// 定常のサービス再起動では世代番号が不変ゆえ既発行セッションは生存し、緊急時のみ
/// <see cref="Bump"/> で世代 +1 して旧世代の全 Cookie を即時無効化する——「再起動では生存・緊急時のみ全失効」
/// の非対称を crypto 層に触れずに実現する。<see cref="Bump"/> は DC 非依存のローカル操作であり、
/// DC 障害中でも実行できる（緊急剥奪の最後の砦）。ログイン経路は殺さない（既発行セッションのみ無効化）。
/// </para>
/// </remarks>
public interface IAdminSessionGenerationStore
{
    /// <summary>現在のセッション世代番号。認証セッション Cookie の世代クレームと各要求で照合する。</summary>
    int CurrentGeneration { get; }

    /// <summary>
    /// 世代番号をバンプ（+1）して永続化し、新しい世代番号を返す（ADR-0013 決定 2 の緊急全失効）。
    /// これにより旧世代の全認証セッション Cookie が次要求で fail-closed に無効化される。
    /// </summary>
    int Bump();
}
