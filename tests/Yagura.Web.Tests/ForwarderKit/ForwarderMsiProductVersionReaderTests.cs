using System.Runtime.Versioning;
using Yagura.Web.ForwarderKit;

namespace Yagura.Web.Tests.ForwarderKit;

/// <summary>
/// <see cref="ForwarderMsiProductVersionReader"/> の実読み取り経路のテスト（Issue #436 の再発防止）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ実 MSI データベースを生成するのか</b>: 初版実装（<c>MsiGetFileVersion</c>）の欠陥は
/// 「単体テストが読み取り関数を偽実装で差し替えていたため CI では検出不能」だった（Issue #436）。
/// 本テストは <c>msi.dll</c> 自身で本物の MSI データベース（OLE 構造化ストレージ + Property
/// テーブル）を生成し、読み取り実装の実体（P/Invoke 経路そのもの）を CI（windows-latest）で
/// 検証する——偽実装の差し替えでは原理的に検出できない「API の前提が実機で成立しない」型の
/// 欠陥を再び見逃さないため。
/// </para>
/// <para>
/// 非 Windows 実行時は <c>[SkippableFact]</c> で skip として明示する（silent pass にしない。
/// <c>Yagura.Storage.ConformanceTests</c> と同じ判断）。CI は windows-latest のため常に実行される。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ForwarderMsiProductVersionReaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public ForwarderMsiProductVersionReaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "yagura-msi-reader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 一時フォルダの掃除失敗はテスト結果に影響させない。
        }
    }

    [SkippableFact]
    public void TryRead_RealMsiDatabaseWithProductVersion_ReturnsIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "msi.dll（Windows Installer）が必要");

        var msiPath = Path.Combine(_tempDirectory, "with-version.msi");
        CreateMsiDatabase(msiPath, productVersion: "5.0.8");

        Assert.Equal("5.0.8", ForwarderMsiProductVersionReader.TryRead(msiPath));
    }

    [SkippableFact]
    public void TryRead_RealMsiDatabaseWithoutProductVersionRow_ReturnsNull()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "msi.dll（Windows Installer）が必要");

        var msiPath = Path.Combine(_tempDirectory, "without-version.msi");
        CreateMsiDatabase(msiPath, productVersion: null);

        Assert.Null(ForwarderMsiProductVersionReader.TryRead(msiPath));
    }

    [SkippableFact]
    public void TryRead_NonMsiFile_ReturnsNull()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "msi.dll（Windows Installer）が必要");

        var filePath = Path.Combine(_tempDirectory, "not-an-msi.msi");
        File.WriteAllBytes(filePath, [0x59, 0x41, 0x47, 0x55, 0x52, 0x41, 0x00, 0x01]);

        Assert.Null(ForwarderMsiProductVersionReader.TryRead(filePath));
    }

    [SkippableFact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "msi.dll（Windows Installer）が必要");

        Assert.Null(ForwarderMsiProductVersionReader.TryRead(Path.Combine(_tempDirectory, "missing.msi")));
    }

    /// <summary>
    /// <c>msi.dll</c> で本物の MSI データベースを生成する（Property テーブル +
    /// ProductVersion 行。<paramref name="productVersion"/> が null なら行を入れない）。
    /// </summary>
    private static void CreateMsiDatabase(string path, string? productVersion)
    {
        // MSIDBOPEN_CREATE = (LPCTSTR)3（msiquery.h の定義値）。
        Check(TestNativeMethods.MsiOpenDatabase(path, 3, out var database), "MsiOpenDatabase(create)");
        try
        {
            ExecuteSql(
                database,
                "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Property`)");

            if (productVersion is not null)
            {
                ExecuteSql(
                    database,
                    $"INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductVersion', '{productVersion}')");
            }

            Check(TestNativeMethods.MsiDatabaseCommit(database), "MsiDatabaseCommit");
        }
        finally
        {
            _ = TestNativeMethods.MsiCloseHandle(database);
        }
    }

    private static void ExecuteSql(uint database, string sql)
    {
        Check(TestNativeMethods.MsiDatabaseOpenView(database, sql, out var view), "MsiDatabaseOpenView");
        try
        {
            Check(TestNativeMethods.MsiViewExecute(view, 0), "MsiViewExecute");
        }
        finally
        {
            _ = TestNativeMethods.MsiCloseHandle(view);
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{operation} failed: {result}（テスト用 MSI の生成失敗）");
        }
    }

    /// <summary>
    /// テスト用 MSI 生成のための <c>msi.dll</c> P/Invoke 宣言（生成側のみ。読み取り側の宣言は
    /// 製品コード <see cref="ForwarderMsiProductVersionReader"/> が持つ——生成と読み取りの宣言を
    /// 共有しないことで、読み取り側の宣言ミスをテストが巻き添えで隠さない）。
    /// </summary>
    private static class TestNativeMethods
    {
        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out uint phDatabase);

        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint MsiDatabaseOpenView(uint hDatabase, string szQuery, out uint phView);

        [System.Runtime.InteropServices.DllImport("msi.dll")]
        public static extern uint MsiViewExecute(uint hView, uint hRecord);

        [System.Runtime.InteropServices.DllImport("msi.dll")]
        public static extern uint MsiDatabaseCommit(uint hDatabase);

        [System.Runtime.InteropServices.DllImport("msi.dll")]
        public static extern uint MsiCloseHandle(uint hAny);
    }
}
