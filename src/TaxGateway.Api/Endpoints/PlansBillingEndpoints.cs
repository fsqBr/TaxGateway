using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Api.Http;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Billing;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Planos e cobrança (D-C01 a D-C07): vitrine pública de planos, cadastro de planos no backoffice (com histórico),
/// contratação e troca de plano pela Stripe (Checkout e portal do cliente) e o webhook da Stripe.
/// </summary>
public static class PlansBillingEndpoints
{
    private const string PlanColumns = """
        code, name, description, features, price_cents, included_docs, included_simulations, included_cnpjs, max_users,
        overage_doc_millicents, overage_sim_millicents, cnpj_price_cents, rpm, burst, concurrency, batch_max_docs, batch_max_bytes,
        queue_priority, bill_schema_errors, hard_quota, dfe_enabled, is_public, self_service, active, sort_order, updated_at, updated_by,
        stripe_product_id, stripe_price_id, stripe_synced_at
        """;

    /// <summary>Vitrine (anônima): planos públicos e ativos, para a página de planos e o cadastro.</summary>
    public static void MapPublicPlans(this WebApplication app)
    {
        app.MapGet("/v1/conta/planos", async (NpgsqlDataSource appDb, CancellationToken ct) =>
        {
            await using var cmd = appDb.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object(
                         'codigo', code, 'nome', name, 'descricao', description, 'recursos', features, 'preco_centavos', price_cents,
                         'documentos_incluidos', included_docs, 'simulacoes_incluidas', included_simulations, 'cnpjs_incluidos', included_cnpjs,
                         'usuarios', max_users, 'excedente_documento_centavos', overage_doc_millicents / 1000.0, 'cnpj_extra_centavos', cnpj_price_cents,
                         'contratacao_online', self_service, 'requisicoes_por_minuto', rpm, 'requisicoes_simultaneas', concurrency,
                         'requisicoes_por_minuto_por_cnpj', per_cnpj_rpm, 'lote_max_documentos', batch_max_docs, 'lote_max_bytes', batch_max_bytes,
                         'excedente_simulacao_milicentavos', overage_sim_millicents, 'captura_dfe', dfe_enabled, 'franquia_rigida', hard_quota)
                         ORDER BY sort_order), '[]')::text
                  FROM plans WHERE is_public AND active
                """);
            return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
        }).WithTags("Conta");
    }

    /// <summary>Webhook da Stripe: verifica a assinatura e grava o evento; os workers aplicam (D-C04).</summary>
    public static void MapStripeWebhook(this WebApplication app)
    {
        app.MapPost("/webhooks/stripe", async (HttpContext http, NpgsqlDataSource appDb, IOptions<StripeOptions> options, TimeProvider clock) =>
        {
            byte[] body;
            using (var ms = new MemoryStream())
            {
                await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
                body = ms.ToArray();
            }

            if (body.Length > 1_000_000 || !StripeClient.VerifyWebhook(http.Request.Headers["Stripe-Signature"], body, options.Value.WebhookSecret, clock.GetUtcNow()))
            {
                return Results.StatusCode(400);
            }

            var ev = JsonNode.Parse(body) as JsonObject;
            if (ev?["id"]?.GetValue<string>() is not { } id || ev["type"]?.GetValue<string>() is not { } type)
            {
                return Results.StatusCode(400);
            }

            await using var cmd = appDb.CreateCommand("INSERT INTO stripe_events (id, type, payload) VALUES ($1, $2, $3::jsonb) ON CONFLICT (id) DO NOTHING");
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = type });
            cmd.Parameters.Add(new() { Value = System.Text.Encoding.UTF8.GetString(body) });
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
            return Results.Ok();
        }).ExcludeFromDescription();
    }

    /// <summary>Cobrança do próprio tenant (sessão de dono, admin ou financeiro: escopo billing:manage).</summary>
    public static RouteGroupBuilder MapBilling(this RouteGroupBuilder v1)
    {
        v1.MapGet("/cobranca", BillingStatusAsync).WithMetadata(new RequiredScope(Scopes.BillingManage)).ExcludeFromDescription();
        v1.MapPost("/cobranca/checkout", CheckoutAsync).WithMetadata(new RequiredScope(Scopes.BillingManage)).ExcludeFromDescription();
        v1.MapPost("/cobranca/portal", PortalAsync).WithMetadata(new RequiredScope(Scopes.BillingManage)).ExcludeFromDescription();
        return v1;
    }

    private sealed record TenantBilling(string Name, string Root, string Plan, string? Customer, string? Subscription, string? Status, DateTimeOffset? PeriodEnd);

    private static Task<TenantBilling> TenantAsync(HttpContext http, ITenantDb db) =>
        db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT name, root_cnpj, plan_code, billing_customer_ref, stripe_subscription_id, subscription_status, current_period_end FROM tenants");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return new TenantBilling(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));
        }, http.RequestAborted);

    private static async Task<IResult> BillingStatusAsync(HttpContext http, ITenantDb db, StripeClient stripe, TimeProvider clock)
    {
        var t = await TenantAsync(http, db);
        var invoices = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object('id', id, 'numero', number, 'situacao', status, 'valor_centavos', amount_due_cents,
                         'pago_centavos', amount_paid_cents, 'link', hosted_invoice_url, 'pdf', invoice_pdf, 'inicio', period_start, 'fim', period_end,
                         'criada_em', created_at) ORDER BY created_at DESC), '[]')::text
                  FROM (SELECT * FROM invoices ORDER BY created_at DESC LIMIT 24) x
                """);
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);
        var caller = http.GetCaller();
        var extra = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT t.overage_cap_cents, t.cnpj_verified_at, t.cnpj_verification_method, t.payment_failed_at, t.status,
                       (SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = 'DOC_AUDIT'),
                       (SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = 'SIMULATION')
                  FROM tenants t
                """);
            cmd.Parameters.Add(new() { Value = TaxGateway.Api.Simulation.SimulationService.BillingMonth(clock.GetUtcNow()), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (Cap: r.GetInt64(0), Verified: r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1), Method: r.IsDBNull(2) ? null : r.GetString(2),
                Failed: r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3), Status: r.GetString(4), Docs: r.GetInt64(5), Sims: r.GetInt64(6));
        }, http.RequestAborted);
        var overage = QuotaFilter.OverageCents(caller.Limits, extra.Docs, extra.Sims);
        return Results.Ok(new JsonObject
        {
            ["plano"] = t.Plan,
            ["assinatura"] = t.Subscription is null ? null : new JsonObject { ["situacao"] = t.Status, ["renova_em"] = t.PeriodEnd },
            ["pagamento_online"] = stripe.Enabled,
            ["faturas"] = JsonNode.Parse(invoices),
            ["excedente"] = new JsonObject
            {
                ["teto_centavos"] = extra.Cap,
                ["usado_centavos"] = (long)Math.Round(overage),
                ["franquia_rigida"] = caller.Limits.HardQuota,
                ["preco_documento_milicentavos"] = caller.Limits.OverageDocMillicents,
                ["preco_simulacao_milicentavos"] = caller.Limits.OverageSimMillicents,
            },
            ["verificacao"] = new JsonObject { ["verificada"] = extra.Verified is not null, ["verificada_em"] = extra.Verified, ["metodo"] = extra.Method },
            ["inadimplencia"] = extra.Failed is null ? null : new JsonObject
            {
                ["desde"] = extra.Failed,
                ["situacao"] = extra.Status,
                ["restringe_em"] = extra.Failed.Value.AddDays(10),
                ["suspende_em"] = extra.Failed.Value.AddDays(15),
            },
        });
    }

    private sealed record PaidPlan(string Code, string Fixed, string Docs, string? Cnpjs, bool SelfService, bool Active, string? Sims = null);

    private static async Task<PaidPlan?> PaidPlanAsync(NpgsqlDataSource appDb, string code, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("""
            SELECT code, stripe_price_id, stripe_metered_price_id, stripe_cnpj_price_id, self_service, active, stripe_sim_price_id FROM plans WHERE code = $1 AND price_cents > 0
            """);
        cmd.Parameters.Add(new() { Value = code });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        if (r.IsDBNull(1) || r.IsDBNull(2))
        {
            throw new ApiProblemException(503, "ERR_PAGAMENTO_INDISPONIVEL", "Plano ainda não disponível para contratação online", "Tente em alguns minutos ou fale com vendas.");
        }

        return new PaidPlan(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    /// <summary>
    /// Contratar (sem assinatura): Checkout da Stripe. Trocar de plano (com assinatura ativa): troca os preços da assinatura
    /// com cobrança proporcional. Só planos públicos de contratação online; Enterprise e OEM passam por vendas.
    /// </summary>
    private static async Task<IResult> CheckoutAsync(HttpContext http, ITenantDb db, NpgsqlDataSource appDb, StripeClient stripe, IOptions<AccountOptions> accounts)
    {
        if (!stripe.Enabled)
        {
            return ApiProblem.Result(503, "ERR_PAGAMENTO_INDISPONIVEL", "Pagamento online não configurado", "Fale com vendas para contratar.");
        }

        // D-R10: plano pago só com a titularidade do CNPJ verificada.
        if (http.GetCaller().Billing is not { CnpjVerified: true })
        {
            return ApiProblem.Result(403, "ERR_EMPRESA_NAO_VERIFICADA", "Verifique a empresa antes de contratar",
                "Confirme a titularidade do CNPJ em Plano e cobrança (certificado e-CNPJ, código no e-mail do cadastro da Receita ou análise do suporte).");
        }

        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
        var code = body?["plano"]?.GetValue<string>() ?? "";
        var plan = await PaidPlanAsync(appDb, code, http.RequestAborted);
        if (plan is null || !plan.SelfService || !plan.Active)
        {
            return ApiProblem.Result(422, "ERR_PLANO_INVALIDO", "Plano não disponível para contratação online", "Escolha um plano da página de planos ou fale com vendas.");
        }

        var t = await TenantAsync(http, db);
        var prices = new List<(string, bool)> { (plan.Fixed, false), (plan.Docs, true) };
        if (plan.Cnpjs is not null)
        {
            prices.Add((plan.Cnpjs, true));
        }

        if (plan.Sims is not null)
        {
            prices.Add((plan.Sims, true));   // D-R15
        }

        // D-R02: assinatura existente e não encerrada (inclusive incompleta ou inadimplente) é resolvida no portal de
        // cobrança — um segundo checkout criaria uma segunda assinatura para o mesmo cliente.
        if (t.Subscription is not null && t.Status is "incomplete" or "unpaid" or "paused")
        {
            return ApiProblem.Result(409, "ERR_ASSINATURA_PENDENTE", "Há uma assinatura com pagamento pendente",
                "Conclua ou cancele o pagamento em \"Gerenciar pagamento\" (portal de cobrança) antes de assinar de novo.");
        }

        if (t.Subscription is not null && t.Status is "active" or "trialing" or "past_due")
        {
            await stripe.ChangeSubscriptionPlanAsync(t.Subscription, plan.Code, prices, http.RequestAborted);
            return Results.Ok(new JsonObject { ["acao"] = "trocado", ["mensagem"] = "Plano trocado. A diferença proporcional entra na próxima fatura." });
        }

        var tenant = http.GetCaller().Identity.Tenant.Value;
        var customer = t.Customer;
        if (customer is null)
        {
            var user = SessionAuthenticator.User(http);
            customer = await stripe.CreateCustomerAsync(tenant, t.Name, user?.Session.Email, t.Root, http.RequestAborted);
            await using var set = appDb.CreateCommand("SELECT app.set_billing_customer($1, $2)");
            set.Parameters.Add(new() { Value = tenant });
            set.Parameters.Add(new() { Value = customer });
            customer = (string?)await set.ExecuteScalarAsync(http.RequestAborted) ?? customer;
        }

        var baseUrl = accounts.Value.PublicBaseUrl.TrimEnd('/');
        var url = await stripe.CreateCheckoutSessionAsync(customer, tenant, plan.Code, prices,
            $"{baseUrl}/portal/cobranca?contratado=1", $"{baseUrl}/portal/cobranca?cancelado=1", http.RequestAborted);
        return Results.Ok(new JsonObject { ["acao"] = "checkout", ["url"] = url });
    }

    private static async Task<IResult> PortalAsync(HttpContext http, ITenantDb db, StripeClient stripe, IOptions<AccountOptions> accounts)
    {
        var t = await TenantAsync(http, db);
        if (!stripe.Enabled || t.Customer is null)
        {
            return ApiProblem.Result(409, "ERR_SEM_ASSINATURA", "Ainda não há cobrança online para esta empresa", "Contrate um plano primeiro.");
        }

        var url = await stripe.CreatePortalSessionAsync(t.Customer, $"{accounts.Value.PublicBaseUrl.TrimEnd('/')}/portal/cobranca", http.RequestAborted);
        return Results.Ok(new JsonObject { ["url"] = url });
    }

    // ---------------------------------------------------------------------------------------------------- backoffice

    /// <summary>Cadastro de planos pelo operador comercial: lista, edição (função com histórico) e plano por tenant.</summary>
    public static RouteGroupBuilder MapAdminPlans(this RouteGroupBuilder admin)
    {
        admin.MapGet("/planos", async (AdminDataSource db, CancellationToken ct) =>
        {
            await using var cmd = db.DataSource.CreateCommand($"SELECT coalesce(json_agg(x ORDER BY x.sort_order, x.code), '[]')::text FROM (SELECT {PlanColumns} FROM plans) x");
            return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapGet("/planos/{code}/historico", async (string code, AdminDataSource db, CancellationToken ct) =>
        {
            await using var cmd = db.DataSource.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object('em', changed_at, 'por', changed_by, 'acao', action, 'dados', data) ORDER BY changed_at DESC), '[]')::text
                  FROM plan_history WHERE code = $1
                """);
            cmd.Parameters.Add(new() { Value = code });
            return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapPut("/planos/{code}", SavePlanAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapPost("/tenants/{id:guid}/plano", async (Guid id, HttpContext http, AdminDataSource db, AccountService accounts, IConnectionMultiplexer valkey) =>
        {
            var b = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
            var code = b?["plano"]?.GetValue<string>() ?? "";
            await using var cmd = db.DataSource.CreateCommand("UPDATE tenants SET plan_code = $2 WHERE id = $1 AND EXISTS (SELECT 1 FROM plans WHERE code = $2) RETURNING id");
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = code });
            if (await cmd.ExecuteScalarAsync(http.RequestAborted) is null)
            {
                return ApiProblem.Result(404, "ERR_NOT_FOUND", "Tenant ou plano não encontrado");
            }

            await TenantProfileCache.PublishChangedAsync(valkey, id);   // limites do plano novo valem na hora
            await accounts.AuditAsync(null, "operator", AdminKeyFilter.Get(http).Name, "tenant.plano_alterado", id.ToString(), http.Connection.RemoteIpAddress?.ToString(),
                new JsonObject { ["plano"] = code }, http.RequestAborted);
            return Results.Ok(new JsonObject { ["tenant_id"] = id, ["plano"] = code });
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        return admin;
    }

    private static readonly HashSet<string> NumericFields = ["price_cents", "included_docs", "included_simulations", "included_cnpjs", "max_users",
        "overage_doc_millicents", "overage_sim_millicents", "cnpj_price_cents", "rpm", "burst", "concurrency", "batch_max_docs", "batch_max_bytes", "sort_order", "per_cnpj_rpm"];

    private static readonly HashSet<string> BoolFields = ["bill_schema_errors", "hard_quota", "dfe_enabled", "is_public", "self_service", "active"];

    private static async Task<IResult> SavePlanAsync(string code, HttpContext http, AdminDataSource db, AccountService accounts, IConnectionMultiplexer valkey)
    {
        if (await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) is not JsonObject body)
        {
            return ApiProblem.Result(400, "ERR_JSON_INVALIDO", "Corpo JSON inválido");
        }

        var clean = new JsonObject();
        var errors = new List<string>();
        foreach (var (k, v) in body)
        {
            if (NumericFields.Contains(k))
            {
                if (v is null && k is "included_cnpjs" or "max_users")
                {
                    clean[k] = null;   // ilimitado
                }
                else if (v is JsonValue n && n.TryGetValue<long>(out var x) && x >= 0)
                {
                    clean[k] = x;
                }
                else
                {
                    errors.Add($"{k} deve ser um inteiro ≥ 0");
                }
            }
            else if (BoolFields.Contains(k))
            {
                if (v is JsonValue b && b.TryGetValue<bool>(out var x))
                {
                    clean[k] = x;
                }
                else
                {
                    errors.Add($"{k} deve ser true/false");
                }
            }
            else if (k is "name" or "description")
            {
                var s = v?.GetValue<string>()?.Trim() ?? "";
                if (k == "name" && s.Length is < 2 or > 60)
                {
                    errors.Add("name deve ter de 2 a 60 caracteres");
                }

                clean[k] = s.Length > 500 ? s[..500] : s;
            }
            else if (k == "queue_priority")
            {
                var s = v?.GetValue<string>();
                if (s is not ("low" or "normal" or "high" or "realtime"))
                {
                    errors.Add("queue_priority: low, normal, high ou realtime");
                }

                clean[k] = s;
            }
            else if (k == "features")
            {
                if (v is JsonArray a && a.Count <= 12 && a.All(x => x is JsonValue j && j.TryGetValue<string>(out var str) && str.Length is > 0 and <= 120))
                {
                    clean[k] = a.DeepClone();
                }
                else
                {
                    errors.Add("features: até 12 textos de até 120 caracteres");
                }
            }
            else
            {
                errors.Add($"campo desconhecido: {k}");
            }
        }

        if (clean["burst"] is JsonValue burst && clean["rpm"] is JsonValue rpm && burst.GetValue<long>() > rpm.GetValue<long>())
        {
            errors.Add("burst não pode ser maior que rpm");
        }

        if (errors.Count > 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Dados do plano inválidos", string.Join("; ", errors));
        }

        var operatorName = AdminKeyFilter.Get(http).Name;
        try
        {
            await using var cmd = db.DataSource.CreateCommand("SELECT app.save_plan($1, $2::jsonb, $3)");
            cmd.Parameters.Add(new() { Value = code.ToUpperInvariant() });
            cmd.Parameters.Add(new() { Value = clean.ToJsonString() });
            cmd.Parameters.Add(new() { Value = operatorName });
            await cmd.ExecuteScalarAsync(http.RequestAborted);
        }
        catch (PostgresException ex) when (ex.SqlState == "22023")
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Código de plano inválido", "Use letras maiúsculas, números e _ (ex.: PROFISSIONAL).");
        }

        // Todas as instâncias recarregam os parâmetros (limites e franquia valem na hora); a Stripe é sincronizada pelos workers.
        try
        {
            await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(ParamSnapshotService.ChangedChannel), "plans");
        }
        catch (RedisException)
        {
            // sem pub/sub, a releitura periódica (30 s) aplica
        }

        await accounts.AuditAsync(null, "operator", operatorName, "plano.salvo", code.ToUpperInvariant(), http.Connection.RemoteIpAddress?.ToString(), clean, http.RequestAborted);
        return Results.Ok(new JsonObject { ["codigo"] = code.ToUpperInvariant(), ["mensagem"] = "Plano salvo. Preços novos chegam à Stripe em até 5 minutos." });
    }
}
