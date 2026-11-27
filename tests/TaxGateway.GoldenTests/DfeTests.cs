using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using TaxGateway.Infrastructure.Dfe;
using TaxGateway.TestKit;

using Xunit.Abstractions;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Fase 9 (SDD §4.2, §6.5; D-901 a D-907): custódia do A1, distribuição DF-e contra a SEFAZ simulada com as regras da
/// NT 2014.002, ciência da operação, auditoria INBOUND automática, webhook dfe.document.audited e alertas de vencimento.
/// O critério real ("30 dias em homologação sem 656") depende da SEFAZ de homologação e fica pendente.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class DfeTests(GoldenFixture env, ITestOutputHelper output)
{
    private const string Destinatario = "11444777000161";   // dest dos XMLs do corpus (emitente 11222333000181)

    [Fact]
    public async Task Certificado_A1_validado_e_guardado_cifrado()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("DF-e cert", GoldenFixture.NewCnpj());
        using var a1 = Issue(Destinatario, env.Clock.GetUtcNow().AddYears(1));
        var pfx = a1.Export(X509ContentType.Pfx, "segredo");

        Assert.Equal("ERR_CERT_INVALID", (await UploadAsync(key, Destinatario, pfx, "errada")).Body!["code"]!.GetValue<string>());
        var mismatch = await UploadAsync(key, "11222333000181", pfx, "segredo");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.Status);
        Assert.Equal("ERR_CERT_CNPJ_MISMATCH", mismatch.Body!["code"]!.GetValue<string>());

        var ok = await UploadAsync(key, Destinatario, pfx, "segredo");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(a1.Thumbprint, ok.Body!["thumbprint"]!.GetValue<string>());
        Assert.Null(ok.Body["senha"]);

        // No banco: só cifrado (nonce+tag+cifra), DEK embrulhada; sem o PFX em claro.
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT pfx_ciphertext, password_ciphertext, dek_wrapped FROM certificates WHERE tenant_id = $1", su);
        cmd.Parameters.AddWithValue(tenant);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        var ct = r.GetFieldValue<byte[]>(0);
        Assert.Equal(pfx.Length + 28, ct.Length);
        Assert.False(ct.AsSpan().IndexOf(pfx.AsSpan(0, 64)) >= 0);
        Assert.NotEqual("segredo"u8.ToArray(), r.GetFieldValue<byte[]>(1)[28..]);

        // O worker recupera o certificado com a chave privada (em memória).
        using var loaded = await env.WorkerServices.GetRequiredService<CertificateStore>()
            .LoadAsync(new Core.Tenancy.TenantId(tenant), Destinatario, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.True(loaded!.HasPrivateKey);
        Assert.Equal(a1.Thumbprint, loaded.Thumbprint);
    }

    /// <summary>
    /// D-R37: troca do certificado avisa dono e admin; "Testar conexão" confere senha, prazo e aceite pela SEFAZ sem gastar a
    /// cota de consultas; o status diz a situação em português; captura parada gera aviso automático.
    /// </summary>
    [Fact]
    public async Task Certificado_avisa_testa_conexao_e_mostra_a_saude_da_captura()
    {
        var cnpj = GoldenFixture.NewCnpj();
        var (tenant, key) = await env.CreateTenantWithIdAsync("DF-e saúde", cnpj);
        var testar = $"/v1/cnpjs/{cnpj}/certificado/testar";

        var none = await SendAsync(key, HttpMethod.Post, testar, null);
        Assert.Equal("FALHA", none.Body!["resultado"]!.GetValue<string>());

        using var a1 = Issue(cnpj, env.Clock.GetUtcNow().AddDays(20));
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(key, cnpj, a1.Export(X509ContentType.Pfx, "s"), "s")).Status);
        using var a2 = Issue(cnpj, env.Clock.GetUtcNow().AddYears(1));
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(key, cnpj, a2.Export(X509ContentType.Pfx, "s"), "s")).Status);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM tenant_notifications WHERE tenant_id = $1 AND kind = 'SEGURANCA' AND title LIKE 'Certificado A1%'", tenant));

        var sim = env.Sefaz.For(cnpj);
        var queries = sim.Queries.Count;
        var ok = await SendAsync(key, HttpMethod.Post, testar, null);
        Assert.True(ok.Body!["resultado"]!.GetValue<string>() == "OK", ok.Body.ToJsonString());
        Assert.Equal(4, ok.Body["verificacoes"]!.AsArray().Count);
        Assert.Equal(queries, sim.Queries.Count);   // não gastou consulta de distribuição

        env.Sefaz.RejectedRoots.Add(cnpj[..8]);
        try
        {
            var refused = await SendAsync(key, HttpMethod.Post, testar, null);
            Assert.Equal("FALHA", refused.Body!["resultado"]!.GetValue<string>());
            Assert.Contains("recusou", refused.Body.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            env.Sefaz.RejectedRoots.Remove(cnpj[..8]);
        }

        // Saúde: ligada e ainda sem consulta → AGUARDANDO; depois de consultar → FUNCIONANDO.
        var on = await SendAsync(key, HttpMethod.Post, $"/v1/cnpjs/{cnpj}/dfe/sync", new JsonObject { ["c_uf_autor"] = 35 });
        Assert.Equal("AGUARDANDO", on.Body!["saude"]!["situacao"]!.GetValue<string>());
        await env.WorkerServices.GetRequiredService<DfeSyncService>().SyncOneAsync(new Core.Tenancy.TenantId(tenant), cnpj, CancellationToken.None);
        var status = await SendAsync(key, HttpMethod.Get, $"/v1/cnpjs/{cnpj}/dfe/status", null);
        Assert.True(status.Body!["saude"]!["ok"]!.GetValue<bool>(), status.Body.ToJsonString());
        Assert.Equal("FUNCIONANDO", status.Body["saude"]!["situacao"]!.GetValue<string>());

        // Parada: sem consulta há mais de 6 h (o "agora" da função avança 1 dia) → um aviso por dia.
        var db = env.WorkerServices.GetRequiredService<NpgsqlDataSource>();
        async Task<int> StalledAsync(DateTimeOffset at)
        {
            await using var cmd = db.CreateCommand("SELECT app.notify_dfe_stalled($1)");
            cmd.Parameters.Add(new() { Value = at.ToUniversalTime() });
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM tenant_notifications WHERE tenant_id = $1 AND kind = 'DFE_PARADO'", tenant));
        await StalledAsync(env.Clock.GetUtcNow().AddDays(1));
        await StalledAsync(env.Clock.GetUtcNow().AddDays(1).AddMinutes(5));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM tenant_notifications WHERE tenant_id = $1 AND kind = 'DFE_PARADO'", tenant));

        // Certificado removido: aviso de segurança, saúde "sem certificado" e teste falha.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(key, HttpMethod.Delete, $"/v1/cnpjs/{cnpj}/certificado", null)).Status);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM tenant_notifications WHERE tenant_id = $1 AND title = 'Certificado A1 removido'", tenant));
        var semCert = await SendAsync(key, HttpMethod.Get, $"/v1/cnpjs/{cnpj}/dfe/status", null);
        Assert.Equal("SEM_CERTIFICADO", semCert.Body!["saude"]!["situacao"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(key, HttpMethod.Delete, $"/v1/cnpjs/{cnpj}/certificado", null)).Status);
    }

    [Fact]
    public async Task Simulador_responde_656_quando_as_regras_sao_violadas()
    {
        // Prova de que o simulador pune o consumo indevido (senão o teste principal não provaria nada).
        var cnpj = GoldenFixture.NewCnpj();
        using var cert = Issue(cnpj, env.Clock.GetUtcNow().AddYears(1));
        var client = env.WorkerServices.GetRequiredService<ISefazDfeClient>();
        Assert.Equal("137", (await client.DistNsuAsync(cert, 35, cnpj, 0, CancellationToken.None)).CStat);
        Assert.Equal("656", (await client.DistNsuAsync(cert, 35, cnpj, 0, CancellationToken.None)).CStat);   // dentro do bloqueio de 1 h
        Assert.Equal(1, env.Sefaz.For(cnpj).Count656);
    }

    [Fact]
    public async Task Tres_dias_simulados_sem_656_com_ciencia_auditoria_inbound_e_webhook()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("DF-e sync", GoldenFixture.NewCnpj());
        using var a1 = Issue(Destinatario, env.Clock.GetUtcNow().AddYears(1));
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(key, Destinatario, a1.Export(X509ContentType.Pfx, "s"), "s")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(key, HttpMethod.Post, $"/v1/cnpjs/{Destinatario}/dfe/sync", new JsonObject { ["c_uf_autor"] = 11 })).Status);

        // Estado inicial na SEFAZ: resumo de A (completo só após a ciência), procNFe de B e um evento.
        var sim = env.Sefaz.For(Destinatario);
        var (chaveA, procA) = Proc(970_001);
        var (chaveB, procB) = Proc(970_002);
        env.Sefaz.PublishResumoWithFull(Destinatario, chaveA, Resumo(chaveA), procA);
        env.Sefaz.Publish(Destinatario, "procNFe_v4.00.xsd", procB);
        env.Sefaz.Publish(Destinatario, "resEvento_v1.01.xsd", Evento(chaveB));
        var startQueries = sim.Queries.Count;

        var sync = env.WorkerServices.GetRequiredService<DfeSyncService>();
        string? chaveC = null;
        for (var step = 0; step < 3 * 24 * 4; step++)   // 3 dias em passos de 15 min
        {
            env.Clock.Advance(TimeSpan.FromMinutes(15));
            if (step == 33)
            {
                (chaveC, var procC) = Proc(970_003);
                env.Sefaz.Publish(Destinatario, "procNFe_v4.00.xsd", procC);
            }

            if (step == 133)
            {
                for (var i = 0; i < 60; i++)   // backlog maior que uma página (50): exige consultas seguidas
                {
                    env.Sefaz.Publish(Destinatario, "resEvento_v1.01.xsd", Evento(chaveB));
                }
            }

            await sync.RunOnceAsync(CancellationToken.None);
        }

        var queries = sim.Queries.Count - startQueries;
        output.WriteLine($"consultas em 3 dias: {queries}; 656: {sim.Count656} {string.Join(" | ", sim.Reasons656)}");
        var docs = await DocsAsync(tenant);
        foreach (var d in docs.Where(d => d.Kind != "EVENTO"))
        {
            output.WriteLine($"nsu {d.Nsu} {d.Kind} {d.Chave} {d.Status} {d.Detail}");
        }

        Assert.Equal(0, sim.Count656);
        Assert.InRange(queries, 70, 90);   // ~1 por hora após alcançar o fim, mais a drenagem
        Assert.Contains(chaveA, sim.Ciencias);
        Assert.Equal("CIENCIA_ENVIADA", docs.Single(d => d.Chave == chaveA && d.Kind == "RESUMO").Status);
        foreach (var chave in new[] { chaveA, chaveB, chaveC! })
        {
            var full = docs.Single(d => d.Chave == chave && d.Kind == "COMPLETO");
            Assert.True(full.Status == "AUDITADO", $"{chave}: {full.Status} {full.Detail}");
        }

        Assert.Equal(61, docs.Count(d => d.Kind == "EVENTO" && d.Status == "IGNORADO"));
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM outbox WHERE tenant_id = $1 AND topic = 'dfe.document.audited'", tenant));
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM fiscal_documents WHERE tenant_id = $1 AND kind = 'INBOUND'", tenant));

        var status = (await SendAsync(key, HttpMethod.Get, $"/v1/cnpjs/{Destinatario}/dfe/status", null)).Body!;
        Assert.Equal(status["max_nsu"]!.GetValue<long>(), status["ult_nsu"]!.GetValue<long>());
        Assert.True(status["proxima_consulta"]!.GetValue<DateTimeOffset>() > env.Clock.GetUtcNow());

        // Ligar de novo a captura não antecipa a consulta bloqueada (é o que geraria 656).
        var before = status["proxima_consulta"]!.GetValue<DateTimeOffset>();
        var again = (await SendAsync(key, HttpMethod.Post, $"/v1/cnpjs/{Destinatario}/dfe/sync", null)).Body!;
        Assert.Equal(before, again["proxima_consulta"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Alerta_de_vencimento_uma_vez_por_limiar()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("DF-e vencimento", GoldenFixture.NewCnpj());
        using var a1 = Issue(Destinatario, env.Clock.GetUtcNow().AddDays(10));
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(key, Destinatario, a1.Export(X509ContentType.Pfx, "s"), "s")).Status);
        var expiry = env.WorkerServices.GetRequiredService<CertificateExpiryService>();
        const string sql = "SELECT count(*) FROM outbox WHERE tenant_id = $1 AND topic = 'certificate.expiring'";

        await expiry.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await CountAsync(sql, tenant));    // limiar de 15 dias
        await expiry.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await CountAsync(sql, tenant));    // não repete
        env.Clock.Advance(TimeSpan.FromDays(6));
        await expiry.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await CountAsync(sql, tenant));    // limiar de 5 dias
        await expiry.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await CountAsync(sql, tenant));
    }

    // ---------- apoio ----------

    private X509Certificate2 Issue(string cnpj, DateTimeOffset notAfter)
    {
        using var leaf = TestPki.IssueLeaf(env.Pki.Root, cnpj, env.Clock.GetUtcNow().AddDays(-1), notAfter);
        return X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
    }

    private (string Chave, byte[] Proc) Proc(int numero)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        var chave = GoldenXmlTests.Chave(doc);
        var xml = $"""<nfeProc xmlns="{NfeBuilder.Ns}" versao="4.00">{doc.DocumentElement!.OuterXml}<protNFe versao="4.00"><infProt><tpAmb>2</tpAmb><chNFe>{chave}</chNFe><cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo></infProt></protNFe></nfeProc>""";
        return (chave, Encoding.UTF8.GetBytes(xml));
    }

    private static byte[] Resumo(string chave) => Encoding.UTF8.GetBytes(
        $"""<resNFe xmlns="{NfeBuilder.Ns}" versao="1.01"><chNFe>{chave}</chNFe><CNPJ>11222333000181</CNPJ><xNome>EMITENTE</xNome><IE>1</IE><dhEmi>2027-06-15T14:00:00-03:00</dhEmi><tpNF>1</tpNF><vNF>1000.00</vNF><dhRecbto>2027-06-15T14:00:01-03:00</dhRecbto><nProt>1</nProt><cSitNFe>1</cSitNFe></resNFe>""");

    private static byte[] Evento(string chave) => Encoding.UTF8.GetBytes(
        $"""<resEvento xmlns="{NfeBuilder.Ns}" versao="1.01"><cOrgao>91</cOrgao><CNPJ>11222333000181</CNPJ><chNFe>{chave}</chNFe><dhEvento>2027-06-16T10:00:00-03:00</dhEvento><tpEvento>110110</tpEvento><nSeqEvento>1</nSeqEvento><xEvento>Carta de Correcao</xEvento><dhRecbto>2027-06-16T10:00:01-03:00</dhRecbto><nProt>2</nProt></resEvento>""");

    private async Task<List<(long Nsu, string Kind, string? Chave, string Status, string? Detail)>> DocsAsync(Guid tenant)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT nsu, kind, access_key, status, detail FROM dfe_documents WHERE tenant_id = $1 ORDER BY nsu", su);
        cmd.Parameters.AddWithValue(tenant);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<(long, string, string?, string, string?)>();
        while (await r.ReadAsync())
        {
            list.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return list;
    }

    private async Task<long> CountAsync(string sql, Guid tenant)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, su);
        cmd.Parameters.AddWithValue(tenant);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> UploadAsync(string key, string cnpj, byte[] pfx, string senha) =>
        SendAsync(key, HttpMethod.Put, $"/v1/cnpjs/{cnpj}/certificado", new JsonObject { ["pfx_base64"] = Convert.ToBase64String(pfx), ["senha"] = senha });

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(string key, HttpMethod method, string path, JsonObject? body)
    {
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", key);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
