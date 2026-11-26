using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Xml;

using Npgsql;

using TaxGateway.Infrastructure.Storage;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Direitos do titular (SDD §4.3; D-H05): NF-e com destinatário CPF auditada com retenção de XML → localizar pelo CPF →
/// expurgar → o XML some do S3, o vínculo com o CPF some do banco, o registro fiscal fica, e tudo é registrado sem o CPF.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class TitularTests(GoldenFixture env)
{
    private const string Cpf = "52998224725";   // CPF válido de exemplo (DV correto), sem relação com pessoa real conhecida

    [Fact]
    public async Task Localiza_e_expurga_documentos_do_titular()
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, 960_001);   // faixa 96x_xxx reservada a este teste (900_xxx é das mutações)
        var ns = GoldenXmlTests.Ns(doc);
        var destCnpj = doc.SelectSingleNode("//n:dest/n:CNPJ", ns)!;
        var cpfNode = doc.CreateElement("CPF", NfeBuilder.Ns);
        cpfNode.InnerText = Cpf;
        destCnpj.ParentNode!.ReplaceChild(cpfNode, destCnpj);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        var chave = GoldenXmlTests.Chave(doc);

        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND") { Content = new ByteArrayContent(NfeBuilder.Bytes(doc)) })
        {
            req.Content.Headers.ContentType = new("application/xml");
            req.Headers.Add("X-Api-Key", env.ApiKey);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var resp = await env.Api.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }

        var (storageKey, hmac) = await RowAsync(chave);
        Assert.NotNull(storageKey);   // tenant do fixture retém XML por 90 dias
        Assert.NotNull(hmac);
        Assert.NotEqual(System.Text.Encoding.ASCII.GetBytes(Cpf), hmac);   // nunca o CPF em claro
        Assert.True(await S3ExistsAsync(storageKey!));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/v1/titulares/localizar", "12345678900")).Status);

        var found = await PostAsync("/v1/titulares/localizar", "529.982.247-25");
        Assert.Equal(HttpStatusCode.OK, found.Status);
        var item = Assert.Single(found.Body!["documentos"]!.AsArray())!;
        Assert.Equal(chave, item["chave_acesso"]!.GetValue<string>());
        Assert.True(item["xml_armazenado"]!.GetValue<bool>());

        var purged = await PostAsync("/v1/titulares/expurgar", Cpf);
        Assert.Equal(HttpStatusCode.OK, purged.Status);
        Assert.Equal(1, purged.Body!["documentos_expurgados"]!.GetValue<int>());
        Assert.Equal(1, purged.Body["xmls_apagados"]!.GetValue<int>());

        var after = await RowAsync(chave);
        Assert.Null(after.StorageKey);
        Assert.Null(after.Hmac);
        Assert.False(await S3ExistsAsync(storageKey!));
        Assert.Empty((await PostAsync("/v1/titulares/localizar", Cpf)).Body!["documentos"]!.AsArray());

        // Registro fiscal preservado e solicitações registradas (sem CPF em claro).
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM fiscal_documents WHERE access_key = $1", su))
        {
            cmd.Parameters.AddWithValue(chave);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        await using (var cmd = new NpgsqlCommand("SELECT string_agg(action || ':' || documents || ':' || xml_deleted, ',' ORDER BY created_at) FROM titular_requests WHERE cpf_hmac = $1", su))
        {
            cmd.Parameters.AddWithValue(hmac!);
            Assert.Equal("LOCALIZAR:1:0,EXPURGAR:1:1,LOCALIZAR:0:0", (string)(await cmd.ExecuteScalarAsync())!);
        }
    }

    private async Task<(string? StorageKey, byte[]? Hmac)> RowAsync(string chave)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT xml_storage_key, cpf_hmac FROM fiscal_documents WHERE access_key = $1", su);
        cmd.Parameters.AddWithValue(chave);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetFieldValue<byte[]>(1));
    }

    private async Task<bool> S3ExistsAsync(string key)
    {
        using var s3 = XmlVault.CreateClient(new StorageOptions
        {
            ServiceUrl = new Uri(env.Settings["Storage:ServiceUrl"]!),
            AccessKey = env.Settings["Storage:AccessKey"]!,
            SecretKey = env.Settings["Storage:SecretKey"]!,
            Bucket = env.Settings["Storage:Bucket"]!,
        });
        try
        {
            await s3.GetObjectMetadataAsync(env.Settings["Storage:Bucket"]!, key);
            return true;
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string path, string cpf)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new JsonObject { ["cpf"] = cpf }) };
        req.Headers.Add("X-Api-Key", env.ApiKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
