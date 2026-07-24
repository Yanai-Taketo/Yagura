using System.ComponentModel;
using Yagura.Storage.SqlServer;

namespace Yagura.Storage.Tests;

/// <summary>
/// Windows 統合認証での接続失敗の一次切り分け
/// （<see cref="SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore"/>）の単体テスト
/// （ADR-0015 決定 5 の観測性要件。database.md §6.1。Issue #418）。
/// </summary>
/// <remarks>
/// <see cref="Microsoft.Data.SqlClient.SqlException"/> は公開コンストラクタを持たずテストから
/// 生成できないため、番号 + 内側例外による分類コアを直接検証する
/// （<c>SqlConnectionFailureClassifierTests</c> と同じ方式）。番号と失敗形の対応は SEC-14 (a)/(c)
/// の AD lab 実測（2026-07-24。ADR-0015 改訂履歴 2）で確認済み。
/// </remarks>
public sealed class SqlServerIntegratedAuthFailureClassifierTests
{
    // lab 実測（2026-07-24）で DC 停止時の SSPI 失敗の内側 Win32Exception に観測された値。
    private const int SecETargetUnknown = unchecked((int)0x80090303);

    [Fact]
    public void Core_LoginFailed18456_IsSqlServerOrigin()
    {
        var classified = SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore(18456, innerException: null);

        Assert.NotNull(classified);
        Assert.Equal(IntegratedAuthFailureOrigin.SqlServer, classified.Value.Origin);
        Assert.Contains("18456", classified.Value.Description);
        Assert.Contains("SQL Server 起因", classified.Value.Description);
    }

    [Fact]
    public void Core_CannotOpenDatabase4060_IsSqlServerOrigin()
    {
        var classified = SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore(4060, innerException: null);

        Assert.NotNull(classified);
        Assert.Equal(IntegratedAuthFailureOrigin.SqlServer, classified.Value.Origin);
        Assert.Contains("4060", classified.Value.Description);
        Assert.Contains("SQL Server 起因", classified.Value.Description);
    }

    [Fact]
    public void Core_SspiFailure_Number0WithNestedWin32Inner_IsDomainControllerOrigin_AndTranscribesWin32Code()
    {
        // lab 実測の形: Number=0、内側（ネストされ得る）に Win32Exception（SEC_E_TARGET_UNKNOWN）。
        var inner = new InvalidOperationException("outer", new Win32Exception(SecETargetUnknown));

        var classified = SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore(0, inner);

        Assert.NotNull(classified);
        Assert.Equal(IntegratedAuthFailureOrigin.DomainController, classified.Value.Origin);
        Assert.Contains("DC 起因", classified.Value.Description);
        // SEC_E コードは状況により異なり得るため個別コードで分岐しないが、事後調査のため
        // 本文へ転写する（ADR-0015 改訂履歴 2 の決定）。
        Assert.Contains("0x80090303", classified.Value.Description);
    }

    [Fact]
    public void Core_Number0WithoutWin32Inner_IsUnclassified()
    {
        // Number=0 でも内側に Win32Exception が無い形は lab で観測した SSPI 失敗の形ではない——
        // DC 起因と断定せず未分類（発火点は従来どおり 1030 で扱う安全側）。
        Assert.Null(SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore(
            0, new InvalidOperationException("client-side failure without win32 inner")));
        Assert.Null(SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore(0, innerException: null));
    }

    [Theory]
    [InlineData(1205)]  // デッドロック（Transient）——接続失敗ではない
    [InlineData(10061)] // 接続拒否（ネットワーク到達性）——統合認証固有の失敗ではない
    [InlineData(229)]   // ステートメント権限不足——接続は成立している
    public void Core_OtherNumbers_AreUnclassified(int number)
    {
        Assert.Null(SqlServerFailureClassifier.ClassifyIntegratedAuthFailureCore(number, innerException: null));
    }
}
