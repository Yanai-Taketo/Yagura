using System.Runtime.Versioning;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="SystemForwarderMsiStore"/>（ADR-0020 決定 3。配置経路 (b)）の単体テスト。
/// ADR-0020 決定 5 の CI 回帰項目のうち、②ステージングの <c>Lookup()</c> 不可視・
/// ③排他（単一飛行）・⑤サイズ上限・パス構成にクライアントファイル名を使わないこと、を固定する
/// （①fail-closed は <c>ForwarderMsiUploadConfigurationTests</c> + E2E、④エンドポイント 404 は
/// 構造的非存在——登録自体の省略——のため <c>MapYaguraAdmin</c> の分岐で担保）。
/// </summary>
/// <remarks>
/// ProductVersion の読み取りは注入した偽実装で固定する（<c>msi.dll</c> は Windows 専用。
/// 実 MSI からの読み取りは lab 検証——ADR-0020 決定 5——の領分）。
/// </remarks>
[SupportedOSPlatform("windows")] // SystemForwarderMsiSource（検出突合に使用）の注釈の伝播（SystemForwarderMsiSourceTests と同じ扱い）
public sealed class SystemForwarderMsiStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"yagura-msistore-test-{Guid.NewGuid():N}");

    public SystemForwarderMsiStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private SystemForwarderMsiStore CreateStore(string? productVersion = "5.0.8") =>
        new(_folder, _ => productVersion);

    private static MemoryStream Content(int length = 16) => new(new byte[length]);

    [Fact]
    public async Task StageAndCommit_PlacesFileWithGeneratedName_AndLookupDetectsIt()
    {
        var store = CreateStore();

        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);

        Assert.True(stage.Success);
        // 格納ファイル名はクライアント申告ではなく ProductVersion から Yagura が生成する（決定 3）。
        Assert.Equal("fluent-bit-5.0.8-win64.msi", stage.FinalFileName);
        Assert.False(File.Exists(Path.Combine(_folder, stage.FinalFileName!)));

        var commit = store.Commit(stage.StagingToken!, versionMismatchAcknowledged: true, replaceAcknowledged: false);

        Assert.True(commit.Success);
        Assert.True(File.Exists(Path.Combine(_folder, "fluent-bit-5.0.8-win64.msi")));

        var lookup = new SystemForwarderMsiSource(_folder).Lookup(ForwarderMsiArchitecture.Win64);
        Assert.Equal(ForwarderMsiLookupState.Single, lookup.State);
    }

    [Fact]
    public async Task StagingFile_IsInvisibleToLookup()
    {
        // 決定 5 ②: ステージング名は検出パターン非一致であり、確定前の中間状態が
        // Lookup()・生成処理から見えないことの回帰。
        var store = CreateStore();
        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);
        Assert.True(stage.Success);

        Assert.Single(Directory.GetFiles(_folder)); // ステージングファイルは実在する
        var lookup = new SystemForwarderMsiSource(_folder).Lookup(ForwarderMsiArchitecture.Win64);
        Assert.Equal(ForwarderMsiLookupState.NotFound, lookup.State); // しかし検出されない
    }

    [Fact]
    public async Task Stage_DeclaredLengthExceedsLimit_RejectsBeforeReadingBody()
    {
        var store = CreateStore();
        var neverReadable = new ThrowingStream();

        var stage = await store.StageAsync(
            ForwarderMsiArchitecture.Win64, neverReadable,
            ForwarderMsiUploadConstraints.MaxUploadBytes + 1, CancellationToken.None);

        Assert.False(stage.Success);
        Assert.Equal(ForwarderMsiStageError.DeclaredLengthExceedsLimit, stage.Error);
        Assert.False(neverReadable.WasRead); // 本文を読む前に拒否（決定 3）
    }

    [Fact]
    public async Task Stage_StreamExceedsLimit_AbortsAndCleansStaging()
    {
        // Content-Length 申告なし（null）——累積カウントでの打ち切り側の経路。
        var store = CreateStore();
        var oversized = new ZeroStream(ForwarderMsiUploadConstraints.MaxUploadBytes + 1);

        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, oversized, null, CancellationToken.None);

        Assert.False(stage.Success);
        Assert.Equal(ForwarderMsiStageError.StreamExceedsLimit, stage.Error);
        Assert.Empty(Directory.GetFiles(_folder)); // ステージングは削除済み
    }

    [Fact]
    public async Task Stage_ProductVersionUnreadable_Rejects()
    {
        // アップロードではファイル名フォールバックを適用しない（決定 3——クライアント申告の
        // ファイル名を信用しないため、版の根拠は ProductVersion のみ）。
        var store = CreateStore(productVersion: null);

        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);

        Assert.False(stage.Success);
        Assert.Equal(ForwarderMsiStageError.ProductVersionUnreadable, stage.Error);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Stage_ProductVersionWithPathSeparator_Rejects()
    {
        // MSI 内メタデータ由来の値も信用しない（パターン破壊・パス区切りは拒否）。
        var store = CreateStore(productVersion: "5.0.8\\..\\evil");

        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);

        Assert.False(stage.Success);
        Assert.Equal(ForwarderMsiStageError.ProductVersionInvalid, stage.Error);
    }

    [Fact]
    public async Task Stage_WhileAnotherStagePending_LaterStageDiscardsPreviousPending()
    {
        // 保留（確認待ち）は 1 件のみ——新しい stage は前の保留を破棄して置き換える
        // （孤児掃除の「新規アップロード開始時」の実装。進行中の書き込みとの排他は
        // Stage_ConcurrentWrites_SecondIsRejected が固定する）。
        var store = CreateStore();
        var first = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);
        Assert.True(first.Success);

        var second = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);
        Assert.True(second.Success);

        // 先行の保留トークンはもう確定できない。
        var commitFirst = store.Commit(first.StagingToken!, true, false);
        Assert.False(commitFirst.Success);
        Assert.Equal(ForwarderMsiCommitError.UnknownStagingToken, commitFirst.Error);

        // 後行は確定できる。
        Assert.True(store.Commit(second.StagingToken!, true, false).Success);
    }

    [Fact]
    public async Task Stage_ConcurrentWrites_SecondIsRejected()
    {
        // 決定 5 ③: 単一飛行（プロセス全体）。書き込み中のストリームを停止させて並行状態を作る。
        var store = CreateStore();
        var gate = new SemaphoreSlim(0);
        var blocking = new GatedStream(gate, totalBytes: 32);

        var firstTask = store.StageAsync(ForwarderMsiArchitecture.Win64, blocking, null, CancellationToken.None);

        // 1 本目が書き込み中（gate 待ち）の間に 2 本目を開始する。
        await blocking.FirstReadStarted.Task;
        var second = await store.StageAsync(ForwarderMsiArchitecture.WinArm64, Content(), 16, CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(ForwarderMsiStageError.AnotherUploadInProgress, second.Error);

        gate.Release(int.MaxValue);
        var first = await firstTask;
        Assert.True(first.Success);
    }

    [Fact]
    public async Task Commit_HashMismatchWithoutAcknowledgement_IsRejected()
    {
        // 公式ハッシュは未確定（Unverified）でも Match ではない——二段階確認必須側（決定 3）。
        var store = CreateStore();
        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);

        var commit = store.Commit(stage.StagingToken!, versionMismatchAcknowledged: false, replaceAcknowledged: false);

        Assert.False(commit.Success);
        Assert.Equal(ForwarderMsiCommitError.VersionMismatchNotAcknowledged, commit.Error);
    }

    [Fact]
    public async Task Commit_ReplaceWithoutAcknowledgement_IsRejected_AndReplaceFlowSingleizes()
    {
        var store = CreateStore(productVersion: "5.0.8");
        await PlaceAsync(store, ForwarderMsiArchitecture.Win64);

        // 別版のアップロード → 置換確認なしは拒否。
        var storeV2 = new SystemForwarderMsiStore(_folder, _ => "5.0.9");
        var stage = await storeV2.StageAsync(ForwarderMsiArchitecture.Win64, Content(32), 32, CancellationToken.None);
        Assert.True(stage.Success);
        Assert.Equal("fluent-bit-5.0.8-win64.msi", stage.ExistingFileName);

        var rejected = storeV2.Commit(stage.StagingToken!, true, replaceAcknowledged: false);
        Assert.False(rejected.Success);
        Assert.Equal(ForwarderMsiCommitError.ReplaceNotAcknowledged, rejected.Error);

        // 置換確認ありは成功し、旧版が除去されて単一状態が保たれる（決定 3——単一化）。
        var committed = storeV2.Commit(stage.StagingToken!, true, replaceAcknowledged: true);
        Assert.True(committed.Success);
        Assert.NotNull(committed.ReplacedSha256);
        var files = Directory.GetFiles(_folder).Select(Path.GetFileName).ToList();
        Assert.Equal(["fluent-bit-5.0.9-win64.msi"], files);
    }

    [Fact]
    public async Task Commit_FolderChangedAfterStage_IsRejected()
    {
        // TOCTOU ガード: 確認表示から確定までの間に配置フォルダが変わったら確定しない（決定 3）。
        var store = CreateStore();
        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);
        Assert.Null(stage.ExistingFileName);

        // stage 後に手動配置（経路 (a)）で同アーキの MSI が現れた状況を作る。
        File.WriteAllBytes(Path.Combine(_folder, "fluent-bit-9.9.9-win64.msi"), new byte[8]);

        var commit = store.Commit(stage.StagingToken!, true, false);

        Assert.False(commit.Success);
        Assert.Equal(ForwarderMsiCommitError.FolderStateChanged, commit.Error);
    }

    [Fact]
    public async Task Discard_RemovesStagingAndInvalidatesToken()
    {
        var store = CreateStore();
        var stage = await store.StageAsync(ForwarderMsiArchitecture.Win64, Content(), 16, CancellationToken.None);

        var discard = store.Discard(stage.StagingToken!);

        Assert.True(discard.Found);
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.False(store.Commit(stage.StagingToken!, true, false).Success);
    }

    [Fact]
    public async Task Delete_WithMatchingSha256_DeletesAndReportsPreDeleteHash()
    {
        var store = CreateStore();
        var placed = await PlaceAsync(store, ForwarderMsiArchitecture.Win64);

        var deleted = store.Delete(ForwarderMsiArchitecture.Win64, placed.Sha256!);

        Assert.True(deleted.Success);
        Assert.Equal(placed.Sha256, deleted.DeletedSha256); // 削除前 SHA256 の記録（決定 3）
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Delete_WithStaleSha256_IsRejected()
    {
        // TOCTOU ガード: 表示と確定の間に差し替わったファイルは消さない（決定 3）。
        var store = CreateStore();
        await PlaceAsync(store, ForwarderMsiArchitecture.Win64);

        var deleted = store.Delete(ForwarderMsiArchitecture.Win64, new string('0', 64));

        Assert.False(deleted.Success);
        Assert.Equal(ForwarderMsiDeleteError.Sha256Mismatch, deleted.Error);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task CleanupStagingFiles_RemovesOrphans_ButNotPlacedFiles()
    {
        var store = CreateStore();
        await PlaceAsync(store, ForwarderMsiArchitecture.Win64);
        File.WriteAllBytes(Path.Combine(_folder, ".uploading-deadbeef.msi"), new byte[8]);

        var removed = store.CleanupStagingFiles();

        Assert.Equal(1, removed);
        Assert.Equal(
            ["fluent-bit-5.0.8-win64.msi"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void CheckWriteAccess_WritableFolder_ReturnsTrue_AndLeavesNoProbeResidue()
    {
        var store = CreateStore();

        var access = store.CheckWriteAccess();

        Assert.True(access.CanWrite);
        Assert.Empty(Directory.GetFiles(_folder)); // プローブは痕跡を残さない（委任 2）
    }

    [Fact]
    public async Task Stage_ArchitectureIsIndependent_Arm64NamingAndDetection()
    {
        var store = CreateStore();
        var stage = await store.StageAsync(ForwarderMsiArchitecture.WinArm64, Content(), 16, CancellationToken.None);

        Assert.True(stage.Success);
        Assert.Equal("fluent-bit-5.0.8-winarm64.msi", stage.FinalFileName);
        Assert.True(store.Commit(stage.StagingToken!, true, false).Success);

        // アーキごとに独立（ADR-0008 改訂履歴 2 と整合）: win64 側の検出には現れない。
        var lookup = new SystemForwarderMsiSource(_folder).Lookup(ForwarderMsiArchitecture.Win64);
        Assert.Equal(ForwarderMsiLookupState.NotFound, lookup.State);
    }

    private static async Task<ForwarderMsiCommitResult> PlaceAsync(
        SystemForwarderMsiStore store, ForwarderMsiArchitecture architecture)
    {
        var stage = await store.StageAsync(architecture, Content(), 16, CancellationToken.None);
        Assert.True(stage.Success);
        var commit = store.Commit(stage.StagingToken!, true, false);
        Assert.True(commit.Success);
        return commit;
    }

    /// <summary>読まれたら失敗するストリーム（「本文を読む前に拒否」の検証用）。</summary>
    private sealed class ThrowingStream : Stream
    {
        public bool WasRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            throw new InvalidOperationException("本文は読まれないはず。");
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>指定バイト数のゼロを返すストリーム（実メモリを確保せずに上限超過を作る）。</summary>
    private sealed class ZeroStream(long totalBytes) : Stream
    {
        private long _remaining = totalBytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, read);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>読み取りをゲートで停止できるストリーム（並行アップロードの排他検証用）。</summary>
    private sealed class GatedStream(SemaphoreSlim gate, long totalBytes) : Stream
    {
        private long _remaining = totalBytes;

        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstReadStarted.TrySetResult();
            await gate.WaitAsync(cancellationToken);
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Clear();
            _remaining -= read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
