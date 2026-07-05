namespace Yagura.Bench.LoadGeneration;

/// <summary>
/// 負荷生成器の実行結果（Issue #60「送信数を正確に把握できる」の実体）。
/// </summary>
/// <param name="RunId">送出に使った実行 ID。</param>
/// <param name="AttemptedCount">
/// 送出を試みた総数（成功・失敗を問わない。「送出した数を自前カウント」の基準値）。
/// </param>
/// <param name="SucceededCount">
/// 送出 API 呼び出し（<c>SendAsync</c>）が例外を投げずに完了した数。fire-and-forget にせず、
/// 各送出の完了を待って計上する（Issue #60「fire-and-forget にしない」）。
/// </param>
/// <param name="FailedCount">
/// 送出 API 呼び出しが例外で失敗した数（送信側ソケットバッファ溢れ等）。
/// <see cref="SucceededCount"/> + <see cref="FailedCount"/> = <see cref="AttemptedCount"/>。
/// </param>
/// <param name="Elapsed">送出開始から完了までの経過時間。</param>
/// <param name="SenderSocketCount">実際に使用した送信側ソケット数。</param>
public sealed record LoadGeneratorResult(
    string RunId,
    long AttemptedCount,
    long SucceededCount,
    long FailedCount,
    TimeSpan Elapsed,
    int SenderSocketCount)
{
    /// <summary>
    /// 検証器が使う「送信数」の基準値。送出 API が成功を返した数のみを送信数として扱う
    /// （呼び出し自体が失敗した分は送信されていないため、検証の母数から除外する。
    /// architecture.md §5.1「送信数 = 保存件数 + 全カウンタの合計」の左辺はこの値を使う）。
    /// </summary>
    public long SentCount => SucceededCount;
}
