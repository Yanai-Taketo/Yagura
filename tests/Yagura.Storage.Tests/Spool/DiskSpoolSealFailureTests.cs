using Yagura.Storage.Spool;

namespace Yagura.Storage.Tests.Spool;

/// <summary>
/// アクティブセグメントの封止（<c>Dispose</c>）が I/O 障害で失敗したときの回帰テスト（Issue #360）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜこのテストが要るか</b>: <see cref="FileStream.Dispose()"/> はバッファのフラッシュに失敗すると
/// <see cref="IOException"/> を投げうる（ディスク満杯・I/O 障害）。従来の実装は <c>Dispose</c> を先に
/// 呼んでから状態をクリアしていたため、失敗時に <c>_activeSegmentStream</c> が非 null のまま残り、
/// <b>次の追記が破棄済みストリームへ書き込んで <see cref="ObjectDisposedException"/> になる</b>——これは
/// 追記経路の catch フィルタ（<c>IOException or UnauthorizedAccessException</c>）に掛からないため、
/// スプールの追記経路ごと例外が抜ける二次被害になっていた。
/// </para>
/// <para>
/// ディスク満杯そのものは実機でしか作れないが、<b>その帰結（<c>Dispose</c> の失敗）は注入できる</b>
/// ——<see cref="DiskSpool.TryOpenForTests"/> でセグメントの <see cref="FileStream"/> 生成を差し替える。
/// </para>
/// </remarks>
public sealed class DiskSpoolSealFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"yagura-spool-seal-{Guid.NewGuid():N}");
    private readonly List<DiskSpool> _openedSpools = [];

    public void Dispose()
    {
        foreach (var spool in _openedSpools)
        {
            try
            {
                spool.Dispose();
            }
            catch (IOException)
            {
                // 本テストは Dispose が失敗するストリームを注入するため、後片付けでも同じ例外が出る。
            }
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// 封止が失敗しても内部状態は整合し、<b>次の追記は新しいセグメントへ向かう</b>
    /// （破棄済みストリームへ書き込まない）。
    /// </summary>
    [Fact]
    public async Task SealFailure_DoesNotPoisonActiveSegment_NextAppendOpensNewSegment()
    {
        var failNextDispose = true;
        var spool = OpenSpool(path => new ThrowOnDisposeFileStream(path, () => failNextDispose));

        Assert.Equal(SpoolAppendResult.Appended, await spool.TryAppendAsync(CreateRecord("1")));

        // 封止（Dispose）が失敗する——例外は呼び出し側へ伝播する（失敗を隠さない契約）。
        Assert.Throws<IOException>(() => spool.SealActiveSegmentAndListDrainable());

        // 以降の Dispose は成功させ、追記が回復することを確認する。
        failNextDispose = false;

        // ここが本丸: 従来は _activeSegmentStream が残り ObjectDisposedException になっていた。
        // 状態が整合していれば新しいセグメントが開かれ、追記は成功する。
        Assert.Equal(SpoolAppendResult.Appended, await spool.TryAppendAsync(CreateRecord("2")));

        var segments = spool.SealActiveSegmentAndListDrainable();

        // 失敗した封止のファイルと、その後に開かれた新しいファイルの 2 本が drain 対象になる
        // （封止に失敗したファイルも「もう追記されない」ため drain 対象として正しい）。
        Assert.Equal(2, segments.Count);
    }

    /// <summary>
    /// 封止が失敗したセグメントも、次の周期の列挙で drain 対象として拾える
    /// （<see cref="DiskSpool.SealActiveSegmentAndListDrainable"/> の remarks の主張を固定する）。
    /// </summary>
    [Fact]
    public async Task SealFailure_SegmentIsStillListedOnNextCall()
    {
        var failNextDispose = true;
        var spool = OpenSpool(path => new ThrowOnDisposeFileStream(path, () => failNextDispose));

        Assert.Equal(SpoolAppendResult.Appended, await spool.TryAppendAsync(CreateRecord("1")));
        Assert.Throws<IOException>(() => spool.SealActiveSegmentAndListDrainable());

        failNextDispose = false;

        // 2 回目の呼び出しではアクティブセグメントが無い（既にクリア済み）ため封止は走らず、
        // 列挙だけが行われて対象を返す。
        var segments = spool.SealActiveSegmentAndListDrainable();

        Assert.Single(segments);
    }

    private DiskSpool OpenSpool(Func<string, FileStream> streamFactory)
    {
        var spool = DiskSpool.TryOpenForTests(
            new DiskSpoolOptions { Directory = _directory },
            streamFactory,
            out var failure);
        Assert.Null(failure);
        Assert.NotNull(spool);
        _openedSpools.Add(spool);
        return spool;
    }

    private static SpoolRecord CreateRecord(string message)
    {
        var baseline = DateTimeOffset.UtcNow;
        return SpoolRecord.ForLog(new LogRecord(
            ReceivedAt: baseline,
            SourceAddress: "192.0.2.1",
            SourcePort: 5140,
            Protocol: Protocol.Udp,
            ParseStatus: ParseStatus.Parsed,
            DeviceTimestamp: baseline.AddSeconds(-1),
            Facility: 16,
            Severity: 6,
            Hostname: "myhost",
            AppName: "myapp",
            ProcId: "42",
            MsgId: "MSG1",
            StructuredData: null,
            Message: message));
    }

    /// <summary>
    /// <see cref="Dispose(bool)"/> が条件付きで <see cref="IOException"/> を投げる
    /// <see cref="FileStream"/>（ディスク満杯時のフラッシュ失敗の模擬）。
    /// </summary>
    private sealed class ThrowOnDisposeFileStream(string path, Func<bool> shouldThrow)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
    {
        private readonly Func<bool> _shouldThrow = shouldThrow;

        protected override void Dispose(bool disposing)
        {
            // 実ハンドルは必ず解放する（テストの後片付けでディレクトリ削除が失敗しないように）。
            base.Dispose(disposing);

            if (disposing && _shouldThrow())
            {
                throw new IOException("テスト: 封止時のフラッシュに失敗した（ディスク満杯の模擬）。");
            }
        }
    }
}
