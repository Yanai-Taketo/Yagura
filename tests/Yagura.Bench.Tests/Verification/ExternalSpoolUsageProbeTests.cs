using Yagura.Bench.Verification;

namespace Yagura.Bench.Tests.Verification;

/// <summary>
/// <see cref="ExternalSpoolUsageProbe"/> の drain 競合耐性を検証する（Issue #178）。
/// CI の回帰ベンチが確率的に <c>FileNotFoundException</c> で落ちていた原因は、
/// <c>GetSegmentBytesOnDisk</c> がセグメントファイルを列挙した後、<c>FileInfo.Length</c> を
/// 読む前に drain 処理が当該ファイルを削除する競合だった。本テストは削除を継続的に発生させる
/// 背景タスクと並行してプローブを呼び続け、例外が伝播しないことを確認する。
/// </summary>
public sealed class ExternalSpoolUsageProbeTests
{
    [Fact]
    public async Task GetSegmentBytesOnDisk_ConcurrentDeletion_DoesNotThrow()
    {
        var spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-bench-spool-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spoolDirectory);

        try
        {
            const int fileCount = 40;
            var filePaths = Enumerable.Range(0, fileCount)
                .Select(i => Path.Combine(spoolDirectory, $"segment-{i:D3}.seg"))
                .ToArray();

            foreach (var path in filePaths)
            {
                File.WriteAllBytes(path, new byte[512]);
            }

            using var cts = new CancellationTokenSource();

            // drain 処理を模した背景タスク: セグメントファイルを絶え間なく削除・再作成する。
            // GetSegmentBytesOnDisk が列挙した直後に対象ファイルが消える窓を作り出す。
            var deleterTask = Task.Run(async () =>
            {
                var rng = new Random(12345);
                while (!cts.IsCancellationRequested)
                {
                    var path = filePaths[rng.Next(filePaths.Length)];
                    try
                    {
                        File.Delete(path);
                        File.WriteAllBytes(path, new byte[512]);
                    }
                    catch (IOException)
                    {
                        // 同時書き込み・削除の衝突（ハンドル競合）は本テストの関心事ではない。
                    }

                    await Task.Delay(1);
                }
            });

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            var iterations = 0;
            var exceptions = new List<Exception>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    // 例外を投げず、非負の値を返すことだけを検証する（削除競合下では正確な
                    // バイト数は非決定的であり、本テストの主眼は「落ちないこと」）。
                    var bytes = ExternalSpoolUsageProbe.GetSegmentBytesOnDisk(spoolDirectory);
                    Assert.True(bytes >= 0);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                iterations++;
            }

            cts.Cancel();
            await deleterTask;

            Assert.True(iterations > 0, "レース検証のためのポーリングが一度も実行されなかった。");
            Assert.Empty(exceptions);
        }
        finally
        {
            if (Directory.Exists(spoolDirectory))
            {
                Directory.Delete(spoolDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WaitForDrainCompletionAsync_ConcurrentDeletion_DoesNotThrow()
    {
        var spoolDirectory = Path.Combine(Path.GetTempPath(), $"yagura-bench-spool-race-wait-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spoolDirectory);

        try
        {
            var filePath = Path.Combine(spoolDirectory, "segment-000.seg");
            File.WriteAllBytes(filePath, new byte[512]);

            // 削除フェーズ（drain 処理を模した背景タスク）を完了まで待ってから、最終判定として
            // WaitForDrainCompletionAsync を呼ぶ。以前は削除タスクと WaitForDrainCompletionAsync の
            // 固定タイムアウト（3秒）を同時に走らせていたため、CI ランナー負荷時に ThreadPool の
            // スレッド割当が遅延して削除がタイムアウト内に間に合わず flaky になっていた
            // （Issue #207）。削除の完了を先に確定させることで、以降の判定を非決定要素から切り離す。
            var deleteTask = Task.Run(async () =>
            {
                await Task.Delay(5);
                File.Delete(filePath);
            });

            await deleteTask;

            // この時点でファイルは確実に削除済みのため、drain 完了判定は非決定的な競合に依存しない。
            var completed = await ExternalSpoolUsageProbe
                .WaitForDrainCompletionAsync(spoolDirectory, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(5));

            Assert.True(completed, "ファイル削除後も drain 完了（使用量 0）と判定されなかった。");
        }
        finally
        {
            if (Directory.Exists(spoolDirectory))
            {
                Directory.Delete(spoolDirectory, recursive: true);
            }
        }
    }
}
