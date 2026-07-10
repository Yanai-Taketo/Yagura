using System.Security;

namespace Yagura.Storage.Spool;

/// <summary>
/// ディスクスプール本体（architecture.md §3.2）。追記型のセグメントファイル群として
/// 実装し、レコード単位の破損検出（<see cref="SpoolSegmentReader"/>）・上限到達時の
/// 新規破棄（§3.2.3）・書込失敗時のリトライ後破棄を提供する。
/// </summary>
/// <remarks>
/// <para>
/// <b>本クラスの責務境界</b>: 「セグメントへの追記」「セグメント一覧の列挙（drain 対象の
/// 提供）」「消化済みセグメントの削除」という <i>ストレージ層の一次操作</i> のみを持つ。
/// drain のオーケストレーション（Q2 使用率によるヒステリシス・速度上限・保存先障害時の
/// バックオフ。§3.2.2）は Q2（<c>Channel&lt;LogRecord&gt;</c>）と <c>ILogStore</c> の
/// 両方を知る必要があり、それらは Yagura.Ingestion 側にあるため、本クラスはその
/// オーケストレーションを持たない（<c>Yagura.Ingestion.Persistence.SpoolDrainCoordinator</c>
/// が本クラスを呼び出す形で担う）。
/// </para>
/// <para>
/// <b>スレッド安全性</b>: <see cref="TryAppendAsync"/> と drain 側の
/// <see cref="TrySealActiveSegmentAndListDrainable"/> ・<see cref="DeleteSegment"/> は
/// 並行に呼ばれ得る（ライブ経路の退避と drain が並行動作する。§3.2.2「並行動作」）。
/// 内部状態（アクティブセグメントの参照・使用量カウンタ）は 1 つの <see cref="_gate"/>
/// で直列化する——スプールは「所定時間内に DB へ書けない」という飽和シグナルの経路で
/// あり高頻度呼び出しを想定しないため、単純なロックで正しさを優先する。
/// </para>
/// </remarks>
public sealed class DiskSpool : IDisposable
{
    private readonly object _gate = new();
    private readonly DiskSpoolOptions _options;

    private string? _activeSegmentPath;
    private FileStream? _activeSegmentStream;
    private int _segmentSequence;
    private long _currentUsageBytes;
    private long _deletedSegmentsTotal;

    private DiskSpool(DiskSpoolOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// このプロセスで現在把握しているスプールのディスク使用量（バイト）。
    /// テスト・ゲージ（§4.6）用に公開する。
    /// </summary>
    public long CurrentUsageBytes
    {
        get { lock (_gate) { return _currentUsageBytes; } }
    }

    /// <summary>
    /// このプロセスで <see cref="DeleteSegment"/> により実際に削除されたセグメントの累積数
    /// （単調増加。プロセス内累積のみで再起動をまたぐ永続化はしない）。
    /// </summary>
    /// <remarks>
    /// 自己検証タイムアウトのバックログ起因判別（architecture.md §3.2.5。Issue #202・PR #211
    /// レビュー対応）が「drain の進捗」の観測に使う。<see cref="CurrentUsageBytes"/> の周期
    /// サンプリング差分（純増減）では、持続的な速度不足（追記速度が消化速度を恒常的に上回る
    /// 状態。§3.2.2）下で drain が実際にセグメントを消化していても純減少が一度も観測されず
    /// 「進捗なし」に誤分類されるため、追記と混ざらない単調増加の累積カウンタとして分離した。
    /// <see cref="DeleteSegment"/> は drain（<c>SpoolDrainCoordinator</c>）が DB 書き込み確定後に
    /// のみ呼ぶため、本カウンタの増分は drain 消化の直接証拠になる。
    /// </remarks>
    public long DeletedSegmentsTotal
    {
        get { lock (_gate) { return _deletedSegmentsTotal; } }
    }

    /// <summary>
    /// スプールディレクトリのパス。
    /// </summary>
    public string Directory => _options.Directory;

    /// <summary>
    /// スプール領域を開く（architecture.md §1.2 起動手順 1「スプール領域を開く
    /// （前回退避分の存在確認を含む）」）。
    /// </summary>
    /// <remarks>
    /// ディレクトリが存在しない場合は作成を試みる。作成・書き込み確認のいずれかに
    /// 失敗した場合（ディスク障害・ACL 破損等）は例外を投げずに <c>null</c> を返す——
    /// 呼び出し側（ホスト）はこれを「スプールなし縮退運転」（§1.2）の判定に使う。
    /// </remarks>
    public static DiskSpool? TryOpen(DiskSpoolOptions options, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            System.IO.Directory.CreateDirectory(options.Directory);

            // 書き込み確認: 実際に一時ファイルを作成・削除できることを確認する
            // （ディレクトリの存在確認だけでは ACL 不整合等の書込不可を見逃すため）。
            var probePath = Path.Combine(options.Directory, $".open-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);

            var spool = new DiskSpool(options);
            spool.ScanExistingSegments();

            failure = null;
            return spool;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            failure = ex;
            return null;
        }
    }

    private void ScanExistingSegments()
    {
        // 前回退避分（存在確認。§1.2 手順 1）: 既存セグメントファイルの合計サイズを
        // 使用量カウンタへ反映する。ファイル自体は drain 対象として残す
        // （列挙は TrySealActiveSegmentAndListDrainable が行う）。
        long total = 0;
        foreach (var path in EnumerateSegmentFilesUnlocked())
        {
            total += new FileInfo(path).Length;
        }

        _currentUsageBytes = total;

        // セグメント連番の再開値: 既存ファイル名から最大連番を読み取る必要はない
        // （ファイル名は Ticks 主体でソートされ、連番は同一 Ticks 内の衝突回避のみが
        // 目的のため、プロセス再起動後は 0 から再開しても新しい Ticks 値により
        // ファイル名は一意になる）。
        _segmentSequence = 0;
    }

    private IEnumerable<string> EnumerateSegmentFilesUnlocked() =>
        System.IO.Directory.Exists(_options.Directory)
            ? System.IO.Directory.EnumerateFiles(_options.Directory)
                .Where(p => SpoolSegmentFileNames.IsSegmentFile(Path.GetFileName(p)))
                .OrderBy(p => p, StringComparer.Ordinal)
            : [];

    /// <summary>
    /// 1 レコードをスプールへ退避する（architecture.md §3.2.1）。
    /// </summary>
    /// <returns>
    /// <see cref="SpoolAppendResult.Appended"/>: 追記に成功した（スプール退避カウンタ対象）。
    /// <see cref="SpoolAppendResult.QuotaExceeded"/>: 上限到達のため新規破棄した（§3.2.3。スプール破棄カウンタ対象）。
    /// <see cref="SpoolAppendResult.WriteFailed"/>: リトライしても書き込めず破棄した（スプール書込失敗カウンタ対象）。
    /// </returns>
    public async Task<SpoolAppendResult> TryAppendAsync(SpoolRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var frame = SpoolRecordSerializer.SerializeFrame(record);

        Exception? lastFailure = null;

        for (var attempt = 0; attempt <= SpoolConstants.WriteRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var appended = AppendFrameUnderGate(frame);
                if (!appended)
                {
                    // 上限到達（§3.2.3「新規到着分を破棄する」）。リトライしても状況が
                    // 変わる保証がない（drain が進まない限り空かない）ため、リトライせず即破棄。
                    return SpoolAppendResult.QuotaExceeded;
                }

                return SpoolAppendResult.Appended;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;

                if (attempt < SpoolConstants.WriteRetryCount)
                {
                    await Task.Delay(SpoolConstants.WriteRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // リトライ回数を使い切っても書き込めない——破棄する（スプール書込失敗カウンタ対象）。
        _ = lastFailure;
        return SpoolAppendResult.WriteFailed;
    }

    /// <summary>
    /// フレームをアクティブセグメントへ追記する。上限到達時は書き込まず <c>false</c> を返す。
    /// セグメントが目標サイズを超えたら封止して次のセグメントを開始する。
    /// </summary>
    private bool AppendFrameUnderGate(byte[] frame)
    {
        lock (_gate)
        {
            if (_currentUsageBytes + frame.Length > _options.QuotaBytes)
            {
                return false;
            }

            EnsureActiveSegmentUnderGate();

            _activeSegmentStream!.Write(frame, 0, frame.Length);
            _activeSegmentStream.Flush(flushToDisk: true);

            _currentUsageBytes += frame.Length;

            if (_activeSegmentStream.Length >= SpoolConstants.TargetSegmentSizeBytes)
            {
                SealActiveSegmentUnderGate();
            }

            return true;
        }
    }

    private void EnsureActiveSegmentUnderGate()
    {
        if (_activeSegmentStream is not null)
        {
            return;
        }

        var fileName = SpoolSegmentFileNames.CreateSegmentFileName(DateTimeOffset.UtcNow, _segmentSequence++);
        _activeSegmentPath = Path.Combine(_options.Directory, fileName);
        _activeSegmentStream = new FileStream(
            _activeSegmentPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
    }

    private void SealActiveSegmentUnderGate()
    {
        _activeSegmentStream?.Dispose();
        _activeSegmentStream = null;
        _activeSegmentPath = null;
    }

    /// <summary>
    /// drain 対象のセグメントファイル一覧を古い順に返す。アクティブセグメントが
    /// 存在する場合は封止してから含める（drain 中に成長し続けるファイルを読ませない
    /// ため——封止後は新規追記が新しいセグメントへ向かう）。
    /// </summary>
    public IReadOnlyList<string> TrySealActiveSegmentAndListDrainable()
    {
        lock (_gate)
        {
            SealActiveSegmentUnderGate();
            return EnumerateSegmentFilesUnlocked().ToList();
        }
    }

    /// <summary>
    /// セグメントファイルを読み、正常なフレームを <see cref="SpoolRecord"/> として返す
    /// （architecture.md §3.2.1「レコード単位の破損検出」）。破損した末尾を検出した場合は
    /// <paramref name="corruptTailDetected"/> に <c>true</c> を返す（それ以前の正常な
    /// レコードは全件回収済みで返す）。<paramref name="corruptTailBytes"/> は読み捨てた
    /// 破損末尾のバイト数（Issue #201。<see cref="SpoolSegmentReader.ReadValidRecords"/> 参照）。
    /// drain オーケストレーション（Yagura.Ingestion 側）がフレーム形式の内部実装
    /// （<see cref="SpoolSegmentReader"/>）を直接参照せずに済むよう、本クラスがその窓口になる。
    /// </summary>
    public IReadOnlyList<SpoolRecord> ReadSegmentRecords(string segmentPath, out bool corruptTailDetected, out long corruptTailBytes) =>
        SpoolSegmentReader.ReadValidRecords(segmentPath, out corruptTailDetected, out corruptTailBytes);

    /// <summary>
    /// drain が確定書き込みを終えたセグメントを削除する（architecture.md §3.2.4
    /// 「消化（DB コミット確認）済みセグメントは、通常のファイル削除で速やかに削除する」）。
    /// </summary>
    public void DeleteSegment(string segmentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentPath);

        lock (_gate)
        {
            long length = 0;
            if (File.Exists(segmentPath))
            {
                length = new FileInfo(segmentPath).Length;
                File.Delete(segmentPath);

                // 実在したファイルを削除できた場合のみ数える（存在しないパスの no-op 削除を
                // 「drain の進捗」に数えない。DeletedSegmentsTotal の remarks 参照）。
                _deletedSegmentsTotal++;
            }

            _currentUsageBytes = Math.Max(0, _currentUsageBytes - length);
        }
    }

    /// <summary>
    /// 自己検証用（§3.2.5）に現在の使用量比率（0.0〜1.0 超もあり得る）を返す。
    /// </summary>
    public double UsageRatio => _options.QuotaBytes <= 0 ? 0 : (double)CurrentUsageBytes / _options.QuotaBytes;

    /// <summary>
    /// アクティブセグメントのファイルハンドルを解放する。プロセス終了時・テストでの
    /// 後片付け（ディレクトリ削除等）のために、開いたままの <see cref="FileStream"/> を
    /// 明示的に閉じる。封止済みセグメント（drain 対象）には影響しない。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            SealActiveSegmentUnderGate();
        }
    }
}

/// <summary>
/// <see cref="DiskSpool.TryAppendAsync"/> の結果（architecture.md §3.2）。
/// </summary>
public enum SpoolAppendResult
{
    /// <summary>退避に成功した（スプール退避カウンタ対象）。</summary>
    Appended,

    /// <summary>上限到達により新規破棄した（§3.2.3。スプール破棄カウンタ対象）。</summary>
    QuotaExceeded,

    /// <summary>リトライ後も書き込めず破棄した（スプール書込失敗カウンタ対象）。</summary>
    WriteFailed,
}
