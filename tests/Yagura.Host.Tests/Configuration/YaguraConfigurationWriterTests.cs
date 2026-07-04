using Microsoft.Extensions.Logging.Testing;
using Yagura.Host.Configuration;

namespace Yagura.Host.Tests.Configuration;

/// <summary>
/// <see cref="YaguraConfigurationWriter"/> の単体テスト（M3-3）。
/// </summary>
/// <remarks>
/// configuration.md §3 の要求を軸に構成する: 保存 → 読み込みの往復（値が保持され、
/// <see cref="YaguraConfigurationLoader"/> 経由で読んでも未知キー警告が出ない）、
/// 楽観的な競合検出（保存前に外部変更が入った場合の失敗とファイル無傷）、
/// 原子性の代替検証（一時ファイルの残骸が残らないこと）。
/// </remarks>
[Collection(ConfigurationEnvironmentVariableTestCollection.Name)]
public sealed class YaguraConfigurationWriterTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"yagura-writer-test-{Guid.NewGuid():N}");

    public YaguraConfigurationWriterTests()
    {
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private string ConfigurationFilePath =>
        Path.Combine(_dataRoot, YaguraConfigurationLoader.ConfigurationFileName);

    // ------------------------------------------------------------------
    // 保存 → 読み込みの往復
    // ------------------------------------------------------------------

    [Fact]
    public void Save_NoExistingFile_CreatesFileAndReturnsNewToken()
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        Assert.Equal(ConfigurationVersionToken.FileAbsent, snapshot.VersionToken);

        var options = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };

        var newToken = YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);

        Assert.True(File.Exists(ConfigurationFilePath));
        Assert.NotEqual(ConfigurationVersionToken.FileAbsent, newToken);
    }

    [Fact]
    public void SaveThenLoadViaLoader_RoundTripsValuesWithoutWarningsOrUnknownKeys()
    {
        var options = new YaguraConfigurationOptions
        {
            Ingestion = new YaguraConfigurationOptions.IngestionOptions
            {
                Udp = new YaguraConfigurationOptions.IngestionOptions.UdpOptions
                {
                    BindAddress = "192.168.1.10",
                    Port = "5140",
                },
            },
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
            Storage = new YaguraConfigurationOptions.StorageOptions { SqliteFileName = "custom.db" },
        };

        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);

        var logger = new FakeLogger();
        var loadResult = YaguraConfigurationLoader.Load(_dataRoot, logger);

        Assert.Equal("192.168.1.10", loadResult.Configuration.UdpBindAddress);
        Assert.Equal(5140, loadResult.Configuration.UdpPort);
        Assert.Equal(9100, loadResult.Configuration.HttpPort);
        Assert.Equal("custom.db", loadResult.Configuration.SqliteFileName);
        Assert.Empty(loadResult.Warnings);
        Assert.Empty(loadResult.UnknownKeys);
    }

    [Fact]
    public void SaveTwiceInSequence_SecondSaveUsesTokenFromFirstSave_Succeeds()
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var firstOptions = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };
        var tokenAfterFirstSave = YaguraConfigurationWriter.Save(_dataRoot, firstOptions, snapshot.VersionToken);

        var secondOptions = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9200" },
        };

        // 例外を送出しないこと（前回保存が返したトークンで正しく検証を通過する）。
        var tokenAfterSecondSave = YaguraConfigurationWriter.Save(_dataRoot, secondOptions, tokenAfterFirstSave);

        Assert.NotEqual(tokenAfterFirstSave, tokenAfterSecondSave);

        var logger = new FakeLogger();
        var loadResult = YaguraConfigurationLoader.Load(_dataRoot, logger);
        Assert.Equal(9200, loadResult.Configuration.HttpPort);
    }

    // ------------------------------------------------------------------
    // 楽観的な競合検出
    // ------------------------------------------------------------------

    [Fact]
    public void Save_FileChangedExternallyAfterRead_ThrowsConflictAndLeavesFileUntouched()
    {
        // 初期状態を書き込む。
        var initialOptions = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };
        var initialSnapshot = YaguraConfigurationWriter.Read(_dataRoot);
        YaguraConfigurationWriter.Save(_dataRoot, initialOptions, initialSnapshot.VersionToken);

        // 保存前の読み込み（ウィザードがこれから変更しようとする時点のスナップショット）。
        var readForEdit = YaguraConfigurationWriter.Read(_dataRoot);

        // その後、外部（手編集）がファイルを変更する。
        File.WriteAllText(ConfigurationFilePath, """{ "Viewer": { "HttpPort": "9300" } }""");
        var contentAfterExternalEdit = File.ReadAllBytes(ConfigurationFilePath);

        // ウィザードは古いスナップショットのトークンで保存しようとする → 競合失敗。
        var attemptedOptions = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9200" },
        };

        Assert.Throws<ConfigurationConflictException>(
            () => YaguraConfigurationWriter.Save(_dataRoot, attemptedOptions, readForEdit.VersionToken));

        // ファイルは無傷（外部変更後の内容のまま。ウィザードの変更もデフォルトも書き込まれない）。
        // YaguraConfigurationLoader.Load ではなく YaguraConfigurationWriter.Read で検証する:
        // Load は環境変数 YAGURA_HTTP_PORT による上書きも考慮する実効値解決 API であり、
        // 他のテストクラス（YaguraConfigurationLoaderTests）が並列実行時にプロセス全体で
        // 共有される環境変数を一時的に設定することがあるため、本テストの意図（ファイルの
        // 内容そのものが無傷であること）を検証するには不適切（環境変数由来の値と取り違えうる）。
        var contentAfterFailedSave = File.ReadAllBytes(ConfigurationFilePath);
        Assert.Equal(contentAfterExternalEdit, contentAfterFailedSave);

        var snapshotAfterFailedSave = YaguraConfigurationWriter.Read(_dataRoot);
        Assert.Equal("9300", snapshotAfterFailedSave.Options.Viewer?.HttpPort);
    }

    [Fact]
    public void Save_FileCreatedExternallyAfterInitialAbsentRead_ThrowsConflict()
    {
        // ファイル不在の状態で読み込む（初回保存を意図したスナップショット）。
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        Assert.Equal(ConfigurationVersionToken.FileAbsent, snapshot.VersionToken);

        // その間に他の書き手（手編集）がファイルを作成する。
        File.WriteAllText(ConfigurationFilePath, """{ "Viewer": { "HttpPort": "9999" } }""");

        var options = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };

        Assert.Throws<ConfigurationConflictException>(
            () => YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken));

        // YaguraConfigurationLoader.Load を使わない理由は直前のテストのコメントを参照
        // （環境変数 YAGURA_HTTP_PORT のプロセス全体共有による誤検出を避ける）。
        var snapshotAfterFailedSave = YaguraConfigurationWriter.Read(_dataRoot);
        Assert.Equal("9999", snapshotAfterFailedSave.Options.Viewer?.HttpPort);
    }

    // ------------------------------------------------------------------
    // 原子性の代替検証（一時ファイルの残骸がないこと）
    // ------------------------------------------------------------------

    [Fact]
    public void Save_Success_LeavesNoTemporaryFilesInDataRoot()
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };

        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);

        var entries = Directory.GetFileSystemEntries(_dataRoot);
        var entry = Assert.Single(entries);
        Assert.Equal(ConfigurationFilePath, entry);
    }

    [Fact]
    public void Save_ConflictFailure_LeavesNoTemporaryFilesInDataRoot()
    {
        var initialOptions = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };
        var initialSnapshot = YaguraConfigurationWriter.Read(_dataRoot);
        YaguraConfigurationWriter.Save(_dataRoot, initialOptions, initialSnapshot.VersionToken);

        // 古いトークン（初回保存前の FileAbsent）で保存を試み、競合させる。
        var staleToken = ConfigurationVersionToken.FileAbsent;
        var attemptedOptions = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9200" },
        };

        Assert.Throws<ConfigurationConflictException>(
            () => YaguraConfigurationWriter.Save(_dataRoot, attemptedOptions, staleToken));

        var entries = Directory.GetFileSystemEntries(_dataRoot);
        var entry = Assert.Single(entries);
        Assert.Equal(ConfigurationFilePath, entry);
    }

    // ------------------------------------------------------------------
    // 文字コード（BOM なし。クラスコメント参照）
    // ------------------------------------------------------------------

    [Fact]
    public void Save_WritesUtf8WithoutByteOrderMark()
    {
        var snapshot = YaguraConfigurationWriter.Read(_dataRoot);
        var options = new YaguraConfigurationOptions
        {
            Viewer = new YaguraConfigurationOptions.ViewerOptions { HttpPort = "9100" },
        };

        YaguraConfigurationWriter.Save(_dataRoot, options, snapshot.VersionToken);

        var bytes = File.ReadAllBytes(ConfigurationFilePath);
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };
        Assert.False(bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(utf8Bom));
    }
}
