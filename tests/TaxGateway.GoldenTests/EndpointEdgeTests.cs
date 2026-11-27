using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using System.Xml;

using TaxGateway.Identity.Accounts;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Caminhos de borda das rotas públicas, de conta e de administração contra a pilha real: validação de entrada (400,
/// nunca 500), permissões (403), recursos inexistentes (404), conflitos (409), estados vazios e idempotência.
/// Faixa de numeração de notas 760_000–769_999.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class EndpointEdgeTests(GoldenFixture env)
{
    private const string Senha = "Uma frase longa e boa 2027!";
    private const string SecondOperatorKey = "tg_admin_golden_operador_b_000000000000";
    private static readonly string[] FiscalScope = ["admin:fiscal"];
    private static readonly string[] UnknownOperatorScope = ["admin:tudo"];
    private static readonly string[] NoScopes = [];
    private static readonly string[] AnyEvent = ["*"];
    private static readonly string[] NoEvents = [];
    private static readonly string[] PeopleOnlyScope = ["membros:manage"];
    private static readonly string[] KeysAndSimulation = ["keys:manage", "simulacao:write"];
    private static readonly string[] KeysOnly = ["keys:manage"];
    private static readonly string[] AuditOnly = ["auditoria:write"];
    private static readonly string[] SimulationOnly = ["simulacao:write"];
    private static readonly string[] InvalidCnpjs = ["123"];
    private static readonly string[] CnpjsAndRead = ["cnpjs:manage", "auditoria:read"];
    private static readonly string[] ExpectedSimulationErrors =
    [
        "cnpj_emitente", "municipio_ibge", "uf", "compra_governamental.tipo_ente", "itens[0].cst_ibs_cbs", "itens[0].c_class_trib", "itens[0].ncm",
        "itens[0].nbs", "itens[0].valor_base_calculo", "itens[0].quantidade", "itens[0].imposto_seletivo", "itens[0].aliquotas_nominais",
        "itens[0].aliquotas_nominais.cbs", "itens[1].numero", "itens[2]", "crt", "tp_regime_ibs_cbs",
    ];

    private static readonly string[] ExpectedExceptionErrors =
        ["documento_base64 inválido", "tributo:", "aliquota entre", "vigencia_fim", "motivo:", "processo", "cnpj inválido", "ente_codigo", "produto:", "PDF"];
    private static readonly Lazy<TestPki> Untrusted = new(() => TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"tg-edge-untrusted-{Guid.NewGuid():N}")));

    // ------------------------------------------------------------------------------------------------ apoio HTTP

    private sealed record Reply(HttpStatusCode Status, JsonNode? Body, string Text, HttpResponseMessage Response)
    {
        public string? Code => Body is JsonObject o ? o["code"]?.GetValue<string>() : null;
    }

    private static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, string? apiKey = null,
        string? adminKey = null, bool csrf = true, HttpContent? content = null, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(method, path) { Content = content ?? (body is null ? null : JsonContent.Create(body)) };
        if (apiKey is not null)
        {
            req.Headers.Add("X-Api-Key", apiKey);
        }

        if (adminKey is not null)
        {
            req.Headers.Add("X-Admin-Key", adminKey);
        }

        if (csrf)
        {
            req.Headers.Add("X-TaxGateway-Csrf", "1");
        }

        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (text.Length > 0 && resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true)
        {
            json = JsonNode.Parse(text);
        }

        return new Reply(resp.StatusCode, json, text, resp);
    }

    private Task<Reply> Api(string key, HttpMethod m, string path, object? body = null, HttpContent? content = null, string? idem = null) =>
        SendAsync(env.Api, m, path, body, apiKey: key, csrf: false, content: content, idempotencyKey: idem);

    private Task<Reply> Admin(HttpMethod m, string path, object? body = null, HttpContent? content = null) =>
        SendAsync(env.Api, m, path, body, adminKey: GoldenFixture.OperatorKey, csrf: false, content: content);

    private static Task<Reply> Browser(HttpClient b, HttpMethod m, string path, object? body = null, HttpContent? content = null, bool csrf = true) =>
        SendAsync(b, m, path, body, csrf: csrf, content: content);

    private static StringContent Raw(string json, string mediaType = "application/json") => new(json, Encoding.UTF8, mediaType);

    private static string Token(string link) => HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;

    private static byte[] FromBase32(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(s.Select(c => Convert.ToString(Alphabet.IndexOf(c, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
    }

    private string Code(string secret, int ahead = 0) => Totp.Code(FromBase32(secret), Totp.StepAt(env.Clock.GetUtcNow()) + ahead);

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "condição não atingida no tempo");
            await Task.Delay(250);
        }
    }

    /// <summary>CNPJ válido com a raiz informada (8 dígitos) e filial 0001.</summary>
    private static string CnpjWithRoot(string root)
    {
        var stem = root + "0001";
        static int Dv(string s, int[] w)
        {
            var sum = 0;
            for (var i = 0; i < w.Length; i++)
            {
                sum += (s[i] - '0') * w[i];
            }

            var r = sum % 11;
            return r < 2 ? 0 : 11 - r;
        }

        var d1 = Dv(stem, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var d2 = Dv(stem + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        return $"{stem}{d1}{d2}";
    }

    private string Pfx(string cnpj, TestPki? pki = null, string password = "senha-a1")
    {
        var now = env.Clock.GetUtcNow();
        using var leaf = TestPki.IssueLeaf((pki ?? env.Pki).Root, cnpj, now.AddDays(-30), now.AddYears(2));
        return Convert.ToBase64String(leaf.Export(X509ContentType.Pfx, password));
    }

    /// <summary>Cadastro → confirmação → login → MFA. Devolve o navegador logado, o e-mail, o segredo TOTP e a empresa.</summary>
    private async Task<(HttpClient Browser, string Email, string Secret, Guid Tenant)> SignupAndLoginAsync(string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@exemplo.test";
        var browser = env.NewBrowser();
        var signup = await Browser(browser, HttpMethod.Post, "/v1/conta/cadastro", new
        {
            nome = "Pessoa de Borda",
            email,
            senha = Senha,
            empresa = new { nome = $"Empresa {label}", cnpj = GoldenFixture.NewCnpj() },
            aceite_termos = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, signup.Status);
        Assert.Equal(HttpStatusCode.OK, (await Browser(browser, HttpMethod.Post, "/v1/conta/confirmar-email", new { token = Token(env.Emails.LastLinkTo(email)!) })).Status);
        Assert.Equal("configurar", (await Browser(browser, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        var secret = await EnableMfaAsync(browser, "/v1/conta");
        var tenant = (await Browser(browser, HttpMethod.Get, "/v1/conta/sessao")).Body!["empresa_ativa"]!.GetValue<Guid>();
        return (browser, email, secret, tenant);
    }

    private async Task<string> EnableMfaAsync(HttpClient browser, string prefix)
    {
        var secret = (await Browser(browser, HttpMethod.Post, $"{prefix}/mfa/iniciar")).Body!["segredo"]!.GetValue<string>();
        var enabled = await Browser(browser, HttpMethod.Post, $"{prefix}/mfa/ativar", new { codigo = Code(secret) });
        Assert.Equal(HttpStatusCode.OK, enabled.Status);
        return secret;
    }

    private async Task UsePlanWithUsersAsync(Guid tenant, int maxUsers)
    {
        var code = $"TEDGE{Random.Shared.Next(10000, 99999)}";
        var plan = await Admin(HttpMethod.Put, $"/admin/v1/planos/{code}", new
        {
            name = "Borda muitos usuários",
            price_cents = 0,
            included_docs = 10,
            included_simulations = 10,
            hard_quota = true,
            max_users = maxUsers,
            rpm = 600,
            burst = 100,
            concurrency = 10,
            is_public = false,
            self_service = false,
            active = true,
        });
        Assert.Equal(HttpStatusCode.OK, plan.Status);
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = code })).Status);
    }

    private static async Task<string> RootAsync(HttpClient browser) =>
        (await Browser(browser, HttpMethod.Get, "/v1/me")).Body!["cnpj_raiz"]!.GetValue<string>();

    private XmlDocument Nfe(int numero)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        return doc;
    }

    private static ByteArrayContent XmlContent(XmlDocument doc)
    {
        var c = new ByteArrayContent(NfeBuilder.Bytes(doc));
        c.Headers.ContentType = new("application/xml");
        return c;
    }

    // ------------------------------------------------------------------------------------------------ operadores

    /// <summary>D-U08/D-R04: gestão de operadores pela Admin API — criação validada, primeiro acesso, desativação e trilha.</summary>
    [Fact]
    public async Task Operadores_sao_criados_validados_desativados_e_aparecem_na_trilha()
    {
        var email = $"op-edge-{Guid.NewGuid():N}@taxgateway.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Admin(HttpMethod.Post, "/admin/v1/operadores", new { email, nome = "Operadora de Borda", escopos = FiscalScope })).Status);
        Assert.Equal("ERR_CONFLICT", (await Admin(HttpMethod.Post, "/admin/v1/operadores", new { email, nome = "Repetida", escopos = FiscalScope })).Code);
        Assert.Equal("ERR_VALIDATION", (await Admin(HttpMethod.Post, "/admin/v1/operadores", new { email = "nao-e-email", nome = "X", escopos = FiscalScope })).Code);
        Assert.Equal("ERR_VALIDATION", (await Admin(HttpMethod.Post, "/admin/v1/operadores",
            new { email = $"x-{Guid.NewGuid():N}@taxgateway.test", nome = "X", escopos = UnknownOperatorScope })).Code);
        Assert.Equal("ERR_VALIDATION", (await Admin(HttpMethod.Post, "/admin/v1/operadores",
            new { email = $"x-{Guid.NewGuid():N}@taxgateway.test", nome = "X", escopos = NoScopes })).Code);

        var listed = (await Admin(HttpMethod.Get, "/admin/v1/operadores")).Body!.AsArray().Single(o => o!["email"]!.GetValue<string>() == email)!;
        Assert.False(listed["mfa"]!.GetValue<bool>());
        Assert.Equal("ACTIVE", listed["situacao"]!.GetValue<string>());
        Assert.Equal(FiscalScope, listed["escopos"]!.AsArray().Select(x => x!.GetValue<string>()));
        var id = listed["id"]!.GetValue<Guid>();

        // Primeiro acesso pelo link do e-mail, MFA e sessão de operador com os escopos concedidos.
        var link = env.Emails.LastLinkTo(email)!;
        Assert.Contains("/backoffice/redefinir-senha?token=", link, StringComparison.Ordinal);
        var bo = env.NewBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(bo, HttpMethod.Post, "/admin/v1/conta/senha/redefinir", new { token = Token(link), senha = Senha })).Status);
        Assert.Equal("ERR_LINK_INVALIDO", (await Browser(bo, HttpMethod.Post, "/admin/v1/conta/senha/redefinir", new { token = Token(link), senha = Senha })).Code);   // uso único
        Assert.Equal("configurar", (await Browser(bo, HttpMethod.Post, "/admin/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        await EnableMfaAsync(bo, "/admin/v1/conta");
        var session = (await Browser(bo, HttpMethod.Get, "/admin/v1/conta/sessao")).Body!;
        Assert.Equal("ok", session["mfa"]!.GetValue<string>());
        Assert.Equal(FiscalScope, session["escopos"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(session["empresas"]);   // operador não tem empresas

        // Esqueci a senha de operador: o link aponta para o backoffice.
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(env.NewBrowser(), HttpMethod.Post, "/admin/v1/conta/senha/esqueci", new { email })).Status);
        Assert.StartsWith("http://backoffice.golden.test/backoffice/redefinir-senha?token=", env.Emails.LastLinkTo(email)!, StringComparison.Ordinal);

        Assert.Equal("ERR_NOT_FOUND", (await Admin(HttpMethod.Post, $"/admin/v1/operadores/{Guid.NewGuid()}/desativar")).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin(HttpMethod.Post, $"/admin/v1/operadores/{id}/desativar")).Status);
        Assert.Equal("DISABLED", (await Admin(HttpMethod.Get, "/admin/v1/operadores")).Body!.AsArray().Single(o => o!["id"]!.GetValue<Guid>() == id)!["situacao"]!.GetValue<string>());

        // Desativado, a sessão aberta deixa de valer na hora.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser(bo, HttpMethod.Get, "/admin/v1/conta/sessao")).Status);

        var trail = (await Admin(HttpMethod.Get, "/admin/v1/auditoria-acoes?ator=golden")).Body!.AsArray();
        Assert.Contains(trail, a => a!["acao"]!.GetValue<string>() == "operador.desativado" && a["alvo"]!.GetValue<string>() == id.ToString());
        Assert.Contains(trail, a => a!["acao"]!.GetValue<string>() == "operador.criado" && a["alvo"]!.GetValue<string>() == email);
        Assert.Empty((await Admin(HttpMethod.Get, $"/admin/v1/auditoria-acoes?tenant={Guid.NewGuid()}")).Body!.AsArray());
    }

    // ------------------------------------------------------------------------------------------------ API keys

    /// <summary>D-U09/D-P31: escopos da chave nova contidos nos da atual, validade limitada pela da mãe e revogação imediata.</summary>
    [Fact]
    public async Task Chaves_de_API_respeitam_escopos_validade_e_revogacao()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Chaves de borda", GoldenFixture.NewCnpj());
        Assert.Equal("ERR_FORBIDDEN_SCOPE", (await Api(key, HttpMethod.Post, "/v1/api-keys", new { nome = "pessoas", scopes = PeopleOnlyScope })).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Post, "/v1/api-keys", new { nome = new string('n', 81) })).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Post, "/v1/api-keys", new { nome = "passado", expira_em = DateTimeOffset.UtcNow.AddDays(-1) })).Code);

        // Chave temporária (2 dias): as filhas precisam expirar até ela e não ganham escopo que ela não tem.
        var exp = DateTimeOffset.UtcNow.AddDays(2);
        var temp = await Api(key, HttpMethod.Post, "/v1/api-keys", new { nome = "temporaria", scopes = KeysAndSimulation, expira_em = exp });
        Assert.Equal(HttpStatusCode.Created, temp.Status);
        var tempKey = temp.Body!["api_key"]!.GetValue<string>();
        var eternal = await Api(tempKey, HttpMethod.Post, "/v1/api-keys", new { nome = "eterna" });
        Assert.Equal("ERR_VALIDATION", eternal.Code);
        Assert.Contains("expira", eternal.Body!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ERR_VALIDATION", (await Api(tempKey, HttpMethod.Post, "/v1/api-keys", new { nome = "longa", expira_em = exp.AddDays(1) })).Code);
        var wider = await Api(tempKey, HttpMethod.Post, "/v1/api-keys", new { nome = "mais", scopes = AuditOnly, expira_em = exp.AddHours(-1) });
        Assert.Equal(HttpStatusCode.Forbidden, wider.Status);
        Assert.Contains("fora da chave atual", wider.Body!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        var child = await Api(tempKey, HttpMethod.Post, "/v1/api-keys", new { nome = "filha", expira_em = exp.AddHours(-1) });
        Assert.Equal(HttpStatusCode.Created, child.Status);
        Assert.Equal(SimulationOnly, child.Body!["scopes"]!.AsArray().Select(x => x!.GetValue<string>()));   // padrão ∩ escopos da mãe

        // Chave só de gestão de chaves não cria chave "padrão" (simular/auditar) sem dizer os escopos.
        var keysOnly = (await Api(key, HttpMethod.Post, "/v1/api-keys", new { nome = "so-chaves", scopes = KeysOnly })).Body!["api_key"]!.GetValue<string>();
        Assert.Contains("Informe os escopos", (await Api(keysOnly, HttpMethod.Post, "/v1/api-keys", new { nome = "x" })).Body!["title"]!.GetValue<string>(), StringComparison.Ordinal);

        // Revogação: efeito imediato; segunda revogação e id desconhecido = 404.
        var childId = child.Body["id"]!.GetValue<Guid>();
        var childKey = child.Body["api_key"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await Api(childKey, HttpMethod.Get, "/v1/me")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Api(key, HttpMethod.Delete, $"/v1/api-keys/{childId}")).Status);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Delete, $"/v1/api-keys/{childId}")).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Delete, $"/v1/api-keys/{Guid.NewGuid()}")).Code);
        await WaitAsync(async () => (await Api(childKey, HttpMethod.Get, "/v1/me")).Status == HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------------------------------------ Admin API

    private static JsonObject Line(string tributo, string tipo, string esfera, decimal aliquota, string from, string? ente = null, string? forma = null,
        string? to = null, string? fonte = "LEI_FEDERAL", string? produto = null) => new()
        {
            ["tributo"] = tributo,
            ["tipo"] = tipo,
            ["esfera"] = esfera,
            ["ente_codigo"] = ente,
            ["forma_aplicacao"] = forma,
            ["aliquota"] = aliquota,
            ["valid_from"] = from,
            ["valid_to"] = to,
            ["ato_legal"] = "Lei de teste",
            ["fonte"] = fonte,
            ["produto"] = produto,
        };

    /// <summary>Admin API: provisionamento de tenant e propostas de alíquota recusam entrada inválida com o motivo.</summary>
    [Fact]
    public async Task Admin_valida_tenant_e_propostas_de_aliquota()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/admin/v1/tenants", new { nome = "", cnpj_raiz = "1", plano = "OEM" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/admin/v1/tenants",
            new { nome = "Retenção zero", cnpj_raiz = GoldenFixture.NewCnpj(), plano = "OEM", retencao_xml_dias = 0 })).Status);
        var badCnpj = await Admin(HttpMethod.Post, "/admin/v1/tenants", new { nome = "CNPJ ruim", cnpj_raiz = GoldenFixture.NewCnpj(), plano = "OEM", cnpjs = InvalidCnpjs });
        Assert.Contains("CNPJ inválido: 123", badCnpj.Body!["title"]!.GetValue<string>(), StringComparison.Ordinal);
        var noPlan = await Admin(HttpMethod.Post, "/admin/v1/tenants", new { nome = "Sem plano", cnpj_raiz = GoldenFixture.NewCnpj(), plano = "NAO_EXISTE" });
        Assert.Equal("Plano inexistente", noPlan.Body!["title"]!.GetValue<string>());

        var root = GoldenFixture.NewCnpj();
        var created = await Admin(HttpMethod.Post, "/admin/v1/tenants", new { nome = "Com filiais", cnpj_raiz = root, plano = "OEM", cnpjs = new JsonArray(GoldenFixture.NewCnpj()) });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("ERR_CONFLICT", (await Admin(HttpMethod.Post, "/admin/v1/tenants", new { nome = "Repetido", cnpj_raiz = root, plano = "OEM" })).Code);

        const string Changes = "/admin/v1/params/rates/changes";
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, Changes, new JsonObject { ["justificativa"] = " ", ["linhas"] = new JsonArray() })).Status);
        var many = new JsonArray();
        for (var i = 0; i < 6001; i++)
        {
            many.Add(new JsonObject { ["tributo"] = "CBS" });
        }

        Assert.Contains("6.000", (await Admin(HttpMethod.Post, Changes, new JsonObject { ["justificativa"] = "muitas", ["linhas"] = many })).Body!["title"]!.GetValue<string>(), StringComparison.Ordinal);

        async Task<string> Error(JsonObject line)
        {
            var r = await Admin(HttpMethod.Post, Changes, new JsonObject { ["justificativa"] = "teste de borda", ["linhas"] = new JsonArray(line) });
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            return r.Body!["detail"]!.GetValue<string>();
        }

        Assert.Contains("são obrigatórios", await Error(Line("CBS", "REFERENCIA", "UNIAO", 1m, "2031-01-01", fonte: "JORNAL")), StringComparison.Ordinal);
        Assert.Contains("não tem ente", await Error(Line("CBS", "REFERENCIA", "UNIAO", 1m, "2031-01-01", ente: "35")), StringComparison.Ordinal);
        Assert.Contains("fim da vigência", await Error(Line("CBS", "REFERENCIA", "UNIAO", 1m, "2031-01-01", to: "2030-12-31")), StringComparison.Ordinal);

        // Prévia: sem linhas é 400; com linha inválida, mostra o erro sem gravar.
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, $"{Changes}/preview", new JsonObject { ["linhas"] = new JsonArray() })).Status);
        var preview = await Admin(HttpMethod.Post, $"{Changes}/preview", new JsonObject { ["linhas"] = new JsonArray(Line("CBS", "REFERENCIA", "UNIAO", 1m, "2031-01-01", ente: "35")) });
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Contains("não tem ente", preview.Body!["erros"]![0]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal("ERR_NOT_FOUND", (await Admin(HttpMethod.Post, $"/admin/v1/params/changes/{Guid.NewGuid()}/approve")).Code);
        Assert.Equal("ERR_PARAM_CONFLICT", (await Admin(HttpMethod.Post, $"/admin/v1/params/changes/{Guid.NewGuid()}/reject", new { motivo = "Não existe esta proposta" })).Code);
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Get, "/admin/v1/params/changes?status=REJECTED")).Status);
    }

    // ------------------------------------------------------------------------------------------------ simulação

    private static object Sim(string cnpj) => new
    {
        cnpj_emitente = cnpj,
        regime_tributario = "REGIME_REGULAR",
        data_fato_gerador = "2027-06-15T14:00:00-03:00",
        municipio_ibge = 3550308,
        uf = "SP",
        itens = new[] { new { numero = 1, cst_ibs_cbs = "000", c_class_trib = "000001", valor_base_calculo = 1000, ncm = "84713012", quantidade = 1, unidade = "UN" } },
    };

    /// <summary>SDD §6: corpo inválido é 400 com código; erro determinístico do cliente fica guardado na Idempotency-Key.</summary>
    [Fact]
    public async Task Simulacao_valida_o_corpo_e_guarda_erro_do_cliente_na_idempotencia()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Simulação de borda", GoldenFixture.NewCnpj(), autoProvision: false);
        Assert.Equal("ERR_JSON_INVALIDO", (await Api(key, HttpMethod.Post, "/v1/calculo/simular", content: Raw("{\"cnpj_emitente\": "))).Code);
        var empty = await Api(key, HttpMethod.Post, "/v1/calculo/simular", content: Raw("null"));
        Assert.Equal("Corpo vazio", empty.Body!["title"]!.GetValue<string>());
        Assert.Equal("ERR_CAMPO_DESCONHECIDO", (await Api(key, HttpMethod.Post, "/v1/calculo/simular", content: Raw("{\"campo_inexistente\": 1}"))).Code);
        Assert.Equal("ERR_IDEMPOTENCY_KEY_INVALIDA", (await Api(key, HttpMethod.Post, "/v1/calculo/simular", Sim(GoldenFixture.NewCnpj()), idem: new string('k', 256))).Code);

        // CNPJ não cadastrado (tenant sem autoprovisionamento): o 403 fica guardado e o replay devolve o mesmo erro.
        var idem = Guid.NewGuid().ToString("N");
        var cnpj = GoldenFixture.NewCnpj();
        var first = await Api(key, HttpMethod.Post, "/v1/calculo/simular", Sim(cnpj), idem: idem);
        Assert.Equal(HttpStatusCode.Forbidden, first.Status);
        var replay = await Api(key, HttpMethod.Post, "/v1/calculo/simular", Sim(cnpj), idem: idem);
        Assert.Equal(HttpStatusCode.Forbidden, replay.Status);
        Assert.Equal(first.Code, replay.Code);
        Assert.Equal("true", replay.Response.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal("ERR_IDEMPOTENCY_KEY_REUSED", (await Api(key, HttpMethod.Post, "/v1/calculo/simular", Sim(GoldenFixture.NewCnpj()), idem: idem)).Code);
    }

    // ------------------------------------------------------------------------------------------------ DF-e

    /// <summary>SDD §6.5: certificado e captura validam CNPJ, corpo e estado (sem certificado, sem configuração).</summary>
    [Fact]
    public async Task DFe_certificado_e_captura_validam_entrada_e_estado()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("DF-e de borda", GoldenFixture.NewCnpj(), autoProvision: false);
        var cnpj = GoldenFixture.NewCnpj();
        Assert.Equal(HttpStatusCode.Created, (await Api(key, HttpMethod.Post, "/v1/oem/cnpjs", new { cnpj })).Status);
        var cert = $"/v1/cnpjs/{cnpj}/certificado";

        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Put, "/v1/cnpjs/123/certificado", new { pfx_base64 = "", senha = "x" })).Code);
        Assert.Equal("ERR_CNPJ_NOT_PROVISIONED", (await Api(key, HttpMethod.Get, $"/v1/cnpjs/{GoldenFixture.NewCnpj()}/certificado")).Code);
        Assert.Equal("ERR_CNPJ_NOT_PROVISIONED", (await Api(key, HttpMethod.Get, $"/v1/cnpjs/{GoldenFixture.NewCnpj()}/dfe/status")).Code);
        Assert.Contains("inválido", (await Api(key, HttpMethod.Put, cert, new { pfx_base64 = "@@não é base64@@", senha = "x" })).Body!["title"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Put, cert, new { pfx_base64 = "", senha = "x" })).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Put, cert, new { pfx_base64 = Pfx(cnpj) })).Code);   // sem senha

        // Estados vazios: sem certificado, sem captura configurada; ligar a captura exige o A1.
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Get, cert)).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Delete, cert)).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Get, $"/v1/cnpjs/{cnpj}/dfe/status")).Code);
        Assert.Equal("ERR_CERT_REQUIRED", (await Api(key, HttpMethod.Post, $"/v1/cnpjs/{cnpj}/dfe/sync")).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Post, "/v1/cnpjs/123/dfe/sync")).Code);
        Assert.Equal("ERR_CNPJ_NOT_PROVISIONED", (await Api(key, HttpMethod.Delete, $"/v1/cnpjs/{GoldenFixture.NewCnpj()}/certificado")).Code);

        var up = await Api(key, HttpMethod.Put, cert, new { pfx_base64 = Pfx(cnpj), senha = "senha-a1" });
        Assert.Equal(HttpStatusCode.OK, up.Status);
        Assert.Equal(up.Body!["thumbprint"]!.GetValue<string>(), (await Api(key, HttpMethod.Get, cert)).Body!["thumbprint"]!.GetValue<string>());
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Post, $"/v1/cnpjs/{cnpj}/dfe/sync", new { c_uf_autor = 99 })).Code);

        // Sem corpo: padrão SP (35) e ciência ligada.
        var sync = await Api(key, HttpMethod.Post, $"/v1/cnpjs/{cnpj}/dfe/sync");
        Assert.Equal(HttpStatusCode.OK, sync.Status);
        Assert.True(sync.Body!["ativo"]!.GetValue<bool>());
        Assert.True(sync.Body["manifestar_ciencia"]!.GetValue<bool>());
        Assert.Empty(sync.Body["documentos"]!.AsObject());
        Assert.Equal(HttpStatusCode.OK, (await Api(key, HttpMethod.Get, $"/v1/cnpjs/{cnpj}/dfe/status")).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Api(key, HttpMethod.Delete, cert)).Status);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Get, cert)).Code);
    }

    // ------------------------------------------------------------------------------------------------ documentos

    /// <summary>D-R32: resumo do mês anterior nos dias 1 a 3, só para quem auditou algo, uma vez por mês.</summary>
    [Fact]
    public async Task Resumo_mensal_sai_no_inicio_do_mes_seguinte()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Resumo do mês", GoldenFixture.NewCnpj(), retention: 30);
        var audit = await Api(key, HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND", content: XmlContent(Nfe(770_001)), idem: Guid.NewGuid().ToString("N"));
        Assert.True(audit.Status == HttpStatusCode.OK, audit.Text);

        // Mês de competência da auditoria (o relógio do fixture pode ter sido avançado por outro teste): o resumo dele
        // sai entre os dias 1 e 3 do mês seguinte.
        var month = DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime);
        month = new DateOnly(month.Year, month.Month, 1);
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Npgsql.NpgsqlDataSource>(env.WorkerServices);
        async Task<int> RunAsync(DateOnly day)
        {
            await using var cmd = db.CreateCommand("SELECT app.notify_monthly_summary($1)");
            cmd.Parameters.Add(new() { Value = day });
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        var next = month.AddMonths(1);
        Assert.Equal(0, await RunAsync(next.AddDays(9)));   // fora da janela
        Assert.True(await RunAsync(next.AddDays(1)) >= 1);
        Assert.Equal(0, await RunAsync(next.AddDays(2)));    // uma vez por mês
        var avisos = (await Api(key, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray();
        var resumo = Assert.Single(avisos, a => a!["tipo"]!.GetValue<string>() == "RESUMO_MENSAL");
        Assert.Equal($"Resumo de {month:MM/yyyy}", resumo!["titulo"]!.GetValue<string>());
    }

    /// <summary>D-J01: filtros combinados, exportação por ocorrência (com fórmula neutralizada) e lista de DF-e validada.</summary>
    [Fact]
    public async Task Documentos_filtrados_exportados_por_ocorrencia_e_lista_de_DFe()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("Documentos de borda", GoldenFixture.NewCnpj(), retention: 30);
        var saida = Nfe(760_001);
        var entrada = Nfe(760_002);
        var outbound = await Api(key, HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND", content: XmlContent(saida), idem: Guid.NewGuid().ToString("N"));
        Assert.True(outbound.Status == HttpStatusCode.OK, outbound.Text);
        var inbound = await Api(key, HttpMethod.Post, "/v1/auditoria/xml?direction=INBOUND", content: XmlContent(entrada), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, inbound.Status);
        Assert.NotEmpty(inbound.Body!["ocorrencias"]!.AsArray());
        var chaveSaida = GoldenXmlTests.Chave(saida);
        var chaveEntrada = GoldenXmlTests.Chave(entrada);

        // Todos os filtros juntos: o lote inexistente esvazia o resultado (estado vazio, não erro).
        var all = $"direcao=saida&status=conforme,divergente&cnpj=11222333000181&chave={chaveSaida}";
        Assert.Single((await Api(key, HttpMethod.Get, $"/v1/auditoria/documentos?{all}")).Body!["documentos"]!.AsArray());
        Assert.Empty((await Api(key, HttpMethod.Get, $"/v1/auditoria/documentos?{all}&lote={Guid.NewGuid()}")).Body!["documentos"]!.AsArray());
        var entradas = (await Api(key, HttpMethod.Get, "/v1/auditoria/documentos?direcao=entrada")).Body!["documentos"]!.AsArray();
        Assert.Equal(chaveEntrada, Assert.Single(entradas)!["chave_acesso"]!.GetValue<string>());
        Assert.Equal("entrada", entradas[0]!["direcao"]!.GetValue<string>());
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Get, "/v1/auditoria/documentos?chave=123")).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Get, "/v1/auditoria/documentos?cursor=a")).Code);   // base64 inválido
        var resumo = (await Api(key, HttpMethod.Get, "/v1/auditoria/resumo?direcao=inbound")).Body!;
        Assert.Equal(1, resumo["total"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(key, HttpMethod.Get, "/v1/auditoria/resumo?status=FOO")).Status);

        var csv = await Api(key, HttpMethod.Get, $"/v1/auditoria/documentos/exportar?nivel=ocorrencia&chave={chaveEntrada}");
        Assert.Equal(HttpStatusCode.OK, csv.Status);
        Assert.Contains("-ocorrencias.csv", csv.Response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var lines = csv.Text.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("chave_acesso;direcao;status;criado_em;regra", lines[0], StringComparison.Ordinal);
        Assert.True(lines.Length > 1);
        Assert.All(lines.Skip(1), l => Assert.StartsWith($"{chaveEntrada};entrada;", l, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(";R040;", StringComparison.Ordinal));
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Get, "/v1/auditoria/documentos/exportar?nivel=item")).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(key, HttpMethod.Get, "/v1/auditoria/documentos/exportar?direcao=lateral")).Status);

        // DF-e recebidos por CNPJ: entradas validadas; sem captura, lista vazia.
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Get, "/v1/cnpjs/123/dfe/documentos")).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Get, "/v1/cnpjs/11444777000161/dfe/documentos?limite=0")).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Get, "/v1/cnpjs/11444777000161/dfe/documentos?status=PERDIDO")).Code);
        Assert.Empty((await Api(key, HttpMethod.Get, "/v1/cnpjs/11444777000161/dfe/documentos?status=auditado&limite=999")).Body!["documentos"]!.AsArray());

        // Com documentos capturados (o que o worker de DF-e grava): filtro por situação e limite.
        await using (var su = new Npgsql.NpgsqlConnection(env.SuperuserConnection))
        {
            await su.OpenAsync();
            await using var ins = new Npgsql.NpgsqlCommand("""
                INSERT INTO dfe_documents (tenant_id, cnpj, nsu, schema, kind, access_key, status, audit_status, detail) VALUES
                  ($1, '11444777000161', 1, 'resNFe_v1.01.xsd', 'RESUMO', NULL, 'CIENCIA_ENVIADA', NULL, NULL),
                  ($1, '11444777000161', 2, 'procNFe_v4.00.xsd', 'COMPLETO', $2, 'AUDITADO', 'CONFORME', 'auditado na entrada')
                """, su);
            ins.Parameters.AddWithValue(tenant);
            ins.Parameters.AddWithValue(chaveEntrada);
            await ins.ExecuteNonQueryAsync();
        }

        var auditados = (await Api(key, HttpMethod.Get, "/v1/cnpjs/11444777000161/dfe/documentos?status=auditado")).Body!["documentos"]!.AsArray();
        var auditado = Assert.Single(auditados)!;
        Assert.Equal(chaveEntrada, auditado["chave_acesso"]!.GetValue<string>());
        Assert.Equal("CONFORME", auditado["status_auditoria"]!.GetValue<string>());
        Assert.Equal(2, (await Api(key, HttpMethod.Get, "/v1/cnpjs/11444777000161/dfe/documentos")).Body!["documentos"]!.AsArray().Count);
        var one = Assert.Single((await Api(key, HttpMethod.Get, "/v1/cnpjs/11444777000161/dfe/documentos?limite=1&status=ciencia_enviada")).Body!["documentos"]!.AsArray())!;
        Assert.Null(one["chave_acesso"]);
        Assert.Null(one["status_auditoria"]);
    }

    /// <summary>Campo CSV: fórmula vira texto e separador/aspas/quebra de linha são escapados (injeção de fórmula no Excel).</summary>
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+55", "'+55")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("diz \"oi\"", "\"diz \"\"oi\"\"\"")]
    [InlineData("linha\nnova", "\"linha\nnova\"")]
    [InlineData("simples", "simples")]
    [InlineData("", "")]
    public void Campo_CSV_neutraliza_formula_e_escapa(string input, string expected) =>
        Assert.Equal(expected, TaxGateway.Api.Endpoints.DocumentEndpoints.Csv(input));

    // ------------------------------------------------------------------------------------------------ webhooks e lotes

    /// <summary>Webhooks e lotes: JSON e URL inválidos, recursos inexistentes e corpo acima do limite do contrato.</summary>
    [Fact]
    public async Task Webhooks_e_lotes_recusam_entrada_invalida_e_recurso_inexistente()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("Webhooks de borda", GoldenFixture.NewCnpj());
        Assert.Equal("ERR_JSON_INVALIDO", (await Api(key, HttpMethod.Post, "/v1/webhooks", content: Raw("{\"url\": "))).Code);
        Assert.Equal("ERR_JSON_INVALIDO", (await Api(key, HttpMethod.Post, "/v1/webhooks", new { url = "https://hooks.exemplo.test/x", eventos = "*" })).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Post, "/v1/webhooks", new { url = "nao e url", eventos = AnyEvent })).Code);
        Assert.Equal("ERR_VALIDATION", (await Api(key, HttpMethod.Post, "/v1/webhooks", new { url = "https://hooks.exemplo.test/x", eventos = NoEvents })).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Post, $"/v1/webhooks/{Guid.NewGuid()}/rotate-secret")).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Delete, $"/v1/webhooks/{Guid.NewGuid()}")).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Post, $"/v1/webhooks/deliveries/{Guid.NewGuid()}/retry")).Code);
        Assert.Empty((await Api(key, HttpMethod.Get, "/v1/webhooks")).Body!.AsArray());

        Assert.Equal("ERR_DIRECTION_INVALIDA", (await Api(key, HttpMethod.Post, "/v1/auditoria/lotes?direction=LATERAL", content: Raw("{}"), idem: "x")).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Get, $"/v1/auditoria/lotes/{Guid.NewGuid()}")).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Get, $"/v1/auditoria/lotes/{Guid.NewGuid()}/relatorio")).Code);

        // Limite de contrato de 200 bytes: o ZIP é recusado já na leitura, antes de reservar a Idempotency-Key.
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/limites", new { batch_max_bytes = 200, rpm = 6007 })).Status);
        await WaitAsync(async () => (await Api(key, HttpMethod.Get, "/v1/me")).Body?["limites"]?["rpm"]?.GetValue<int>() == 6007);
        using var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var s = z.CreateEntry("nota.xml", CompressionLevel.NoCompression).Open();
            await s.WriteAsync(NfeBuilder.Bytes(Nfe(760_010)));
        }

        var body = new ByteArrayContent(zip.ToArray());
        body.Headers.ContentType = new("application/zip");
        var idem = Guid.NewGuid().ToString("N");
        var tooBig = await Api(key, HttpMethod.Post, "/v1/auditoria/lotes", content: body, idem: idem);
        Assert.Equal("ERR_BATCH_TOO_LARGE", tooBig.Code);
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/limites", new { })).Status);   // volta ao plano
    }

    // ------------------------------------------------------------------------------------------------ empresa

    /// <summary>D-R10/D-R14: teto de excedente, verificação por certificado/e-mail/manual e disputa validam a entrada.</summary>
    [Fact]
    public async Task Empresa_teto_verificacao_e_disputa_validam_entrada()
    {
        var (owner, _, _, tenant) = await SignupAndLoginAsync("empresa-borda");
        Assert.Equal(HttpStatusCode.BadRequest, (await Browser(owner, HttpMethod.Put, "/v1/cobranca/teto", content: Raw("{\"teto_centavos\": "))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Browser(owner, HttpMethod.Put, "/v1/cobranca/teto", new { teto_centavos = -1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Browser(owner, HttpMethod.Put, "/v1/cobranca/teto", new { teto_centavos = 100_000_001 })).Status);
        var cap = await Browser(owner, HttpMethod.Put, "/v1/cobranca/teto", new { teto_centavos = 5000 });
        Assert.Equal(HttpStatusCode.OK, cap.Status);
        Assert.Equal(5000, cap.Body!["teto_centavos"]!.GetValue<long>());

        // Código por e-mail: confirmar sem ter pedido = expirado.
        Assert.Equal("ERR_CODIGO_EXPIRADO", (await Browser(owner, HttpMethod.Post, "/v1/verificacao-cnpj/email/confirmar", new { codigo = "123456" })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Browser(owner, HttpMethod.Post, "/v1/verificacao-cnpj/certificado", new { pfx_base64 = "", senha = "x" })).Status);
        Assert.Contains("base64", (await Browser(owner, HttpMethod.Post, "/v1/verificacao-cnpj/certificado", new { pfx_base64 = "@@@@", senha = "x" })).Body!["title"]!.GetValue<string>(), StringComparison.Ordinal);

        // A1 na tela de DF-e: fora da ICP-Brasil é guardado mas não prova a titularidade; o da ICP-Brasil verifica a empresa.
        var root = await RootAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await Browser(owner, HttpMethod.Put, $"/v1/cnpjs/{root}/certificado", new { pfx_base64 = Pfx(root, Untrusted.Value), senha = "senha-a1" })).Status);
        Assert.False((await Browser(owner, HttpMethod.Get, "/v1/verificacao-cnpj")).Body!["verificada"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await Browser(owner, HttpMethod.Put, $"/v1/cnpjs/{root}/certificado", new { pfx_base64 = Pfx(root), senha = "senha-a1" })).Status);
        var status = (await Browser(owner, HttpMethod.Get, "/v1/verificacao-cnpj")).Body!;
        Assert.True(status["verificada"]!.GetValue<bool>());
        Assert.Equal("CERTIFICADO", status["metodo"]!.GetValue<string>());

        // Backoffice: teto negativo, observação curta, verificação manual, disputa inexistente.
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/teto", new { teto_centavos = -5 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/teto", content: Raw("[1, 2]"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/verificar", new { observacao = "ok" })).Status);
        var manual = await Admin(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/verificar", new { observacao = "Contrato social conferido pelo comercial" });
        Assert.Equal("MANUAL", manual.Body!["metodo"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, $"/admin/v1/disputas/{Guid.NewGuid()}/resolver", new { acao = "apagar" })).Status);
        Assert.Equal("ERR_NOT_FOUND", (await Admin(HttpMethod.Post, $"/admin/v1/disputas/{Guid.NewGuid()}/resolver", new { acao = "rejeitar" })).Code);

        // Disputa: sem sessão, sem PFX, certificado fora da ICP-Brasil e CNPJ sem cadastro.
        Assert.Equal("ERR_UNAUTHENTICATED", (await Browser(env.NewBrowser(), HttpMethod.Post, "/v1/conta/disputa-cnpj", new { pfx_base64 = Pfx(root), senha = "senha-a1" })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Browser(owner, HttpMethod.Post, "/v1/conta/disputa-cnpj", new { pfx_base64 = "", senha = "x" })).Status);
        Assert.Equal("ERR_CERT_NAO_ICP_BRASIL", (await Browser(owner, HttpMethod.Post, "/v1/conta/disputa-cnpj", new { pfx_base64 = Pfx(root, Untrusted.Value), senha = "senha-a1" })).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Browser(owner, HttpMethod.Post, "/v1/conta/disputa-cnpj", new { pfx_base64 = Pfx(GoldenFixture.NewCnpj()), senha = "senha-a1" })).Code);
    }

    /// <summary>D-R10: CNPJ sem e-mail no cadastro público da Receita não recebe código (422 com alternativa).</summary>
    [Fact]
    public async Task Verificacao_por_email_sem_email_na_Receita_oferece_alternativa()
    {
        var root = $"99{Random.Shared.Next(0, 999_999):D6}";
        var (_, key) = await env.CreateTenantWithIdAsync("Sem e-mail na Receita", CnpjWithRoot(root));
        var r = await Api(key, HttpMethod.Post, "/v1/verificacao-cnpj/email");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Equal("ERR_SEM_EMAIL_NA_RECEITA", r.Code);
        Assert.Contains("CERTIFICADO", (await Api(key, HttpMethod.Get, "/v1/verificacao-cnpj")).Body!["metodos_disponiveis"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    // ------------------------------------------------------------------------------------------------ contas

    /// <summary>D-U01: entradas inválidas de cadastro, confirmação, MFA fora de ordem e links inválidos têm código estável.</summary>
    [Fact]
    public async Task Conta_recusa_entrada_invalida_e_MFA_fora_de_ordem()
    {
        var b = env.NewBrowser();
        Assert.Equal("ERR_JSON_INVALIDO", (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", content: Raw("{\"nome\": "))).Code);
        Assert.Equal("ERR_JSON_INVALIDO", (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", content: Raw("[]"))).Code);
        Assert.Equal("ERR_VALIDATION", (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", new { nome = "Sem e-mail" })).Code);
        var email = $"borda-{Guid.NewGuid():N}@exemplo.test";
        object Signup(string nome, string cnpj, bool termos) => new { nome, email, senha = Senha, empresa = new { nome = "Empresa Borda", cnpj }, aceite_termos = termos };
        Assert.Contains("Termos", (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", Signup("Pessoa Borda", GoldenFixture.NewCnpj(), false))).Body!["title"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Dados inválidos", (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", Signup("P", GoldenFixture.NewCnpj(), true))).Body!["title"]!.GetValue<string>());
        Assert.Equal("CNPJ inválido", (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", Signup("Pessoa Borda", "11111111111111", true))).Body!["title"]!.GetValue<string>());

        // Cadastro válido; reenvio da confirmação troca o link (o anterior deixa de valer).
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(b, HttpMethod.Post, "/v1/conta/cadastro", Signup("Pessoa Borda", GoldenFixture.NewCnpj(), true))).Status);
        var firstLink = env.Emails.LastLinkTo(email)!;
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(b, HttpMethod.Post, "/v1/conta/reenviar-confirmacao", new { email })).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(b, HttpMethod.Post, "/v1/conta/reenviar-confirmacao", new { email = "ninguem@exemplo.test" })).Status);
        var secondLink = env.Emails.LastLinkTo(email)!;
        Assert.NotEqual(firstLink, secondLink);
        Assert.Equal("ERR_LINK_INVALIDO", (await Browser(b, HttpMethod.Post, "/v1/conta/confirmar-email", new { token = Token(firstLink) })).Code);
        Assert.Equal(HttpStatusCode.OK, (await Browser(b, HttpMethod.Post, "/v1/conta/confirmar-email", new { token = Token(secondLink) })).Status);

        Assert.Equal("ERR_CREDENCIAIS", (await Browser(b, HttpMethod.Post, "/v1/conta/entrar", new { email = "ninguem@exemplo.test", senha = Senha })).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser(b, HttpMethod.Get, "/v1/conta/sessao")).Status);
        Assert.Equal("configurar", (await Browser(b, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());

        // Sessão sem MFA: rotas que exigem MFA recusam; MFA fora de ordem tem código próprio.
        Assert.Equal("ERR_MFA_NECESSARIO", (await Browser(b, HttpMethod.Get, "/v1/conta/sessoes")).Code);
        Assert.Equal("configurar", (await Browser(b, HttpMethod.Get, "/v1/conta/sessao")).Body!["mfa"]!.GetValue<string>());
        Assert.Equal("ERR_MFA_NAO_CONFIGURADO", (await Browser(b, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = "000000" })).Code);
        Assert.Equal("ERR_MFA_NAO_INICIADO", (await Browser(b, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = "000000" })).Code);
        var secret = await EnableMfaAsync(b, "/v1/conta");
        Assert.Equal("ERR_MFA_JA_ATIVO", (await Browser(b, HttpMethod.Post, "/v1/conta/mfa/iniciar")).Code);
        Assert.Equal("ERR_MFA_JA_ATIVO", (await Browser(b, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = Code(secret) })).Code);
        Assert.Equal("ERR_VALIDATION", (await Browser(b, HttpMethod.Post, "/v1/conta/empresa-ativa", new { tenant_id = "nao-e-guid" })).Code);

        // D-R03: conta bloqueada (senha errada em outro navegador) recusa também o segundo fator de uma entrada já iniciada.
        var pendingMfa = env.NewBrowser();
        Assert.Equal("verificar", (await Browser(pendingMfa, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        var attacker = env.NewBrowser();
        for (var i = 0; i < 5; i++)
        {
            await Browser(attacker, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = "senha errada de verdade" });
        }

        var locked = await Browser(pendingMfa, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = Code(secret, 1) });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.Status);
        Assert.Equal("ERR_CONTA_BLOQUEADA", locked.Code);

        // Links e convites inválidos.
        Assert.Equal("ERR_LINK_INVALIDO", (await Browser(b, HttpMethod.Post, "/v1/conta/senha/redefinir", new { token = "invalido", senha = Senha })).Code);
        Assert.Equal("ERR_LINK_INVALIDO", (await Browser(b, HttpMethod.Get, "/v1/conta/convite?token=invalido")).Code);
        Assert.Equal("ERR_LINK_INVALIDO", (await Browser(b, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = "invalido", nome = "X Y", senha = Senha })).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(b, HttpMethod.Post, "/v1/conta/sair")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(b, HttpMethod.Post, "/v1/conta/sair")).Status);   // sem sessão: idempotente
    }

    /// <summary>D-U03/D-R14: convites (cancelar, aceitar com conta existente), troca de papel e remoção respeitam o último dono.</summary>
    [Fact]
    public async Task Convites_papeis_e_remocao_respeitam_permissoes_e_o_ultimo_dono()
    {
        var (owner, ownerEmail, _, tenant) = await SignupAndLoginAsync("dono-borda");
        await UsePlanWithUsersAsync(tenant, 10);
        Assert.Equal("ERR_VALIDATION", (await Browser(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = "x@exemplo.test", papel = "chefe" })).Code);
        Assert.Equal("ERR_CONFLICT", (await Browser(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = ownerEmail, papel = "viewer" })).Code);

        var adminEmail = $"admin-borda-{Guid.NewGuid():N}@exemplo.test";
        var viewerEmail = $"leitura-borda-{Guid.NewGuid():N}@exemplo.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = adminEmail, papel = "admin" })).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = viewerEmail, papel = "viewer" })).Status);
        foreach (var (papel, rotulo) in new[] { ("developer", "Desenvolvedor"), ("finance", "Financeiro"), ("fiscal", "Fiscal") })
        {
            var convidado = $"{papel}-borda-{Guid.NewGuid():N}@exemplo.test";
            Assert.Equal(HttpStatusCode.Accepted, (await Browser(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = convidado, papel })).Status);
            Assert.Contains(env.Emails.Sent, m => m.To == convidado && m.Text.Contains(rotulo, StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.NoContent, (await Browser(owner, HttpMethod.Delete, $"/v1/conta/membros/convites?email={Uri.EscapeDataString(convidado)}")).Status);
        }

        var pending = (await Browser(owner, HttpMethod.Get, "/v1/conta/membros")).Body!["convites"]!.AsArray();
        Assert.Equal(2, pending.Count);

        // Convite cancelado deixa de valer.
        var viewerToken = Token(env.Emails.LastLinkTo(viewerEmail)!);
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(owner, HttpMethod.Delete, $"/v1/conta/membros/convites?email={Uri.EscapeDataString(viewerEmail)}")).Status);
        Assert.Equal("ERR_LINK_INVALIDO", (await Browser(env.NewBrowser(), HttpMethod.Get, $"/v1/conta/convite?token={Uri.EscapeDataString(viewerToken)}")).Code);
        Assert.Single((await Browser(owner, HttpMethod.Get, "/v1/conta/membros")).Body!["convites"]!.AsArray());

        // Aceite de quem não tem conta: nome e senha validados.
        var adminToken = Token(env.Emails.LastLinkTo(adminEmail)!);
        var guest = env.NewBrowser();
        Assert.Equal("ERR_VALIDATION", (await Browser(guest, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = adminToken, nome = "A", senha = Senha })).Code);
        Assert.Equal("ERR_SENHA_FRACA", (await Browser(guest, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = adminToken, nome = "Admin Borda", senha = "curta" })).Code);
        var accepted = await Browser(guest, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = adminToken, nome = "Admin Borda", senha = Senha });
        Assert.Equal("entrar", accepted.Body!["proximo"]!.GetValue<string>());
        Assert.Equal("configurar", (await Browser(guest, HttpMethod.Post, "/v1/conta/entrar", new { email = adminEmail, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        await EnableMfaAsync(guest, "/v1/conta");

        var members = (await Browser(owner, HttpMethod.Get, "/v1/conta/membros")).Body!["membros"]!.AsArray();
        var ownerId = members.Single(m => m!["papel"]!.GetValue<string>() == "owner")!["user_id"]!.GetValue<Guid>();
        var adminId = members.Single(m => m!["papel"]!.GetValue<string>() == "admin")!["user_id"]!.GetValue<Guid>();

        // Administrador: não promove a dono, não rebaixa o único dono; papel inválido e membro inexistente.
        Assert.Equal("ERR_FORBIDDEN_SCOPE", (await Browser(guest, HttpMethod.Patch, $"/v1/conta/membros/{adminId}", new { papel = "owner" })).Code);
        // D-R29: administrador não rebaixa um dono; o próprio dono não pode deixar a empresa sem dono.
        Assert.Equal("ERR_FORBIDDEN_SCOPE", (await Browser(guest, HttpMethod.Patch, $"/v1/conta/membros/{ownerId}", new { papel = "viewer" })).Code);
        Assert.Equal("ERR_FORBIDDEN_SCOPE", (await Browser(guest, HttpMethod.Delete, $"/v1/conta/membros/{ownerId}")).Code);
        Assert.Equal("ERR_ULTIMO_DONO", (await Browser(owner, HttpMethod.Patch, $"/v1/conta/membros/{ownerId}", new { papel = "viewer" })).Code);
        Assert.Equal("ERR_VALIDATION", (await Browser(guest, HttpMethod.Patch, $"/v1/conta/membros/{ownerId}", new { papel = "chefe" })).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Browser(guest, HttpMethod.Patch, $"/v1/conta/membros/{Guid.NewGuid()}", new { papel = "viewer" })).Code);
        Assert.Equal("ERR_NOT_FOUND", (await Browser(guest, HttpMethod.Delete, $"/v1/conta/membros/{Guid.NewGuid()}")).Code);

        // Dono transfere a posição; com dois donos, rebaixar um deles é permitido.
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(owner, HttpMethod.Patch, $"/v1/conta/membros/{adminId}", new { papel = "owner" })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(guest, HttpMethod.Patch, $"/v1/conta/membros/{ownerId}", new { papel = "fiscal" })).Status);
        Assert.Equal("fiscal", (await Browser(owner, HttpMethod.Get, "/v1/conta/sessao")).Body!["papel"]!.GetValue<string>());
        Assert.Equal("ERR_FORBIDDEN_SCOPE", (await Browser(owner, HttpMethod.Get, "/v1/conta/membros")).Code);   // fiscal não gerencia acessos

        // Quem já tem conta aceita logado com o mesmo e-mail; sem login, o aceite pede a entrada.
        var (other, otherEmail, _, otherTenant) = await SignupAndLoginAsync("convidado-com-conta");
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(guest, HttpMethod.Post, "/v1/conta/membros/convites", new { email = otherEmail, papel = "viewer" })).Status);
        var otherToken = Token(env.Emails.LastLinkTo(otherEmail)!);
        Assert.True((await Browser(env.NewBrowser(), HttpMethod.Get, $"/v1/conta/convite?token={Uri.EscapeDataString(otherToken)}")).Body!["tem_conta"]!.GetValue<bool>());
        Assert.Equal("ERR_LOGIN_NECESSARIO", (await Browser(env.NewBrowser(), HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = otherToken })).Code);
        var joined = await Browser(other, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = otherToken });
        Assert.Equal("portal", joined.Body!["proximo"]!.GetValue<string>());
        var otherSession = (await Browser(other, HttpMethod.Get, "/v1/conta/sessao")).Body!;
        Assert.Equal(tenant, otherSession["empresa_ativa"]!.GetValue<Guid>());
        Assert.Equal("viewer", otherSession["papel"]!.GetValue<string>());
        Assert.Equal(2, otherSession["empresas"]!.AsArray().Count);

        // Troca a empresa ativa de volta para a própria.
        var back = await Browser(other, HttpMethod.Post, "/v1/conta/empresa-ativa", new { tenant_id = otherTenant });
        Assert.Equal("owner", back.Body!["papel"]!.GetValue<string>());

        // Remoção: o novo dono remove a pessoa convidada; ela perde a empresa.
        var otherId = (await Browser(guest, HttpMethod.Get, "/v1/conta/membros")).Body!["membros"]!.AsArray()
            .Single(m => m!["email"]!.GetValue<string>() == otherEmail)!["user_id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await Browser(guest, HttpMethod.Delete, $"/v1/conta/membros/{otherId}")).Status);
        Assert.Single((await Browser(other, HttpMethod.Get, "/v1/conta/sessao")).Body!["empresas"]!.AsArray());
        Assert.Equal("ERR_NOT_FOUND", (await Browser(other, HttpMethod.Post, "/v1/conta/empresa-ativa", new { tenant_id = tenant })).Code);
    }

    // ------------------------------------------------------------------------------------------------ erros mapeados

    /// <summary>Erro do banco (limite de CNPJs do plano, P0001) vira problem+json com código pela rota real.</summary>
    [Fact]
    public async Task Erros_mapeados_pelas_rotas_reais_tem_codigo_estavel()
    {
        // Plano sem CNPJ extra (OEM criado com 1 incluído) → ERR_LIMITE_CNPJS vindo do banco (P0001).
        var code = $"TEDGC{Random.Shared.Next(10000, 99999)}";
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Put, $"/admin/v1/planos/{code}", new
        {
            name = "Borda 1 CNPJ",
            price_cents = 0,
            included_docs = 10,
            included_simulations = 10,
            hard_quota = true,
            included_cnpjs = 1,
            cnpj_price_cents = 0,
            rpm = 600,
            burst = 100,
            concurrency = 10,
            is_public = false,
            self_service = false,
            active = true,
        })).Status);
        var (tenant, key) = await env.CreateTenantWithIdAsync("Borda limite CNPJ", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = code })).Status);
        await WaitAsync(async () => (await Api(key, HttpMethod.Get, "/v1/me")).Body?["plano"]?.GetValue<string>() == code);
        var limit = await Api(key, HttpMethod.Post, "/v1/oem/cnpjs", new { cnpj = GoldenFixture.NewCnpj() });
        Assert.Equal(HttpStatusCode.PaymentRequired, limit.Status);
        Assert.Equal("ERR_LIMITE_CNPJS", limit.Code);
        Assert.Equal("application/problem+json", limit.Response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("1 CNPJ(s)", limit.Body!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>Datas da consulta pública de alíquotas fora da faixa e cobertura de parâmetros pelo backoffice.</summary>
    [Fact]
    public async Task Consulta_publica_de_aliquotas_recusa_ano_fora_da_faixa()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(env.Api, HttpMethod.Get, "/v1/publico/aliquotas?municipio=3550308&data=2025-06-01", csrf: false)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(env.Api, HttpMethod.Get, "/v1/publico/aliquotas?municipio=12", csrf: false)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Get, "/admin/v1/params/cobertura")).Status);
    }

    /// <summary>D-J05/D-R06: a validação da simulação junta todos os campos inválidos num único 400 com a lista de erros.</summary>
    [Fact]
    public async Task Simulacao_invalida_lista_todos_os_campos_com_problema()
    {
        var bad = new JsonObject
        {
            ["cnpj_emitente"] = "123",
            ["regime_tributario"] = "SIMPLES_NACIONAL",
            ["crt"] = "3",
            ["tp_regime_ibs_cbs"] = "9",
            ["data_fato_gerador"] = "2026-01-15T10:00:00-03:00",
            ["municipio_ibge"] = 12,
            ["uf"] = "sp",
            ["compra_governamental"] = new JsonObject { ["tipo_operacao"] = 1 },
            ["itens"] = new JsonArray(
                new JsonObject
                {
                    ["numero"] = 1,
                    ["cst_ibs_cbs"] = "0",
                    ["c_class_trib"] = "1",
                    ["ncm"] = "8471",
                    ["nbs"] = "1",
                    ["valor_base_calculo"] = -1,
                    ["quantidade"] = 0,
                    ["imposto_seletivo"] = new JsonObject { ["cst"] = "000" },
                    ["aliquotas_nominais"] = new JsonObject { ["cbs"] = 150 },
                },
                new JsonObject { ["numero"] = 1, ["cst_ibs_cbs"] = "000", ["c_class_trib"] = "000001", ["valor_base_calculo"] = 10 },
                null),
        };
        var r = await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", bad);
        Assert.Equal("ERR_VALIDATION", r.Code);
        var campos = r.Body!["errors"]!.AsArray().Select(e => e!["campo"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var campo in ExpectedSimulationErrors)
        {
            Assert.Contains(campo, campos);
        }

        var ano = await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", new JsonObject
        {
            ["cnpj_emitente"] = "11222333000181",
            ["regime_tributario"] = "OUTRO",
            ["data_fato_gerador"] = "2101-01-01T10:00:00-03:00",
            ["municipio_ibge"] = 3550308,
            ["uf"] = "SP",
            ["itens"] = new JsonArray(),
        });
        var anoCampos = ano.Body!["errors"]!.AsArray().Select(e => e!["codigo"]!.GetValue<string>() + ":" + e["campo"]!.GetValue<string>()).ToList();
        Assert.Contains("FORA_DO_INTERVALO:data_fato_gerador", anoCampos);
        Assert.Contains("INVALIDO:regime_tributario", anoCampos);
        Assert.Contains("OBRIGATORIO:itens", anoCampos);

        // Simples sem cAtivSN e sem as alíquotas do DAS; município de outra UF; itens demais.
        var simples = await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", new JsonObject
        {
            ["cnpj_emitente"] = "11222333000181",
            ["regime_tributario"] = "SIMPLES_NACIONAL",
            ["data_fato_gerador"] = "2027-06-15T14:00:00-03:00",
            ["municipio_ibge"] = 3550308,
            ["uf"] = "SP",
            ["itens"] = new JsonArray(new JsonObject { ["numero"] = 1, ["cst_ibs_cbs"] = "000", ["c_class_trib"] = "000001", ["valor_base_calculo"] = 10 }, null),
        });
        var simplesCampos = simples.Body!["errors"]!.AsArray().Select(e => e!["campo"]!.GetValue<string>()).ToList();
        Assert.Contains("itens[0].codigo_atividade_sn", simplesCampos);
        Assert.Contains("itens[0].aliquotas_simples", simplesCampos);

        var outraUf = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Sim("11222333000181")))!.AsObject();
        outraUf["uf"] = "RJ";
        Assert.Equal("ERR_MUNICIPIO_INVALIDO", (await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", outraUf)).Code);
        var muitos = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Sim("11222333000181")))!.AsObject();
        var item = muitos["itens"]![0]!;
        muitos["itens"] = new JsonArray([.. Enumerable.Range(1, 501).Select(n =>
        {
            var c = item.DeepClone();
            c["numero"] = n;
            return c;
        })]);
        Assert.Equal("ERR_TOO_MANY_ITEMS", (await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", muitos)).Code);
    }

    /// <summary>D-R20: exceções de alíquota são de pessoas (API key = 403), validadas campo a campo e decididas por duas pessoas.</summary>
    [Fact]
    public async Task Excecoes_de_aliquota_exigem_pessoa_e_validam_cada_campo()
    {
        var (owner, _, _, _) = await SignupAndLoginAsync("excecao-borda");
        var key = (await Browser(owner, HttpMethod.Post, "/v1/api-keys", new { nome = "erp", scopes = CnpjsAndRead })).Body!["api_key"]!.GetValue<string>();
        Assert.Equal("ERR_SESSAO_NECESSARIA", (await Api(key, HttpMethod.Post, "/v1/excecoes-aliquota", new { tributo = "CBS" })).Code);
        Assert.Equal("ERR_SESSAO_NECESSARIA", (await Api(key, HttpMethod.Post, $"/v1/excecoes-aliquota/{Guid.NewGuid()}/aprovar")).Code);
        Assert.Empty((await Api(key, HttpMethod.Get, "/v1/excecoes-aliquota")).Body!["excecoes"]!.AsArray());
        Assert.Equal("ERR_NOT_FOUND", (await Api(key, HttpMethod.Get, $"/v1/excecoes-aliquota/{Guid.NewGuid()}/documento")).Code);

        var invalid = await Browser(owner, HttpMethod.Post, "/v1/excecoes-aliquota", new
        {
            tributo = "ICMS",
            aliquota = 101,
            vigencia_inicio = "2027-02-01",
            vigencia_fim = "2027-01-01",
            motivo = "PORQUE",
            processo = "x",
            cnpj = "123",
            ente_codigo = "35",
            produto = "SKU:1",
            documento_base64 = "@@@",
        });
        var detail = invalid.Body!["detail"]!.GetValue<string>();
        foreach (var part in ExpectedExceptionErrors)
        {
            Assert.Contains(part, detail, StringComparison.Ordinal);
        }

        var pdf = Convert.ToBase64String("%PDF-1.4\n% regime especial de teste\n"u8.ToArray());
        var root = await RootAsync(owner);
        var proposed = await Browser(owner, HttpMethod.Post, "/v1/excecoes-aliquota", new
        {
            tributo = "IBS_UF",
            ente_codigo = "35",
            aliquota = 10,
            vigencia_inicio = "2027-01-01",
            vigencia_fim = "2027-12-31",
            motivo = "REGIME_ESPECIAL",
            processo = "Termo 123/2027",
            cnpj = root,
            documento_nome = "../../termo.pdf",
            documento_base64 = pdf,
        });
        Assert.Equal(HttpStatusCode.Created, proposed.Status);
        var id = proposed.Body!["id"]!.GetValue<Guid>();
        Assert.Equal("ERR_NOT_FOUND", (await Browser(owner, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/apagar")).Code);
        Assert.Equal("ERR_CONFLICT", (await Browser(owner, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/revogar")).Code);   // só ativas
        Assert.Equal("ERR_CONFLICT", (await Browser(owner, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/recusar")).Code);   // duas pessoas
        var listed = (await Browser(owner, HttpMethod.Get, "/v1/excecoes-aliquota")).Body!["excecoes"]![0]!;
        Assert.Equal("termo.pdf", listed["documento"]!.GetValue<string>());   // sem caminho
        Assert.Equal("PROPOSTA", listed["situacao"]!.GetValue<string>());
        // Duas pessoas: uma fiscal aprova; a exceção ativa aparece ao lado do cálculo oficial (simulação) e como aviso na auditoria.
        var fiscalEmail = $"fiscal-borda-{Guid.NewGuid():N}@exemplo.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Browser(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = fiscalEmail, papel = "fiscal" })).Status);
        var fiscal = env.NewBrowser();
        Assert.Equal(HttpStatusCode.OK, (await Browser(fiscal, HttpMethod.Post, "/v1/conta/convite/aceitar",
            new { token = Token(env.Emails.LastLinkTo(fiscalEmail)!), nome = "Fiscal de Borda", senha = Senha })).Status);
        await Browser(fiscal, HttpMethod.Post, "/v1/conta/entrar", new { email = fiscalEmail, senha = Senha });
        await EnableMfaAsync(fiscal, "/v1/conta");
        Assert.Equal("ATIVA", (await Browser(fiscal, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/aprovar")).Body!["situacao"]!.GetValue<string>());
        var cbs = await Browser(owner, HttpMethod.Post, "/v1/excecoes-aliquota", new
        {
            tributo = "CBS",
            aliquota = 11.2,
            vigencia_inicio = "2027-01-01",
            motivo = "LIMINAR",
            processo = "5000999-11.2027.4.01.0001",
            cnpj = root,
            documento_base64 = pdf,
        });
        Assert.Equal("ATIVA", (await Browser(fiscal, HttpMethod.Post, $"/v1/excecoes-aliquota/{cbs.Body!["id"]!.GetValue<Guid>()}/aprovar")).Body!["situacao"]!.GetValue<string>());

        var simulated = (await Browser(owner, HttpMethod.Post, "/v1/calculo/simular", Sim(root))).Body!;
        var ajustada = Assert.Single(simulated["cenario_ajustado"]!["excecoes"]!.AsArray(), e => e!["id"]!.GetValue<Guid>() == id)!;
        Assert.Equal(10m, ajustada["aliquota"]!.GetValue<decimal>());

        // Auditoria de NF-e do próprio CNPJ (sem assinatura, modo opcional): a exceção de CBS confere com o declarado.
        var auditKey = (await Browser(owner, HttpMethod.Post, "/v1/api-keys", new { nome = "auditoria", scopes = AuditOnly })).Body!["api_key"]!.GetValue<string>();
        var doc = Mutated(760_200, d =>
        {
            SetText(d, "//n:emit/n:CNPJ", root);
            foreach (var sig in d.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Cast<XmlNode>().ToList())
            {
                sig.ParentNode!.RemoveChild(sig);
            }
        }, sign: false);
        var audited = await AuditXmlAsync(auditKey, doc, "?assinatura=opcional");
        Assert.True(audited.Status == HttpStatusCode.OK, audited.Text);
        var aviso = audited.Body!["avisos"]!.AsArray().Single(a => a!["codigo"]!.GetValue<string>() == "EXCECAO_DO_CONTRIBUINTE")!;
        Assert.Contains("confere com a exceção", aviso["mensagem"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ auditoria de XML

    private static void SetText(XmlDocument doc, string xpath, string value)
    {
        foreach (XmlNode n in doc.SelectNodes(xpath, GoldenXmlTests.Ns(doc))!)
        {
            n.InnerText = value;
        }
    }

    private static void ReplaceWithCpf(XmlDocument doc, string parent)
    {
        var ns = GoldenXmlTests.Ns(doc);
        var cnpj = doc.SelectSingleNode($"//n:{parent}/n:CNPJ", ns)!;
        var cpf = doc.CreateElement("CPF", NfeBuilder.Ns);
        cpf.InnerText = "12345678909";
        cnpj.ParentNode!.ReplaceChild(cpf, cnpj);
    }

    private XmlDocument Mutated(int numero, Action<XmlDocument> change, string file = "A-000001-RO.xml", bool sign = true)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, file));
        GoldenXmlTests.Rekey(doc, numero);
        change(doc);
        if (sign)
        {
            NfeBuilder.Sign(doc, env.Pki.Leaf);
        }

        return doc;
    }

    private Task<Reply> AuditXmlAsync(string key, XmlDocument doc, string query = "") =>
        Api(key, HttpMethod.Post, $"/v1/auditoria/xml{query}", content: XmlContent(doc), idem: Guid.NewGuid().ToString("N"));

    /// <summary>SDD §6.2: XML com chave, emitente ou destinatário fora do suportado é recusado com código; município inexistente e regime sem rota viram achados.</summary>
    [Fact]
    public async Task Auditoria_de_XML_recusa_documento_fora_do_suportado()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Auditoria de borda", GoldenFixture.NewCnpj(), autoProvision: true);
        static List<string> Rules(Reply r) => [.. r.Body!["ocorrencias"]!.AsArray().Select(o => o!["regra"]!.GetValue<string>())];

        Assert.Equal("ERR_DIRECTION_INVALIDA", (await AuditXmlAsync(key, Nfe(760_100), "?direction=LATERAL")).Code);
        Assert.Equal("ERR_CHAVE_INVALIDA", (await AuditXmlAsync(key, Mutated(760_101,
            d => ((XmlElement)d.SelectSingleNode("//n:infNFe", GoldenXmlTests.Ns(d))!).SetAttribute("Id", "NFe12345")))).Code);
        Assert.Equal("ERR_CHAVE_INVALIDA", (await AuditXmlAsync(key, Mutated(760_102, d => SetText(d, "//n:ide/n:mod", "57")))).Code);   // CT-e não é NF-e
        var emitCpf = await AuditXmlAsync(key, Mutated(760_103, d => ReplaceWithCpf(d, "emit")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, emitCpf.Status);
        Assert.Equal("ERR_EMITENTE_NAO_SUPORTADO", emitCpf.Code);
        Assert.Equal("ERR_DESTINATARIO_NAO_SUPORTADO", (await AuditXmlAsync(key, Mutated(760_104, d => ReplaceWithCpf(d, "dest")), "?direction=INBOUND")).Code);

        // Município inexistente no cadastro do bundle: achado R029 sem chamar o motor.
        var municipio = await AuditXmlAsync(key, Mutated(760_105, d => SetText(d, "//n:ide/n:cMunFG", "1100000")));
        Assert.Equal("DIVERGENTE", municipio.Body!["status"]!.GetValue<string>());
        Assert.Equal(["R029"], Rules(municipio));

        // Emissão sem alíquota no catálogo (2031): indeterminado (I006), não um cálculo com alíquota inventada.
        var semAliquota = await AuditXmlAsync(key, Mutated(760_111, d => SetText(d, "//n:ide/n:dhEmi", "2031-11-10T11:00:00-04:00")));
        Assert.Equal("INDETERMINADO", semAliquota.Body!["status"]!.GetValue<string>());
        Assert.Equal(["I006"], Rules(semAliquota));

        // Emitente do Simples (CRT 1) com grupo do regime regular: o motor do Simples recusa e vira achado R029.
        var simples = await AuditXmlAsync(key, Mutated(760_107, d => SetText(d, "//n:emit/n:CRT", "1")));
        Assert.Equal("DIVERGENTE", simples.Body!["status"]!.GetValue<string>());
        Assert.Contains("R029", Rules(simples));

        // CNPJ não cadastrado (tenant sem autoprovisionamento): o detalhe traz o CNPJ para o portal oferecer o cadastro.
        var (_, closed) = await env.CreateTenantWithIdAsync("Auditoria fechada", GoldenFixture.NewCnpj(), autoProvision: false);
        var naoCadastrado = await AuditXmlAsync(closed, Nfe(760_112));
        Assert.Equal("ERR_CNPJ_NOT_PROVISIONED", naoCadastrado.Code);
        Assert.Contains("11222333000181", naoCadastrado.Body!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>D-R17: o IS declarado é conferido com o catálogo do produto (R014); sem linha no catálogo, a auditoria avisa que não conferiu.</summary>
    [Fact]
    public async Task Imposto_Seletivo_declarado_e_conferido_com_o_catalogo()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("IS de borda", GoldenFixture.NewCnpj());
        static void AddIs(XmlDocument d, string ncm, string pIs)
        {
            var ns = GoldenXmlTests.Ns(d);
            d.SelectSingleNode("//n:det/n:prod/n:NCM", ns)!.InnerText = ncm;
            var ibscbs = d.SelectSingleNode("//n:det/n:imposto/n:IBSCBS", ns)!;
            var isg = d.CreateElement("IS", NfeBuilder.Ns);
            foreach (var (name, value) in new[] { ("CSTIS", "000"), ("cClassTribIS", "000001"), ("vBCIS", "1000.00"), ("pIS", pIs), ("vIS", "100.00") })
            {
                var e = d.CreateElement(name, NfeBuilder.Ns);
                e.InnerText = value;
                isg.AppendChild(e);
            }

            ibscbs.ParentNode!.InsertBefore(isg, ibscbs);
        }

        var semCatalogo = await AuditXmlAsync(key, Mutated(760_300, d => AddIs(d, "84713012", "10.0000")));
        Assert.True(semCatalogo.Status == HttpStatusCode.OK, semCatalogo.Text);
        Assert.Contains(semCatalogo.Body!["avisos"]!.AsArray(), a => a!["codigo"]!.GetValue<string>() == "IS_NAO_CONFERIDO");

        // O IS declarado entra no total recalculado e a ausência do ISTot é apontada (R030).
        Assert.Contains(semCatalogo.Body["ocorrencias"]!.AsArray(), o => o!["regra"]!.GetValue<string>() == "R030" && o["campo_xml"]!.GetValue<string>() == "total/ISTot/vIS");

        // Alíquota de IS publicada para o produto (duas pessoas no backoffice): o declarado diferente vira R014.
        await using var other = env.NewApiInstance(new Dictionary<string, string?>
        {
            ["Admin:Operators:1:Name"] = "golden-b",
            ["Admin:Operators:1:KeyHash"] = GoldenFixture.OperatorHash(SecondOperatorKey),
            ["Admin:Operators:1:Scopes:0"] = "admin:fiscal",
        });
        using var otherHttp = other.CreateClient();
        var proposed = await Admin(HttpMethod.Post, "/admin/v1/params/rates/changes", new JsonObject
        {
            ["justificativa"] = "IS de teste para um produto sem uso no corpus",
            ["linhas"] = new JsonArray(Line("IS", "REFERENCIA", "UNIAO", 5m, "2027-01-01", to: "2027-12-31", produto: "NCM:87032100")),
        });
        Assert.Equal(HttpStatusCode.Created, proposed.Status);
        var approved = await SendAsync(otherHttp, HttpMethod.Post, $"/admin/v1/params/changes/{proposed.Body!["change_id"]!.GetValue<string>()}/approve",
            adminKey: SecondOperatorKey, csrf: false);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Text);

        Reply? conferido = null;
        for (var i = 0; i < 40; i++)
        {
            conferido = await AuditXmlAsync(key, Mutated(760_310 + i, d => AddIs(d, "87032100", "9.0000")));
            if (!conferido.Body!["avisos"]!.AsArray().Any(a => a!["codigo"]!.GetValue<string>() == "IS_NAO_CONFERIDO"))
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal("DIVERGENTE", conferido!.Body!["status"]!.GetValue<string>());
        var r014 = conferido.Body["ocorrencias"]!.AsArray().Single(o => o!["regra"]!.GetValue<string>() == "R014")!;
        Assert.Equal("9.0000", r014["valor_declarado"]!.GetValue<string>());
        Assert.Equal("5.0000", r014["valor_referencia"]!.GetValue<string>());
        Assert.Contains(conferido.Body["aliquotas_aplicadas"]!.AsArray(), a => a!["tributo"]!.GetValue<string>() == "IS" && a["produto"]!.GetValue<string>() == "NCM:87032100");
    }

    /// <summary>D-310: auditorias simultâneas da mesma NF-e gravam uma vez; as outras devolvem a gravada (replay, sem cobrança).</summary>
    [Fact]
    public async Task Auditorias_simultaneas_da_mesma_nota_gravam_uma_vez()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Corrida de borda", GoldenFixture.NewCnpj());
        var doc = Nfe(760_400);
        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AuditXmlAsync(key, doc)));
        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Single(replies.Select(r => r.Body!["audit_id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal));
        Assert.Equal(5, replies.Count(r => r.Body!["replay"]?.GetValue<bool>() == true));
        var listed = (await Api(key, HttpMethod.Get, $"/v1/auditoria/documentos?chave={GoldenXmlTests.Chave(doc)}")).Body!["documentos"]!.AsArray();
        Assert.Single(listed);
    }

    /// <summary>Simulação sem alíquota no catálogo para a data pede as alíquotas nominais; data impossível é inválida; regime sem rota é 422.</summary>
    [Fact]
    public async Task Simulacao_sem_aliquota_no_catalogo_pede_aliquotas_nominais()
    {
        var sim = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Sim("11222333000181")))!.AsObject();
        sim["data_fato_gerador"] = "2031-06-15T14:00:00-03:00";
        var r = await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", sim);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Equal("ERR_ALIQUOTA_INDEFINIDA", r.Code);
        Assert.Contains(r.Body!["errors"]!.AsArray(), e => e!["campo"]!.GetValue<string>() == "itens[0].aliquotas_nominais.cbs");

        sim["data_fato_gerador"] = "2027-02-30T10:00:00-03:00";
        var invalid = await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", sim);
        Assert.Contains(invalid.Body!["errors"]!.AsArray(), e => e!["campo"]!.GetValue<string>() == "data_fato_gerador" && e["codigo"]!.GetValue<string>() == "INVALIDO");

        // Regime regular com tpRegimeIBSCBS 2: sem rota na tabela engine_routing.
        var semRota = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Sim("11222333000181")))!.AsObject();
        semRota["tp_regime_ibs_cbs"] = "2";
        Assert.Equal("ERR_REGIME_INDETERMINADO", (await Api(env.ApiKey, HttpMethod.Post, "/v1/calculo/simular", semRota)).Code);
    }
}
