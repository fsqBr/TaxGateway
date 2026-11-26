using System.Net;
using System.Net.Sockets;

namespace TaxGateway.Infrastructure.Webhooks;

public sealed class WebhookUrlRejectedException(string reason) : Exception(reason);

/// <summary>
/// Proteção contra SSRF nos webhooks (SDD §7; D-604): só https (http apenas em teste), sem credenciais na URL,
/// porta 443/8443 (ou qualquer uma em teste), e NENHUM endereço interno — checado no cadastro e de novo no
/// momento da conexão (ConnectCallback), o que fecha o DNS rebinding: o IP conectado é o IP validado.
/// </summary>
public static class SsrfGuard
{
    public static void ValidateUrl(Uri url, bool allowInsecure)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && !(allowInsecure && url.Scheme == Uri.UriSchemeHttp)))
        {
            throw new WebhookUrlRejectedException("A URL do webhook precisa ser https.");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            throw new WebhookUrlRejectedException("Credenciais na URL não são permitidas.");
        }

        if (!allowInsecure && url.Port is not (443 or 8443))
        {
            throw new WebhookUrlRejectedException("Porta não permitida (use 443 ou 8443).");
        }

        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal) && IsBlocked(literal))
        {
            throw new WebhookUrlRejectedException($"Endereço {literal} é interno/reservado.");
        }
    }

    /// <summary>Resolve o host e falha se QUALQUER endereço for interno (não basta o primeiro).</summary>
    public static async Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken ct)
    {
        IPAddress[] addrs;
        try
        {
            addrs = IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Host que não resolve: URL recusada (422), não erro interno (D-W13: achado ao gerar as capturas).
            throw new WebhookUrlRejectedException($"Host {host} não foi encontrado no DNS.");
        }

        if (addrs.Length == 0)
        {
            throw new WebhookUrlRejectedException($"Host {host} sem endereço.");
        }

        var blocked = addrs.FirstOrDefault(IsBlocked);
        if (blocked is not null)
        {
            throw new WebhookUrlRejectedException($"Host {host} resolve para endereço interno/reservado ({blocked}).");
        }

        return addrs;
    }

    public static bool IsBlocked(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b6[0] & 0xFE) == 0xFC                                  // fc00::/7 (ULA)
                                                                           // D-P15: prefixos que embutem um IPv4 (o roteador traduz para dentro da rede) ou não são roteáveis.
                || (b6[0] == 0x00 && b6[1] == 0x64 && b6[2] == 0xff && b6[3] == 0x9b)   // 64:ff9b::/96 e 64:ff9b:1::/48 (NAT64)
                || (b6[0] == 0x20 && b6[1] == 0x02)                                     // 2002::/16 (6to4)
                || (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x00 && b6[3] == 0x00)   // 2001::/32 (Teredo)
                || (b6[0] == 0x01 && b6[1] == 0x00 && b6.Skip(2).Take(6).All(x => x == 0))   // 100::/64 (descarte)
                || (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x0d && b6[3] == 0xb8)   // 2001:db8::/32 (documentação)
                || (b6[0] == 0x00 && b6.Take(12).All(x => x == 0));          // ::/96 (IPv4-compatível)
        }

        var b = ip.GetAddressBytes();
        return b[0] == 0                                   // 0.0.0.0/8
            || b[0] == 10                                  // 10/8
            || b[0] == 127                                 // loopback
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64/10 (CGNAT)
            || (b[0] == 169 && b[1] == 254)                // link-local e metadados de nuvem (169.254.169.254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16/12
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0/24
            || (b[0] == 192 && b[1] == 168)                // 192.168/16
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // 198.18/15 (benchmark)
            || (b[0] == 192 && b[1] == 0 && b[2] == 2)     // 192.0.2/24 (TEST-NET-1, documentação)
            || (b[0] == 198 && b[1] == 51 && b[2] == 100)  // 198.51.100/24 (TEST-NET-2)
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)   // 203.0.113/24 (TEST-NET-3)
            || b[0] >= 224;                                // multicast e reservados
    }

    /// <summary>Handler HTTP dos webhooks: sem redirect, sem proxy, conexão só a IPs validados.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowInsecure, string? testTrustedThumbprint = null) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        // Só em testes: certificado autoassinado do receptor local. Produção valida a cadeia TLS normalmente.
        // Só em testes: aceita o certificado autoassinado do receptor local por pinning de thumbprint. Produção: validação TLS padrão.
        SslOptions = allowInsecure && testTrustedThumbprint is not null
            ? new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) => errors == System.Net.Security.SslPolicyErrors.None
                    || (cert is not null && string.Equals(cert.GetCertHashString(), testTrustedThumbprint, StringComparison.OrdinalIgnoreCase)),
            }
            : new(),
        ConnectCallback = async (ctx, ct) =>
        {
            // Só em testes (AllowInsecureForTests): aceita o servidor local do teste. Produção: IP validado na conexão.
            var addrs = allowInsecure
                ? await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false)
                : await ResolveAllowedAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
