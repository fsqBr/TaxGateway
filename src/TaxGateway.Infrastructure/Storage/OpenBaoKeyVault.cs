using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

namespace TaxGateway.Infrastructure.Storage;

public sealed class OpenBaoOptions
{
    public const string Section = "Kms:OpenBao";

    /// <summary>Ex.: https://openbao.kms:8200 (vazio = cofre local de dev).</summary>
    public string Address { get; set; } = "";

    /// <summary>Token (dev/teste). Em produção: auth Kubernetes do OpenBao, renovado por sidecar/agent.</summary>
    public string Token { get; set; } = "";

    public string TransitMount { get; set; } = "transit";
}

/// <summary>
/// KMS de produção sobre o engine Transit do OpenBao/Vault (SDD §4.2/§4.3; D-406): uma chave AES-256-GCM por
/// tenant ("tenant-{id}"), criada sob demanda. A aplicação só pede encrypt/decrypt de DEKs (com dado associado)
/// e HMAC do CPF; nenhuma chave de tenant sai do cofre. Crypto-shredding = apagar a chave do tenant no Transit.
/// </summary>
public sealed class OpenBaoKeyVault(HttpClient http, IOptions<OpenBaoOptions> options) : IKeyVault
{
    private readonly ConcurrentDictionary<string, bool> _ensured = new(StringComparer.Ordinal);

    public static string KeyName(string keyRef) => keyRef.Replace('/', '-').Replace(':', '-');

    public async Task<byte[]> WrapAsync(string keyRef, byte[] dek, byte[] aad, CancellationToken ct)
    {
        await EnsureKeyAsync(keyRef, ct).ConfigureAwait(false);
        var r = await PostAsync($"encrypt/{KeyName(keyRef)}", new JsonObject
        {
            ["plaintext"] = Convert.ToBase64String(dek),
            ["associated_data"] = Convert.ToBase64String(aad),
        }, ct).ConfigureAwait(false);
        return System.Text.Encoding.ASCII.GetBytes(r["data"]!["ciphertext"]!.GetValue<string>());
    }

    public async Task<byte[]> UnwrapAsync(string keyRef, byte[] wrapped, byte[] aad, CancellationToken ct)
    {
        var r = await PostAsync($"decrypt/{KeyName(keyRef)}", new JsonObject
        {
            ["ciphertext"] = System.Text.Encoding.ASCII.GetString(wrapped),
            ["associated_data"] = Convert.ToBase64String(aad),
        }, ct).ConfigureAwait(false);
        return Convert.FromBase64String(r["data"]!["plaintext"]!.GetValue<string>());
    }

    public async Task<byte[]> HmacAsync(string keyRef, byte[] data, CancellationToken ct)
    {
        await EnsureKeyAsync(keyRef, ct).ConfigureAwait(false);
        var r = await PostAsync($"hmac/{KeyName(keyRef)}/sha2-256", new JsonObject { ["input"] = Convert.ToBase64String(data) }, ct).ConfigureAwait(false);
        var v = r["data"]!["hmac"]!.GetValue<string>();          // "vault:v1:<base64>"
        return Convert.FromBase64String(v[(v.LastIndexOf(':') + 1)..]);
    }

    /// <summary>Crypto-shredding do tenant (encerramento de contrato, §4.3): sem a chave, nenhum XML abre.</summary>
    public async Task ShredAsync(string keyRef, CancellationToken ct)
    {
        var name = KeyName(keyRef);
        await PostAsync($"keys/{name}/config", new JsonObject { ["deletion_allowed"] = true }, ct).ConfigureAwait(false);
        using var req = Request(HttpMethod.Delete, $"keys/{name}");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        _ensured.TryRemove(name, out _);
    }

    private async Task EnsureKeyAsync(string keyRef, CancellationToken ct)
    {
        var name = KeyName(keyRef);
        if (_ensured.ContainsKey(name))
        {
            return;
        }

        // Criar uma chave existente não muda nada (idempotente).
        await PostAsync($"keys/{name}", new JsonObject { ["type"] = "aes256-gcm96" }, ct).ConfigureAwait(false);
        _ensured[name] = true;
    }

    private async Task<JsonNode> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, path);
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new System.Security.Cryptography.CryptographicException($"KMS recusou {path}: HTTP {(int)resp.StatusCode}.");
        }

        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var o = options.Value;
        var req = new HttpRequestMessage(method, new Uri(new Uri(o.Address.TrimEnd('/') + "/"), $"v1/{o.TransitMount}/{path}"));
        req.Headers.Add("X-Vault-Token", o.Token);
        return req;
    }
}
