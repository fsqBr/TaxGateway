using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Billing;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Fluxo de cobrança pela Stripe ponta a ponta contra um simulador local da API da Stripe (<see cref="StripeSimulator"/>):
/// catálogo (produto, preços e medidores), contratação e troca de plano, portal, consumo medido, fechamento mensal,
/// reconciliação, ajustes e os laços dos workers. A API com a Stripe ligada é uma segunda instância (a principal segue
/// com a Stripe desligada, como os outros testes esperam).
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class StripeFlowTests(GoldenFixture env)
{
    private const string Senha = "Uma frase longa e boa 2027!";
    private static readonly string[] AllKinds = ["cnpjs", "documentos", "fixed", "simulacoes"];
    private static readonly string[] FixedAndDocs = ["documentos", "fixed"];
    private static readonly int[] NotAnObject = [1];
    private static readonly string[] ValidFeatures = ["Suporte", "API"];
    private static readonly string[] MeterSetup = ["taxgateway_documentos:sum", "taxgateway_cnpjs:last", "taxgateway_simulacoes:sum"];

    // ------------------------------------------------------------------------------------------------ apoio

    private static string Code(string prefix) => $"{prefix}{Random.Shared.Next(100_000, 999_999)}";

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> AdminAsync(HttpClient api, HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var resp = await api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> AdminAsync(HttpMethod m, string path, object? body = null) => AdminAsync(env.Api, m, path, body);

    private async Task SavePlanAsync(string code, object body)
    {
        var r = await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{code}", body);
        Assert.True(r.Status == HttpStatusCode.OK, r.Body?.ToJsonString());
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpClient c, HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-TaxGateway-Csrf", "1");
        using var resp = await c.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task ExecAsync(string sql, params object?[] args)
    {
        await using var conn = new NpgsqlConnection(env.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params object?[] args)
    {
        await using var conn = new NpgsqlConnection(env.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        }

        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default! : (T)v;
    }

    private Task AddLedgerAsync(Guid tenant, DateOnly month, string metric, int n) =>
        ExecAsync("""
            INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id)
            SELECT $1, t.id, $3, t.root_cnpj, gen_random_uuid() FROM tenants t, generate_series(1, $4) WHERE t.id = $2
            """, month, tenant, metric, n);

    private async Task AddCnpjsAsync(Guid tenant, DateOnly month, int n)
    {
        for (var i = 0; i < n; i++)
        {
            await ExecAsync("INSERT INTO cnpj_monthly_activity (tenant_id, billing_month, cnpj) VALUES ($1, $2, $3)", tenant, month, GoldenFixture.NewCnpj());
        }
    }

    private Task SetBillingAsync(Guid tenant, string? customer, string? subscription = null, string? status = null) =>
        ExecAsync("UPDATE tenants SET billing_customer_ref = $2, stripe_subscription_id = $3, subscription_status = $4 WHERE id = $1", tenant, customer, subscription, status);

    private NpgsqlDataSource BillingDb() => NpgsqlDataSource.Create(env.ConnectionFor("tg_billing"));

    private static IOptions<StripeOptions> Options(StripeSimulator sim, string apiVersion = "2025-03-31.basil") =>
        Microsoft.Extensions.Options.Options.Create(new StripeOptions { SecretKey = StripeSimulator.SecretKey, ApiBase = sim.BaseUri, ApiVersion = apiVersion });

    private static StripeClient Client(StripeSimulator sim, string apiVersion = "2025-03-31.basil") => new(new HttpClient(), Options(sim, apiVersion));

    private static long Unix(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    /// <summary>Segunda instância da API com a Stripe (simulador) e o faturamento administrativo (tg_billing) ligados.</summary>
    private WebApplicationFactory<Program> StripeApi(StripeSimulator sim)
    {
        var extra = sim.Settings;
        extra["ConnectionStrings:Billing"] = env.ConnectionFor("tg_billing");
        return env.NewApiInstance(extra);
    }

    private static byte[] FromBase32(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(s.Select(c => Convert.ToString(Alphabet.IndexOf(c, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
    }

    /// <summary>Cadastro → confirmação → login → MFA, no navegador dado; devolve o e-mail e o tenant (Sandbox, dono).</summary>
    private async Task<(string Email, Guid Tenant)> SignupAsync(HttpClient browser)
    {
        var email = $"cobranca-{Guid.NewGuid():N}@exemplo.test";
        var signup = await SendAsync(browser, HttpMethod.Post, "/v1/conta/cadastro", new
        {
            nome = "Fernanda Financeiro",
            email,
            senha = Senha,
            empresa = new { nome = $"Empresa cobrança {Guid.NewGuid():N}"[..30], cnpj = GoldenFixture.NewCnpj() },
            aceite_termos = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, signup.Status);
        var link = env.Emails.LastLinkTo(email)!;
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Post, "/v1/conta/confirmar-email", new { token })).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Status);
        var secret = (await SendAsync(browser, HttpMethod.Post, "/v1/conta/mfa/iniciar")).Body!["segredo"]!.GetValue<string>();
        var code = Totp.Code(FromBase32(secret), Totp.StepAt(env.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = code })).Status);
        var session = (await SendAsync(browser, HttpMethod.Get, "/v1/conta/sessao")).Body!;
        Assert.Equal("owner", session["papel"]!.GetValue<string>());
        return (email, session["empresa_ativa"]!.GetValue<Guid>());
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "condição não atingida no tempo");
            await Task.Delay(200);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));

        public bool Has(LogLevel level) => Entries.Any(e => e.Level == level);
    }

    // ------------------------------------------------------------------------------------------------ catálogo

    /// <summary>
    /// D-C05: medidores (documentos e simulações somados, CNPJs pelo último valor) e, por plano pago, produto + preço fixo
    /// + preços graduados (franquia a zero) de documentos, CNPJs e simulações — só os que o plano cobra. Plano sem mudança
    /// não ressincroniza; plano alterado ganha preços novos no MESMO produto.
    /// </summary>
    [Fact]
    public async Task Catalogo_cria_produto_e_precos_por_plano_e_so_ressincroniza_quando_o_plano_muda()
    {
        await using var sim = new StripeSimulator();
        var full = Code("SCAT");
        var lite = Code("SLIT");
        await SavePlanAsync(full, new
        {
            name = "Plano completo",
            description = "Com tudo",
            price_cents = 9900,
            included_docs = 100,
            overage_doc_millicents = 500,
            included_cnpjs = 2,
            cnpj_price_cents = 300,
            included_simulations = 50,
            overage_sim_millicents = 250,
            rpm = 600,
            burst = 100,
            concurrency = 10,
            self_service = true,
            active = true,
            is_public = false,
        });
        await SavePlanAsync(lite, new { name = "Plano enxuto", price_cents = 4900, included_docs = 10, overage_doc_millicents = 1500, rpm = 600, burst = 100, concurrency = 10, is_public = false });

        await using var billing = BillingDb();
        var syncLog = new ListLogger<StripeCatalogSync>();
        var sync = new StripeCatalogSync(new BillingDataSource(billing), Client(sim), Options(sim), syncLog);
        Assert.True(await sync.RunOnceAsync(CancellationToken.None) >= 2);

        // Medidores criados uma vez, com o mapeamento de cliente e o campo de valor que o reporter usa.
        Assert.Contains(syncLog.Entries, e => e.Level == LogLevel.Information && e.Message == $"Plano {full} sincronizado com a Stripe.");
        var meters = sim.CallsTo("POST", "/v1/billing/meters");
        Assert.Equal(MeterSetup, meters.Select(m => $"{m["event_name"]}:{m["default_aggregation[formula]"]}"));
        Assert.All(meters, m =>
        {
            Assert.Equal("stripe_customer_id", m["customer_mapping[event_payload_key]"]);
            Assert.Equal("value", m["value_settings[event_payload_key]"]);
            Assert.Equal($"meter:{m["event_name"]}", m.IdempotencyKey);
        });
        Assert.All(sim.Calls, c => Assert.Equal("2025-03-31.basil", c.StripeVersion));
        var meterId = sim.Meters.ToDictionary(m => m["event_name"]!.GetValue<string>(), m => m["id"]!.GetValue<string>());

        var product = Assert.Single(sim.CallsTo("POST", "/v1/products"), c => c["metadata[plan_code]"] == full);
        Assert.Equal("TaxGateway Plano completo", product["name"]);
        Assert.Equal("Com tudo", product["description"]);
        Assert.Equal($"product:{full}", product.IdempotencyKey);
        Assert.Null(Assert.Single(sim.CallsTo("POST", "/v1/products"), c => c["metadata[plan_code]"] == lite)["description"]);   // descrição vazia não vai

        var prices = sim.CallsTo("POST", "/v1/prices").Where(c => c["metadata[plan_code]"] == full).ToDictionary(c => c["metadata[kind]"]!);
        Assert.Equal(AllKinds, prices.Keys.Order());
        var fixedPrice = prices["fixed"];
        Assert.Equal(("9900", "brl", "month", null), (fixedPrice["unit_amount"], fixedPrice["currency"], fixedPrice["recurring[interval]"], fixedPrice["recurring[usage_type]"]));
        Assert.StartsWith($"price:{full}:fixed:", fixedPrice.IdempotencyKey, StringComparison.Ordinal);
        foreach (var (kind, meter, upTo, unit) in new[]
        {
            ("documentos", "taxgateway_documentos", "100", "0.5"),
            ("cnpjs", "taxgateway_cnpjs", "2", "300"),
            ("simulacoes", "taxgateway_simulacoes", "50", "0.25"),
        })
        {
            var p = prices[kind];
            Assert.Equal("metered", p["recurring[usage_type]"]);
            Assert.Equal(meterId[meter], p["recurring[meter]"]);
            Assert.Equal(("tiered", "graduated"), (p["billing_scheme"], p["tiers_mode"]));
            Assert.Equal((upTo, "0", "inf", unit), (p["tiers[0][up_to]"], p["tiers[0][unit_amount]"], p["tiers[1][up_to]"], p["tiers[1][unit_amount_decimal]"]));
            Assert.Equal($"{full} {kind} excedente", p["nickname"]);
        }

        // Plano sem CNPJ extra nem excedente de simulação: só mensalidade e documentos.
        Assert.Equal(FixedAndDocs, sim.CallsTo("POST", "/v1/prices").Where(c => c["metadata[plan_code]"] == lite).Select(c => c["metadata[kind]"]!).Order());

        async Task<(string? Product, string? Fixed, string? Docs, string? Cnpj, string? Sim)> PlanIds(string code)
        {
            await using var cmd = billing.CreateCommand("SELECT stripe_product_id, stripe_price_id, stripe_metered_price_id, stripe_cnpj_price_id, stripe_sim_price_id FROM plans WHERE code = $1");
            cmd.Parameters.Add(new() { Value = code });
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            return (S(0), S(1), S(2), S(3), S(4));
        }

        var ids = await PlanIds(full);
        Assert.StartsWith("prod_", ids.Product, StringComparison.Ordinal);
        Assert.All(new[] { ids.Fixed, ids.Docs, ids.Cnpj, ids.Sim }, id => Assert.StartsWith("price_", id, StringComparison.Ordinal));
        var liteIds = await PlanIds(lite);
        Assert.NotNull(liteIds.Docs);
        Assert.Null(liteIds.Cnpj);
        Assert.Null(liteIds.Sim);

        // Sem mudança: nada a sincronizar, nenhum produto/preço/medidor novo (os medidores são reaproveitados pela lista).
        var before = sim.Calls.Count(c => c.Method == "POST");
        Assert.Equal(0, await sync.RunOnceAsync(CancellationToken.None));
        Assert.Equal(before, sim.Calls.Count(c => c.Method == "POST"));
        Assert.Equal(6, sim.CallsTo("GET", "/v1/billing/meters").Count);

        // Plano alterado (a versão do preço é o segundo de updated_at): preços novos no mesmo produto.
        await Task.Delay(1100);
        await SavePlanAsync(full, new { price_cents = 12900 });
        Assert.Equal(1, await sync.RunOnceAsync(CancellationToken.None));
        Assert.Single(sim.CallsTo("POST", "/v1/products"), c => c["metadata[plan_code]"] == full);
        var newFixed = sim.CallsTo("POST", "/v1/prices").Last(c => c["metadata[plan_code]"] == full && c["metadata[kind]"] == "fixed");
        Assert.Equal("12900", newFixed["unit_amount"]);
        Assert.NotEqual(fixedPrice.IdempotencyKey, newFixed.IdempotencyKey);
        var after = await PlanIds(full);
        Assert.Equal(ids.Product, after.Product);
        Assert.NotEqual(ids.Fixed, after.Fixed);
    }

    // ------------------------------------------------------------------------------------------------ contratação

    /// <summary>
    /// Checkout (D-C02, D-R02, D-R10, D-R15): sem verificação = 403; plano inexistente ou só por vendas = 422; plano ainda
    /// sem preços na Stripe = 503; primeira contratação cria o cliente (com o CNPJ) e a sessão com mensalidade + medidos;
    /// assinatura pendente = 409; com assinatura ativa troca os itens com proporcional. Portal e GET /v1/cobranca.
    /// </summary>
    [Fact]
    public async Task Checkout_troca_de_plano_portal_e_situacao_da_cobranca()
    {
        await using var sim = new StripeSimulator();
        var plan = Code("SCHK");
        var other = Code("SCHO");
        var sales = Code("SVEN");
        await SavePlanAsync(plan, new
        {
            name = "Checkout completo",
            price_cents = 14900,
            included_docs = 500,
            overage_doc_millicents = 800,
            included_cnpjs = 1,
            cnpj_price_cents = 500,
            included_simulations = 100,
            overage_sim_millicents = 300,
            rpm = 600,
            burst = 100,
            concurrency = 10,
            self_service = true,
            active = true,
            is_public = false,
        });
        await SavePlanAsync(other, new { name = "Checkout enxuto", price_cents = 29900, included_docs = 2000, overage_doc_millicents = 600, rpm = 600, burst = 100, concurrency = 10, self_service = true, active = true, is_public = false });
        await SavePlanAsync(sales, new { name = "Só com vendas", price_cents = 99900, rpm = 600, burst = 100, concurrency = 10, self_service = false, active = true, is_public = false });
        await using var billing = BillingDb();
        await new StripeCatalogSync(new BillingDataSource(billing), Client(sim), Options(sim), NullLogger<StripeCatalogSync>.Instance).RunOnceAsync(CancellationToken.None);
        var unsynced = Code("SNSY");   // depois da sincronização: sem preços na Stripe ainda
        await SavePlanAsync(unsynced, new { name = "Recém-criado", price_cents = 100, rpm = 600, burst = 100, concurrency = 10, self_service = true, active = true, is_public = false });

        await using var parent = StripeApi(sim);
        await using var api = parent.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(env.Emails)));
        using var browser = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (email, tenant) = await SignupAsync(browser);

        var status = await SendAsync(browser, HttpMethod.Get, "/v1/cobranca");
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal("SANDBOX", status.Body!["plano"]!.GetValue<string>());
        Assert.Null(status.Body["assinatura"]);
        Assert.True(status.Body["pagamento_online"]!.GetValue<bool>());
        Assert.Empty(status.Body["faturas"]!.AsArray());
        Assert.False(status.Body["verificacao"]!["verificada"]!.GetValue<bool>());
        Assert.Null(status.Body["inadimplencia"]);

        var noPortal = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/portal");
        Assert.Equal((HttpStatusCode.Conflict, "ERR_SEM_ASSINATURA"), (noPortal.Status, noPortal.Body!["code"]!.GetValue<string>()));

        var unverified = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = plan });
        Assert.Equal((HttpStatusCode.Forbidden, "ERR_EMPRESA_NAO_VERIFICADA"), (unverified.Status, unverified.Body!["code"]!.GetValue<string>()));
        Assert.Empty(sim.CallsTo("POST", "/v1/customers"));

        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/verificar", new { observacao = "Contrato social conferido pelo suporte" })).Status);
        await WaitAsync(async () => (await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = "NAO_EXISTE" })).Status == HttpStatusCode.UnprocessableEntity);
        Assert.Equal("ERR_PLANO_INVALIDO", (await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = sales })).Body!["code"]!.GetValue<string>());
        var notReady = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = unsynced });
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "ERR_PAGAMENTO_INDISPONIVEL"), (notReady.Status, notReady.Body!["code"]!.GetValue<string>()));

        // Primeira contratação: cliente na Stripe (com CNPJ formatado) + sessão de Checkout.
        var checkout = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = plan });
        Assert.Equal(HttpStatusCode.OK, checkout.Status);
        Assert.Equal("checkout", checkout.Body!["acao"]!.GetValue<string>());
        Assert.StartsWith("https://checkout.stripe.test/c/cs_", checkout.Body["url"]!.GetValue<string>(), StringComparison.Ordinal);

        var customerCall = Assert.Single(sim.CallsTo("POST", "/v1/customers"));
        Assert.Equal(email, customerCall["email"]);
        Assert.Equal(tenant.ToString(), customerCall["metadata[tenant_id]"]);
        Assert.Equal("pt-BR", customerCall["preferred_locales[0]"]);
        Assert.Equal($"customer:{tenant}", customerCall.IdempotencyKey);
        var root = await ScalarAsync<string>("SELECT root_cnpj FROM tenants WHERE id = $1", tenant);
        var taxId = Assert.Single(sim.Calls, c => c.Method == "POST" && c.Path.EndsWith("/tax_ids", StringComparison.Ordinal));
        Assert.Equal(("br_cnpj", $"{root[..2]}.{root[2..5]}.{root[5..8]}/{root[8..12]}-{root[12..]}"), (taxId["type"], taxId["value"]));
        var customer = await ScalarAsync<string>("SELECT billing_customer_ref FROM tenants WHERE id = $1", tenant);
        Assert.StartsWith("cus_sim", customer, StringComparison.Ordinal);
        Assert.Equal($"/v1/customers/{customer}/tax_ids", taxId.Path);

        var prices = await ScalarAsync<string[]>("SELECT ARRAY[stripe_price_id, stripe_metered_price_id, stripe_cnpj_price_id, stripe_sim_price_id] FROM plans WHERE code = $1", plan);
        var session = Assert.Single(sim.CallsTo("POST", "/v1/checkout/sessions"));
        Assert.Equal(("subscription", customer, "pt-BR", tenant.ToString(), plan), (session["mode"], session["customer"], session["locale"], session["client_reference_id"], session["metadata[plan_code]"]));
        Assert.Equal((tenant.ToString(), plan), (session["subscription_data[metadata][tenant_id]"], session["subscription_data[metadata][plan_code]"]));
        Assert.Equal(("http://golden.test/portal/cobranca?contratado=1", "http://golden.test/portal/cobranca?cancelado=1"), (session["success_url"], session["cancel_url"]));
        Assert.Equal(prices, new[] { session["line_items[0][price]"], session["line_items[1][price]"], session["line_items[2][price]"], session["line_items[3][price]"] });
        Assert.Equal("1", session["line_items[0][quantity]"]);   // mensalidade
        Assert.Null(session["line_items[1][quantity]"]);          // medidos: sem quantidade
        Assert.Null(session["line_items[3][quantity]"]);          // simulações excedentes (D-R15)
        Assert.Null(session.IdempotencyKey);

        // Segunda tentativa (desistiu do primeiro Checkout): reaproveita o cliente.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = plan })).Status);
        Assert.Single(sim.CallsTo("POST", "/v1/customers"));
        Assert.Equal(customer, sim.CallsTo("POST", "/v1/checkout/sessions")[^1]["customer"]);

        var portal = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/portal");
        Assert.Equal(HttpStatusCode.OK, portal.Status);
        Assert.StartsWith("https://billing.stripe.test/p/bps_", portal.Body!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        var portalCall = Assert.Single(sim.CallsTo("POST", "/v1/billing_portal/sessions"));
        Assert.Equal((customer, "http://golden.test/portal/cobranca", "pt-BR"), (portalCall["customer"], portalCall["return_url"], portalCall["locale"]));

        // Assinatura com pagamento pendente: resolve no portal, não abre um segundo Checkout.
        var sub = $"sub_chk_{Guid.NewGuid():N}"[..24];
        await SetBillingAsync(tenant, customer, sub, "incomplete");
        var pending = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = other });
        Assert.Equal((HttpStatusCode.Conflict, "ERR_ASSINATURA_PENDENTE"), (pending.Status, pending.Body!["code"]!.GetValue<string>()));

        // Assinatura ativa: troca os itens da assinatura (remove os antigos, adiciona os do plano novo) com proporcional.
        sim.AddSubscription(sub, "si_antigo_fixo", "si_antigo_docs");
        await SetBillingAsync(tenant, customer, sub, "active");
        var changed = await SendAsync(browser, HttpMethod.Post, "/v1/cobranca/checkout", new { plano = other });
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        Assert.Equal("trocado", changed.Body!["acao"]!.GetValue<string>());
        var otherPrices = await ScalarAsync<string[]>("SELECT ARRAY[stripe_price_id, stripe_metered_price_id] FROM plans WHERE code = $1", other);
        Assert.Single(sim.CallsTo("GET", $"/v1/subscriptions/{sub}"));
        var change = Assert.Single(sim.CallsTo("POST", $"/v1/subscriptions/{sub}"));
        Assert.Equal(("create_prorations", other), (change["proration_behavior"], change["metadata[plan_code]"]));
        Assert.Equal(("si_antigo_fixo", "true", "si_antigo_docs", "true"), (change["items[0][id]"], change["items[0][deleted]"], change["items[1][id]"], change["items[1][deleted]"]));
        Assert.Equal((otherPrices[0], "1", otherPrices[1], null), (change["items[2][price]"], change["items[2][quantity]"], change["items[3][price]"], change["items[3][quantity]"]));
        Assert.Null(change["items[4][price]"]);
        Assert.Equal($"change:{sub}:{other}:{otherPrices[0]},{otherPrices[1]}", change.IdempotencyKey);
        Assert.Equal(2, sim.SubscriptionItems(sub).Count);
        Assert.DoesNotContain(sim.CallsTo("POST", "/v1/checkout/sessions"), c => c["metadata[plan_code]"] == other);   // troca não abre Checkout

        // Situação: assinatura, fatura espelhada e régua de inadimplência aberta.
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
        await ExecAsync("UPDATE tenants SET current_period_end = $2, payment_failed_at = now() - interval '2 days' WHERE id = $1", tenant, periodEnd);
        await ExecAsync("""
            INSERT INTO invoices (id, tenant_id, number, status, currency, amount_due_cents, amount_paid_cents, hosted_invoice_url, invoice_pdf, period_start, period_end)
            VALUES ($1, $2, 'TG-SIM-1', 'open', 'brl', 14900, 0, 'https://invoice.stripe.test/1', 'https://invoice.stripe.test/1.pdf', now(), now() + interval '30 days')
            """, $"in_chk_{Guid.NewGuid():N}", tenant);
        try
        {
            var full = (await SendAsync(browser, HttpMethod.Get, "/v1/cobranca")).Body!;
            Assert.Equal("active", full["assinatura"]!["situacao"]!.GetValue<string>());
            Assert.Equal(periodEnd.ToUnixTimeSeconds(), full["assinatura"]!["renova_em"]!.GetValue<DateTimeOffset>().ToUnixTimeSeconds());
            var invoice = Assert.Single(full["faturas"]!.AsArray())!;
            Assert.Equal(("TG-SIM-1", "open", 14900L), (invoice["numero"]!.GetValue<string>(), invoice["situacao"]!.GetValue<string>(), invoice["valor_centavos"]!.GetValue<long>()));
            Assert.Equal("https://invoice.stripe.test/1.pdf", invoice["pdf"]!.GetValue<string>());
            Assert.True(full["verificacao"]!["verificada"]!.GetValue<bool>());
            Assert.Equal("MANUAL", full["verificacao"]!["metodo"]!.GetValue<string>());
            var dunning = full["inadimplencia"]!;
            Assert.Equal("ACTIVE", dunning["situacao"]!.GetValue<string>());
            Assert.Equal(TimeSpan.FromDays(10), dunning["restringe_em"]!.GetValue<DateTimeOffset>() - dunning["desde"]!.GetValue<DateTimeOffset>());
            Assert.Equal(TimeSpan.FromDays(15), dunning["suspende_em"]!.GetValue<DateTimeOffset>() - dunning["desde"]!.GetValue<DateTimeOffset>());
            Assert.Equal(0, full["excedente"]!["teto_centavos"]!.GetValue<long>());
        }
        finally
        {
            // Não deixa a régua aberta para os outros testes (o DunningWorker deles varre todos os tenants).
            await ExecAsync("UPDATE tenants SET payment_failed_at = NULL WHERE id = $1", tenant);
        }
    }

    // ------------------------------------------------------------------------------------------------ consumo medido

    /// <summary>
    /// D-C06/D-R05: o reporter envia o delta de documentos e de simulações e o total de CNPJs; sem uso novo não envia
    /// delta; em falha da Stripe o intervalo fica pendente e o próximo envio repete o MESMO identificador (a Stripe
    /// deduplica) mesmo que o ledger tenha crescido; no dia 1 reporta também o mês anterior, com o horário dentro dele.
    /// </summary>
    [Fact]
    public async Task Reporter_envia_delta_reenvia_pendente_com_o_mesmo_identificador_e_fecha_o_mes_anterior_no_dia_1()
    {
        await using var sim = new StripeSimulator();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (tenant, _) = await env.CreateTenantWithIdAsync("Consumo medido", GoldenFixture.NewCnpj());
        var (canceled, _) = await env.CreateTenantWithIdAsync("Assinatura cancelada", GoldenFixture.NewCnpj());
        var (pastDue, _) = await env.CreateTenantWithIdAsync("Assinatura atrasada", GoldenFixture.NewCnpj());
        var customer = $"cus_rep_{suffix}";
        await SetBillingAsync(tenant, customer, $"sub_rep_{suffix}", "active");
        await SetBillingAsync(canceled, $"cus_can_{suffix}", $"sub_can_{suffix}", "canceled");
        await SetBillingAsync(pastDue, $"cus_pd_{suffix}", $"sub_pd_{suffix}", "past_due");

        var feb = new DateOnly(2027, 2, 1);
        await AddLedgerAsync(tenant, feb, "DOC_AUDIT", 3);
        await AddLedgerAsync(tenant, feb, "SIMULATION", 2);
        await AddLedgerAsync(canceled, feb, "DOC_AUDIT", 4);
        await AddCnpjsAsync(tenant, feb, 2);

        await using var billing = BillingDb();
        StripeUsageReporter Reporter(string at) => new(new BillingDataSource(billing), Client(sim), Options(sim), new FakeTimeProvider(DateTimeOffset.Parse(at, CultureInfo.InvariantCulture)),
            NullLogger<StripeUsageReporter>.Instance);
        IEnumerable<StripeSimulator.Request> Sent(string evt) => sim.CallsTo("POST", "/v1/billing/meter_events").Where(c => c["payload[stripe_customer_id]"] == customer && c["event_name"] == evt);
        var t = tenant.ToString("N");

        async Task<(long Reported, long? Pending, string? Id)> Report(string metric, DateOnly month)
        {
            await using var cmd = billing.CreateCommand("SELECT reported_qty, pending_qty, pending_identifier FROM stripe_usage_reports WHERE tenant_id = $1 AND billing_month = $2 AND metric = $3");
            cmd.Parameters.Add(new() { Value = tenant });
            cmd.Parameters.Add(new() { Value = month });
            cmd.Parameters.Add(new() { Value = metric });
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2));
        }

        const string Mid = "2027-02-15T12:00:00Z";
        Assert.True(await Reporter(Mid).RunOnceAsync(CancellationToken.None) >= 2);
        var doc = Assert.Single(Sent("taxgateway_documentos"));
        Assert.Equal(("3", $"{t}-202702-doc-0-3", Unix(Mid).ToString(CultureInfo.InvariantCulture)), (doc["payload[value]"], doc["identifier"], doc["timestamp"]));
        var simEvent = Assert.Single(Sent("taxgateway_simulacoes"));
        Assert.Equal(("2", $"{t}-202702-sim-0-2"), (simEvent["payload[value]"], simEvent["identifier"]));
        var cnpj = Assert.Single(Sent("taxgateway_cnpjs"));
        Assert.Equal("2", cnpj["payload[value]"]);
        Assert.StartsWith($"{t}-202702-cnpj-2027021512", cnpj["identifier"], StringComparison.Ordinal);   // segundos no fim: único por envio
        Assert.Equal((3L, (long?)null, (string?)null), await Report("documentos", feb));
        Assert.Equal(2L, (await Report("simulacoes", feb)).Reported);
        Assert.Equal(2L, (await Report("cnpjs", feb)).Reported);

        // Só assinaturas ativas/em teste/atrasadas: a cancelada não reporta; a atrasada sem uso só manda o total de CNPJs (0).
        Assert.DoesNotContain(sim.Calls, c => c["payload[stripe_customer_id]"] == $"cus_can_{suffix}");
        var pd = Assert.Single(sim.Calls, c => c["payload[stripe_customer_id]"] == $"cus_pd_{suffix}");
        Assert.Equal(("taxgateway_cnpjs", "0"), (pd["event_name"], pd["payload[value]"]));

        // Sem uso novo: nenhum delta; o total de CNPJs da mesma hora repete o identificador (deduplicado).
        await Reporter(Mid).RunOnceAsync(CancellationToken.None);
        Assert.Single(Sent("taxgateway_documentos"));
        Assert.Single(Sent("taxgateway_simulacoes"));
        Assert.Equal(2, Sent("taxgateway_cnpjs").Count());
        Assert.Single(sim.MeterEvents, e => e.Customer == customer && e.EventName == "taxgateway_cnpjs");

        await AddLedgerAsync(tenant, feb, "DOC_AUDIT", 2);
        await Reporter(Mid).RunOnceAsync(CancellationToken.None);
        Assert.Equal(("2", $"{t}-202702-doc-3-5"), (Sent("taxgateway_documentos").Last()["payload[value]"], Sent("taxgateway_documentos").Last()["identifier"]));

        // Falha da Stripe no envio: o intervalo 5→6 fica pendente com identificador fixo.
        await AddLedgerAsync(tenant, feb, "DOC_AUDIT", 1);
        sim.FailWhen(c => c.Path == "/v1/billing/meter_events" && c["payload[stripe_customer_id]"] == customer && c["event_name"] == "taxgateway_documentos");
        var ex = await Assert.ThrowsAsync<StripeException>(() => Reporter(Mid).RunOnceAsync(CancellationToken.None));
        Assert.Equal((500, "simulated_failure"), (ex.Status, ex.Code));
        Assert.Equal((5L, (long?)6, $"{t}-202702-doc-5-6"), await Report("documentos", feb));

        // Mais uso antes do reenvio: o pendente vai igual (mesmo identificador e valor), o novo uso fica para a próxima.
        await AddLedgerAsync(tenant, feb, "DOC_AUDIT", 1);
        await Reporter(Mid).RunOnceAsync(CancellationToken.None);
        var retried = Sent("taxgateway_documentos").Last();
        Assert.Equal(("1", $"{t}-202702-doc-5-6"), (retried["payload[value]"], retried["identifier"]));
        Assert.Equal((6L, (long?)null, (string?)null), await Report("documentos", feb));
        await Reporter(Mid).RunOnceAsync(CancellationToken.None);
        Assert.Equal($"{t}-202702-doc-6-7", Sent("taxgateway_documentos").Last()["identifier"]);
        Assert.Equal(7, sim.MeterEvents.Where(e => e.Customer == customer && e.EventName == "taxgateway_documentos").Sum(e => e.Value));   // = ledger, sem dupla cobrança

        // Dia 1 (02:00 em Brasília): fecha fevereiro com o horário do último segundo do mês, e reporta março.
        await AddLedgerAsync(tenant, feb, "DOC_AUDIT", 1);
        const string Day1 = "2027-03-01T05:00:00Z";
        await Reporter(Day1).RunOnceAsync(CancellationToken.None);
        var lastFeb = Sent("taxgateway_documentos").Last();
        Assert.Equal(($"{t}-202702-doc-7-8", "1", Unix("2027-02-28T23:59:59-03:00").ToString(CultureInfo.InvariantCulture)),
            (lastFeb["identifier"], lastFeb["payload[value]"], lastFeb["timestamp"]));
        Assert.Contains(Sent("taxgateway_cnpjs"), c => (c["identifier"] ?? "").StartsWith($"{t}-202702-cnpj-2027030105", StringComparison.Ordinal) && c["payload[value]"] == "2");
        var march = Assert.Single(Sent("taxgateway_cnpjs"), c => (c["identifier"] ?? "").StartsWith($"{t}-202703-cnpj-2027030105", StringComparison.Ordinal));
        Assert.Equal(("0", Unix(Day1).ToString(CultureInfo.InvariantCulture)), (march["payload[value]"], march["timestamp"]));
        Assert.Equal(8L, (await Report("documentos", feb)).Reported);

        // Dia 2: fevereiro já não é reportado.
        var calls = sim.Calls.Count;
        await Reporter("2027-03-02T15:00:00Z").RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(sim.Calls.Skip(calls), c => c["identifier"]?.Contains("-202702-", StringComparison.Ordinal) == true);
    }

    // ------------------------------------------------------------------------------------------------ fechamento

    /// <summary>
    /// §10.3/D-R01/D-P07: o fechamento só roda a partir das 03:00 do dia 1; calcula mensalidade + excedentes; assinante
    /// fica SUBSCRIPTION (sem fatura avulsa); sem cliente fica CLOSED; quem não assina recebe fatura avulsa idempotente.
    /// Rodar de novo não refatura; período preso em CHARGING é recuperado sem fatura duplicada. Rotas administrativas de
    /// fechamento, reconciliação e ajustes.
    /// </summary>
    [Fact]
    public async Task Fechamento_mensal_idempotente_reconciliacao_e_ajustes()
    {
        await using var sim = new StripeSimulator();
        var plan = Code("SFEC");
        await SavePlanAsync(plan, new
        {
            name = "Plano fechamento",
            price_cents = 5000,
            included_docs = 2,
            overage_doc_millicents = 1000,
            included_simulations = 1,
            overage_sim_millicents = 2000,
            included_cnpjs = 1,
            cnpj_price_cents = 300,
            rpm = 600,
            burst = 100,
            concurrency = 10,
            is_public = false,
        });
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var jan = new DateOnly(2027, 1, 1);
        var (a, keyA) = await env.CreateTenantWithIdAsync("Fechamento avulso", GoldenFixture.NewCnpj());
        var (b, _) = await env.CreateTenantWithIdAsync("Fechamento assinante", GoldenFixture.NewCnpj());
        var (c, _) = await env.CreateTenantWithIdAsync("Fechamento sem cliente", GoldenFixture.NewCnpj());
        foreach (var t in new[] { a, b, c })
        {
            Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{t}/plano", new { plano = plan })).Status);
            await AddLedgerAsync(t, jan, "DOC_AUDIT", 1);
        }

        await SetBillingAsync(a, $"cus_fa_{suffix}");
        await SetBillingAsync(b, $"cus_fb_{suffix}", $"sub_fb_{suffix}", "active");
        await AddLedgerAsync(a, jan, "DOC_AUDIT", 4);
        await AddLedgerAsync(a, jan, "SIMULATION", 3);
        await AddCnpjsAsync(a, jan, 3);

        // /v1/usage do mês (cache de 60 s) e competência inválida.
        async Task<(HttpStatusCode, JsonNode?)> Usage(string mes)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/usage?mes={mes}");
            req.Headers.Add("X-Api-Key", keyA);
            using var resp = await env.Api.SendAsync(req);
            return (resp.StatusCode, JsonNode.Parse(await resp.Content.ReadAsStringAsync()));
        }

        var (usageStatus, usage) = await Usage("2027-01");
        Assert.Equal(HttpStatusCode.OK, usageStatus);
        Assert.Equal((plan, 5L, 3L, 3L, 2L, 3L), (usage!["plano"]!.GetValue<string>(), usage["documentos_auditados"]!.GetValue<long>(), usage["simulacoes"]!.GetValue<long>(),
            usage["cnpjs_ativos"]!.GetValue<long>(), usage["franquia_documentos"]!.GetValue<long>(), usage["excedente_documentos"]!.GetValue<long>()));
        Assert.Equal(usage["atualizado_em"]!.ToJsonString(), (await Usage("2027-01")).Item2!["atualizado_em"]!.ToJsonString());   // do cache
        Assert.Equal(HttpStatusCode.BadRequest, (await Usage("2027-1")).Item1);

        await using var billing = BillingDb();
        var ds = new BillingDataSource(billing);
        var serviceLog = new ListLogger<BillingService>();
        var service = new BillingService(ds, new StripeBillingProvider(Client(sim)), serviceLog);
        BillingCloseWorker Worker(string at) => new(service, ds, new FakeTimeProvider(DateTimeOffset.Parse(at, CultureInfo.InvariantCulture)), NullLogger<BillingCloseWorker>.Instance);

        async Task<(string Status, long Amount, long Docs, long Sims, int Cnpjs, string? Ref)?> Period(Guid t)
        {
            await using var cmd = billing.CreateCommand("SELECT status, amount_cents, docs_count, simulations_count, active_cnpjs, provider_ref FROM billing_periods WHERE tenant_id = $1 AND billing_month = $2");
            cmd.Parameters.Add(new() { Value = t });
            cmd.Parameters.Add(new() { Value = jan });
            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)) : null;
        }

        IReadOnlyList<StripeSimulator.Request> InvoicesFor(Guid t, int v = 1) => [.. sim.CallsTo("POST", "/v1/invoices").Where(x => x["metadata[idempotency_key]"] == BillingService.Key(t, jan, v))];

        // Dia 1 antes das 03:00 (Brasília): ainda não fecha.
        await Worker("2027-02-01T05:00:00Z").RunOnceAsync(CancellationToken.None);
        Assert.Null(await Period(a));

        await Worker("2027-02-01T07:00:00Z").RunOnceAsync(CancellationToken.None);
        // 5000 + 3 docs × 1 centavo + 2 simulações × 2 centavos + 2 CNPJs × 300.
        var pa = (await Period(a))!.Value;
        Assert.Equal(("INVOICED", 5607L, 5L, 3L, 3), (pa.Status, pa.Amount, pa.Docs, pa.Sims, pa.Cnpjs));
        var invoice = Assert.Single(InvoicesFor(a));
        Assert.Equal(pa.Ref, sim.Invoice(pa.Ref!)["id"]!.GetValue<string>());
        Assert.Equal(($"cus_fa_{suffix}", "send_invoice", "true", "TaxGateway 01/2027"), (invoice["customer"], invoice["collection_method"], invoice["auto_advance"], invoice["description"]));
        Assert.Equal(Unix("2027-02-10T12:00:00Z").ToString(CultureInfo.InvariantCulture), invoice["due_date"]);
        var item = Assert.Single(sim.CallsTo("POST", "/v1/invoiceitems"), x => x["invoice"] == pa.Ref);
        Assert.Equal(("5607", "brl"), (item["amount"], item["currency"]));
        Assert.Equal($"fin:{BillingService.Key(a, jan, 1)}", Assert.Single(sim.CallsTo("POST", $"/v1/invoices/{pa.Ref}/finalize")).IdempotencyKey);
        Assert.Equal("open", sim.Invoice(pa.Ref!)["status"]!.GetValue<string>());

        Assert.Equal(("SUBSCRIPTION", 5000L, (string?)null), ((await Period(b))!.Value.Status, (await Period(b))!.Value.Amount, (await Period(b))!.Value.Ref));
        Assert.DoesNotContain(sim.Calls, x => x["customer"] == $"cus_fb_{suffix}");   // assinante: a Stripe cobra pela assinatura
        Assert.Equal(("CLOSED", (string?)null), ((await Period(c))!.Value.Status, (await Period(c))!.Value.Ref));
        Assert.Contains(serviceLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(c.ToString(), StringComparison.Ordinal));   // sem cliente: avisa e espera

        // Idempotente: rodar de novo não refatura.
        await Worker("2027-02-01T08:00:00Z").RunOnceAsync(CancellationToken.None);
        Assert.Single(InvoicesFor(a));

        // Queda no meio da cobrança: CHARGING há 1 h volta a CLOSED e reaproveita a fatura já criada (busca pela chave).
        await ExecAsync("UPDATE billing_periods SET status = 'CHARGING', charging_since = now() - interval '1 hour', provider_ref = NULL WHERE tenant_id = $1 AND billing_month = $2", a, jan);
        await Worker("2027-02-01T09:00:00Z").RunOnceAsync(CancellationToken.None);
        Assert.Equal(("INVOICED", pa.Ref), ((await Period(a))!.Value.Status, (await Period(a))!.Value.Ref));
        Assert.Single(InvoicesFor(a));
        Assert.Contains(sim.CallsTo("GET", "/v1/invoices/search"), x => Uri.UnescapeDataString(x.Query).Contains(BillingService.Key(a, jan, 1), StringComparison.Ordinal));

        // Outra réplica faturando (lock global ocupado): não fatura nada.
        await using (var holder = new NpgsqlConnection(env.SuperuserConnection))
        {
            await holder.OpenAsync();
            await using (var l = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext('taxgateway:billing:invoice'))", holder))
            {
                await l.ExecuteNonQueryAsync();
            }

            Assert.Equal(0, await service.InvoiceAsync(jan, CancellationToken.None));
        }

        // Rotas administrativas (instância com tg_billing e Stripe).
        await using var api = StripeApi(sim);
        using var http = api.CreateClient();
        await SetBillingAsync(c, $"cus_fc_{suffix}");
        var close = await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/close?mes=2027-01");
        Assert.Equal(HttpStatusCode.OK, close.Status);
        Assert.Equal("2027-01", close.Body!["competencia"]!.GetValue<string>());
        Assert.True(close.Body["faturados"]!.GetValue<int>() >= 1);
        Assert.Equal("INVOICED", (await Period(c))!.Value.Status);
        Assert.Single(InvoicesFor(c));
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/close?mes=2027-13")).Status);

        async Task<JsonObject?> Divergence(Guid t)
        {
            var r = await AdminAsync(http, HttpMethod.Get, "/admin/v1/billing/reconcile?mes=2027-01");
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.True(r.Body!["tenants"]!.GetValue<int>() >= 3);
            return r.Body["divergencias"]!.AsArray().FirstOrDefault(d => d!["tenant_id"]!.GetValue<Guid>() == t)?.AsObject();
        }

        Assert.Null(await Divergence(a));
        Assert.Null(await Divergence(b));
        sim.VoidInvoice(pa.Ref!);   // anulada na Stripe: o provedor não tem mais o valor
        var voided = (await Divergence(a))!;
        Assert.Equal((5607L, -1L), (voided["valor_periodo_centavos"]!.GetValue<long>(), voided["valor_provedor_centavos"]!.GetValue<long>()));
        await AddLedgerAsync(b, jan, "DOC_AUDIT", 1);   // uso que chegou depois do fechamento
        var late = (await Divergence(b))!;
        Assert.Equal((2L, 1L, (long?)null), (late["ledger_documentos"]!.GetValue<long>(), late["periodo_documentos"]!.GetValue<long>(), late["valor_provedor_centavos"]?.GetValue<long>()));

        // Ajustes (v2, v3…): débito vira fatura avulsa idempotente; crédito fica a abater; sem cliente fica pendente.
        var debit = await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/adjustments", new { tenant_id = a, competencia = "2027-01", valor_centavos = 1500, motivo = "Documentos reprocessados" });
        Assert.Equal(HttpStatusCode.OK, debit.Status);
        Assert.Equal(("v2", "INVOICED"), (debit.Body!["versao"]!.GetValue<string>(), debit.Body["status"]!.GetValue<string>()));
        var adj = Assert.Single(InvoicesFor(a, 2));
        Assert.Equal("TaxGateway ajuste 01/2027 v2", adj["description"]);
        Assert.Equal(1500, sim.Invoice(sim.CallsTo("POST", "/v1/invoiceitems")[^1]["invoice"]!)["amount_due"]!.GetValue<long>());
        var credit = await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/adjustments", new { tenant_id = a, competencia = "2027-01", valor_centavos = -200, motivo = "Cortesia" });
        Assert.Equal(("v3", "CREDITO_A_ABATER"), (credit.Body!["versao"]!.GetValue<string>(), credit.Body["status"]!.GetValue<string>()));
        Assert.Empty(InvoicesFor(a, 3));
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/adjustments", new { tenant_id = a, competencia = "2027-01", valor_centavos = 10 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/adjustments", new { tenant_id = "x", competencia = "2027-01", valor_centavos = 10, motivo = "m" })).Status);

        var (e, _) = await env.CreateTenantWithIdAsync("Ajuste sem cliente", GoldenFixture.NewCnpj());
        await AddLedgerAsync(e, jan, "DOC_AUDIT", 1);
        await service.CloseMonthAsync(jan, CancellationToken.None);
        var pendingAdj = await AdminAsync(http, HttpMethod.Post, "/admin/v1/billing/adjustments", new { tenant_id = e, competencia = "2027-01", valor_centavos = 100, motivo = "Sem cliente ainda" });
        Assert.Equal(("v2", "PENDING"), (pendingAdj.Body!["versao"]!.GetValue<string>(), pendingAdj.Body["status"]!.GetValue<string>()));

        // Falha do provedor na cobrança: o período volta a CLOSED (sem ficar preso em CHARGING) e a próxima rodada cobra.
        var (f, _) = await env.CreateTenantWithIdAsync("Fechamento com falha", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{f}/plano", new { plano = plan })).Status);
        await SetBillingAsync(f, $"cus_ff_{suffix}");
        await AddLedgerAsync(f, jan, "DOC_AUDIT", 2);
        await service.CloseMonthAsync(jan, CancellationToken.None);
        sim.FailWhen(x => x.Is("POST", "/v1/invoices") && x["customer"] == $"cus_ff_{suffix}", status: 503);
        await Assert.ThrowsAsync<StripeException>(() => service.InvoiceAsync(jan, CancellationToken.None));
        Assert.Equal("CLOSED", (await Period(f))!.Value.Status);
        Assert.Null(await ScalarAsync<DateTime?>("SELECT charging_since FROM billing_periods WHERE tenant_id = $1 AND billing_month = $2", f, jan));
        Assert.True(await service.InvoiceAsync(jan, CancellationToken.None) >= 1);
        Assert.Equal("INVOICED", (await Period(f))!.Value.Status);
    }

    // ------------------------------------------------------------------------------------------------ eventos

    /// <summary>
    /// D-C04: o plano vem do PREÇO da assinatura (troca pelo portal não muda a metadata) e o fim do período vem do item;
    /// evento malformado não derruba o processador: fica com o erro registrado, sem efeito, e os demais seguem.
    /// </summary>
    [Fact]
    public async Task Evento_de_assinatura_usa_o_preco_e_evento_malformado_fica_com_erro()
    {
        var plan = Code("SEVT");
        await SavePlanAsync(plan, new { name = "Plano por preço", price_cents = 1000, rpm = 600, burst = 100, concurrency = 10, is_public = false });
        var price = $"price_evt_{Guid.NewGuid():N}";
        await ExecAsync("UPDATE plans SET stripe_price_id = $2 WHERE code = $1", plan, price);
        var (tenant, _) = await env.CreateTenantWithIdAsync("Eventos", GoldenFixture.NewCnpj());
        var s = Guid.NewGuid().ToString("N")[..10];
        var customer = $"cus_evt_{s}";
        await SetBillingAsync(tenant, customer);

        await using var billing = BillingDb();
        await using var valkey = await ConnectionMultiplexer.ConnectAsync(env.ValkeyConnection);
        var processorLog = new ListLogger<StripeEventProcessor>();
        var processor = new StripeEventProcessor(new BillingDataSource(billing), valkey, processorLog);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var periodEnd = now + 2_592_000;
        await PostWebhookAsync($$$$"""{"id":"evt_bad_{{{{s}}}}","type":"invoice.created","created":{{{{now}}}},"data":{"object":{"id":"in_bad_{{{{s}}}}","customer":"{{{{customer}}}}","amount_due":"muito"}}}""");
        await PostWebhookAsync($$$$$"""{"id":"evt_sub_{{{{{s}}}}}","type":"customer.subscription.created","created":{{{{{now}}}}},"data":{"object":{"id":"sub_evt_{{{{{s}}}}}","customer":"{{{{{customer}}}}}","status":"active","metadata":{"plan_code":"ESSENCIAL"},"items":{"data":[{"price":{"id":"{{{{{price}}}}}"},"current_period_end":{{{{{periodEnd}}}}}}]}}}}""");
        Assert.True(await processor.RunOnceAsync(CancellationToken.None) >= 2);

        await using (var cmd = billing.CreateCommand("SELECT plan_code, subscription_status, stripe_subscription_id, current_period_end FROM tenants WHERE id = $1"))
        {
            cmd.Parameters.Add(new() { Value = tenant });
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal((plan, "active", $"sub_evt_{s}"), (r.GetString(0), r.GetString(1), r.GetString(2)));
            Assert.Equal(periodEnd, r.GetFieldValue<DateTimeOffset>(3).ToUnixTimeSeconds());
        }

        await using (var cmd = billing.CreateCommand("SELECT processed_at IS NULL, attempts, last_error FROM stripe_events WHERE id = $1"))
        {
            cmd.Parameters.Add(new() { Value = $"evt_bad_{s}" });
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.True(r.GetBoolean(0));
            Assert.Equal(1, r.GetInt32(1));   // uma tentativa por ciclo (antes gastava as 10 de uma vez)
            Assert.False(r.IsDBNull(2));
        }

        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM invoices WHERE id = $1", $"in_bad_{s}"));
        Assert.Contains(processorLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"evt_bad_{s}", StringComparison.Ordinal));
    }

    private static string Sign(string payload, long t)
    {
        byte[] signed = [.. Encoding.UTF8.GetBytes($"{t}."), .. Encoding.UTF8.GetBytes(payload)];
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(GoldenFixture.StripeWebhookSecret), signed))}";
    }

    private async Task<HttpStatusCode> PostWebhookAsync(string json, bool expectOk = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("Stripe-Signature", Sign(json, env.Clock.GetUtcNow().ToUnixTimeSeconds()));
        using var resp = await env.Api.SendAsync(req);
        if (expectOk)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        return resp.StatusCode;
    }

    /// <summary>Webhook assinado mas sem id/tipo, ou grande demais: 400 (nada é gravado).</summary>
    [Fact]
    public async Task Webhook_sem_tipo_ou_grande_demais_e_recusado()
    {
        Assert.Equal(HttpStatusCode.BadRequest, await PostWebhookAsync("""{"id":"evt_sem_tipo"}""", expectOk: false));
        Assert.Equal(HttpStatusCode.BadRequest, await PostWebhookAsync("""{"type":"invoice.paid"}""", expectOk: false));
        var big = $$"""{"id":"evt_grande","type":"invoice.paid","pad":"{{new string('x', 1_000_100)}}"}""";
        Assert.Equal(HttpStatusCode.BadRequest, await PostWebhookAsync(big, expectOk: false));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM stripe_events WHERE id IN ('evt_sem_tipo', 'evt_grande')"));
    }

    // ------------------------------------------------------------------------------------------------ backoffice

    /// <summary>Cadastro de plano: cada regra de validação, campos ilimitados (null), truncamento e plano inexistente.</summary>
    [Fact]
    public async Task Cadastro_de_plano_valida_cada_campo()
    {
        var code = Code("SVAL");
        var notObject = await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{code}", NotAnObject);
        Assert.Equal("ERR_JSON_INVALIDO", notObject.Body!["code"]!.GetValue<string>());

        var invalid = await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{code}", new
        {
            name = "X",
            hard_quota = "sim",
            queue_priority = "urgente",
            features = Enumerable.Range(0, 13).Select(i => $"recurso {i}").ToArray(),
            rpm = "muito",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        var detail = invalid.Body!["detail"]!.GetValue<string>();
        foreach (var expected in new[] { "name deve ter de 2 a 60 caracteres", "hard_quota deve ser true/false", "queue_priority: low, normal, high ou realtime", "features: até 12 textos", "rpm deve ser um inteiro" })
        {
            Assert.Contains(expected, detail, StringComparison.Ordinal);
        }

        var ok = await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{code}", new
        {
            name = "  Plano validado  ",
            description = new string('d', 600),
            included_cnpjs = (int?)null,
            max_users = (int?)null,
            queue_priority = "high",
            features = ValidFeatures,
            hard_quota = true,
            rpm = 600,
            burst = 100,
            concurrency = 10,
        });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(code, ok.Body!["codigo"]!.GetValue<string>());
        var saved = (await AdminAsync(HttpMethod.Get, "/admin/v1/planos")).Body!.AsArray().Single(p => p!["code"]!.GetValue<string>() == code)!;
        Assert.Equal(("Plano validado", 500, "high", true), (saved["name"]!.GetValue<string>(), saved["description"]!.GetValue<string>().Length, saved["queue_priority"]!.GetValue<string>(), saved["hard_quota"]!.GetValue<bool>()));
        Assert.Null(saved["included_cnpjs"]);
        Assert.Null(saved["max_users"]);

        var (tenant, _) = await env.CreateTenantWithIdAsync("Plano inexistente", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = "NAO_EXISTE_X" })).Status);
    }

    // ------------------------------------------------------------------------------------------------ workers

    private static NpgsqlDataSource BrokenDb()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        return NpgsqlDataSource.Create($"Host=127.0.0.1;Port={port};Username=x;Password=x;Database=x;Timeout=2;Pooling=false");
    }

    private static async Task RunUntilLoggedAsync<T>(BackgroundService worker, ListLogger<T> log, LogLevel level)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitAsync(() => Task.FromResult(log.Has(level)), seconds: 30);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Laços em segundo plano: sem Stripe configurada, catálogo e consumo nem rodam; com o banco fora do ar, cada worker
    /// registra a falha e segue vivo (para no StopAsync, sem derrubar o host).
    /// </summary>
    [Fact]
    public async Task Workers_em_segundo_plano_registram_falha_e_param_sem_derrubar_o_host()
    {
        await using var sim = new StripeSimulator();
        await using var broken = BrokenDb();
        var ds = new BillingDataSource(broken);
        var disabled = new StripeClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new StripeOptions()));
        await using var valkey = await ConnectionMultiplexer.ConnectAsync(env.ValkeyConnection);

        using (var off = new StripeCatalogSync(ds, disabled, Microsoft.Extensions.Options.Options.Create(new StripeOptions()), NullLogger<StripeCatalogSync>.Instance))
        {
            await off.StartAsync(CancellationToken.None);
            await WaitAsync(() => Task.FromResult(off.ExecuteTask!.IsCompleted), seconds: 5);
            await off.StopAsync(CancellationToken.None);
        }

        using (var off = new StripeUsageReporter(ds, disabled, Microsoft.Extensions.Options.Options.Create(new StripeOptions()), TimeProvider.System, NullLogger<StripeUsageReporter>.Instance))
        {
            await off.StartAsync(CancellationToken.None);
            await WaitAsync(() => Task.FromResult(off.ExecuteTask!.IsCompleted), seconds: 5);
            await off.StopAsync(CancellationToken.None);
        }

        Assert.Empty(sim.Calls);

        var catalogLog = new ListLogger<StripeCatalogSync>();
        using (var w = new StripeCatalogSync(ds, Client(sim), Options(sim), catalogLog))
        {
            await RunUntilLoggedAsync(w, catalogLog, LogLevel.Error);
        }

        Assert.Equal(3, sim.CallsTo("POST", "/v1/billing/meters").Count);   // falhou só no banco, depois dos medidores

        var reporterLog = new ListLogger<StripeUsageReporter>();
        using (var w = new StripeUsageReporter(ds, Client(sim), Options(sim), TimeProvider.System, reporterLog))
        {
            await RunUntilLoggedAsync(w, reporterLog, LogLevel.Error);
        }

        var eventsLog = new ListLogger<StripeEventProcessor>();
        using (var w = new StripeEventProcessor(ds, valkey, eventsLog))
        {
            await RunUntilLoggedAsync(w, eventsLog, LogLevel.Error);
        }

        var dunningLog = new ListLogger<DunningWorker>();
        using (var w = new DunningWorker(ds, valkey, TimeProvider.System, dunningLog))
        {
            await RunUntilLoggedAsync(w, dunningLog, LogLevel.Warning);
        }

        var closeLog = new ListLogger<BillingCloseWorker>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));
        using (var w = new BillingCloseWorker(new BillingService(ds, new StripeBillingProvider(Client(sim)), NullLogger<BillingService>.Instance), ds, clock, closeLog))
        {
            await RunUntilLoggedAsync(w, closeLog, LogLevel.Error);
        }
    }

    /// <summary>
    /// Valkey fora do ar: a régua de inadimplência e o processador de eventos aplicam a mudança no banco e não falham ao
    /// avisar as instâncias (o cache de perfil expira sozinho).
    /// </summary>
    [Fact]
    public async Task Sem_valkey_a_regua_e_os_eventos_seguem_aplicando()
    {
        var (tenant, _) = await env.CreateTenantWithIdAsync("Sem valkey", GoldenFixture.NewCnpj());
        var s = Guid.NewGuid().ToString("N")[..10];
        var customer = $"cus_nv_{s}";
        await SetBillingAsync(tenant, customer);
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var deadPort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var options = new ConfigurationOptions { AbortOnConnectFail = false, ConnectTimeout = 300, AsyncTimeout = 1000, SyncTimeout = 1000, BacklogPolicy = BacklogPolicy.FailFast };
        options.EndPoints.Add(IPAddress.Loopback, deadPort);
        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);

        await using var billing = BillingDb();
        await PostWebhookAsync($$$$"""{"id":"evt_nv_{{{{s}}}}","type":"invoice.payment_failed","created":{{{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}},"data":{"object":{"id":"in_nv_{{{{s}}}}","customer":"{{{{customer}}}}","status":"open","currency":"brl","amount_due":100,"amount_paid":0}}}""");
        await new StripeEventProcessor(new BillingDataSource(billing), dead, NullLogger<StripeEventProcessor>.Instance).RunOnceAsync(CancellationToken.None);
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT payment_failed_at FROM tenants WHERE id = $1", tenant));
        try
        {
            var dunningLog = new ListLogger<DunningWorker>();
            var worker = new DunningWorker(new BillingDataSource(billing), dead, new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(1)), dunningLog);
            Assert.True(await worker.RunOnceAsync(CancellationToken.None) >= 1);
            Assert.Contains(dunningLog.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(tenant.ToString(), StringComparison.Ordinal));
            var applied = dunningLog.Entries.Count;
            await worker.RunOnceAsync(CancellationToken.None);   // mesmo passo: o aviso do dia 0 não sai de novo
            Assert.DoesNotContain(dunningLog.Entries.Skip(applied), e => e.Message.Contains(tenant.ToString(), StringComparison.Ordinal));
            Assert.Equal(0, await ScalarAsync<int>("SELECT dunning_stage FROM tenants WHERE id = $1", tenant));
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM tenant_notifications WHERE tenant_id = $1 AND kind = 'PAGAMENTO_PENDENTE'", tenant));
        }
        finally
        {
            await ExecAsync("UPDATE tenants SET payment_failed_at = NULL, dunning_stage = 0 WHERE id = $1", tenant);
        }
    }

    /// <summary>Cliente da Stripe: desligado, erro sem corpo, CNPJ recusado pela Stripe e versão da API vazia.</summary>
    [Fact]
    public async Task Cliente_da_Stripe_trata_desligado_erro_sem_corpo_e_cnpj_recusado()
    {
        var off = new StripeClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new StripeOptions()));
        Assert.False(off.Enabled);
        var notConfigured = await Assert.ThrowsAsync<StripeException>(() => off.SendAsync(HttpMethod.Get, "v1/billing/meters"));
        Assert.Equal((503, "not_configured"), (notConfigured.Status, notConfigured.Code));

        await using var sim = new StripeSimulator();
        var client = Client(sim, apiVersion: "");
        var tenant = Guid.NewGuid();
        sim.FailWhen(c => c.Path.EndsWith("/tax_ids", StringComparison.Ordinal), status: 400);
        var id = await client.CreateCustomerAsync(tenant, "Empresa alfanumérica", null, "AB12CD34000155", CancellationToken.None);
        Assert.StartsWith("cus_sim", id, StringComparison.Ordinal);   // CNPJ recusado: o cliente segue sem tax id
        Assert.Equal("AB.12C.D34/0001-55", Assert.Single(sim.Calls, c => c.Path.EndsWith("/tax_ids", StringComparison.Ordinal))["value"]);
        Assert.Null(sim.CallsTo("POST", "/v1/customers")[0]["email"]);
        Assert.All(sim.Calls, c => Assert.Null(c.StripeVersion));   // versão vazia = padrão da conta

        await client.CreateCustomerAsync(Guid.NewGuid(), "Sem CNPJ completo", "a@b.test", "11222333", CancellationToken.None);
        Assert.Single(sim.Calls, c => c.Path.EndsWith("/tax_ids", StringComparison.Ordinal));

        sim.FailWhen(c => c.Path == "/v1/products", status: 502, emptyBody: true);
        var bare = await Assert.ThrowsAsync<StripeException>(() => client.CreateProductAsync("X", "X", "", CancellationToken.None));
        Assert.Equal((502, "Stripe HTTP 502", (string?)null), (bare.Status, bare.Message, bare.Code));

        var wrongKey = new StripeClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new StripeOptions { SecretKey = "sk_test_outra", ApiBase = sim.BaseUri }));
        var unauthorized = await Assert.ThrowsAsync<StripeException>(() => wrongKey.CreatePortalSessionAsync("cus_x", "http://x", CancellationToken.None));
        Assert.Equal((401, "api_key_invalid"), (unauthorized.Status, unauthorized.Code));

        // Cobrança avulsa idempotente: a segunda cobrança com a mesma chave acha a fatura e não cria outra.
        var provider = new StripeBillingProvider(Client(sim));
        Assert.Equal("STRIPE", provider.Name);
        var charge = new BillingCharge(id, 4321, "TaxGateway teste", $"{tenant}:2027-01:v9", new DateOnly(2027, 2, 10));
        var first = await provider.ChargeAsync(charge, CancellationToken.None);
        Assert.Equal(first, await provider.ChargeAsync(charge, CancellationToken.None));
        Assert.Single(sim.CallsTo("POST", "/v1/invoices"));
        Assert.Equal((first, 4321L), await provider.FindAsync(charge.IdempotencyKey, CancellationToken.None));
        Assert.Null(await provider.FindAsync("chave-que-nao-existe", CancellationToken.None));
    }
}
