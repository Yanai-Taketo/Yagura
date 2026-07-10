using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Yagura.Ingestion.Net;

namespace Yagura.Ingestion.Tcp;

/// <summary>
/// IPv6 ワイルドカード（<c>::</c>）向けの DualMode <see cref="TcpListener"/> を作成する共通処理
/// （Issue #133 で <see cref="TcpSyslogListener"/> 向けに実装されたものを、TLS 受信リスナ
/// （<see cref="Yagura.Ingestion.Tls.TlsSyslogListener"/>。Issue #137）と共有するために抽出した）。
/// </summary>
/// <remarks>
/// IPv6 スタックが無効な環境（PR #193 レビュー指摘 Major）の分岐は UDP 側
/// （<c>UdpSyslogListener.CreateDualModeUdpClientOrFallBack</c>）と対称: 既定値
/// （<paramref name="bindAddressIsExplicit"/> = <c>false</c>）なら IPv4 ワイルドカードへ自動縮小して
/// 警告ログを出し、明示指定なら復旧手順を含むエラーで起動を失敗させる。
/// </remarks>
internal static class DualStackTcpListenerFactory
{
    /// <summary>
    /// <c>::</c>（IPv6 ワイルドカード）で bind した DualMode <see cref="TcpListener"/> を作成し、
    /// <see cref="TcpListener.Start()"/> まで行う。IPv6 が利用できない環境では
    /// <paramref name="bindAddressIsExplicit"/> に応じて縮小継続または fail-fast する。
    /// </summary>
    public static TcpListener CreateOrFallBack(int port, bool bindAddressIsExplicit, ILogger? logger)
    {
        // 事前チェック: OS が IPv6 を提供しない環境ではソケット作成の実試行を待たずに分岐する。
        if (!Socket.OSSupportsIPv6)
        {
            return HandleIPv6Unavailable(port, bindAddressIsExplicit, logger, socketException: null);
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, port);

            // DualMode ソケット（Issue #133）。TcpListener.Server は Start() 前であれば
            // 直接設定できる（.NET の確立されたパターン——Kestrel 側の同種の扱いは
            // Yagura.Host.ListenerBindPlan の remarks 参照）。
            listener.Server.DualMode = true;
            listener.Start();
            return listener;
        }
        catch (SocketException ex)
        {
            listener?.Dispose();

            // 縮小の対象は「IPv6 が使えない」場合のみ。ポート競合（AddressInUse）等の
            // 別要因を IPv4 縮小で握り潰すと、ポート事故が黙って「IPv4 のみ受信」に化ける
            // （DualStackBindAddress の remarks）。
            if (ex.SocketErrorCode != SocketError.AddressFamilyNotSupported)
            {
                throw;
            }

            return HandleIPv6Unavailable(port, bindAddressIsExplicit, logger, ex);
        }
        catch
        {
            listener?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// IPv6 不可（事前チェックまたは bind 実試行での確定）時の分岐: 既定値なら IPv4 縮小、
    /// 明示指定なら fail-fast（UDP 側と対称）。
    /// </summary>
    private static TcpListener HandleIPv6Unavailable(
        int port,
        bool bindAddressIsExplicit,
        ILogger? logger,
        SocketException? socketException)
    {
        if (bindAddressIsExplicit)
        {
            throw new InvalidOperationException(
                DualStackBindAddress.BuildExplicitIPv6WildcardUnavailableMessage(),
                socketException);
        }

        // 警告はイベントログに届くレベル（Warning）で出す——既定構成の縮小は無言にしない。
        logger?.LogWarning(
            socketException,
            "この環境では IPv6 が利用できないため、受信リスナは既定の '::'（IPv4/IPv6 両受信）" +
            "ではなく IPv4 のみ（0.0.0.0）で受信します。IPv6 の syslog を受信する必要がある場合は" +
            " OS の IPv6 スタックを有効化してください。");

        var fallback = new TcpListener(IPAddress.Any, port);
        fallback.Start();
        return fallback;
    }
}
