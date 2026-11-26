using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

namespace TaxGateway.Infrastructure.Billing;

public sealed class StripeOptions
{
    public const string Section = "Stripe";

    /// <summary>Chave secreta (sk_live_… / sk_test_…). Vazia = pagamento online desligado nesta instalação.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Segredo de assinatura do endpoint de webhook (whsec_…).</summary>
    public string WebhookSecret { get; set; } = "";

    public Uri ApiBase { get; set; } = new("https://api.stripe.com/");

    /// <summary>Versão fixa da API (Billing Meters exigem versão recente). Vazia = padrão da conta.</summary>
    public string ApiVersion { get; set; } = "2025-03-31.basil";

    public string Currency { get; set; } = "brl";
    public string DocumentsMeterEvent { get; set; } = "taxgateway_documentos";
    public string CnpjsMeterEvent { get; set; } = "taxgateway_cnpjs";

    public bool Enabled => SecretKey.Length > 0;
}

public sealed class StripeException(int status, string message, string? code) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>
/// Cliente da API da Stripe (D-C03): HTTP direto com corpo form-urlencoded (sem SDK), versão da API fixa e
/// <c>Idempotency-Key</c> nas criações — reenviar depois de uma falha de rede não duplica cliente, preço nem cobrança.
/// </summary>
public sealed class StripeClient(HttpClient http, IOptions<StripeOptions> options)
{
    private StripeOptions O => options.Value;

    public bool Enabled => O.Enabled;

    public async Task<JsonObject> SendAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        if (!O.Enabled)
        {
            throw new StripeException(503, "Pagamento online não configurado nesta instalação (Stripe:SecretKey).", "not_configured");
        }

        using var req = new HttpRequestMessage(method, new Uri(O.ApiBase, path.TrimStart('/')));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.SecretKey);
        if (O.ApiVersion.Length > 0)
        {
            req.Headers.Add("Stripe-Version", O.ApiVersion);
        }

        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (form is not null)
        {
            req.Content = new FormUrlEncodedContent(form);
        }

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = text.Length == 0 ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        if (!resp.IsSuccessStatusCode)
        {
            var err = json["error"];
            throw new StripeException((int)resp.StatusCode, err?["message"]?.GetValue<string>() ?? $"Stripe HTTP {(int)resp.StatusCode}", err?["code"]?.GetValue<string>());
        }

        return json;
    }

    private static List<KeyValuePair<string, string>> Form(params (string K, string? V)[] pairs) =>
        [.. pairs.Where(p => p.V is not null).Select(p => new KeyValuePair<string, string>(p.K, p.V!))];

    private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);

    public async Task<string> CreateCustomerAsync(Guid tenant, string name, string? email, string? taxId, CancellationToken ct)
    {
        var form = Form(("name", name), ("email", email), ("metadata[tenant_id]", tenant.ToString()), ("preferred_locales[0]", "pt-BR"));
        var c = await SendAsync(HttpMethod.Post, "v1/customers", form, $"customer:{tenant}", ct).ConfigureAwait(false);
        var id = c["id"]!.GetValue<string>();
        if (taxId is { Length: 14 })
        {
            try
            {
                await SendAsync(HttpMethod.Post, $"v1/customers/{id}/tax_ids", Form(("type", "br_cnpj"), ("value", FormatCnpj(taxId))), $"taxid:{tenant}", ct).ConfigureAwait(false);
            }
            catch (StripeException)
            {
                // CNPJ alfanumérico (2026) ainda pode não ser aceito pela Stripe: a cobrança segue sem o tax id.
            }
        }

        return id;
    }

    private static string FormatCnpj(string c) => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}";

    /// <summary>Medidor (Billing Meter) por nome de evento; cria se não existir.</summary>
    public async Task<string> EnsureMeterAsync(string eventName, string displayName, string aggregation, CancellationToken ct)
    {
        var list = await SendAsync(HttpMethod.Get, "v1/billing/meters?limit=100&status=active", ct: ct).ConfigureAwait(false);
        var found = list["data"]?.AsArray().FirstOrDefault(m => m?["event_name"]?.GetValue<string>() == eventName);
        if (found is not null)
        {
            return found["id"]!.GetValue<string>();
        }

        var meter = await SendAsync(HttpMethod.Post, "v1/billing/meters", Form(
            ("display_name", displayName), ("event_name", eventName), ("default_aggregation[formula]", aggregation),
            ("customer_mapping[type]", "by_id"), ("customer_mapping[event_payload_key]", "stripe_customer_id"),
            ("value_settings[event_payload_key]", "value")), $"meter:{eventName}", ct).ConfigureAwait(false);
        return meter["id"]!.GetValue<string>();
    }

    public async Task<string> CreateProductAsync(string planCode, string name, string description, CancellationToken ct)
    {
        var p = await SendAsync(HttpMethod.Post, "v1/products", Form(("name", $"TaxGateway {name}"), ("description", description.Length > 0 ? description : null),
            ("metadata[plan_code]", planCode)), $"product:{planCode}", ct).ConfigureAwait(false);
        return p["id"]!.GetValue<string>();
    }

    /// <summary>Mensalidade fixa (preço recorrente mensal).</summary>
    public async Task<string> CreateFixedPriceAsync(string product, string planCode, long cents, string version, CancellationToken ct)
    {
        var p = await SendAsync(HttpMethod.Post, "v1/prices", Form(("product", product), ("currency", O.Currency), ("unit_amount", S(cents)),
            ("recurring[interval]", "month"), ("nickname", $"{planCode} mensalidade"), ("metadata[plan_code]", planCode), ("metadata[kind]", "fixed")),
            $"price:{planCode}:fixed:{version}", ct).ConfigureAwait(false);
        return p["id"]!.GetValue<string>();
    }

    /// <summary>
    /// Excedente medido: preço graduado ligado ao medidor — as primeiras <paramref name="included"/> unidades do mês saem a
    /// zero (franquia) e as demais a <paramref name="unitMillicents"/> (milésimos de centavo).
    /// </summary>
    public async Task<string> CreateMeteredPriceAsync(string product, string planCode, string kind, string meterId, long included, long unitMillicents, string version, CancellationToken ct)
    {
        var unitCentsDecimal = (unitMillicents / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
        var p = await SendAsync(HttpMethod.Post, "v1/prices", Form(("product", product), ("currency", O.Currency),
            ("recurring[interval]", "month"), ("recurring[usage_type]", "metered"), ("recurring[meter]", meterId),
            ("billing_scheme", "tiered"), ("tiers_mode", "graduated"),
            ("tiers[0][up_to]", S(Math.Max(included, 0))), ("tiers[0][unit_amount]", "0"),
            ("tiers[1][up_to]", "inf"), ("tiers[1][unit_amount_decimal]", unitCentsDecimal),
            ("nickname", $"{planCode} {kind} excedente"), ("metadata[plan_code]", planCode), ("metadata[kind]", kind)),
            $"price:{planCode}:{kind}:{version}", ct).ConfigureAwait(false);
        return p["id"]!.GetValue<string>();
    }

    public async Task<string> CreateCheckoutSessionAsync(string customer, Guid tenant, string planCode, IReadOnlyList<(string Price, bool Metered)> prices,
        string successUrl, string cancelUrl, CancellationToken ct)
    {
        var form = Form(("mode", "subscription"), ("customer", customer), ("success_url", successUrl), ("cancel_url", cancelUrl),
            ("locale", "pt-BR"), ("client_reference_id", tenant.ToString()), ("metadata[tenant_id]", tenant.ToString()), ("metadata[plan_code]", planCode),
            ("subscription_data[metadata][tenant_id]", tenant.ToString()), ("subscription_data[metadata][plan_code]", planCode),
            ("allow_promotion_codes", "true"), ("billing_address_collection", "required"));
        for (var i = 0; i < prices.Count; i++)
        {
            form.Add(new($"line_items[{i}][price]", prices[i].Price));
            if (!prices[i].Metered)
            {
                form.Add(new($"line_items[{i}][quantity]", "1"));
            }
        }

        var s = await SendAsync(HttpMethod.Post, "v1/checkout/sessions", form, null, ct).ConfigureAwait(false);
        return s["url"]!.GetValue<string>();
    }

    public async Task<string> CreatePortalSessionAsync(string customer, string returnUrl, CancellationToken ct)
    {
        var s = await SendAsync(HttpMethod.Post, "v1/billing_portal/sessions", Form(("customer", customer), ("return_url", returnUrl), ("locale", "pt-BR")), null, ct).ConfigureAwait(false);
        return s["url"]!.GetValue<string>();
    }

    /// <summary>Troca de plano com assinatura ativa: substitui os itens (preços) e cobra a diferença proporcional.</summary>
    public async Task ChangeSubscriptionPlanAsync(string subscription, string planCode, IReadOnlyList<(string Price, bool Metered)> prices, CancellationToken ct)
    {
        var sub = await SendAsync(HttpMethod.Get, $"v1/subscriptions/{subscription}", ct: ct).ConfigureAwait(false);
        var items = sub["items"]?["data"]?.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList() ?? [];
        var form = Form(("proration_behavior", "create_prorations"), ("metadata[plan_code]", planCode));
        var n = 0;
        foreach (var id in items)
        {
            form.Add(new($"items[{n}][id]", id));
            form.Add(new($"items[{n}][deleted]", "true"));
            n++;
        }

        foreach (var (price, metered) in prices)
        {
            form.Add(new($"items[{n}][price]", price));
            if (!metered)
            {
                form.Add(new($"items[{n}][quantity]", "1"));
            }

            n++;
        }

        await SendAsync(HttpMethod.Post, $"v1/subscriptions/{subscription}", form, $"change:{subscription}:{planCode}:{string.Join(',', prices.Select(p => p.Price))}", ct).ConfigureAwait(false);
    }

    /// <summary>Evento de consumo para o medidor. O <paramref name="identifier"/> deduplica reenvios na Stripe.</summary>
    public Task SendMeterEventAsync(string eventName, string customer, long value, string identifier, DateTimeOffset at, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/billing/meter_events", Form(("event_name", eventName), ("payload[stripe_customer_id]", customer),
            ("payload[value]", S(value)), ("identifier", identifier), ("timestamp", S(at.ToUnixTimeSeconds()))), null, ct);

    /// <summary>Cobrança avulsa (ajuste de faturamento, contrato sem assinatura): item + fatura finalizada, idempotentes.</summary>
    public async Task<string> CreateOneOffInvoiceAsync(string customer, long cents, string description, string idempotencyKey, DateOnly dueDate, CancellationToken ct)
    {
        var inv = await SendAsync(HttpMethod.Post, "v1/invoices", Form(("customer", customer), ("collection_method", "send_invoice"),
            ("due_date", S(new DateTimeOffset(dueDate.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds())),
            ("auto_advance", "true"), ("metadata[idempotency_key]", idempotencyKey), ("description", description)), $"inv:{idempotencyKey}", ct).ConfigureAwait(false);
        var invoiceId = inv["id"]!.GetValue<string>();
        await SendAsync(HttpMethod.Post, "v1/invoiceitems", Form(("customer", customer), ("invoice", invoiceId), ("currency", O.Currency),
            ("amount", S(cents)), ("description", description)), $"item:{idempotencyKey}", ct).ConfigureAwait(false);
        await SendAsync(HttpMethod.Post, $"v1/invoices/{invoiceId}/finalize", [], $"fin:{idempotencyKey}", ct).ConfigureAwait(false);
        return invoiceId;
    }

    public async Task<(string Ref, long AmountCents)?> FindInvoiceAsync(string idempotencyKey, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"metadata['idempotency_key']:'{idempotencyKey.Replace("'", "", StringComparison.Ordinal)}'");
        var r = await SendAsync(HttpMethod.Get, $"v1/invoices/search?query={q}", ct: ct).ConfigureAwait(false);
        var first = r["data"]?.AsArray().FirstOrDefault(i => i?["status"]?.GetValue<string>() != "void");
        return first is null ? null : (first["id"]!.GetValue<string>(), first["amount_due"]!.GetValue<long>());
    }

    /// <summary>
    /// Assinatura do webhook da Stripe: header <c>Stripe-Signature: t=…,v1=…</c>, HMAC-SHA256 de "{t}.{corpo}" com o
    /// segredo whsec_ — o mesmo esquema dos webhooks do TaxGateway (reaproveita a verificação, com tolerância de 5 min).
    /// </summary>
    public static bool VerifyWebhook(string? header, byte[] body, string secret, DateTimeOffset now) =>
        header is not null && secret.Length > 0
        && Webhooks.WebhookSignature.Verify(header, body, secret, TimeSpan.FromMinutes(5), now);
}

/// <summary>
/// Cobranças avulsas pela Stripe (substitui o Asaas, D-C02): ajustes de faturamento (v2+) e clientes de contrato sem
/// assinatura. A mensalidade e o excedente dos planos com assinatura são cobrados pela própria Stripe.
/// </summary>
public sealed class StripeBillingProvider(StripeClient stripe) : IBillingProvider
{
    public string Name => "STRIPE";

    public Task<(string Ref, long AmountCents)?> FindAsync(string idempotencyKey, CancellationToken ct) => stripe.FindInvoiceAsync(idempotencyKey, ct);

    public async Task<string> ChargeAsync(BillingCharge charge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(charge);
        return await FindAsync(charge.IdempotencyKey, ct).ConfigureAwait(false) is { } existing
            ? existing.Ref
            : await stripe.CreateOneOffInvoiceAsync(charge.CustomerRef, charge.AmountCents, charge.Description, charge.IdempotencyKey, charge.DueDate, ct).ConfigureAwait(false);
    }
}

internal static class StripeText
{
    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
