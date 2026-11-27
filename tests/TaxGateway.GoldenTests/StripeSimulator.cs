using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Simulador local da API da Stripe (HttpListener em porta livre), no formato que o <c>StripeClient</c> usa: corpo
/// form-urlencoded, resposta JSON, <c>Idempotency-Key</c> (mesma chave e mesmos parâmetros devolvem a resposta guardada;
/// parâmetros diferentes = erro, como na Stripe) e deduplicação de eventos de medidor pelo <c>identifier</c>. Guarda
/// cada requisição recebida para os testes conferirem o que foi enviado; falhas podem ser injetadas por predicado.
/// </summary>
public sealed class StripeSimulator : IAsyncDisposable
{
    public const string SecretKey = "sk_test_golden";

    /// <summary>Requisição recebida (form na ordem enviada).</summary>
    public sealed record Request(string Method, string Path, string Query, IReadOnlyList<KeyValuePair<string, string>> Form, string? IdempotencyKey,
        string? Authorization, string? StripeVersion)
    {
        public string? this[string key] => Form.LastOrDefault(p => p.Key == key).Value;

        public bool Is(string method, string path) => Method == method && Path == path;
    }

    /// <summary>Evento de medidor aceito (um por identificador).</summary>
    public sealed record MeterEvent(string EventName, string Customer, long Value, string Identifier, long Timestamp);

    private sealed record Fault(Func<Request, bool> When, int Status, bool EmptyBody)
    {
        public int Remaining { get; set; }
    }

    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<Request> _calls = new();
    private readonly Lock _gate = new();
    private readonly List<Fault> _faults = [];
    private readonly Dictionary<string, (string Signature, int Status, string Body)> _idempotent = [];
    private readonly List<JsonObject> _meters = [];
    private readonly Dictionary<string, MeterEvent> _meterEvents = [];
    private readonly Dictionary<string, JsonObject> _invoices = [];
    private readonly Dictionary<string, List<string>> _subscriptions = [];
    private int _seq;

    public StripeSimulator()
    {
        var port = FreeTcpPort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public Uri BaseUri { get; }

    /// <summary>Configuração para ligar a Stripe numa instância da API ou dos workers.</summary>
    public Dictionary<string, string?> Settings => new()
    {
        ["Stripe:SecretKey"] = SecretKey,
        ["Stripe:ApiBase"] = BaseUri.ToString(),
    };

    public IReadOnlyList<Request> Calls => [.. _calls];

    public IReadOnlyList<Request> CallsTo(string method, string path) => [.. _calls.Where(c => c.Is(method, path))];

    public IReadOnlyList<JsonObject> Meters
    {
        get
        {
            lock (_gate)
            {
                return [.. _meters.Select(m => (JsonObject)m.DeepClone())];
            }
        }
    }

    public IReadOnlyList<MeterEvent> MeterEvents
    {
        get
        {
            lock (_gate)
            {
                return [.. _meterEvents.Values];
            }
        }
    }

    /// <summary>Próximas <paramref name="times"/> requisições que casarem com o predicado falham com o status dado.</summary>
    public void FailWhen(Func<Request, bool> when, int status = 500, int times = 1, bool emptyBody = false)
    {
        lock (_gate)
        {
            _faults.Add(new Fault(when, status, emptyBody) { Remaining = times });
        }
    }

    /// <summary>Assinatura existente na "Stripe" com os itens dados (para a troca de plano).</summary>
    public void AddSubscription(string id, params string[] items)
    {
        lock (_gate)
        {
            _subscriptions[id] = [.. items];
        }
    }

    public IReadOnlyList<string> SubscriptionItems(string id)
    {
        lock (_gate)
        {
            return [.. _subscriptions[id]];
        }
    }

    /// <summary>Anula a fatura (some da busca por chave idempotente, como uma fatura "void").</summary>
    public void VoidInvoice(string id)
    {
        lock (_gate)
        {
            _invoices[id]["status"] = "void";
        }
    }

    public JsonObject Invoice(string id)
    {
        lock (_gate)
        {
            return (JsonObject)_invoices[id].DeepClone();
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            try
            {
                string text;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    text = await reader.ReadToEndAsync();
                }

                var call = new Request(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query, ParseForm(text),
                    ctx.Request.Headers["Idempotency-Key"], ctx.Request.Headers["Authorization"], ctx.Request.Headers["Stripe-Version"]);
                _calls.Enqueue(call);
                var (status, body) = Handle(call);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
                // cliente desistiu da requisição
            }
        }
    }

    private static List<KeyValuePair<string, string>> ParseForm(string text) =>
        text.Length == 0
            ? []
            : [.. text.Split('&').Select(p => p.Split('=', 2)).Select(kv => new KeyValuePair<string, string>(WebUtility.UrlDecode(kv[0]), kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : ""))];

    private static string Error(string message, string code) => new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["code"] = code } }.ToJsonString();

    private (int Status, string Body) Handle(Request call)
    {
        lock (_gate)
        {
            if (call.Authorization != $"Bearer {SecretKey}")
            {
                return (401, Error("Invalid API Key provided", "api_key_invalid"));
            }

            var fault = _faults.FirstOrDefault(f => f.Remaining > 0 && f.When(call));
            if (fault is not null)
            {
                fault.Remaining--;
                return (fault.Status, fault.EmptyBody ? "" : Error("Falha simulada da Stripe", "simulated_failure"));
            }

            var signature = $"{call.Method} {call.Path} {string.Join('&', call.Form.Select(p => $"{p.Key}={p.Value}"))}";
            if (call.IdempotencyKey is { } key && _idempotent.TryGetValue(key, out var stored))
            {
                return stored.Signature == signature
                    ? (stored.Status, stored.Body)
                    : (400, Error("Keys for idempotent requests can only be used with the same parameters they were first used with.", "idempotency_error"));
            }

            var result = Route(call);
            if (call.IdempotencyKey is { } k && result.Status < 500)
            {
                _idempotent[k] = (signature, result.Status, result.Body);
            }

            return result;
        }
    }

    private string Next(string prefix) => $"{prefix}_sim{Interlocked.Increment(ref _seq):D5}";

    private (int Status, string Body) Route(Request c)
    {
        var segments = c.Path.Trim('/').Split('/');
        switch (c.Method, segments)
        {
            case ("GET", ["v1", "billing", "meters"]):
                return (200, new JsonObject { ["object"] = "list", ["data"] = new JsonArray([.. _meters.Select(m => (JsonNode)m.DeepClone())]) }.ToJsonString());
            case ("POST", ["v1", "billing", "meters"]):
                {
                    var meter = new JsonObject
                    {
                        ["id"] = Next("mtr"),
                        ["event_name"] = c["event_name"],
                        ["display_name"] = c["display_name"],
                        ["default_aggregation"] = new JsonObject { ["formula"] = c["default_aggregation[formula]"] },
                    };
                    _meters.Add(meter);
                    return (200, meter.ToJsonString());
                }

            case ("POST", ["v1", "billing", "meter_events"]):
                {
                    var identifier = c["identifier"]!;
                    // A Stripe deduplica pelo identificador: o reenvio não soma de novo.
                    _meterEvents.TryAdd(identifier, new MeterEvent(c["event_name"]!, c["payload[stripe_customer_id]"]!,
                        long.Parse(c["payload[value]"]!, CultureInfo.InvariantCulture), identifier, long.Parse(c["timestamp"]!, CultureInfo.InvariantCulture)));
                    return (200, new JsonObject { ["object"] = "billing.meter_event", ["identifier"] = identifier }.ToJsonString());
                }

            case ("POST", ["v1", "products"]):
                return (200, new JsonObject { ["id"] = Next("prod"), ["name"] = c["name"] }.ToJsonString());
            case ("POST", ["v1", "prices"]):
                return (200, new JsonObject { ["id"] = Next("price"), ["product"] = c["product"] }.ToJsonString());
            case ("POST", ["v1", "customers"]):
                return (200, new JsonObject { ["id"] = Next("cus"), ["name"] = c["name"] }.ToJsonString());
            case ("POST", ["v1", "customers", var customer, "tax_ids"]):
                return (200, new JsonObject { ["id"] = Next("txi"), ["customer"] = customer, ["value"] = c["value"] }.ToJsonString());
            case ("POST", ["v1", "checkout", "sessions"]):
                {
                    var id = Next("cs");
                    return (200, new JsonObject { ["id"] = id, ["url"] = $"https://checkout.stripe.test/c/{id}" }.ToJsonString());
                }

            case ("POST", ["v1", "billing_portal", "sessions"]):
                {
                    var id = Next("bps");
                    return (200, new JsonObject { ["id"] = id, ["url"] = $"https://billing.stripe.test/p/{id}" }.ToJsonString());
                }

            case ("GET", ["v1", "subscriptions", var sub]):
                return _subscriptions.TryGetValue(sub, out var items)
                    ? (200, new JsonObject
                    {
                        ["id"] = sub,
                        ["items"] = new JsonObject { ["data"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject { ["id"] = i })]) },
                    }.ToJsonString())
                    : (404, Error($"No such subscription: '{sub}'", "resource_missing"));
            case ("POST", ["v1", "subscriptions", var sub]):
                {
                    if (!_subscriptions.TryGetValue(sub, out var current))
                    {
                        return (404, Error($"No such subscription: '{sub}'", "resource_missing"));
                    }

                    for (var n = 0; c[$"items[{n}][id]"] is not null || c[$"items[{n}][price]"] is not null; n++)
                    {
                        if (c[$"items[{n}][deleted]"] == "true")
                        {
                            current.Remove(c[$"items[{n}][id]"]!);
                        }
                        else if (c[$"items[{n}][price]"] is not null)
                        {
                            current.Add(Next("si"));
                        }
                    }

                    return (200, new JsonObject { ["id"] = sub }.ToJsonString());
                }

            case ("POST", ["v1", "invoices"]):
                {
                    var invoice = new JsonObject
                    {
                        ["id"] = Next("in"),
                        ["customer"] = c["customer"],
                        ["status"] = "draft",
                        ["amount_due"] = 0L,
                        ["due_date"] = long.Parse(c["due_date"]!, CultureInfo.InvariantCulture),
                        ["description"] = c["description"],
                        ["idempotency_key"] = c["metadata[idempotency_key]"],
                    };
                    _invoices[invoice["id"]!.GetValue<string>()] = invoice;
                    return (200, invoice.ToJsonString());
                }

            case ("POST", ["v1", "invoiceitems"]):
                {
                    if (!_invoices.TryGetValue(c["invoice"] ?? "", out var inv))
                    {
                        return (404, Error("No such invoice", "resource_missing"));
                    }

                    inv["amount_due"] = inv["amount_due"]!.GetValue<long>() + long.Parse(c["amount"]!, CultureInfo.InvariantCulture);
                    return (200, new JsonObject { ["id"] = Next("ii") }.ToJsonString());
                }

            case ("POST", ["v1", "invoices", var id, "finalize"]):
                if (!_invoices.TryGetValue(id, out var fin))
                {
                    return (404, Error("No such invoice", "resource_missing"));
                }

                fin["status"] = "open";
                return (200, "");   // corpo vazio: o cliente aceita (só o status importa aqui)
            case ("GET", ["v1", "invoices", "search"]):
                {
                    var query = Uri.UnescapeDataString(c.Query.TrimStart('?').Split('=', 2)[1]);
                    var wanted = query[(query.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim('\'');   // metadata['idempotency_key']:'<chave>'
                    var found = _invoices.Values.Where(i => i["idempotency_key"]?.GetValue<string>() == wanted).Select(i => (JsonNode)i.DeepClone());
                    return (200, new JsonObject { ["object"] = "search_result", ["data"] = new JsonArray([.. found]) }.ToJsonString());
                }

            default:
                return (404, Error($"Unrecognized request URL ({c.Method}: {c.Path})", "resource_missing"));
        }
    }

    private static int FreeTcpPort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}
