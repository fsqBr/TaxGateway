using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaxGateway.Infrastructure.Xml;

public enum RevocationStatus { Good, Revoked, Unknown }

public sealed record RevocationResult(RevocationStatus Status, string Detail);

/// <summary>LCR já baixada, com assinatura conferida contra o certificado emissor.</summary>
public sealed record ParsedCrl(Uri Source, string IssuerName, DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate,
    IReadOnlyDictionary<BigInteger, DateTimeOffset> Revoked);

/// <summary>
/// Revogação por LCR da ICP-Brasil (D-407) sem rede no caminho da requisição: o verificador só consulta o cache;
/// URLs novas (do CRLDistributionPoints dos certificados) entram numa fila e são baixadas em segundo plano, com a
/// assinatura da LCR conferida contra o emissor. Revogado com data ≤ emissão do documento = assinatura inválida
/// (revogação posterior não invalida um documento assinado antes). LCR ainda não disponível = "não verificada"
/// (aviso), ou falha se a política for estrita.
/// </summary>
public sealed partial class CrlCache(Func<Uri, CancellationToken, Task<byte[]>> fetch, ILogger<CrlCache> log) : BackgroundService
{
    private const int MaxCrlBytes = 32 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, ParsedCrl> _crls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, X509Certificate2> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);

    public static IReadOnlyList<Uri> DistributionPoints(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        var ext = cert.Extensions["2.5.29.31"];
        var urls = new List<Uri>();
        if (ext is null)
        {
            return urls;
        }

        try
        {
            var seq = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
            while (seq.HasData)
            {
                var dp = seq.ReadSequence();
                if (!dp.HasData || !dp.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    continue;
                }

                var name = dp.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                if (!name.HasData || !name.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    continue;
                }

                var full = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                while (full.HasData)
                {
                    var tag = full.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                    {
                        var s = full.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
                        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                        {
                            urls.Add(u);
                        }
                    }
                    else
                    {
                        full.ReadEncodedValue();
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // extensão malformada: sem pontos de distribuição
        }

        return urls;
    }

    /// <summary>Consulta só o cache. Agenda o download das LCRs que faltam.</summary>
    public RevocationResult Check(X509Certificate2 cert, X509Certificate2 issuer, DateTimeOffset signingTime)
    {
        ArgumentNullException.ThrowIfNull(cert);
        ArgumentNullException.ThrowIfNull(issuer);
        var urls = DistributionPoints(cert);
        if (urls.Count == 0)
        {
            return new(RevocationStatus.Unknown, $"Certificado {cert.Subject} sem ponto de distribuição de LCR.");
        }

        foreach (var url in urls)
        {
            if (_crls.TryGetValue(url.AbsoluteUri, out var crl))
            {
                var serial = new BigInteger(cert.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true);
                if (crl.Revoked.TryGetValue(serial, out var when) && when <= signingTime)
                {
                    return new(RevocationStatus.Revoked, $"Certificado revogado em {when:yyyy-MM-dd} (LCR {url}).");
                }

                return new(RevocationStatus.Good, $"LCR de {crl.ThisUpdate:yyyy-MM-dd HH:mm}.");
            }

            if (_pending.TryAdd(url.AbsoluteUri, issuer))
            {
                _signal.Release();
            }
        }

        return new(RevocationStatus.Unknown, "LCR ainda não disponível no cache (download em segundo plano).");
    }

    /// <summary>Carrega uma LCR conferindo a assinatura com o emissor (usado pelo worker e pelos testes).</summary>
    public ParsedCrl Load(Uri source, byte[] der, X509Certificate2 issuer)
    {
        var crl = Parse(source, der, issuer);
        _crls[source.AbsoluteUri] = crl;
        return crl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var refresh = new PeriodicTimer(TimeSpan.FromHours(1));
        var tick = refresh.WaitForNextTickAsync(stoppingToken).AsTask();
        while (!stoppingToken.IsCancellationRequested)
        {
            var signal = _signal.WaitAsync(stoppingToken);
            var done = await Task.WhenAny(signal, tick).ConfigureAwait(false);
            if (done == tick)
            {
                // Renova as LCRs perto do nextUpdate.
                foreach (var c in _crls.Values.Where(c => c.NextUpdate is null || c.NextUpdate < DateTimeOffset.UtcNow.AddHours(2)))
                {
                    if (FindIssuer(c) is { } issuer)
                    {
                        _pending.TryAdd(c.Source.AbsoluteUri, issuer);
                    }
                }

                tick = refresh.WaitForNextTickAsync(stoppingToken).AsTask();
            }

            foreach (var (url, issuer) in _pending.ToArray())
            {
                try
                {
                    var bytes = await fetch(new Uri(url), stoppingToken).ConfigureAwait(false);
                    if (bytes.Length > MaxCrlBytes)
                    {
                        throw new InvalidDataException("LCR acima do limite.");
                    }

                    Load(new Uri(url), bytes, issuer);
                    _pending.TryRemove(url, out _);
                }
                catch (Exception ex) when (ex is HttpRequestException or CryptographicException or AsnContentException or InvalidDataException or TaskCanceledException
                                           && !stoppingToken.IsCancellationRequested)
                {
                    LogFetchFailed(log, ex, url);
                }
            }
        }
    }

    private X509Certificate2? FindIssuer(ParsedCrl c) => _pending.Values.FirstOrDefault(i => i.Subject == c.IssuerName);

    private static ParsedCrl Parse(Uri source, byte[] der, X509Certificate2 issuer)
    {
        var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        var tbsBytes = outer.PeekEncodedValue().ToArray();
        var tbs = outer.ReadSequence();
        var sigAlg = outer.ReadSequence().ReadObjectIdentifier();
        var signature = outer.ReadBitString(out _);

        var hash = sigAlg switch
        {
            "1.2.840.113549.1.1.11" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" => HashAlgorithmName.SHA512,
            "1.2.840.113549.1.1.5" => HashAlgorithmName.SHA1,
            _ => throw new CryptographicException($"Algoritmo de LCR não suportado: {sigAlg}"),
        };
        using var key = issuer.GetRSAPublicKey() ?? throw new CryptographicException("Emissor sem chave RSA.");
        if (!key.VerifyData(tbsBytes, signature, hash, RSASignaturePadding.Pkcs1))
        {
            throw new CryptographicException($"Assinatura da LCR {source} não confere com {issuer.Subject}.");
        }

        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
        {
            tbs.ReadInteger();   // versão
        }

        tbs.ReadSequence();                                   // algoritmo (repetido)
        var issuerName = new X500DistinguishedName(tbs.ReadEncodedValue().ToArray()).Name;
        var thisUpdate = ReadTime(tbs);
        DateTimeOffset? nextUpdate = tbs.HasData && IsTime(tbs.PeekTag()) ? ReadTime(tbs) : null;
        var revoked = new Dictionary<BigInteger, DateTimeOffset>();
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var list = tbs.ReadSequence();
            while (list.HasData)
            {
                var entry = list.ReadSequence();
                var serial = entry.ReadInteger();
                revoked[BigInteger.Abs(serial)] = ReadTime(entry);
            }
        }

        return new ParsedCrl(source, issuerName, thisUpdate, nextUpdate, revoked);
    }

    private static bool IsTime(Asn1Tag t) => t.HasSameClassAndValue(Asn1Tag.UtcTime) || t.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

    private static DateTimeOffset ReadTime(AsnReader r) =>
        r.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? r.ReadUtcTime() : r.ReadGeneralizedTime();

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao baixar/validar a LCR {Url}; nova tentativa no próximo ciclo.")]
    private static partial void LogFetchFailed(ILogger logger, Exception ex, string url);
}
