using System.Runtime.InteropServices;

namespace Yagura.Bench.HostProcess;

/// <summary>
/// 子プロセスへ Ctrl+C（<c>CTRL_C_EVENT</c>）を送出し、.NET Generic Host のグレースフル停止
/// （<c>ConsoleLifetime</c> のコンソールイベントハンドラ経由の <c>StopApplication()</c>）を
/// 起動する（Issue #60）。
/// </summary>
/// <remarks>
/// <para>
/// <b>この実装が必要な理由</b>: tests/Yagura.E2E.Tests は smoke テストの検証観点上
/// <c>Process.Kill(entireProcessTree: true)</c> で足りると判断していた（ZeroConfigFirstRunE2ETests
/// のコメント「可能なら SIGTERM 相当、無理なら Kill でよい」）。しかし本ベンチの検証器は
/// architecture.md §1.3 停止手順 3「カウンタを最終値で永続化し…」が実行されることに依存する
/// （<see cref="Yagura.Host.IngestionHostedService.StopAsync"/> が
/// <c>ObservabilityCoordinator.WriteStopStep3</c> で最終カウンタを書く）。<c>Kill</c> は
/// この停止シーケンスを実行させないため、直近の定期永続化（既定 10 秒間隔）以降に発生した
/// 破棄が観測できず、「送信数 = 保存件数 + 全カウンタの合計」の突合が実機検証で不成立になった
/// （バースト直後に Kill すると、まだメタデータ領域に書かれていない破棄が差分として現れる）。
/// そのため本ベンチはグレースフル停止を実装する。
/// </para>
/// <para>
/// <b>採用した方式</b>: <c>AttachConsole(childPid)</c> で子プロセスのコンソール（子は
/// <c>Process.Start</c> の既定どおり独自のコンソール・プロセスグループを持つ——親と
/// コンソールを共有しない）に一時的にアタッチし、<c>SetConsoleCtrlHandler(NULL, TRUE)</c> で
/// 自プロセス（ベンチ自身）がこのイベントで終了しないよう無視設定したうえで
/// <c>GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)</c>（グループ ID 0 = アタッチ中のコンソールの
/// 全プロセスへ配送）を送出する。送出後は <c>FreeConsole</c> でデタッチする——この手順は
/// Microsoft のサンプル（"Console Ctrl Handling" 系のイベントを Windows サービス/CLI ツールから
/// 他プロセスへ送る定番パターン。<c>CREATE_NEW_PROCESS_GROUP</c> 付きで子を起動し直す方式より、
/// 既存の <see cref="System.Diagnostics.Process"/> ベースの起動・標準出力リダイレクト実装
/// （<see cref="BenchHostProcess"/> の tests/Yagura.E2E.Tests 由来のパターン）をそのまま
/// 流用できる利点がある。
/// </para>
/// <para>
/// <b>.NET 側の受信</b>: Generic Host の既定 <c>ConsoleLifetime</c> は
/// <c>Console.CancelKeyPress</c>（<c>CTRL_C_EVENT</c>・<c>CTRL_BREAK_EVENT</c> の両方で発火）を
/// 購読し、<c>IHostApplicationLifetime.StopApplication()</c> を呼ぶ（Microsoft Learn
/// ".NET Generic Host" の "ConsoleLifetime" 節の記載どおり。確認日 2026-07-05）。これにより
/// <see cref="Yagura.Host.IngestionHostedService.StopAsync"/> が通常のグレースフル停止経路
/// （SCM 経由の停止と同じ順序）で実行される。
/// </para>
/// <para>
/// <b>既知の環境依存の限界</b>: <c>GenerateConsoleCtrlEvent</c> の公式ドキュメント
/// （learn.microsoft.com/windows/console/generateconsolectrlevent、確認日 2026-07-05）は
/// 「グループ内でも呼び出し元プロセスと同じコンソールを共有するプロセスのみがシグナルを
/// 受信する」と明記している。<b>呼び出し元（本ベンチプロセス自身）が実 Win32 コンソールを
/// 持たない環境では、<c>AttachConsole</c> 自体は成功してもシグナルが子プロセスへ実際には
/// 配送されないことを実機で確認した</b>——具体的には Git Bash（MinTTY/ConPTY）経由での実行、
/// および <c>dotnet test</c>（VSTest テストホストプロセス）経由での実行の両方で
/// <see cref="TrySendCtrlC"/> は <c>true</c> を返すにもかかわらず子プロセスの
/// <c>CancelKeyPress</c> が発火しない事象を観測した。一方、Windows PowerShell（実コンソールを
/// 持つ）から直接 <c>Yagura.Bench.dll</c> を起動した場合は正しく機能することを確認済み。
/// <see cref="BenchHostProcess.StopGracefullyAsync"/> はこの限界を踏まえ、Ctrl+C 送出後に
/// 子プロセスが実際に終了しない場合は <c>Kill</c> へフォールバックする（フォールバック時の
/// 突合精度低下は <see cref="BenchHostProcess.GracefulStopSucceeded"/> で検知可能にする）。
/// 実運用（通常のコンソールからの CLI 実行）では本パスが機能することを優先し、テスト実行環境の
/// 制約はフォールバックで吸収する設計とした。
/// </para>
/// </remarks>
internal static class ConsoleCtrlSender
{
    private const uint CtrlCEvent = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    /// <summary>
    /// 指定プロセスのコンソールへ一時的にアタッチし、<c>CTRL_C_EVENT</c> を送出する。
    /// 送出成功可否を返す（失敗時は呼び出し側が Kill へフォールバックする想定）。
    /// </summary>
    /// <remarks>
    /// <b>NULL ハンドラを外すタイミングに関する実機での発覚</b>: 当初の実装は
    /// <c>GenerateConsoleCtrlEvent</c> 呼び出し直後の <c>finally</c> で
    /// <c>SetConsoleCtrlHandler(IntPtr.Zero, add: false)</c>（NULL ハンドラの除去）を行っていたが、
    /// 実機検証でベンチプロセス自身が終了コード 130（Ctrl+C 相当）で異常終了する事象が発生した。
    /// <c>GenerateConsoleCtrlEvent</c> は非同期にシグナルを配送する（呼び出しの戻り値は「配送を
    /// 試みた」ことを示すのみで、配送・各プロセスでの処理完了を待たない）ため、NULL ハンドラを
    /// 早期に除去すると、シグナルがベンチプロセス自身に実際に届く前に既定の Ctrl+C 動作
    /// （プロセス終了）へ戻ってしまい、ベンチ自身が終了する競合が起きていたと考えられる
    /// （公式ドキュメントは配送のタイミング保証を明記していない——本コメントは実機観測に基づく
    /// 判断であり、推測を実装の正当化根拠にしない方針に従い、対症療法として「除去しない」を
    /// 採用した）。本メソッドはプロセスごとに 1 回だけ・ベンチの寿命の終盤で呼ばれる想定
    /// （<see cref="Yagura.Bench.Scenarios.ScenarioRunner.StopAndReconcileAsync"/> 参照）であり、
    /// 以後ベンチ自身が Ctrl+C を受ける場面はないため、NULL ハンドラを除去せず放置しても実害はない。
    /// </remarks>
    public static bool TrySendCtrlC(int processId)
    {
        // 自プロセス（コンソールを保持している場合）を先にデタッチする——同一コンソールへの
        // 二重アタッチは失敗するため（AttachConsole の公式ドキュメント "A process can be
        // attached to at most one console"。確認日 2026-07-05）。
        FreeConsole();

        if (!AttachConsole((uint)processId))
        {
            ReattachParentConsoleAndRebindStreams();
            return false;
        }

        // ベンチ自身（= アタッチ後は子と同じコンソールの一員）がこのイベントで終了しない
        // よう、NULL ハンドラを追加して無視する（公式ドキュメント "Handling Ctrl+C" の定石。
        // 確認日 2026-07-05）。上記 remarks のとおり、意図的に除去しない。
        SetConsoleCtrlHandler(IntPtr.Zero, add: true);

        var sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);

        // FreeConsole 自体は安全に呼べる（NULL ハンドラの除去とは独立した操作）。
        FreeConsole();

        ReattachParentConsoleAndRebindStreams();

        return sent;
    }

    private const uint AttachParentProcess = unchecked((uint)-1);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    // いずれも FreeConsole の実行前(本クラスの型初期化時 = TrySendCtrlC 呼び出しの前)に確定させる。
    // FreeConsole 後の初参照では無効化されたハンドルに対する判定になり誤る。
    //
    // HadConsoleAtStartup: 起動時に実コンソール(ウィンドウ)を保持していたか。これが false の環境
    // (パイプ経由・ツール経由の実行)では、末尾の「親コンソールへの再アタッチ + 再バインド」を
    // 一切行わない——行うと以後の全出力が消失することを実機確認した(2026-07-05。この環境では
    // FreeConsole 後に何もしない従来挙動で出力・グレースフル停止とも正常に機能する)。
    // 再アタッチが必要なのは「実コンソールから対話実行していて、FreeConsole で自分のコンソール
    // ハンドルが死ぬ」場合のみである。
    private static readonly bool HadConsoleAtStartup = GetConsoleWindow() != IntPtr.Zero;
    private static readonly bool OutputWasRedirected = Console.IsOutputRedirected;
    private static readonly bool ErrorWasRedirected = Console.IsErrorRedirected;

    /// <summary>
    /// 元（親プロセス）のコンソールへ再接続し、.NET の Console ストリームを結び直す。
    /// </summary>
    /// <remarks>
    /// <b>これを怠ると、実コンソールからの対話実行時に以後の <c>Console.WriteLine</c> が
    /// 未処理例外でクラッシュする（オーナー実機で発生した実障害。exit code 0xE0434352 =
    /// CLR 未処理例外）</b>: ベンチの stdout が実コンソールのハンドルだった場合、
    /// <c>FreeConsole</c> は自プロセスのコンソールハンドルを閉じるため（公式ドキュメント
    /// learn.microsoft.com/windows/console/freeconsole。確認日 2026-07-05）、.NET が起動時に
    /// キャッシュした <c>Console.Out</c> の下位ハンドルが無効になる。結果ファイル(JSON/サマリ)の
    /// 書き込みは成功した後、コンソールへのサマリ出力で落ちる——「ファイルはあるのに終了コードが
    /// 異常」という紛らわしい形で現れる。<c>AttachConsole(ATTACH_PARENT_PROCESS)</c> で元の
    /// コンソールへ戻し、<c>Console.SetOut/SetError</c> でストリームを開き直すことで解消する
    /// (stdout がパイプへリダイレクトされている環境——CI・ツール経由の実行——ではパイプは
    /// <c>FreeConsole</c> の影響を受けず、開き直しても同じパイプが返るだけで無害)。
    /// </remarks>
    private static void ReattachParentConsoleAndRebindStreams()
    {
        if (!HadConsoleAtStartup)
        {
            // パイプ・ツール経由の実行(実コンソールなし)では何もしない——従来挙動が正常に
            // 機能しており、AttachConsole(親) を呼ぶとかえって出力経路が壊れることを実機確認
            // (HadConsoleAtStartup のコメント参照)。
            return;
        }

        AttachConsole(AttachParentProcess);

        // 再バインドは「stdout/stderr が実コンソールだった場合」に限定する。パイプへ
        // リダイレクトされている場合(CI・ツール経由)、既存の Console.Out が保持するパイプは
        // FreeConsole の影響を受けず正常なままで、逆に開き直すと壊れたハンドルに差し替わり
        // 以後の出力が消失する。判定は FreeConsole 前に確定した静的フィールドを使う
        // (OutputWasRedirected のコメント参照)。
        if (!OutputWasRedirected)
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
        }

        if (!ErrorWasRedirected)
        {
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
        }
    }
}
