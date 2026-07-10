using System.Net;
using System.Net.Sockets;
using Yagura.Host.Configuration;
using Yagura.Web;

namespace Yagura.Host;

/// <summary>
/// 閲覧・管理の両リスナの bind 先一式を、実際の Kestrel 構成（<c>ConfigureKestrel</c>）から
/// 切り離して計算する（M6-1。Issue #51）。
/// </summary>
/// <remarks>
/// <para>
/// <b>切り出した理由</b>: <see cref="Program"/> 内で直接 <c>KestrelServerOptions</c> を
/// 操作すると、「管理リスナの bind 先が常に loopback になる」という不変条件
/// （configuration.md §1・security.md §1 L-4）を実サーバ起動なしに単体テストで検証できない。
/// 本クラスは bind 先の"計算"だけを純粋関数として切り出し、<see cref="Program"/> 側は
/// 本クラスが返す <see cref="ListenerBindEntry"/> の一覧をそのまま <c>Listen</c>/<c>ListenAnyIP</c>
/// へ渡すだけにする。
/// </para>
/// <para>
/// <b>全インターフェース bind の表現</b>: <see cref="ListenerBindEntry.IsAnyIP"/> が
/// <see langword="true"/> の場合、呼び出し側は <c>KestrelServerOptions.ListenAnyIP(port)</c>
/// を使うこと（<c>Listen(IPAddress.Any, port)</c> と <c>Listen(IPAddress.IPv6Any, port)</c> を
/// 両方呼ぶと、Kestrel のソケットトランスポート層が <c>IPv6Any</c> bind に <c>DualMode = true</c>
/// を設定するため <c>AddressInUseException</c> になる——dotnet/aspnetcore の
/// <c>SocketTransportOptions.CreateDefaultBoundListenSocket</c> 実装より。確認日 2026-07-05）。
/// <see cref="ListenerBindEntry.Address"/> はこの場合 <see langword="null"/> になる。
/// </para>
/// </remarks>
public static class ListenerBindPlan
{
    /// <summary>
    /// 設定から解決済みの <see cref="ResolvedYaguraConfiguration"/> を基に、閲覧・管理
    /// 両リスナの bind 先一式を計算する。
    /// </summary>
    public static IReadOnlyList<ListenerBindEntry> Create(ResolvedYaguraConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var entries = new List<ListenerBindEntry>();

        if (configuration.ViewerPublicAccess == ViewerPublicAccess.Lan)
        {
            entries.Add(ListenerBindEntry.AnyIP(ListenerKind.Viewer, configuration.HttpPort));
        }
        else
        {
            // loopback の両系統(IPv4/IPv6)を同一ポートで bind する必要がある(閲覧 URL の
            // 構築・LocalPort 判定が単一のポート番号を前提とするため)。0(OS 採番。テスト用)の
            // 場合、Listen(Loopback, 0) と Listen(IPv6Loopback, 0) を独立に呼ぶと OS が
            // それぞれ別々のエフェメラルポートを採番し得る(bind(0) はソケットごとに独立——
            // 2 回の呼び出しが同じポートになる保証は POSIX/Winsock のいずれにもない)。
            // 事前に 1 回だけポートを予約し、その具体値を両系統へ渡すことで一致させる。
            var port = ResolvePortForDualStackLoopback(configuration.HttpPort);
            entries.Add(ListenerBindEntry.Specific(ListenerKind.Viewer, IPAddress.Loopback, port));
            entries.Add(ListenerBindEntry.Specific(ListenerKind.Viewer, IPAddress.IPv6Loopback, port));
        }

        // 管理リスナ: 設定値（ViewerPublicAccess 等）に一切依存せず、常に loopback の両系統を
        // 直接構築する——configuration.md §1 の不変条件（「設定がどう壊れていても管理リスナは
        // loopback 以外へ束縛されない」）をコードの構造そのもので保証する。
        // ポート 0(OS 採番)の場合は上記と同じ理由で事前に 1 回だけ予約する。
        var adminPort = ResolvePortForDualStackLoopback(configuration.AdminHttpPort);
        entries.Add(ListenerBindEntry.Specific(ListenerKind.Admin, IPAddress.Loopback, adminPort));
        entries.Add(ListenerBindEntry.Specific(ListenerKind.Admin, IPAddress.IPv6Loopback, adminPort));

        // 管理リスナのリモートバインド（ADR-0010 Phase 2 決定 1）: 上記 loopback 2 エントリを
        // 置き換えるのではなく、別ポート（既定 8516）への AnyIP + HTTPS エントリを追加する。
        // 理由: (1) OS の bind 制約——同一ポートでワイルドカード(AnyIP) bind と特定アドレス
        // （loopback）bind は共存できない（Windows は先に bind した側がポート全体を排他的に
        // 占有する）ため、loopback を残したまま「別ポート」で remote を提供する以外に両立の
        // 手段がない。(2) ADR-0010 Phase 2 決定 4「loopback 経由の管理リスナは HTTPS の対象外の
        // まま残る」——証明書の期限切れ・失効時に管理リスナ全体が道連れにならず、RDP + loopback
        // からの復旧が常に残ることを bind 構成そのもので保証する。
        // 到達可否は YaguraConfigurationLoader.Load の fail-closed 検証（認証・HTTPS が両方
        // 構成済みであること）で既に保証済みだが、実際の証明書ストア参照の成否は環境依存
        // （証明書ストアの状態）のため、ここでは呼び出し元（Program）が RequiresHttps エントリを
        // 実際に bind するかどうかを証明書解決の成否で判断する（本メソッドは常にエントリを返す——
        // configuration.md §1「起動失敗」の対象ではない縮小継続の判断は Program 側に委ねる）。
        if (configuration.AdminRemoteBindingEnabled)
        {
            entries.Add(ListenerBindEntry.AnyIP(ListenerKind.Admin, configuration.AdminHttpsPort, requiresHttps: true));
        }

        return entries;
    }

    /// <summary>
    /// <paramref name="requestedPort"/> が <c>0</c>（OS 採番。テスト用）でなければそのまま返す。
    /// <c>0</c> の場合、IPv4 loopback へ一時的に bind してポートを 1 個だけ予約し、
    /// その具体的なポート番号を返す（bind 直後に解放する「予約してから離す」手法。
    /// TOCTOU の狭い競合は残るが、テスト/CI 用途の 0 指定に限られるため許容する）。
    /// </summary>
    /// <remarks>
    /// これにより、呼び出し側は <c>Listen(IPAddress.Loopback, port)</c> と
    /// <c>Listen(IPAddress.IPv6Loopback, port)</c> の 2 回の <c>Listen</c> 呼び出しに
    /// 同一の具体ポート番号を渡せる——OS の独立したエフェメラルポート採番に頼らない。
    /// </remarks>
    private static int ResolvePortForDualStackLoopback(int requestedPort)
    {
        if (requestedPort != 0)
        {
            return requestedPort;
        }

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}

/// <summary>1 つの bind 先（アドレス + ポート、またはワイルドカード + ポート）。</summary>
public sealed record ListenerBindEntry
{
    private ListenerBindEntry(ListenerKind kind, IPAddress? address, int port, bool isAnyIP, bool requiresHttps)
    {
        Kind = kind;
        Address = address;
        Port = port;
        IsAnyIP = isAnyIP;
        RequiresHttps = requiresHttps;
    }

    /// <summary>このエントリが帰属するリスナの種別。</summary>
    public ListenerKind Kind { get; }

    /// <summary>bind するアドレス。<see cref="IsAnyIP"/> が <see langword="true"/> の場合は <see langword="null"/>。</summary>
    public IPAddress? Address { get; }

    /// <summary>bind するポート番号。</summary>
    public int Port { get; }

    /// <summary>
    /// <see langword="true"/> の場合、呼び出し側は <c>KestrelServerOptions.ListenAnyIP(Port)</c>
    /// を使う（<see cref="Address"/> は参照しない）。
    /// </summary>
    public bool IsAnyIP { get; }

    /// <summary>
    /// <see langword="true"/> の場合、このエントリは HTTPS 必須（ADR-0010 Phase 2 決定 4。
    /// 管理リスナのリモートバインド面）。呼び出し側（<c>Program</c>）は証明書を解決できた場合のみ
    /// <c>UseHttps</c> 付きで bind し、解決できなければこのエントリを縮小継続としてスキップする
    /// （<see cref="ConfigurationEventIds.AdminHttpsCertificateUnavailableAtStartup"/>）。
    /// </summary>
    public bool RequiresHttps { get; }

    public static ListenerBindEntry Specific(ListenerKind kind, IPAddress address, int port, bool requiresHttps = false) =>
        new(kind, address, port, isAnyIP: false, requiresHttps);

    public static ListenerBindEntry AnyIP(ListenerKind kind, int port, bool requiresHttps = false) =>
        new(kind, address: null, port, isAnyIP: true, requiresHttps);
}
