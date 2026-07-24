using System.Runtime.Versioning;

namespace Yagura.Web.ForwarderKit;

/// <summary>
/// MSI の ProductVersion を読み取る共用実装（ADR-0008 設計条件 9「ProductVersion を優先」）。
/// 検出側（<see cref="SystemForwarderMsiSource"/>）とアップロード側
/// （<see cref="SystemForwarderMsiStore"/>。ADR-0020 決定 3）の両方が同一の読み取り経路を使う——
/// 配置時と検出時で版判定の実装が食い違わないための単一ソース。
/// </summary>
/// <remarks>
/// <para>
/// <b>方式</b>: <c>MsiGetFileVersionW</c>（<c>msi.dll</c>）は MSI ファイル自身に対しても呼び出す
/// ことができ、MSI の <c>ProductVersion</c> プロパティ相当の値を直接返す（Windows Installer SDK の
/// ドキュメント上の契約——インストールされた実行可能ファイルだけでなくパッケージファイル自体の
/// 版取得にも使える）。P/Invoke 1 関数のみで完結し、<c>MsiOpenDatabase</c> 系のハンドル管理を
/// 避けられる（採用判断の全文は <see cref="SystemForwarderMsiSource"/> 導入時の記録を参照）。
/// </para>
/// <para>
/// 呼び出しが失敗した場合（0 以外の戻り値）・<c>msi.dll</c> が存在しない実行環境では
/// <see langword="null"/> を返す。null の扱いは呼び出し側の責務——検出側はファイル名由来の版へ
/// フォールバックし（補助手段）、アップロード側は拒否する（クライアント申告のファイル名を
/// 信用しないため、版の根拠が ProductVersion 以外に存在しない——ADR-0020 決定 3）。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ForwarderMsiProductVersionReader
{
    internal static string? TryRead(string filePath)
    {
        try
        {
            var versionBuffer = new System.Text.StringBuilder(64);
            var versionSize = (uint)versionBuffer.Capacity;

            var result = NativeMethods.MsiGetFileVersion(filePath, versionBuffer, ref versionSize, null, ref versionSize);

            // ERROR_SUCCESS = 0。ERROR_MORE_DATA (234) 等の失敗は null（呼び出し側の責務で処理）。
            return result == 0 && versionBuffer.Length > 0 ? versionBuffer.ToString() : null;
        }
        catch (DllNotFoundException)
        {
            // msi.dll が存在しない実行環境（理論上は Windows には存在するが、テスト実行環境等の
            // 予期しない構成を安全側で吸収する）。
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows Installer の <c>msi.dll</c> P/Invoke 宣言（<c>MsiGetFileVersion</c> のみ使用）。
    /// </summary>
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern uint MsiGetFileVersion(
            string szFilePath,
            System.Text.StringBuilder? lpVersionBuf,
            ref uint pcchVersionBuf,
            System.Text.StringBuilder? lpLangBuf,
            ref uint pcchLangBuf);
    }
}
