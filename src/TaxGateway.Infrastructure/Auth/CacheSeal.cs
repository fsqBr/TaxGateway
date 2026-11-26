using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace TaxGateway.Infrastructure.Auth;

/// <summary>
/// Selo de integridade das entradas de cache no Valkey (D-P10). Antes, o cache de autenticação guardava tenant, escopos
/// e plano em JSON puro, lido ANTES do banco: quem escrevesse no Valkey trocava o tenant de uma API key e ganhava todos
/// os escopos (reproduzido na revisão de segurança); o cache de simulação permitia servir resultado fiscal falso.
/// Agora cada valor leva um HMAC-SHA256 sobre (chave do cache ‖ valor) com uma chave derivada do pepper (HKDF) — que
/// nunca vai ao Valkey. Valor sem selo, com selo inválido ou movido para outra chave é descartado como falta de cache.
/// Formato: [1 byte versão = 1][32 bytes HMAC][valor].
/// </summary>
public sealed class CacheSeal
{
    private const byte Version = 1;
    private readonly byte[] _key;

    public CacheSeal(IOptions<ApiKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pepper = Convert.FromBase64String(options.Value.ApiKeyPepper);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, pepper, 32, info: "taxgateway/cache-integrity/v1"u8.ToArray());
    }

    public byte[] Seal(string cacheKey, ReadOnlySpan<byte> value)
    {
        var output = new byte[1 + 32 + value.Length];
        output[0] = Version;
        Mac(cacheKey, value, output.AsSpan(1, 32));
        value.CopyTo(output.AsSpan(33));
        return output;
    }

    public byte[] Seal(string cacheKey, string value) => Seal(cacheKey, Encoding.UTF8.GetBytes(value));

    /// <summary>Devolve o valor se o selo confere; null se ausente, adulterado ou de outra chave.</summary>
    public byte[]? Open(string cacheKey, ReadOnlySpan<byte> sealedValue)
    {
        if (sealedValue.Length < 33 || sealedValue[0] != Version)
        {
            return null;
        }

        Span<byte> expected = stackalloc byte[32];
        var value = sealedValue[33..];
        Mac(cacheKey, value, expected);
        return CryptographicOperations.FixedTimeEquals(expected, sealedValue.Slice(1, 32)) ? value.ToArray() : null;
    }

    public string? OpenString(string cacheKey, ReadOnlySpan<byte> sealedValue) =>
        Open(cacheKey, sealedValue) is { } v ? Encoding.UTF8.GetString(v) : null;

    private void Mac(string cacheKey, ReadOnlySpan<byte> value, Span<byte> destination)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key);
        h.AppendData(Encoding.UTF8.GetBytes(cacheKey));
        h.AppendData([0]);
        h.AppendData(value);
        h.GetHashAndReset(destination);
    }
}
