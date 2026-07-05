using System.Globalization;
using System.Text;

namespace Yagura.Bench.LoadGeneration;

/// <summary>
/// ベンチが送出する syslog メッセージ（RFC 5424 形式）の組み立て（Issue #60）。
/// </summary>
/// <remarks>
/// <para>
/// <b>RFC 5424 形式を採用する理由</b>: src/Yagura.Ingestion/Parsing/SyslogParser.cs の
/// <c>IsRfc5424</c> は「PRI 直後が VERSION "1" + SP」であることで 5424 と判別する。本ファクトリは
/// この判別条件を満たす HEADER（VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID SP
/// STRUCTURED-DATA）を組み立て、MSG 部にベンチ固有の連番マーカーを埋め込む。
/// </para>
/// <para>
/// <b>連番の埋め込み</b>: MSG 本文の先頭に <c>yb-{RunId}-{Sequence}</c>
/// （<see cref="SequenceMarkerPrefix"/>）を付与する。RunId はベンチ実行ごとに一意な短い識別子
/// （同一 SQLite/SQL Server を使い回すシナリオ間で前回実行分の残留レコードと混同しないため）、
/// Sequence は 0 始まりの送出順連番——検証器（Verification 配下）はこの連番の欠落を突合の入力
/// （「どれが落ちたか」の特定）に使う。連番はメッセージ全体ではなく MSG 部の先頭に置く（AppName/
/// ProcId 等の HEADER フィールドは固定値でよく、パーサの解析対象を広くカバーする狙いがある）。
/// </para>
/// </remarks>
public static class BenchMessageFactory
{
    /// <summary>連番マーカーの接頭辞。検証器はこの接頭辞で自ベンチ実行分のレコードのみを対象にする。</summary>
    public const string SequenceMarkerPrefix = "yb";

    /// <summary>RFC 5424 の facility=local0(16)・severity=informational(6) の固定 PRI 値。</summary>
    private const int FixedPriValue = (16 * 8) + 6;

    /// <summary>
    /// 指定した RunId・連番の RFC 5424 メッセージ本文（UTF-8 バイト列。改行は含まない）を組み立てる。
    /// </summary>
    /// <param name="runId">ベンチ実行 ID（英数字のみを推奨。PROCID フィールドへ埋め込む）。</param>
    /// <param name="sequence">送出順連番（0 始まり）。</param>
    /// <param name="timestamp">送信時刻（RFC 5424 TIMESTAMP。UTC で埋め込む）。</param>
    /// <param name="padding">
    /// メッセージ長を調整するための追加パディング文字列（既定は空。バーストシナリオでの
    /// パケットサイズ調整に使う）。
    /// </param>
    public static byte[] BuildMessage(string runId, long sequence, DateTimeOffset timestamp, string padding = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        // HEADER: <PRI>1 SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID SP STRUCTURED-DATA SP MSG
        // HOSTNAME/APP-NAME/MSGID は固定値、PROCID に RunId を埋め込む（連番自体は MSG 側に置く——
        // PROCID は RFC 5424 上 128 バイト以内の PRINTUSASCII 制約があるため、可変長の連番は
        // 制約のない MSG 側に置く方が安全）。
        var text = string.Create(CultureInfo.InvariantCulture,
            $"<{FixedPriValue}>1 {timestamp.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffffZ} yagura-bench yagura-bench {runId} - - " +
            $"{SequenceMarkerPrefix}-{runId}-{sequence}{padding}");

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// TCP octet-counting framing（RFC 6587 §3.4.1）でメッセージをラップする
    /// （src/Yagura.Ingestion/Tcp/TcpFrameDecoder.cs が両フレーミング方式を受理するため、
    /// TCP シナリオは octet-counting を既定で使う——改行を含む本文でも曖昧さがない）。
    /// </summary>
    public static byte[] WrapOctetCounting(byte[] message)
    {
        var prefix = Encoding.ASCII.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + " ");
        var framed = new byte[prefix.Length + message.Length];
        prefix.CopyTo(framed, 0);
        message.CopyTo(framed, prefix.Length);
        return framed;
    }

    /// <summary>
    /// 検証器が連番を抽出するための正規表現パターン文字列（<see cref="SequenceMarkerPrefix"/> と
    /// 対応。<c>yb-{runId}-{sequence}</c> の形式）。
    /// </summary>
    public static string BuildSequenceMarkerText(string runId, long sequence) =>
        $"{SequenceMarkerPrefix}-{runId}-{sequence}";
}
