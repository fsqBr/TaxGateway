using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Options;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Storage;

/// <summary>
/// KMS/Vault (SDD §4.2, §4.3). A KEK de cada tenant nunca sai do cofre: a aplicação só pede para embrulhar ou
/// desembrulhar uma DEK, e para obter a chave HMAC de CPF do tenant. Apagar a KEK torna ilegíveis todos os XMLs
/// do tenant (crypto-shredding).
/// </summary>
public interface IKeyVault
{
    Task<byte[]> WrapAsync(string keyRef, byte[] dek, byte[] aad, CancellationToken ct);
    Task<byte[]> UnwrapAsync(string keyRef, byte[] wrapped, byte[] aad, CancellationToken ct);
    /// <summary>HMAC-SHA256 calculado DENTRO do cofre com a chave do tenant (a chave nunca sai, D-406).</summary>
    Task<byte[]> HmacAsync(string keyRef, byte[] data, CancellationToken ct);
}

public sealed class KeyVaultOptions
{
    public const string Section = "Kms";

    /// <summary>Só dev/teste (D-309): chave mestra local (base64, 32 bytes). Produção usa KMS gerenciado.</summary>
    public string LocalMasterKey { get; set; } = "";
}

/// <summary>
/// Cofre local para dev e testes (D-309): KEK por tenant = HKDF-SHA256(mestra, keyRef); embrulho AES-256-GCM.
/// Mesma interface do KMS de produção, que entra junto com o deploy (Fase 4).
/// </summary>
public sealed class LocalKeyVault : IKeyVault
{
    private readonly byte[] _master;

    public LocalKeyVault(IOptions<KeyVaultOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var raw = options.Value.LocalMasterKey;
        _master = string.IsNullOrEmpty(raw) ? [] : Convert.FromBase64String(raw);
        if (_master.Length != 32)
        {
            throw new InvalidOperationException("Kms:LocalMasterKey ausente ou diferente de 32 bytes (base64).");
        }
    }

    public Task<byte[]> WrapAsync(string keyRef, byte[] dek, byte[] aad, CancellationToken ct) =>
        Task.FromResult(Seal(Kek(keyRef, "kek"), dek, aad));

    public Task<byte[]> UnwrapAsync(string keyRef, byte[] wrapped, byte[] aad, CancellationToken ct) =>
        Task.FromResult(Open(Kek(keyRef, "kek"), wrapped, aad));

    public Task<byte[]> HmacAsync(string keyRef, byte[] data, CancellationToken ct)
    {
        var key = Kek(keyRef, "cpf-hmac");
        try
        {
            return Task.FromResult(HMACSHA256.HashData(key, data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] Kek(string keyRef, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, 32, Encoding.UTF8.GetBytes("taxgateway"), Encoding.UTF8.GetBytes($"{purpose}:{keyRef}"));

    internal static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[12 + 16 + plain.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, output.AsSpan(28), output.AsSpan(12, 16), aad);
        nonce.CopyTo(output, 0);
        return output;
    }

    internal static byte[] Open(byte[] key, byte[] sealedData, byte[] aad)
    {
        if (sealedData.Length < 28)
        {
            throw new CryptographicException("Envelope truncado.");
        }

        var plain = new byte[sealedData.Length - 28];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain, aad);
        return plain;
    }
}

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public Uri ServiceUrl { get; set; } = new("http://127.0.0.1:8333");
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "taxgateway-xml";
    public bool ForcePathStyle { get; set; } = true;
}

/// <summary>
/// XML bruto no S3 (SDD §4.3; D-308): cifrado com uma DEK por objeto, embrulhada pela KEK do tenant no cofre.
/// Prefixo por tenant; o caminho do objeto é dado associado do AES-GCM (um envelope copiado para outro tenant ou
/// outro caminho não abre). Retenção via tag <c>retention-days</c> (regras de lifecycle do bucket).
/// Com <c>zero_retention</c> nada é gravado (quem decide é o serviço de auditoria).
/// </summary>
public sealed class XmlVault(IAmazonS3 s3, IKeyVault vault, IOptions<StorageOptions> options)
{
    private static readonly byte[] Magic = "TGX1"u8.ToArray();

    public static string ObjectKey(TenantId tenant, DateOnly month, Guid documentId) =>
        $"t/{tenant}/xml/{month:yyyy}/{month:MM}/{documentId}.xml.enc";

    public Task<string> PutAsync(TenantId tenant, string kekRef, DateOnly month, Guid documentId, byte[] xml, int retentionDays, CancellationToken ct) =>
        PutBlobAsync(kekRef, ObjectKey(tenant, month, documentId), xml, retentionDays, ct);

    /// <summary>Qualquer objeto do tenant em envelope (lotes, relatórios). Chave = dado associado.</summary>
    public async Task<string> PutBlobAsync(string kekRef, string key, byte[] data, int retentionDays, CancellationToken ct)
    {
        var xml = data;
        var aad = Encoding.UTF8.GetBytes(key);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrapped = await vault.WrapAsync(kekRef, dek, aad, ct).ConfigureAwait(false);
            var sealedXml = LocalKeyVault.Seal(dek, xml, aad);
            var body = new byte[4 + 2 + wrapped.Length + sealedXml.Length];
            Magic.CopyTo(body, 0);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), (ushort)wrapped.Length);
            wrapped.CopyTo(body, 6);
            sealedXml.CopyTo(body, 6 + wrapped.Length);

            using var stream = new MemoryStream(body, writable: false);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Value.Bucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/octet-stream",
                TagSet = [new Tag { Key = "retention-days", Value = retentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
            }, ct).ConfigureAwait(false);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct) => s3.DeleteObjectAsync(options.Value.Bucket, key, ct);

    /// <summary>Objeto inexistente no S3 (NoSuchKey/404): vira 404 para o cliente, não 500 (D-P12).</summary>
    public static bool IsNotFound(Exception ex) =>
        ex is AmazonS3Exception s3ex && (s3ex.StatusCode == System.Net.HttpStatusCode.NotFound || s3ex.ErrorCode is "NoSuchKey" or "NotFound");

    /// <summary>Prefixo dos uploads brutos do modo 2 (D-P24): <c>uploads/{tenant}/{id}.zip</c>, apagados em 24 h.</summary>
    public const string UploadPrefix = "uploads/";

    /// <summary>URL pré-assinada de PUT para upload direto do cliente (lote, modo 2). O tamanho é limitado na ingestão
    /// (leitura com teto do plano) e o objeto vence em 24 h (lifecycle + <see cref="UploadJanitor"/>).</summary>
    public string PresignUpload(string key, TimeSpan validity) => s3.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = options.Value.Bucket,
        Key = key,
        Verb = HttpVerb.PUT,
        Expires = DateTime.UtcNow.Add(validity),
        ContentType = "application/zip",
    });

    /// <summary>Objeto bruto (upload do cliente, ainda sem envelope), para a ingestão do lote.</summary>
    public async Task<Stream> OpenRawAsync(string key, CancellationToken ct)
    {
        var resp = await s3.GetObjectAsync(options.Value.Bucket, key, ct).ConfigureAwait(false);
        return resp.ResponseStream;
    }

    /// <summary>Lê e decifra (uso: serviço de download com URL de 5 min, reprocessamento e testes).</summary>
    public async Task<byte[]> GetAsync(string kekRef, string key, CancellationToken ct)
    {
        using var resp = await s3.GetObjectAsync(options.Value.Bucket, key, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await resp.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        var body = ms.ToArray();
        if (body.Length < 6 || !body.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new CryptographicException("Objeto fora do formato de envelope.");
        }

        var len = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4));
        var aad = Encoding.UTF8.GetBytes(key);
        var dek = await vault.UnwrapAsync(kekRef, body[6..(6 + len)], aad, ct).ConfigureAwait(false);
        try
        {
            return LocalKeyVault.Open(dek, body[(6 + len)..], aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.Value.Bucket }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        // D-P24: uploads do modo 2 vencem em 1 dia no próprio bucket. Nem todo S3 compatível implementa lifecycle
        // (o SeaweedFS de dev não); o UploadJanitor dos workers cobre esse caso.
        try
        {
            await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = options.Value.Bucket,
                Configuration = new LifecycleConfiguration
                {
                    Rules =
                    [
                        new LifecycleRule
                        {
                            Id = "uploads-1d",
                            Status = LifecycleRuleStatus.Enabled,
                            Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = UploadPrefix } },
                            Expiration = new LifecycleRuleExpiration { Days = 1 },
                        },
                    ],
                },
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception)
        {
            // sem suporte a lifecycle: o janitor apaga
        }
    }

    public static IAmazonS3 CreateClient(StorageOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
        {
            ServiceURL = o.ServiceUrl.ToString(),
            ForcePathStyle = o.ForcePathStyle,
            AuthenticationRegion = o.Region,
        });
    }
}

/// <summary>LGPD (SDD §4.3): CPF vira HMAC-SHA256 com chave do tenant derivada no cofre, fora do banco.</summary>
public sealed class CpfAnonymizer(IKeyVault vault)
{
    public async Task<byte[]?> HashAsync(string kekRef, string? cpf, CancellationToken ct)
    {
        var digits = cpf is null ? "" : new string(cpf.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 11)
        {
            return null;
        }

        return await vault.HmacAsync(kekRef, Encoding.ASCII.GetBytes(digits), ct).ConfigureAwait(false);
    }
}
