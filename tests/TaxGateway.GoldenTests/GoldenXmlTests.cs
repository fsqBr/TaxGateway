using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using Npgsql;

using TaxGateway.Infrastructure.Storage;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Critérios de saída da Fase 3 (SDD §12): corpus golden de XML com 0 falsos positivos determinísticos; regras
/// detectam as mutações; replay entre meses não gera cobrança; XML cifrado por tenant e zero_retention; CPF
/// só como HMAC. O corpus é de NF-e/NFC-e assinadas pela PKI de teste (tests/golden/pki) cujos grupos RTC
/// foram gerados pelo próprio motor (/xml/generate) — o emissor "certo" (D-311).
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class GoldenXmlTests(GoldenFixture env)
{
    private static bool Regenerate => Environment.GetEnvironmentVariable("GOLDEN_REGENERATE") == "1";
    internal static string XmlDir => Path.Combine(Paths.RepoRoot, "tests", "golden", "corpus-xml", "v0");
    private const string Cnpj = "11222333000181";

    [Fact]
    public async Task Xml_corpus_has_zero_deterministic_false_positives()
    {
        if (Regenerate)
        {
            await RegenerateAsync();
        }

        var files = Directory.Exists(XmlDir) ? Directory.GetFiles(XmlDir, "*.xml").Order(StringComparer.Ordinal).ToArray() : [];
        Assert.True(files.Length >= 100, $"Corpus XML com {files.Length} documentos; esperado ≥ 100 (GOLDEN_REGENERATE=1 para gerar).");
        var failures = new List<string>();
        foreach (var f in files)
        {
            var (status, body) = await AuditAsync(await File.ReadAllBytesAsync(f), env.ApiKey);
            var st = body?["status"]?.GetValue<string>();
            var det = body?["ocorrencias"]?.AsArray().Where(o => o!["natureza"]!.GetValue<string>() == "DETERMINISTICA")
                .Select(o => $"{o!["regra"]}: {o["mensagem"]}").ToList() ?? [];
            if (status != 200 || st != "CONFORME" || det.Count > 0)
            {
                failures.Add($"{Path.GetFileName(f)}: HTTP {status} {st} {body?["code"]} {string.Join(" | ", det)}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} falso(s) positivo(s) em {files.Length}:\n" + string.Join('\n', failures.Take(30)));
    }

    public static TheoryData<string, string> Mutations() => new()
    {
        { "vbc", "R010" },
        { "pcbs", "R012" },
        { "vcbs", "R011" },
        { "total", "R030" },
        { "cclasstrib", "R022" },
        { "cst", "R020" },
        { "xsd", "R001" },
        { "unsigned_tamper", "R002" },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task Mutations_are_detected_by_the_expected_rule(string mutation, string rule)
    {
        var doc = Base(900_000 + Array.IndexOf(Mutations().Select(m => (string)m[0]).ToArray(), mutation));
        var ns = Ns(doc);
        XmlNode N(string xp) => doc.SelectSingleNode(xp, ns)!;
        var resign = true;
        switch (mutation)
        {
            case "vbc": Shift(N("//n:det[1]//n:gIBSCBS/n:vBC"), 50m); Shift(N("//n:IBSCBSTot/n:vBCIBSCBS"), 50m); break;
            case "pcbs": N("//n:det[1]//n:gCBS/n:pCBS").InnerText = "8.80"; break;
            case "vcbs": Shift(N("//n:det[1]//n:gCBS/n:vCBS"), 1m); Shift(N("//n:IBSCBSTot/n:gCBS/n:vCBS"), 1m); break;
            case "total": Shift(N("//n:IBSCBSTot/n:gCBS/n:vCBS"), 0.01m); break;
            case "cclasstrib": N("//n:det[1]//n:IBSCBS/n:cClassTrib").InnerText = "000999"; break;
            case "cst": N("//n:det[1]//n:IBSCBS/n:CST").InnerText = "200"; break;
            case "xsd": N("//n:det[1]//n:IBSCBS/n:CST").InnerText = "ABC"; break;
            case "unsigned_tamper": resign = false; break;
        }

        if (resign)
        {
            NfeBuilder.Sign(doc, env.Pki.Leaf);
        }
        else
        {
            Shift(N("//n:det[1]//n:gCBS/n:vCBS"), 1m);   // alterado depois da assinatura
        }

        var (status, body) = await AuditAsync(NfeBuilder.Bytes(doc), env.ApiKey);
        Assert.Equal(200, status);
        var rules = body!["ocorrencias"]!.AsArray().Select(o => o!["regra"]!.GetValue<string>()).ToList();
        Assert.Contains(rule, rules);
        var expectedStatus = rule switch { "R001" => "SCHEMA_ERROR", "R002" => "ASSINATURA_INVALIDA", _ => "DIVERGENTE" };
        Assert.Equal(expectedStatus, body["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Replay_in_another_month_is_not_billed_and_reprocess_is_free()
    {
        var doc = Base(800_001);
        var xml = NfeBuilder.Bytes(doc);
        var chave = Chave(doc);

        var (s1, first) = await AuditAsync(xml, env.ApiKey);
        Assert.Equal(200, s1);
        Assert.True(first!["is_billable"]!.GetValue<bool>());
        Assert.False(first["replay"]!.GetValue<bool>());

        env.Clock.Advance(TimeSpan.FromDays(40));   // outra competência (o FakeTimeProvider só anda para a frente)
        {
            var (s2, replay) = await AuditAsync(xml, env.ApiKey);
            Assert.Equal(200, s2);
            Assert.True(replay!["replay"]!.GetValue<bool>());
            Assert.False(replay["is_billable"]!.GetValue<bool>());
            Assert.Equal(first["audit_id"]!.GetValue<string>(), replay["audit_id"]!.GetValue<string>());

            var (s3, reprocess) = await AuditAsync(xml, env.ApiKey, "&reprocess=true");
            Assert.Equal(200, s3);
            Assert.True(reprocess!["reprocessamento"]!.GetValue<bool>());
            Assert.False(reprocess["is_billable"]!.GetValue<bool>());
            Assert.NotEqual(first["audit_id"]!.GetValue<string>(), reprocess["audit_id"]!.GetValue<string>());

            // Depois do reprocessamento, o replay devolve a auditoria mais recente, ainda sem cobrança.
            var (_, again) = await AuditAsync(xml, env.ApiKey);
            Assert.Equal(reprocess["audit_id"]!.GetValue<string>(), again!["audit_id"]!.GetValue<string>());
            Assert.False(again["is_billable"]!.GetValue<bool>());
        }

        await using var conn = new NpgsqlConnection(env.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM usage_ledger u JOIN fiscal_documents f ON f.id = u.source_id WHERE f.access_key = $1),
                   (SELECT count(*) FROM fiscal_documents WHERE access_key = $1),
                   (SELECT count(*) FROM fiscal_documents WHERE access_key = $1 AND reprocess_of IS NOT NULL)
            """, conn);
        cmd.Parameters.Add(new() { Value = chave });
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal(1, r.GetInt64(0));   // uma única cobrança
        Assert.Equal(2, r.GetInt64(1));   // original + reprocessamento
        Assert.Equal(1, r.GetInt64(2));
    }

    [Fact]
    public async Task Xml_is_stored_encrypted_per_tenant_and_zero_retention_stores_nothing()
    {
        var doc = Base(800_002, destCpf: "52998224725");
        var xml = NfeBuilder.Bytes(doc);
        var (status, body) = await AuditAsync(xml, env.ApiKey);
        Assert.Equal(200, status);
        Assert.True(body!["xml_armazenado"]!.GetValue<bool>());

        await using var conn = new NpgsqlConnection(env.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT f.xml_storage_key, t.kms_key_ref, f.cpf_hmac, f.declared::text || f.recalculated::text || f.findings::text
              FROM fiscal_documents f JOIN tenants t ON t.id = f.tenant_id WHERE f.access_key = $1
            """, conn);
        cmd.Parameters.Add(new() { Value = Chave(doc) });
        string key, kek, meta;
        byte[] cpfHmac;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            (key, kek, cpfHmac, meta) = (r.GetString(0), r.GetString(1), r.GetFieldValue<byte[]>(2), r.GetString(3));
        }

        // LGPD: CPF só como HMAC; nada de CPF nem nome nos metadados.
        Assert.Equal(32, cpfHmac.Length);
        Assert.DoesNotContain("52998224725", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("HOMOLOGACAO", meta, StringComparison.Ordinal);

        // Envelope: decifra com a KEK do tenant; o objeto bruto não tem texto claro; outra KEK não abre.
        var vault = (XmlVault)env.Services.GetService(typeof(XmlVault))!;
        Assert.Equal(xml, await vault.GetAsync(kek, key, CancellationToken.None));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => vault.GetAsync("tenant/outro", key, CancellationToken.None));

        // zero_retention: nada gravado.
        var other = Base(800_003);
        var (s2, b2) = await AuditAsync(NfeBuilder.Bytes(other), env.ZeroRetentionApiKey);
        Assert.Equal(200, s2);
        Assert.False(b2!["xml_armazenado"]!.GetValue<bool>());
        await using var cmd2 = new NpgsqlCommand("SELECT xml_storage_key FROM fiscal_documents WHERE access_key = $1", conn);
        cmd2.Parameters.Add(new() { Value = Chave(other) });
        Assert.Equal(DBNull.Value, await cmd2.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Idempotency_key_is_required_and_xxe_is_rejected_without_billing()
    {
        using var noKey = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND")
        {
            Content = new ByteArrayContent(NfeBuilder.Bytes(Base(800_004))),
        };
        noKey.Headers.Add("X-Api-Key", env.ApiKey);
        var r = await env.Api.SendAsync(noKey);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        var xxe = """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]><NFe xmlns="http://www.portalfiscal.inf.br/nfe">&x;</NFe>""";
        var (status, body) = await AuditAsync(Encoding.UTF8.GetBytes(xxe), env.ApiKey);
        Assert.Equal(400, status);
        Assert.Equal("ERR_XML_REJEITADO", body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reprocess_job_reaudits_stored_xml_without_billing()
    {
        var doc = Base(800_010);
        var (status, _) = await AuditAsync(NfeBuilder.Bytes(doc), env.ApiKey);
        Assert.Equal(200, status);

        await using var conn = new NpgsqlConnection(env.SuperuserConnection);
        await conn.OpenAsync();
        async Task<long> Count(string sql)
        {
            await using var c = new NpgsqlCommand(sql, conn);
            c.Parameters.Add(new() { Value = Chave(doc) });
            return (long)(await c.ExecuteScalarAsync())!;
        }

        await using var t = new NpgsqlCommand("SELECT id FROM tenants WHERE root_cnpj = '11222333000181'", conn);
        var tenant = new TaxGateway.Core.Tenancy.TenantId((Guid)(await t.ExecuteScalarAsync())!);
        var ledgerBefore = await Count("SELECT count(*) FROM usage_ledger u JOIN fiscal_documents f ON f.id = u.source_id WHERE f.access_key = $1");

        var job = (TaxGateway.Api.Audit.ReprocessService)env.Services.GetService(typeof(TaxGateway.Api.Audit.ReprocessService))!;
        var summary = await job.RunAsync(tenant, new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 1), CancellationToken.None);

        Assert.Equal(0, summary.Falhas);
        Assert.True(summary.Reprocessados >= 1, summary.ToString());
        Assert.Equal(1, await Count("SELECT count(*) FROM fiscal_documents WHERE access_key = $1 AND reprocess_of IS NOT NULL"));
        Assert.Equal(ledgerBefore, await Count("SELECT count(*) FROM usage_ledger u JOIN fiscal_documents f ON f.id = u.source_id WHERE f.access_key = $1"));
    }

    [Fact]
    public async Task Inbound_audit_computes_credit_and_flags_conditioned_benefits()
    {
        // O destinatário do corpus (11444777000161) é o tenant sem retenção: ele audita a entrada.
        var integral = Load("A-000001-RO.xml", 800_100);
        var (s1, b1) = await AuditAsync(NfeBuilder.Bytes(integral), env.ZeroRetentionApiKey, "&direction=INBOUND");
        Assert.Equal(200, s1);
        Assert.Equal("CONFORME", b1!["status"]!.GetValue<string>());
        Assert.Equal("APROPRIAVEL_POR_DESTAQUE", b1["resumo_financeiro"]!["credito_status"]!.GetValue<string>());
        var declarado = b1["resumo_financeiro"]!["tributo_declarado"]!;
        Assert.Equal(declarado["cbs"]!.GetValue<decimal>() + declarado["ibs"]!.GetValue<decimal>(), b1["resumo_financeiro"]!["credito_potencial"]!.GetValue<decimal>());
        Assert.Contains(b1["ocorrencias"]!.AsArray(), o => o!["regra"]!.GetValue<string>() == "R040");

        var reducao = Load(Directory.GetFiles(XmlDir, "A-2*-arroz-*.xml").Select(Path.GetFileName).Order(StringComparer.Ordinal).First()!, 800_101);
        var (_, b2) = await AuditAsync(NfeBuilder.Bytes(reducao), env.ZeroRetentionApiKey, "&direction=INBOUND");
        Assert.Equal("INDICIO", b2!["status"]!.GetValue<string>());
        Assert.Contains(b2["ocorrencias"]!.AsArray(), o => o!["regra"]!.GetValue<string>() == "R021");

        var confirmada = Load(Directory.GetFiles(XmlDir, "A-2*-arroz-*.xml").Select(Path.GetFileName).Order(StringComparer.Ordinal).First()!, 800_102);
        var (_, b3) = await AuditAsync(NfeBuilder.Bytes(confirmada), env.ZeroRetentionApiKey, "&direction=INBOUND&confirmar=R021");
        Assert.Equal("CONFORME", b3!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Simples_supplier_credit_is_the_das_amount_and_divergence_is_detected()
    {
        var pleno = Load("S-pleno-RO.xml", 800_110);
        var (s1, b1) = await AuditAsync(NfeBuilder.Bytes(pleno), env.ZeroRetentionApiKey, "&direction=INBOUND");
        Assert.Equal(200, s1);
        Assert.Equal("CONFORME", b1!["status"]!.GetValue<string>());
        var ns = Ns(pleno);
        var das = decimal.Parse(pleno.SelectSingleNode("//n:gIBSSN/n:vIBSSN", ns)!.InnerText, CultureInfo.InvariantCulture)
                  + decimal.Parse(pleno.SelectSingleNode("//n:gCBSSN/n:vCBSSN", ns)!.InnerText, CultureInfo.InvariantCulture);
        Assert.Equal(das, b1["resumo_financeiro"]!["credito_potencial"]!.GetValue<decimal>());

        var errado = Load("S-pleno-RO.xml", 800_111);
        Shift(errado.SelectSingleNode("//n:det[1]//n:gCBSSN/n:vCBSSN", Ns(errado))!, 1m);
        Shift(errado.SelectSingleNode("//n:IBSCBSTot/n:gTribSN/n:vCBSSN", Ns(errado))!, 1m);
        NfeBuilder.Sign(errado, env.Pki.Leaf);
        var (_, b2) = await AuditAsync(NfeBuilder.Bytes(errado), env.ApiKey);
        Assert.Equal("DIVERGENTE", b2!["status"]!.GetValue<string>());
        var rules = b2["ocorrencias"]!.AsArray().Select(o => o!["regra"]!.GetValue<string>()).ToList();
        Assert.Contains("R013", rules);
        Assert.Contains("R011", rules);
    }

    [Fact]
    public async Task Mei_with_ibscbs_group_is_R050_and_without_is_conforme()
    {
        var comGrupo = Load("A-000001-RO.xml", 800_120);
        comGrupo.SelectSingleNode("//n:emit/n:CRT", Ns(comGrupo))!.InnerText = "4";
        NfeBuilder.Sign(comGrupo, env.Pki.Leaf);
        var (_, b1) = await AuditAsync(NfeBuilder.Bytes(comGrupo), env.ApiKey);
        Assert.Equal("DIVERGENTE", b1!["status"]!.GetValue<string>());
        Assert.Contains(b1["ocorrencias"]!.AsArray(), o => o!["regra"]!.GetValue<string>() == "R050");

        var sem = Load("A-000001-RO.xml", 800_121);
        var ns = Ns(sem);
        sem.SelectSingleNode("//n:emit/n:CRT", ns)!.InnerText = "4";
        foreach (var node in sem.SelectNodes("//n:IBSCBS | //n:IBSCBSTot", ns)!.Cast<XmlNode>().ToList())
        {
            node.ParentNode!.RemoveChild(node);
        }

        NfeBuilder.Sign(sem, env.Pki.Leaf);
        var (_, b2) = await AuditAsync(NfeBuilder.Bytes(sem), env.ApiKey);
        Assert.Equal("CONFORME", b2!["status"]!.GetValue<string>());
    }

    private XmlDocument Load(string file, int numero)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(XmlDir, file));
        Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        return doc;
    }

    // ------------------------------------------------------------------ helpers

    private XmlDocument Base(int numero, string? destCpf = null)
    {
        var files = Directory.GetFiles(XmlDir, "*.xml").Order(StringComparer.Ordinal).ToArray();
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(files[0]);
        var ns = Ns(doc);
        if (destCpf is not null)
        {
            var dest = doc.SelectSingleNode("//n:dest", ns);
            if (dest?.SelectSingleNode("n:CNPJ", ns) is { } c)
            {
                var cpf = doc.CreateElement("CPF", NfeBuilder.Ns);
                cpf.InnerText = destCpf;
                dest.ReplaceChild(cpf, c);
            }
        }

        Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        return doc;
    }

    /// <summary>Troca nNF e recalcula a chave/DV (cada teste usa uma chave própria, sem replay acidental).</summary>
    internal static void Rekey(XmlDocument doc, int numero)
    {
        var ns = Ns(doc);
        var inf = (XmlElement)doc.SelectSingleNode("//n:infNFe", ns)!;
        var old = inf.GetAttribute("Id")[3..];
        var baseKey = old[..25] + numero.ToString("D9", CultureInfo.InvariantCulture) + old[34..43];
        var sum = 0;
        var weight = 2;
        for (var i = 42; i >= 0; i--)
        {
            sum += (baseKey[i] - 48) * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        var dv = sum % 11 < 2 ? 0 : 11 - (sum % 11);
        inf.SetAttribute("Id", $"NFe{baseKey}{dv}");
        doc.SelectSingleNode("//n:ide/n:nNF", ns)!.InnerText = numero.ToString(CultureInfo.InvariantCulture);
        doc.SelectSingleNode("//n:ide/n:cDV", ns)!.InnerText = dv.ToString(CultureInfo.InvariantCulture);
    }

    internal static string Chave(XmlDocument doc) => ((XmlElement)doc.SelectSingleNode("//n:infNFe", Ns(doc))!).GetAttribute("Id")[3..];

    internal static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeBuilder.Ns);
        return ns;
    }

    private static void Shift(XmlNode node, decimal delta) =>
        node.InnerText = (decimal.Parse(node.InnerText, CultureInfo.InvariantCulture) + delta).ToString("0.00", CultureInfo.InvariantCulture);

    private async Task<(int Status, JsonNode? Body)> AuditAsync(byte[] xml, string apiKey, string extra = "")
    {
        for (var attempt = 0; ; attempt++)
        {
            var query = extra.Contains("direction=", StringComparison.Ordinal) ? extra.TrimStart('&') : "direction=OUTBOUND" + extra;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/auditoria/xml?{query}")
            {
                Content = new ByteArrayContent(xml),
            };
            req.Content.Headers.ContentType = new("application/xml");
            req.Headers.Add("X-Api-Key", apiKey);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var resp = await env.Api.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1));
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
        }
    }

    /// <summary>
    /// Gera o corpus: para cada caso, o motor calcula (com componentes e alíquotas do catálogo) e devolve os grupos
    /// RTC por /xml/generate; só entram documentos que o motor calculou sem erro (o emissor correto).
    /// </summary>
    private async Task RegenerateAsync()
    {
        using var engine = new HttpClient { BaseAddress = new Uri(GoldenFixture.EngineUrl) };
        Directory.CreateDirectory(XmlDir);
        foreach (var old in Directory.GetFiles(XmlDir, "*.xml"))
        {
            File.Delete(old);
        }

        var cases = new List<(string Id, string Modelo, string Data, int Mun, string Uf, (string Ncm, string Cst, string Cls, decimal VProd, decimal VFrete, decimal VDesc, decimal VIcms)[] Itens)>();
        var classes = await engine.GetFromJsonAsync<JsonArray>("calculadora/dados-abertos/classificacoes-tributarias/cbs-ibs?data=2027-06-15");
        var i = 0;
        foreach (var c in classes!.Select(x => x!["codigo"]!.GetValue<string>()).Order(StringComparer.Ordinal))
        {
            var (uf, mun, off) = GoldenCorpus.Capitais[i++ % GoldenCorpus.Capitais.Length];
            cases.Add(($"A-{c}-{uf}", i % 4 == 0 ? "65" : "55", $"2027-06-15T11:00:00{off}", mun, uf, [("84713012", c[..3], c, 1000m, 30m, 10m, 180m)]));
            cases.Add(($"A-{c}-arroz-{uf}", "55", $"2027-06-15T11:00:00{off}", mun, uf, [("10063021", c[..3], c, 250m, 0m, 0m, 17.5m)]));
        }

        foreach (var (uf, mun, off) in GoldenCorpus.Capitais)
        {
            cases.Add(($"B-2026-{uf}", "55", $"2026-03-10T09:00:00{off}", mun, uf,
                [("84713012", "000", "000001", 3000m, 50m, 0m, 540m), ("10063021", "200", "200003", 100m, 0m, 5m, 0m)]));
            cases.Add(($"C-2027-nfce-{uf}", "65", $"2027-02-01T09:30:00{off}", mun, uf,
                [("84713012", "000", "000001", 159.90m, 0m, 0m, 28.78m), ("10063021", "200", "200003", 24.99m, 0m, 0m, 0m), ("84713012", "000", "000001", 0.01m, 0m, 0m, 0m)]));
        }

        var numero = 1;
        await GenerateSimplesAsync(engine, numero);
        numero = 5000;
        foreach (var k in cases)
        {
            var date = DateTimeOffset.Parse(k.Data, CultureInfo.InvariantCulture);
            var rates = await GoldenCorpus.CatalogRatesAsync(env.AppConnection, k.Data);
            var itens = new JsonArray();
            var n = 1;
            foreach (var it in k.Itens)
            {
                var o = new JsonObject
                {
                    ["numero"] = n++,
                    ["ncm"] = it.Ncm,
                    ["cst"] = it.Cst,
                    ["cClassTrib"] = it.Cls,
                    ["gComponentesBC"] = new JsonObject { ["vProd"] = it.VProd, ["vFrete"] = it.VFrete, ["vDesc"] = it.VDesc, ["vICMS"] = it.VIcms },
                };
                if (rates is not null)
                {
                    o["aliquotasNominais"] = new JsonObject { ["cbs"] = rates["CBS"], ["ibsEstadual"] = rates["IBS_UF"], ["ibsMunicipal"] = rates["IBS_MUN"] };
                }

                itens.Add(o);
            }

            var req = new JsonObject
            {
                ["id"] = "golden-xml",
                ["versao"] = "1.0.0",
                ["tpDoc"] = int.Parse(k.Modelo, CultureInfo.InvariantCulture),
                ["dhFatoGerador"] = k.Data,
                ["municipio"] = k.Mun,
                ["uf"] = k.Uf,
                ["itens"] = itens,
            };
            using var calc = await engine.PostAsync("calculadora/observabilidade/regime-geral", new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));
            if (!calc.IsSuccessStatusCode)
            {
                continue;
            }

            var roc = await calc.Content.ReadAsStringAsync();
            var objetos = JsonNode.Parse(roc)!["objetos"]!.AsArray();
            if (objetos.Any(o => o!["estadoItem"]!.GetValue<string>() is not ("CALCULADO" or "CALCULO_SIMULADO")))
            {
                continue;   // o emissor "correto" só existe quando o motor calcula
            }

            using var gen = await engine.PostAsync($"calculadora/xml/generate?tipo={(k.Modelo == "65" ? "nfce" : "nfe")}", new StringContent(roc, Encoding.UTF8, "application/json"));
            if (!gen.IsSuccessStatusCode)
            {
                continue;
            }

            var frag = new XmlDocument();
            frag.LoadXml(await gen.Content.ReadAsStringAsync());
            var fns = Ns(frag);
            var nfeItens = k.Itens.Select((it, idx) =>
            {
                var imposto = frag.SelectSingleNode($"//n:det[@nItem='{idx + 1}']/n:imposto", fns)!;
                var rtc = string.Concat(imposto.ChildNodes.OfType<XmlElement>().Select(e => Strip(e.OuterXml)));
                return new NfeItem(idx + 1, it.Ncm, it.VProd, it.VIcms, rtc, it.VFrete == 0 ? null : it.VFrete, it.VDesc == 0 ? null : it.VDesc);
            }).ToList();
            var total = string.Concat(frag.SelectSingleNode("//n:total", fns)!.ChildNodes.OfType<XmlElement>().Select(e => Strip(e.OuterXml)));

            var spec = new NfeSpec(Cnpj, k.Mun / 100000, k.Mun, k.Uf, date, k.Modelo, numero++, nfeItens, total,
                DestCnpj: k.Modelo == "55" ? "11444777000161" : null);
            var doc = NfeBuilder.Build(spec);
            NfeBuilder.Sign(doc, env.Pki.Leaf);
            await File.WriteAllBytesAsync(Path.Combine(XmlDir, $"{k.Id}.xml"), NfeBuilder.Bytes(doc));
        }
    }

    /// <summary>
    /// Notas do Simples (D-503): o "emissor correto" usa o motor do Simples para vIBSSN/vCBSSN; no sublimite, o IBS
    /// vem do motor regular (com a CBS zerada) e a CBS do DAS vai em gTribSN/gCBSSN.
    /// </summary>
    private async Task GenerateSimplesAsync(HttpClient engine, int numero)
    {
        using var simples = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("RTC_SIMPLES_URL") ?? "http://127.0.0.1:8082/api/") };
        var k = 0;
        foreach (var (uf, mun, off) in GoldenCorpus.Capitais)
        {
            var crt = k % 3 == 2 ? "2" : "1";
            var pi = 0.20m + (k % 7) * 0.15m;
            var pc = 0.60m + (k % 5) * 0.35m;
            var data = $"2027-06-15T11:00:00{off}";
            var vprod = 1000m + (k * 37.5m);
            var req = new JsonObject
            {
                ["id"] = "x",
                ["versao"] = "1.0.0",
                ["data"] = "2027-06-15",
                ["tpDoc"] = "55",
                ["CRT"] = crt,
                ["itens"] = new JsonArray(new JsonObject
                {
                    ["numero"] = 1,
                    ["cAtivSN"] = "01",
                    ["cClassTrib"] = "000001",
                    ["ncm"] = "84713012",
                    ["baseCalculo"] = vprod.ToString("0.00", CultureInfo.InvariantCulture),
                    ["valorImpostoSeletivo"] = "0.00",
                    ["pCBSSN"] = pc.ToString("0.00", CultureInfo.InvariantCulture),
                }),
            };
            if (crt == "1")
            {
                req["itens"]![0]!["pIBSSN"] = pi.ToString("0.00", CultureInfo.InvariantCulture);
            }

            using var resp = await simples.PostAsync("calculadora/observabilidade/simples-nacional", new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));
            var sn = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            var t = sn["objetos"]![0]!["tribCalc"]!;
            if (sn["objetos"]![0]!["estadoItem"]!.GetValue<string>() != "CALCULADO")
            {
                k++;
                continue;
            }

            var gIbs = crt == "1" ? $"<gIBSSN><pIBSSN>{t["pIBSSN"]}</pIBSSN><vIBSSN>{t["vIBSSN"]}</vIBSSN></gIBSSN>" : "";
            var gTribSn = $"<gTribSN>{gIbs}<gCBSSN><pCBSSN>{t["pCBSSN"]}</pCBSSN><vCBSSN>{t["vCBSSN"]}</vCBSSN></gCBSSN></gTribSN>";
            var totSn = $"<gTribSN><vIBSSN>{(crt == "1" ? t["vIBSSN"] : "0.00")}</vIBSSN><vCBSSN>{t["vCBSSN"]}</vCBSSN></gTribSN>";
            string itemXml, totalXml;
            if (crt == "1")
            {
                itemXml = $"<IBSCBS><CST>000</CST><cClassTrib>000001</cClassTrib>{gTribSn}</IBSCBS>";
                totalXml = $"<IBSCBSTot><vBCIBSCBS>0.00</vBCIBSCBS>{totSn}</IBSCBSTot>";
            }
            else
            {
                // Sublimite: gIBSCBS do motor regular com CBS zerada + gTribSN com a CBS do DAS.
                var rates = await GoldenCorpus.CatalogRatesAsync(env.AppConnection, data);
                var regReq = new JsonObject
                {
                    ["id"] = "x",
                    ["versao"] = "1.0.0",
                    ["tpDoc"] = 55,
                    ["dhFatoGerador"] = data,
                    ["municipio"] = mun,
                    ["uf"] = uf,
                    ["itens"] = new JsonArray(new JsonObject
                    {
                        ["numero"] = 1,
                        ["ncm"] = "84713012",
                        ["cst"] = "000",
                        ["cClassTrib"] = "000001",
                        ["gComponentesBC"] = new JsonObject { ["vProd"] = vprod },
                        ["aliquotasNominais"] = new JsonObject { ["cbs"] = 0m, ["ibsEstadual"] = rates!["IBS_UF"], ["ibsMunicipal"] = rates["IBS_MUN"] },
                    }),
                };
                using var calc = await engine.PostAsync("calculadora/observabilidade/regime-geral", new StringContent(regReq.ToJsonString(), Encoding.UTF8, "application/json"));
                var roc = await calc.Content.ReadAsStringAsync();
                using var gen = await engine.PostAsync("calculadora/xml/generate?tipo=nfe", new StringContent(roc, Encoding.UTF8, "application/json"));
                var frag = new XmlDocument();
                frag.LoadXml(await gen.Content.ReadAsStringAsync());
                var fns = Ns(frag);
                var ibscbs = (XmlElement)frag.SelectSingleNode("//n:det/n:imposto/n:IBSCBS", fns)!;
                itemXml = Strip(ibscbs.OuterXml).Replace("</IBSCBS>", gTribSn + "</IBSCBS>", StringComparison.Ordinal);
                var tot = (XmlElement)frag.SelectSingleNode("//n:total/n:IBSCBSTot", fns)!;
                totalXml = Strip(tot.OuterXml).Replace("</IBSCBSTot>", totSn + "</IBSCBSTot>", StringComparison.Ordinal);
            }

            var spec = new NfeSpec(Cnpj, mun / 100000, mun, uf, DateTimeOffset.Parse(data, CultureInfo.InvariantCulture), "55", numero + k,
                [new NfeItem(1, "84713012", vprod, 0m, itemXml, CAtivSn: "01")], totalXml, DestCnpj: "11444777000161", Crt: crt);
            var doc = NfeBuilder.Build(spec);
            NfeBuilder.Sign(doc, env.Pki.Leaf);
            await File.WriteAllBytesAsync(Path.Combine(XmlDir, $"S-{(crt == "1" ? "pleno" : "sublimite")}-{uf}.xml"), NfeBuilder.Bytes(doc));
            k++;
        }
    }

    /// <summary>Remove a declaração de namespace repetida dos fragmentos (o pai já está no namespace da NF-e).</summary>
    private static string Strip(string xml) => xml.Replace($" xmlns=\"{NfeBuilder.Ns}\"", "", StringComparison.Ordinal);
}
