using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaxGateway.Infrastructure.Dfe;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.Infrastructure.Tenancy;

/// <summary>Certificado conferido: titular, CNPJ e validade (a chave privada nunca sai do método).</summary>
public sealed record CertificateProof(string Cnpj, string Subject, string Thumbprint, DateTimeOffset NotAfter);

/// <summary>
/// Prova de titularidade por certificado e-CNPJ (D-R10). Quem apresenta o PFX com a senha prova que tem a chave privada
/// do certificado ICP-Brasil daquele CNPJ — é o mesmo documento que a empresa usa para assinar notas. A cadeia é
/// validada até as âncoras ICP-Brasil (sem isso, um certificado autoassinado com o CNPJ no SAN passaria).
/// </summary>
public sealed class CnpjCertificateVerifier(TrustAnchors anchors, TimeProvider clock)
{
    public CertificateProof Verify(byte[] pfx, string password)
    {
        X509Certificate2Collection bundle;
        try
        {
            bundle = X509CertificateLoader.LoadPkcs12Collection(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new CertificateRejectedException("ERR_CERT_INVALID", "PFX ilegível ou senha incorreta.");
        }

        try
        {
            var cert = bundle.FirstOrDefault(c => c.HasPrivateKey)
                       ?? throw new CertificateRejectedException("ERR_CERT_INVALID", "O PFX não contém a chave privada.");
            var now = clock.GetUtcNow();
            if (cert.NotAfter.ToUniversalTime() <= now.UtcDateTime || cert.NotBefore.ToUniversalTime() > now.UtcDateTime)
            {
                throw new CertificateRejectedException("ERR_CERT_EXPIRED", $"Certificado fora da validade (vence em {cert.NotAfter:yyyy-MM-dd}).");
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors.Roots);
            chain.ChainPolicy.ExtraStore.AddRange(anchors.Intermediates);
            chain.ChainPolicy.ExtraStore.AddRange(bundle);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            if (!chain.Build(cert))
            {
                throw new CertificateRejectedException("ERR_CERT_NAO_ICP_BRASIL", "O certificado não é ICP-Brasil (a cadeia não chega a uma autoridade certificadora reconhecida).");
            }

            var cnpj = NfeSignatureVerifier.CnpjFromCertificate(cert)
                       ?? throw new CertificateRejectedException("ERR_CERT_SEM_CNPJ", "O certificado não é um e-CNPJ (não traz o CNPJ do titular).");
            return new CertificateProof(cnpj, cert.Subject, cert.Thumbprint, new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        finally
        {
            foreach (var c in bundle)
            {
                c.Dispose();
            }
        }
    }
}

public sealed class CnpjRegistryOptions
{
    public const string Section = "CnpjRegistry";

    /// <summary>
    /// Consulta pública de CNPJ que devolve o e-mail do cadastro da Receita (ex.: https://brasilapi.com.br/api/cnpj/v1/).
    /// Vazio = verificação por e-mail desligada (padrão em dev e testes; em produção é configurada).
    /// </summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>E-mail do cadastro público do CNPJ na Receita (D-R10). O código de verificação vai só para esse endereço.</summary>
public interface ICnpjRegistry
{
    bool Enabled { get; }

    Task<string?> EmailAsync(string cnpj, CancellationToken ct);
}

public sealed class HttpCnpjRegistry(HttpClient http, IOptions<CnpjRegistryOptions> options) : ICnpjRegistry
{
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.BaseUrl);

    public async Task<string?> EmailAsync(string cnpj, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        using var resp = await http.GetAsync(new Uri(options.Value.BaseUrl.TrimEnd('/') + "/" + cnpj), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        var email = json.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()?.Trim().ToLowerInvariant() : null;
        return string.IsNullOrEmpty(email) || !email.Contains('@', StringComparison.Ordinal) ? null : email;
    }

    /// <summary>"contato@empresa.com.br" → "c*****o@empresa.com.br" (mostrado ao usuário sem revelar o endereço inteiro).</summary>
    public static string Mask(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return "***";   // não é e-mail: não mostra nada
        }

        if (at <= 1)
        {
            return "***" + email[at..];
        }

        return email[0] + new string('*', Math.Max(3, at - 2)) + email[at - 1] + email[at..];
    }
}
