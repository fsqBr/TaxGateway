using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Formats.Asn1;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using Npgsql;

using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using StackExchange.Redis;

using TaxGateway.Api.Http;
using TaxGateway.Core.Audit;
using TaxGateway.Core.Engines;
using TaxGateway.Core.Tenancy;
using TaxGateway.Identity;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Dfe;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.UnitTests;

/// <summary>Logger que guarda as mensagens formatadas (para conferir os avisos dos laços de fundo).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Desligado = nível filtrado (como em produção com Warning): nada é formatado nem guardado.</summary>
    public bool Enabled { get; init; } = true;

    public bool IsEnabled(LogLevel logLevel) => Enabled;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!Enabled)
        {
            return;   // como os provedores reais: o gerador só pula a chamada abaixo de Error
        }

        Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    public bool Has(LogLevel level, string fragment) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
}

/// <summary>Dependências que falham de verdade: Valkey e Postgres em portas sem ninguém escutando.</summary>
internal static class Dead
{
    public static IConnectionMultiplexer Valkey()
    {
        var o = new ConfigurationOptions { AbortOnConnectFail = false, ConnectTimeout = 300, ConnectRetry = 0, BacklogPolicy = BacklogPolicy.FailFast };
        o.EndPoints.Add(IPAddress.Loopback, ClosedPort());
        return ConnectionMultiplexer.Connect(o);
    }

    public static NpgsqlDataSource Db() =>
        NpgsqlDataSource.Create($"Host=127.0.0.1;Port={ClosedPort()};Username=x;Password=x;Database=x;Timeout=2;Pooling=false");

    public static int ClosedPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;   // liberada ao sair: ninguém escuta nela
    }

    public static async Task Until(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "Condição não atingida no prazo.");
            await Task.Delay(50);
        }
    }
}

/// <summary>HttpClientFactory com um handler programável (motor e cadastros falsos, sem rede).</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : IHttpClientFactory
{
    public ConcurrentQueue<string> Requests { get; } = new();

    public HttpClient CreateClient(string name) =>
        new(new Handler(this, respond)) { BaseAddress = new Uri("http://motor.teste.invalid/api/") };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(FakeHttp owner, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.Requests.Enqueue(request.RequestUri!.PathAndQuery);
            return respond(request);
        }
    }
}

/// <summary>D-P25: sonda HTTP mínima dos workers.</summary>
public sealed class WorkerHealthServerTests
{
    [Fact]
    public async Task Responde_live_e_ready_404_no_resto_e_libera_a_porta_no_stop()
    {
        var port = Dead.ClosedPort();
        var log = new ListLogger<WorkerHealthServer>();
        using var server = new WorkerHealthServer(port, log);
        await server.StartAsync(CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };

        using (var live = await http.GetAsync(new Uri("health/live", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("text/plain", live.Content.Headers.ContentType!.MediaType);
            Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        }

        using (var ready = await http.GetAsync(new Uri("health/ready", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using (var other = await http.GetAsync(new Uri("metrics?x=1", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
            Assert.Empty(await other.Content.ReadAsByteArrayAsync());
        }

        Assert.True(log.Has(LogLevel.Information, $":{port}/health/live"));
        await server.StopAsync(CancellationToken.None);
        Assert.True(server.ExecuteTask!.IsCompletedSuccessfully);   // o laço sai pelo Stop do listener, sem exceção

        // Depois do stop ninguém responde mais "Healthy" nessa porta.
        try
        {
            using var after = await http.GetAsync(new Uri("health/live", UriKind.Relative));
            Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
        }
        catch (HttpRequestException)
        {
            // conexão recusada: esperado
        }

        // Com o nível Information desligado a sonda funciona igual, sem log.
        var port2 = Dead.ClosedPort();
        var quiet = new ListLogger<WorkerHealthServer> { Enabled = false };
        using var server2 = new WorkerHealthServer(port2, quiet);
        await server2.StartAsync(CancellationToken.None);
        using (var live2 = await http.GetAsync(new Uri($"http://127.0.0.1:{port2}/health/live")))
        {
            Assert.Equal(HttpStatusCode.OK, live2.StatusCode);
        }

        await server2.StopAsync(CancellationToken.None);
        Assert.Empty(quiet.Entries);
    }
}

/// <summary>E-mail transacional por SMTP (D-U06) contra um servidor SMTP mínimo no próprio teste.</summary>
public sealed class SmtpEmailSenderTests
{
    private static readonly EmailMessage Message = new("cliente@exemplo.test", "Confirme seu cadastro", "Texto simples http://link.test/a",
        "<p>Corpo <b>HTML</b> do aviso</p>");

    private static SmtpEmailSender Sender(EmailOptions o, ListLogger<SmtpEmailSender>? log = null) => new(Options.Create(o), log ?? new());

    [Fact]
    public async Task Sem_servidor_configurado_so_registra_no_log()
    {
        var log = new ListLogger<SmtpEmailSender>();
        await Sender(new EmailOptions { SmtpHost = " " }, log).SendAsync(Message, CancellationToken.None);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("cliente@exemplo.test", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Texto simples", entry.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sender(new EmailOptions()).SendAsync(null!, CancellationToken.None));
        var quiet = new ListLogger<SmtpEmailSender> { Enabled = false };
        await Sender(new EmailOptions(), quiet).SendAsync(Message, CancellationToken.None);   // padrão: sem servidor
        Assert.Empty(quiet.Entries);
    }

    [Fact]
    public async Task Entrega_texto_e_html_com_remetente_configurado_e_autenticacao()
    {
        await using var smtp = new MiniSmtp(advertiseAuth: true);
        var sender = Sender(new EmailOptions
        {
            SmtpHost = "127.0.0.1",
            SmtpPort = smtp.Port,
            SmtpTls = false,
            SmtpUser = "usuario-smtp",
            SmtpPassword = "senha-smtp",
            From = "Avisos TaxGateway <avisos@taxgateway.test>",
        });

        await sender.SendAsync(Message, CancellationToken.None);
        var got = await smtp.Session.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("usuario-smtp", got.User);
        Assert.Equal("senha-smtp", got.Password);
        Assert.Contains("<avisos@taxgateway.test>", got.MailFrom, StringComparison.Ordinal);
        Assert.Contains("<cliente@exemplo.test>", Assert.Single(got.RcptTo), StringComparison.Ordinal);
        Assert.Contains("Subject: Confirme seu cadastro", got.Data, StringComparison.Ordinal);
        Assert.Contains("Avisos TaxGateway", got.Data, StringComparison.Ordinal);
        Assert.Contains("multipart/alternative", got.Data, StringComparison.Ordinal);
        Assert.Contains("text/html", got.Data, StringComparison.Ordinal);
        var decoded = MiniSmtp.DecodeParts(got.Data!);
        Assert.Contains("Texto simples http://link.test/a", decoded, StringComparison.Ordinal);
        Assert.Contains("<p>Corpo <b>HTML</b> do aviso</p>", decoded, StringComparison.Ordinal);
        Assert.Contains(got.Commands, c => c.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sem_usuario_nao_autentica()
    {
        await using var smtp = new MiniSmtp(advertiseAuth: true);
        await Sender(new EmailOptions { SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpTls = false }).SendAsync(Message, CancellationToken.None);
        var got = await smtp.Session.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(got.User);
        Assert.DoesNotContain(got.Commands, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("TaxGateway", got.Data, StringComparison.Ordinal);   // remetente padrão
    }

    [Fact]
    public async Task Falha_de_conexao_e_tls_indisponivel_viram_SmtpException()
    {
        var closed = Dead.ClosedPort();
        await Assert.ThrowsAsync<SmtpException>(() =>
            Sender(new EmailOptions { SmtpHost = "127.0.0.1", SmtpPort = closed, SmtpTls = false }).SendAsync(Message, CancellationToken.None));

        // TLS exigido (padrão) e o servidor não oferece STARTTLS: não manda em claro.
        await using var smtp = new MiniSmtp(advertiseAuth: false);
        await Assert.ThrowsAsync<SmtpException>(() =>
            Sender(new EmailOptions { SmtpHost = "127.0.0.1", SmtpPort = smtp.Port }).SendAsync(Message, CancellationToken.None));
        var got = await smtp.Session.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(got.Data);
        Assert.DoesNotContain(got.Commands, c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Servidor SMTP de uma sessão (EHLO, AUTH LOGIN, MAIL, RCPT, DATA, QUIT).</summary>
    private sealed class MiniSmtp : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _auth;

        public MiniSmtp(bool advertiseAuth)
        {
            _auth = advertiseAuth;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Session = Task.Run(RunAsync);
        }

        public int Port { get; }

        public Task<Received> Session { get; }

        public sealed class Received
        {
            public List<string> Commands { get; } = [];
            public List<string> RcptTo { get; } = [];
            public string MailFrom { get; set; } = "";
            public string? Data { get; set; }
            public string? User { get; set; }
            public string? Password { get; set; }
        }

        private async Task<Received> RunAsync()
        {
            var got = new Received();
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 mini-smtp ESMTP");
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                got.Commands.Add(line);
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(_auth ? "250-mini-smtp\r\n250 AUTH LOGIN" : "250 mini-smtp");
                }
                else if (upper.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                    {
                        await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                        parts = [.. parts, (await reader.ReadLineAsync())!];
                    }

                    got.User = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                    got.Password = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!));
                    await writer.WriteLineAsync("235 2.7.0 ok");
                }
                else if (upper.StartsWith("MAIL FROM", StringComparison.Ordinal))
                {
                    got.MailFrom = line;
                    await writer.WriteLineAsync("250 ok");
                }
                else if (upper.StartsWith("RCPT TO", StringComparison.Ordinal))
                {
                    got.RcptTo.Add(line);
                    await writer.WriteLineAsync("250 ok");
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 fim com <CRLF>.<CRLF>");
                    var data = new StringBuilder();
                    while ((line = await reader.ReadLineAsync()) is not null and not ".")
                    {
                        data.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
                    }

                    got.Data = data.ToString();
                    await writer.WriteLineAsync("250 queued");
                }
                else if (upper == "QUIT")
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("502 not implemented");
                }
            }

            return got;
        }

        /// <summary>Texto das partes MIME (base64 e quoted-printable decodificados) para conferir o conteúdo.</summary>
        public static string DecodeParts(string data)
        {
            var sb = new StringBuilder(data);
            var lines = data.Split("\r\n");
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Content-Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var enc = lines[i].Split(':')[1].Trim().ToLowerInvariant();
                var j = i + 1;
                while (j < lines.Length && lines[j].Length > 0)
                {
                    j++;   // demais cabeçalhos da parte
                }

                var body = new StringBuilder();
                for (j++; j < lines.Length && !lines[j].StartsWith("--", StringComparison.Ordinal); j++)
                {
                    body.Append(lines[j]).Append(enc == "base64" ? "" : "\r\n");
                }

                sb.Append('\n').Append(enc switch
                {
                    "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(body.ToString())),
                    "quoted-printable" => QuotedPrintable(body.ToString()),
                    _ => body.ToString(),
                });
            }

            return sb.ToString();
        }

        private static string QuotedPrintable(string s)
        {
            var bytes = new List<byte>();
            var raw = s.Replace("=\r\n", "", StringComparison.Ordinal);
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '=' && i + 2 < raw.Length && Uri.IsHexDigit(raw[i + 1]) && Uri.IsHexDigit(raw[i + 2]))
                {
                    bytes.Add(Convert.ToByte(raw.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    bytes.Add((byte)raw[i]);
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await Session.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or TimeoutException or IOException)
            {
                // sessão encerrada pelo cliente ou nunca aberta
            }

            _listener.Dispose();
        }
    }
}

/// <summary>D-P17: métricas, traces por OTLP e log JSON correlacionável.</summary>
public sealed class TelemetryTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, bool prometheus)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(lb => services.AddTaxGatewayTelemetry(config, lb, "taxgateway-teste", prometheus));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Com_OTLP_e_log_JSON_liga_traces_exportador_e_formatador()
    {
        using var sp = Build(new()
        {
            ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:4317",
            ["Logging:Json"] = "true",
        }, prometheus: true);

        Assert.NotNull(sp.GetService<MeterProvider>());
        Assert.NotNull(sp.GetService<TracerProvider>());

        // O filtro do ASP.NET não gera trace para sondas e /metrics.
        var aspnet = sp.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(Options.DefaultName);
        Assert.NotNull(aspnet.Filter);
        Assert.False(aspnet.Filter!(Ctx("/health/live")));
        Assert.False(aspnet.Filter(Ctx("/metrics")));
        Assert.True(aspnet.Filter(Ctx("/v1/simulacoes")));

        var console = sp.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        Assert.Equal(ConsoleFormatterNames.Json, console.FormatterName);
        var json = sp.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue;
        Assert.True(json.IncludeScopes);
        Assert.True(json.UseUtcTimestamp);
        Assert.Equal("yyyy-MM-ddTHH:mm:ss.fffZ", json.TimestampFormat);
        var factory = sp.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value;
        Assert.Equal(ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId, factory.ActivityTrackingOptions);
    }

    [Fact]
    public void Variavel_padrao_do_OpenTelemetry_tambem_liga_os_traces_e_sem_ela_nao_ha_tracing()
    {
        using (var sp = Build(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318" }, prometheus: false))
        {
            Assert.NotNull(sp.GetService<TracerProvider>());
            Assert.NotEqual(ConsoleFormatterNames.Json, sp.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
        }

        using var none = Build([], prometheus: false);
        Assert.NotNull(none.GetService<MeterProvider>());
        Assert.Null(none.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(Options.DefaultName).Filter);
    }

    private static DefaultHttpContext Ctx(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        return ctx;
    }
}

/// <summary>Revogação por LCR (D-407): pontos de distribuição, parser da LCR e o laço de download em segundo plano.</summary>
public sealed class CrlCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static X509Certificate2 Root(string cn = "CN=Raiz LCR Teste, O=TaxGateway")
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(cn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(Now.AddDays(-30), Now.AddYears(5));
    }

    private static X509Certificate2 EcRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Raiz EC", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(Now.AddDays(-30), Now.AddYears(5));
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, X509Extension? dp)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=EMPRESA TESTE:11222333000181", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (dp is not null)
        {
            req.CertificateExtensions.Add(dp);
        }

        var serial = RandomNumberGenerator.GetBytes(9);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
        return req.Create(issuer, Now.AddDays(-10), Now.AddYears(1), serial);
    }

    private static X509Extension Dp(params string[] urls) => CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(urls);

    private static byte[] Crl(X509Certificate2 issuer, HashAlgorithmName hash, DateTimeOffset nextUpdate, params (X509Certificate2 Cert, DateTimeOffset When)[] revoked)
    {
        var b = new CertificateRevocationListBuilder();
        foreach (var (c, w) in revoked)
        {
            b.AddEntry(c, w);
        }

        return b.Build(issuer, BigInteger.One, nextUpdate, hash, issuer.GetRSAPrivateKey() is null ? null : RSASignaturePadding.Pkcs1, Now.AddHours(-1));
    }

    /// <summary>LCR v1 montada à mão: sem versão, datas em GeneralizedTime, sem nextUpdate.</summary>
    private static byte[] HandmadeCrl(X509Certificate2 issuer, BigInteger revokedSerial, DateTimeOffset when, bool sha1 = false)
    {
        var oid = sha1 ? "1.2.840.113549.1.1.5" : "1.2.840.113549.1.1.11";
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        using (tbs.PushSequence())
        {
            using (tbs.PushSequence())
            {
                tbs.WriteObjectIdentifier(oid);
                tbs.WriteNull();
            }

            tbs.WriteEncodedValue(issuer.SubjectName.RawData);
            tbs.WriteGeneralizedTime(Now.AddHours(-2), omitFractionalSeconds: true);
            using (tbs.PushSequence())
            {
                using (tbs.PushSequence())
                {
                    tbs.WriteInteger(revokedSerial);
                    tbs.WriteGeneralizedTime(when, omitFractionalSeconds: true);
                }
            }
        }

        var tbsBytes = tbs.Encode();
        using var key = issuer.GetRSAPrivateKey()!;
        var sig = key.SignData(tbsBytes, sha1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var outer = new AsnWriter(AsnEncodingRules.DER);
        using (outer.PushSequence())
        {
            outer.WriteEncodedValue(tbsBytes);
            using (outer.PushSequence())
            {
                outer.WriteObjectIdentifier(oid);
                outer.WriteNull();
            }

            outer.WriteBitString(sig);
        }

        return outer.Encode();
    }

    private static BigInteger Serial(X509Certificate2 c) => new(c.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true);

    private static CrlCache Cache(ListLogger<CrlCache>? log = null, Func<Uri, CancellationToken, Task<byte[]>>? fetch = null, TimeSpan? refresh = null) =>
        new(fetch ?? ((_, _) => throw new HttpRequestException("sem rede")), log ?? new(), refresh);

    [Fact]
    public void Pontos_de_distribuicao_so_http_e_extensao_malformada_ou_estranha_nao_quebra()
    {
        using var root = Root();
        using (var leaf = Leaf(root, Dp("http://crl.teste.invalid/a.crl", "ldap://crl.teste.invalid/b", "https://crl.teste.invalid/c.crl")))
        {
            Assert.Equal([new Uri("http://crl.teste.invalid/a.crl"), new Uri("https://crl.teste.invalid/c.crl")], CrlCache.DistributionPoints(leaf));
        }

        using (var none = Leaf(root, null))
        {
            Assert.Empty(CrlCache.DistributionPoints(none));
        }

        using (var broken = Leaf(root, new X509Extension("2.5.29.31", [0x30, 0x05, 0x01], false)))
        {
            Assert.Empty(CrlCache.DistributionPoints(broken));
        }

        // DPs sem fullName (só cRLIssuer; nome relativo) são ignorados; GeneralName que não é URI é pulado.
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
            }

            using (w.PushSequence())
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true)))
                {
                    w.WriteCharacterString(UniversalTagNumber.IA5String, "http://emissor.invalid/x.crl", new Asn1Tag(TagClass.ContextSpecific, 6));
                }
            }

            using (w.PushSequence())
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                    {
                        using (w.PushSequence())
                        {
                            w.WriteObjectIdentifier("2.5.4.3");
                            w.WriteCharacterString(UniversalTagNumber.UTF8String, "relativo");
                        }
                    }
                }
            }

            using (w.PushSequence())
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    {
                        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)))
                        {
                            w.WriteEncodedValue(root.SubjectName.RawData);
                        }

                        w.WriteCharacterString(UniversalTagNumber.IA5String, "nao e uri", new Asn1Tag(TagClass.ContextSpecific, 6));
                        w.WriteCharacterString(UniversalTagNumber.IA5String, "http://crl.teste.invalid/d.crl", new Asn1Tag(TagClass.ContextSpecific, 6));
                    }
                }
            }
        }

        using var odd = Leaf(root, new X509Extension("2.5.29.31", w.Encode(), false));
        Assert.Equal([new Uri("http://crl.teste.invalid/d.crl")], CrlCache.DistributionPoints(odd));
        Assert.Throws<ArgumentNullException>(() => CrlCache.DistributionPoints(null!));
    }

    [Fact]
    public void Consulta_so_o_cache_revogacao_conta_pela_data_de_assinatura()
    {
        using var root = Root();
        using var leaf = Leaf(root, Dp("http://crl.teste.invalid/raiz.crl"));
        using var noDp = Leaf(root, null);
        using var cache = Cache();

        Assert.Equal(RevocationStatus.Unknown, cache.Check(noDp, root, Now).Status);
        Assert.Contains("sem ponto de distribuição", cache.Check(noDp, root, Now).Detail, StringComparison.Ordinal);
        Assert.Equal(RevocationStatus.Unknown, cache.Check(leaf, root, Now).Status);     // agenda o download
        Assert.Equal(RevocationStatus.Unknown, cache.Check(leaf, root, Now).Status);     // já pendente: não agenda de novo

        var revokedAt = Now.AddDays(-3);
        var crl = cache.Load(new Uri("http://crl.teste.invalid/raiz.crl"), Crl(root, HashAlgorithmName.SHA256, Now.AddDays(7), (leaf, revokedAt)), root);
        Assert.Equal(root.SubjectName.Name, crl.IssuerName);
        Assert.NotNull(crl.NextUpdate);
        Assert.True(crl.Revoked.ContainsKey(Serial(leaf)));

        var before = cache.Check(leaf, root, revokedAt.AddDays(1));
        Assert.Equal(RevocationStatus.Revoked, before.Status);
        Assert.Contains("revogado", before.Detail, StringComparison.Ordinal);
        Assert.Equal(RevocationStatus.Good, cache.Check(leaf, root, revokedAt.AddDays(-1)).Status);   // assinado antes da revogação
        Assert.Throws<ArgumentNullException>(() => cache.Check(null!, root, Now));
        Assert.Throws<ArgumentNullException>(() => cache.Check(leaf, null!, Now));
        Assert.NotNull(TaxGatewayMetrics.CrlMaxAge?.Invoke());   // idade da LCR mais antiga exposta na métrica
    }

    [Theory]
    [InlineData("SHA384")]
    [InlineData("SHA512")]
    public void Lcr_aceita_os_algoritmos_rsa_da_icp(string hash)
    {
        using var root = Root();
        using var leaf = Leaf(root, null);
        using var cache = Cache();
        var crl = cache.Load(new Uri("http://crl.teste.invalid/x.crl"), Crl(root, new HashAlgorithmName(hash), Now.AddDays(1), (leaf, Now)), root);
        Assert.Single(crl.Revoked);
    }

    [Fact]
    public void Lcr_v1_sem_proxima_atualizacao_e_com_GeneralizedTime()
    {
        using var root = Root();
        using var leaf = Leaf(root, Dp("http://crl.teste.invalid/v1.crl"));
        using var cache = Cache();
        Assert.Null(TaxGatewayMetrics.CrlMaxAge?.Invoke());   // cache vazio: sem idade
        var crl = cache.Load(new Uri("http://crl.teste.invalid/v1.crl"), HandmadeCrl(root, Serial(leaf), Now.AddDays(-1)), root);
        Assert.Null(crl.NextUpdate);
        Assert.Equal(RevocationStatus.Revoked, cache.Check(leaf, root, Now).Status);

        // LCR legada assinada com SHA-1 (ainda aceita no parser).
        var legacy = cache.Load(new Uri("http://crl.teste.invalid/sha1.crl"), HandmadeCrl(root, BigInteger.One, Now.AddDays(-1), sha1: true), root);
        Assert.True(legacy.Revoked.ContainsKey(BigInteger.One));

        // Sem entradas e sem lista: tudo bom.
        var empty = cache.Load(new Uri("http://crl.teste.invalid/vazia.crl"), Crl(root, HashAlgorithmName.SHA256, Now.AddDays(1)), root);
        Assert.Empty(empty.Revoked);
    }

    [Fact]
    public void Lcr_com_algoritmo_nao_suportado_ou_emissor_sem_rsa_e_recusada()
    {
        using var ec = EcRoot();
        using var root = Root();
        using var cache = Cache();
        var ecCrl = new CertificateRevocationListBuilder().Build(ec, BigInteger.One, Now.AddDays(1), HashAlgorithmName.SHA256);
        var ex = Assert.Throws<CryptographicException>(() => cache.Load(new Uri("http://crl.teste.invalid/ec.crl"), ecCrl, ec));
        Assert.Contains("não suportado", ex.Message, StringComparison.Ordinal);

        var rsaCrl = Crl(root, HashAlgorithmName.SHA256, Now.AddDays(1));
        Assert.Throws<CryptographicException>(() => cache.Load(new Uri("http://crl.teste.invalid/r.crl"), rsaCrl, ec));
        Assert.ThrowsAny<AsnContentException>(() => cache.Load(new Uri("http://crl.teste.invalid/lixo.crl"), [0x30, 0x03, 0x02, 0x01, 0x00], root));
    }

    [Fact]
    public async Task Laco_baixa_as_pendentes_renova_perto_do_vencimento_e_registra_falhas()
    {
        using var root = Root();
        using var rogue = Root("CN=Outra raiz");
        const string Good = "http://crl.teste.invalid/boa.crl";
        var fetched = new ConcurrentQueue<string>();
        var goodCrl = Crl(root, HashAlgorithmName.SHA256, Now.AddMinutes(30));   // vence em < 2 h: renovada a cada ciclo
        Task<byte[]> Fetch(Uri u, CancellationToken ct)
        {
            fetched.Enqueue(u.AbsoluteUri);
            return u.AbsolutePath switch
            {
                "/boa.crl" => Task.FromResult(goodCrl),
                "/grande.crl" => Task.FromResult(new byte[(32 * 1024 * 1024) + 1]),
                "/lixo.crl" => Task.FromResult(new byte[] { 0x30, 0x80, 0x01 }),
                "/forjada.crl" => Task.FromResult(Crl(rogue, HashAlgorithmName.SHA256, Now.AddDays(1))),
                "/lenta.crl" => throw new TaskCanceledException("timeout"),
                _ => throw new HttpRequestException("404"),
            };
        }

        var log = new ListLogger<CrlCache>();
        using var cache = Cache(log, Fetch, TimeSpan.FromMilliseconds(250));
        await cache.StartAsync(CancellationToken.None);
        try
        {
            using var leaf = Leaf(root, Dp(Good));
            Assert.Equal(RevocationStatus.Unknown, cache.Check(leaf, root, Now).Status);
            await Dead.Until(() => cache.Check(leaf, root, Now).Status == RevocationStatus.Good);

            // Renovação: a LCR perto do nextUpdate volta para a fila no tique do timer.
            await Dead.Until(() => fetched.Count(u => u == Good) >= 3);

            foreach (var bad in new[] { "grande", "lixo", "forjada", "lenta", "sumiu" })
            {
                using var other = Leaf(root, Dp($"http://crl.teste.invalid/{bad}.crl"));
                Assert.Equal(RevocationStatus.Unknown, cache.Check(other, root, Now).Status);
                await Dead.Until(() => log.Has(LogLevel.Warning, $"/{bad}.crl"));
                Assert.Equal(RevocationStatus.Unknown, cache.Check(other, root, Now).Status);   // segue pendente
            }
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
        }

        Assert.All(log.Entries, e => Assert.NotNull(e.Error));

        // Nível Warning desligado: a falha não é registrada, mas a LCR segue pendente e é tentada de novo.
        var attempts = 0;
        using var quiet = Cache(new ListLogger<CrlCache> { Enabled = false }, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("fora");
        }, TimeSpan.FromMilliseconds(100));
        await quiet.StartAsync(CancellationToken.None);
        using (var leaf2 = Leaf(root, Dp("http://crl.teste.invalid/q.crl")))
        {
            quiet.Check(leaf2, root, Now);
            await Dead.Until(() => Volatile.Read(ref attempts) >= 2);
        }

        await quiet.StopAsync(CancellationToken.None);
        Assert.Contains(log.Entries, e => e.Error is InvalidDataException);
        Assert.Contains(log.Entries, e => e.Error is CryptographicException);
        Assert.Contains(log.Entries, e => e.Error is AsnContentException);
        Assert.Contains(log.Entries, e => e.Error is TaskCanceledException);
    }
}

/// <summary>Adapters dos motores (SDD §8) contra respostas montadas: erros, indisponibilidade e leitura de todos os grupos.</summary>
public sealed class EngineAdapterTests
{
    private static readonly IOptions<EngineOptions> Opts = Options.Create(new EngineOptions { Bundle = "bundle-teste" });

    private static EngineOperation FullOperation() => new("op-1", new DateTimeOffset(2027, 3, 10, 9, 30, 0, TimeSpan.FromHours(-3)), 3550308, "SP", 55,
        new CompraGovernamental(2, 1),
        [
            new EngineItemInput(1, "22030000", null, "000", "000001", 100m, 2m, "UN", new EngineIsInput("000", "000001", 100m, 2m, "LT"),
                new NominalRates(0.9m, 0.1m, 0.05m, 1.5m, 0.2m),
                new BaseComponents(100m, 1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m, 11m, 12m, 13m, 14m),
                new RegularTaxation("000", "000001")),
            new EngineItemInput(2, null, "1.0101.10.00", "200", "200032", null, null, null, null, new NominalRates(0.9m, null, null, null, null)),
            new EngineItemInput(3, null, null, "410", "410001", 5m, null, null, null, null),
        ]);

    [Fact]
    public void Requisicao_canonica_leva_todos_os_grupos_opcionais()
    {
        var json = JsonDocument.Parse(RegularEngineAdapter.CanonicalRequest(FullOperation())).RootElement;
        Assert.Equal("2027-03-10T09:30:00-03:00", json.GetProperty("dhFatoGerador").GetString());
        Assert.Equal(55, json.GetProperty("tpDoc").GetInt32());
        Assert.Equal(1, json.GetProperty("gCompraGov").GetProperty("tpOperGov").GetInt32());
        var i1 = json.GetProperty("itens")[0];
        Assert.Equal("LT", i1.GetProperty("impostoSeletivo").GetProperty("unidade").GetString());
        Assert.Equal("000001", i1.GetProperty("tributacaoRegular").GetProperty("cClassTrib").GetString());
        Assert.Equal(14m, i1.GetProperty("gComponentesBC").GetProperty("vISSQN").GetDecimal());
        Assert.Equal(0.2m, i1.GetProperty("aliquotasNominais").GetProperty("impostoSeletivo").GetProperty("adRem").GetDecimal());
        var i2 = json.GetProperty("itens")[1];
        Assert.False(i2.TryGetProperty("ncm", out _));
        Assert.False(i2.GetProperty("aliquotasNominais").TryGetProperty("impostoSeletivo", out _));
        Assert.False(json.GetProperty("itens")[2].TryGetProperty("aliquotasNominais", out _));

        var minimal = JsonDocument.Parse(RegularEngineAdapter.CanonicalRequest(FullOperation() with { TipoDocumento = null, CompraGovernamental = new CompraGovernamental(1, null) })).RootElement;
        Assert.False(minimal.TryGetProperty("tpDoc", out _));
        Assert.False(minimal.GetProperty("gCompraGov").TryGetProperty("tpOperGov", out _));
    }

    [Fact]
    public async Task Motor_regular_le_reducao_IS_totais_e_avisos()
    {
        const string Body = """
            {"objetos":[
              {"nObj":1,"estadoItem":"CALCULADO","tribCalc":{
                "IBSCBS":{"CST":"200","cClassTrib":"200032","gIBSCBS":{"vBC":"100.00","vIBS":"10.00",
                   "gCBS":{"pCBS":"0.90","vCBS":"0.36","gRed":{"pRedAliq":"60.00","pAliqEfet":"0.36"}},
                   "gIBSUF":{"pIBSUF":0.1,"vIBSUF":0.04},
                   "gIBSMun":{"pIBSMun":"0.05","vIBSMun":"0.02"}}},
                "IS":{"vBCIS":"100.00","pIS":"1.50","pISEspec":"0.20","vIS":"1.90"}}},
              {"nObj":2,"erros":[{"code":"E1","title":"Erro","detail":"d","pointer":"/itens/1"},{"code":1,"title":null}]},
              {"nObj":3,"estadoItem":"CALCULADO","tribCalc":{"IBSCBS":{"CST":"410"}}},
              {"nObj":4,"tribCalc":{"IS":{"pIS":true}}}
            ],
             "total":{"tribCalc":{"IBSCBSTot":{"vBCIBSCBS":"100.00","gCBS":{"vCBS":"0.36"},"gIBS":{"vIBS":"0.06","gIBSUF":{"vIBSUF":"0.04"},"gIBSMun":{"vIBSMun":"0.02"}}},
                                  "ISTot":{"vIS":"1.90"}}},
             "oper":{"x":1}}
            """;
        var http = new FakeHttp(_ =>
        {
            var r = FakeHttp.Json(Body);
            r.Headers.Add("X-CALC-APP-VERSION", "1.7.2");
            r.Headers.Add("X-CALC-DB-VERSION", "db-9");
            r.Headers.Add("x-warning-dados-simulados", ["a", "b"]);
            return Task.FromResult(r);
        });

        var calc = await new RegularEngineAdapter(http, Opts).CalculateAsync(FullOperation(), CancellationToken.None);
        Assert.Equal("calculadora/observabilidade/regime-geral", Assert.Single(http.Requests)!.TrimStart('/')[4..]);
        Assert.Equal(new EngineVersion("bundle-teste", RfbEngine.Regular, "1.7.2", "db-9"), calc.Version);
        Assert.Equal(["a", "b"], calc.AvisosDadosSimulados);
        var i1 = calc.Itens[0];
        Assert.Equal(60m, i1.Cbs!.PercentualReducao);
        Assert.Equal(0.36m, i1.Cbs.AliquotaEfetiva);
        Assert.Equal(0.04m, i1.IbsUf!.Valor);
        Assert.Null(i1.IbsUf.PercentualReducao);
        Assert.Equal(new IsLine(100m, 1.5m, 0.2m, 1.9m), i1.ImpostoSeletivo);
        Assert.Equal(2, calc.Itens[1].Erros.Count);
        Assert.Equal("", calc.Itens[1].Erros[1].Codigo);
        Assert.Null(calc.Itens[1].TributosDfe);
        Assert.Equal("", calc.Itens[1].Estado);
        Assert.Equal("410", calc.Itens[2].Cst);
        Assert.Null(calc.Itens[2].Cbs);
        Assert.Equal(new IsLine(0, null, null, 0), calc.Itens[3].ImpostoSeletivo);
        Assert.Equal(new EngineTotals(100m, 0.36m, 0.04m, 0.02m, 0.06m, 1.9m), calc.Totais);
        Assert.NotNull(calc.OperacaoDfe);
    }

    [Fact]
    public async Task Motor_regular_sem_totais_e_erros_de_transporte()
    {
        var calc = await new RegularEngineAdapter(new FakeHttp(_ => Task.FromResult(FakeHttp.Json("""{"total":{"x":1}}"""))), Opts)
            .CalculateAsync(FullOperation(), CancellationToken.None);
        Assert.Empty(calc.Itens);
        Assert.Equal(new EngineTotals(0, 0, 0, 0, 0, 0), calc.Totais);
        Assert.Null(calc.OperacaoDfe);
        Assert.Empty(calc.AvisosDadosSimulados);
        Assert.Equal("", calc.Version.AppVersion);

        var totalsOnlyIs = await new RegularEngineAdapter(new FakeHttp(_ => Task.FromResult(FakeHttp.Json("""{"total":{"tribCalc":{"IBSCBSTot":{}}}}"""))), Opts)
            .CalculateAsync(FullOperation(), CancellationToken.None);
        Assert.Equal(new EngineTotals(0, 0, 0, 0, 0, 0), totalsOnlyIs.Totais);

        var rejected = await Assert.ThrowsAsync<EngineRejectedException>(() => new RegularEngineAdapter(
            new FakeHttp(_ => Task.FromResult(FakeHttp.Json("<html>não é json</html>", HttpStatusCode.BadRequest))), Opts).CalculateAsync(FullOperation(), CancellationToken.None));
        Assert.Equal(400, rejected.Status);
        Assert.Empty(rejected.Errors);

        var noErrors = await Assert.ThrowsAsync<EngineRejectedException>(() => new RegularEngineAdapter(
            new FakeHttp(_ => Task.FromResult(FakeHttp.Json("""{"title":"x"}""", HttpStatusCode.UnprocessableEntity))), Opts).CalculateAsync(FullOperation(), CancellationToken.None));
        Assert.Empty(noErrors.Errors);

        await Assert.ThrowsAsync<EngineUnavailableException>(() => new RegularEngineAdapter(
            new FakeHttp(_ => Task.FromResult(FakeHttp.Json("{}", HttpStatusCode.BadGateway))), Opts).CalculateAsync(FullOperation(), CancellationToken.None));
        await Assert.ThrowsAsync<EngineUnavailableException>(() => new RegularEngineAdapter(
            new FakeHttp(_ => throw new HttpRequestException("recusada")), Opts).CalculateAsync(FullOperation(), CancellationToken.None));
        await Assert.ThrowsAsync<EngineUnavailableException>(() => new RegularEngineAdapter(
            new FakeHttp(_ => throw new TaskCanceledException("timeout")), Opts).CalculateAsync(FullOperation(), CancellationToken.None));

        // Cancelamento do chamador não vira "motor indisponível".
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RegularEngineAdapter(
            new FakeHttp(_ => throw new TaskCanceledException("cancelado")), Opts).CalculateAsync(FullOperation(), cts.Token));
    }

    private static SimplesOperation SimplesOp(int? enteGov = 3, string? regime = "1") => new(new DateOnly(2027, 2, 1), 55, "1", regime, enteGov,
    [
        new SimplesItemInput(1, "01", "000001", "22030000", null, 1234.5m, 10m, 0.5m, 0.25m),
        new SimplesItemInput(2, null, "000001", null, "1.0101", 10m, 0m, null, null),
    ]);

    [Fact]
    public async Task Motor_do_Simples_monta_o_pedido_e_le_a_resposta()
    {
        var w = JsonDocument.Parse(SimplesEngineAdapter.Write(SimplesOp())).RootElement;
        Assert.Equal("2027-02-01", w.GetProperty("data").GetString());
        Assert.Equal("3", w.GetProperty("tpEnteGov").GetString());
        Assert.Equal("1", w.GetProperty("tpRegimeIBSCBS").GetString());
        Assert.Equal("1234.50", w.GetProperty("itens")[0].GetProperty("baseCalculo").GetString());
        Assert.Equal("0.50", w.GetProperty("itens")[0].GetProperty("pIBSSN").GetString());
        Assert.Equal("01", w.GetProperty("itens")[0].GetProperty("cAtivSN").GetString());
        Assert.False(w.GetProperty("itens")[1].TryGetProperty("pCBSSN", out _));
        Assert.Equal("1.0101", w.GetProperty("itens")[1].GetProperty("nbs").GetString());
        var bare = JsonDocument.Parse(SimplesEngineAdapter.Write(SimplesOp(null, null))).RootElement;
        Assert.False(bare.TryGetProperty("tpEnteGov", out _));
        Assert.False(bare.TryGetProperty("tpRegimeIBSCBS", out _));
        Assert.Throws<ArgumentNullException>(() => SimplesEngineAdapter.Write(null!));

        const string Body = """
            {"objetos":[
               {"nObj":1,"estadoItem":"CALCULADO","tribCalc":{"vBC":"1234.50","pIBSSN":0.5,"vIBSSN":"6.17","pCBSSN":"0.25","vCBSSN":"3.09","outro":[]},
                "motivoEstado":{"descricao":"ok"}},
               {"nObj":2,"estadoItem":"INCONSISTENCIA_ENTRADA","erros":[{"code":"S1","title":"t","detail":"d","pointer":"/itens/1"}],"motivoEstado":"texto"}],
             "total":{"vBC":1244.5,"vIBSSNtot":"6.17","vCBSSNtot":"3.09"}}
            """;
        var http = new FakeHttp(_ =>
        {
            var r = FakeHttp.Json(Body);
            r.Headers.Add("X-CALC-APP-VERSION", "1.0.2");
            return Task.FromResult(r);
        });
        var adapter = new SimplesEngineAdapter(http, Opts);
        Assert.Equal(RfbEngine.Simples, adapter.Engine);
        Assert.Equal(EngineCapability.CalcCbsIbs, adapter.Capabilities);
        var calc = await adapter.CalculateAsync(SimplesOp(), CancellationToken.None);
        Assert.Contains("simples-nacional", Assert.Single(http.Requests), StringComparison.Ordinal);
        Assert.Equal(new EngineVersion("bundle-teste", RfbEngine.Simples, "1.0.2", ""), calc.Version);
        Assert.Equal(6.17m, calc.Itens[0].ValorIbs);
        Assert.Equal(0.5m, calc.Itens[0].AliquotaIbs);
        Assert.Equal("ok", calc.Itens[0].MotivoEstado);
        Assert.NotNull(calc.Itens[0].TributosSimples);
        Assert.Null(calc.Itens[1].BaseCalculo);
        Assert.Null(calc.Itens[1].MotivoEstado);
        Assert.Equal("S1", Assert.Single(calc.Itens[1].Erros).Codigo);
        Assert.Equal(new SimplesTotals(1244.5m, 6.17m, 3.09m, 0m), calc.Totais);

        var empty = await new SimplesEngineAdapter(new FakeHttp(_ => Task.FromResult(FakeHttp.Json("{}"))), Opts).CalculateAsync(SimplesOp(), CancellationToken.None);
        Assert.Empty(empty.Itens);
        Assert.Equal(new SimplesTotals(0, 0, 0, 0), empty.Totais);
    }

    [Fact]
    public async Task Motor_do_Simples_erros_viram_excecoes_de_dominio()
    {
        var adapter = new SimplesEngineAdapter(new FakeHttp(_ => throw new InvalidOperationException("não deveria chamar")), Opts);
        var cap = await Assert.ThrowsAsync<EngineCapabilityNotSupportedException>(() => adapter.CalculateAsync(FullOperationForSimples(), CancellationToken.None));
        Assert.Equal(RfbEngine.Simples, cap.Engine);
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.CalculateAsync((SimplesOperation)null!, CancellationToken.None));

        var rejected = await Assert.ThrowsAsync<EngineRejectedException>(() => new SimplesEngineAdapter(new FakeHttp(_ => Task.FromResult(
            FakeHttp.Json("""{"errors":[{"code":"X","title":"inválido","pointer":"/CRT"}]}""", HttpStatusCode.UnprocessableEntity))), Opts).CalculateAsync(SimplesOp(), CancellationToken.None));
        Assert.Equal(422, rejected.Status);
        Assert.Equal("/CRT", Assert.Single(rejected.Errors).Ponteiro);

        await Assert.ThrowsAsync<EngineUnavailableException>(() => new SimplesEngineAdapter(new FakeHttp(_ => Task.FromResult(
            FakeHttp.Json("{}", HttpStatusCode.ServiceUnavailable))), Opts).CalculateAsync(SimplesOp(), CancellationToken.None));
        var down = await Assert.ThrowsAsync<EngineUnavailableException>(() => new SimplesEngineAdapter(new FakeHttp(_ => throw new HttpRequestException("x")), Opts)
            .CalculateAsync(SimplesOp(), CancellationToken.None));
        Assert.IsType<HttpRequestException>(down.InnerException);
        await Assert.ThrowsAsync<EngineUnavailableException>(() => new SimplesEngineAdapter(new FakeHttp(_ => throw new TaskCanceledException("t")), Opts)
            .CalculateAsync(SimplesOp(), CancellationToken.None));
    }

    private static EngineOperation FullOperationForSimples() => FullOperation();
}

/// <summary>Cadastro de entes do motor (SDD §8.6): carga com Valkey indisponível, cadastro incompleto e classificações.</summary>
public sealed class RfbReferenceDataTests
{
    private static string Ufs(bool complete) =>
        JsonSerializer.Serialize(Enumerable.Range(11, complete ? 27 : 26).Select(i => new { codigo = i, sigla = $"U{i}" })
            .Append(new { codigo = 99, sigla = "EX" }));

    private static string Municipios(string sigla) =>
        sigla == "EX" ? """[{"codigo":9999999}]"""
            : JsonSerializer.Serialize(Enumerable.Range(0, 200).Select(i => new { codigo = (int.Parse(sigla[1..], System.Globalization.CultureInfo.InvariantCulture) * 100000) + i }));

    [Fact]
    public async Task Carrega_do_motor_mesmo_sem_Valkey_e_tenta_de_novo_quando_incompleto()
    {
        var calls = 0;
        var http = new FakeHttp(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.EndsWith("/ufs", StringComparison.Ordinal))
            {
                return Task.FromResult(FakeHttp.Json(Ufs(complete: Interlocked.Increment(ref calls) > 1)));   // 1ª carga: 26 UFs
            }

            return Task.FromResult(FakeHttp.Json(Municipios(path[(path.IndexOf("siglaUf=", StringComparison.Ordinal) + 8)..])));
        });

        using var valkey = Dead.Valkey();
        var log = new ListLogger<RfbReferenceData>();
        using var data = new RfbReferenceData(http, valkey, Options.Create(new EngineOptions { Bundle = "b-unit" }), log);
        Assert.False(data.IsLoaded);
        Assert.Equal(0, data.MunicipioCount);
        Assert.Throws<EngineUnavailableException>(() => data.UfOfMunicipio(1100000));

        await data.StartAsync(CancellationToken.None);
        await Dead.Until(() => data.IsLoaded);
        await data.StopAsync(CancellationToken.None);

        Assert.Equal("b-unit", data.Bundle);
        Assert.Equal((27 * 200) + 1, data.MunicipioCount);
        Assert.Equal("U11", data.UfOfMunicipio(1100000));
        Assert.Equal("EX", data.UfOfMunicipio(9999999));
        Assert.Null(data.UfOfMunicipio(1));
        Assert.Contains(log.Entries, e => e.Error is InvalidOperationException);   // cadastro incompleto → nova tentativa
        Assert.Contains(log.Entries, e => e.Error is RedisException);              // sem L2: segue pelo motor

        var quiet = new ListLogger<RfbReferenceData> { Enabled = false };
        using var data2 = new RfbReferenceData(http, valkey, Options.Create(new EngineOptions { Bundle = "b-unit" }), quiet);
        await data2.StartAsync(CancellationToken.None);
        await Dead.Until(() => data2.IsLoaded);
        await data2.StopAsync(CancellationToken.None);
        Assert.Empty(quiet.Entries);
    }

    [Fact]
    public async Task Classificacoes_por_data_com_cache_e_nova_tentativa_apos_falha()
    {
        var fail = true;
        var http = new FakeHttp(_ => Task.FromResult(fail
            ? FakeHttp.Json("{}", HttpStatusCode.InternalServerError)
            : FakeHttp.Json("""
                [{"codigo":"000001","descricaoTratamentoTributario":"Tributação integral","possuiPercentualReducao":false,
                  "indicaApropriacaoCreditoAdquirenteCbs":true,"indicaApropriacaoCreditoAdquirenteIbs":true},
                 {"codigo":"200032","possuiPercentualReducao":true},
                 {"codigo":"410001","descricaoTratamentoTributario":null,"possuiPercentualReducao":"sim"}]
                """)));
        using var valkey = Dead.Valkey();
        using var data = new RfbReferenceData(http, valkey, Options.Create(new EngineOptions { Bundle = "b" }), new ListLogger<RfbReferenceData>());
        var day = new DateOnly(2027, 1, 15);

        await Assert.ThrowsAsync<HttpRequestException>(() => data.ClassificationsAsync(day, CancellationToken.None));
        fail = false;
        IReadOnlyDictionary<string, ClassificationInfo>? map = null;
        for (var i = 0; i < 3 && map is null; i++)
        {
            try
            {
                map = await data.ClassificationsAsync(day, CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                // a tarefa com falha é descartada na chamada seguinte
            }
        }

        Assert.NotNull(map);
        Assert.Equal(3, map!.Count);
        Assert.True(map["000001"].CreditoAdquirenteCbs);
        Assert.Equal("Tributação integral", map["000001"].TratamentoTributario);
        Assert.Equal("", map["200032"].TratamentoTributario);
        Assert.True(map["200032"].PossuiPercentualReducao);
        Assert.False(map["200032"].CreditoAdquirenteIbs);
        Assert.False(map["410001"].PossuiPercentualReducao);
        Assert.Contains("data=2027-01-15", http.Requests.Last(), StringComparison.Ordinal);

        var before = http.Requests.Count;
        Assert.Same(map, await data.ClassificationsAsync(day, CancellationToken.None));   // mesma data: memória
        Assert.Equal(before, http.Requests.Count);
    }
}

/// <summary>Revogações (D-210): Valkey e banco indisponíveis não derrubam o laço; mensagens de pub/sub aplicadas.</summary>
public sealed class RevocationServiceTests
{
    [Fact]
    public async Task Laco_sobrevive_a_Valkey_e_banco_fora_do_ar()
    {
        using var valkey = Dead.Valkey();
        await using var db = Dead.Db();
        using var quietSvc = new RevocationService(valkey, db, new ListLogger<RevocationService> { Enabled = false });
        await quietSvc.StartAsync(CancellationToken.None);
        var log = new ListLogger<RevocationService>();
        using var svc = new RevocationService(valkey, db, log);
        await svc.StartAsync(CancellationToken.None);
        await Dead.Until(() => log.Has(LogLevel.Warning, "banco indisponível") && log.Entries.Count(e => e.Message.Contains("Valkey", StringComparison.Ordinal)) >= 2);
        await svc.StopAsync(CancellationToken.None);
        await quietSvc.StopAsync(CancellationToken.None);

        var tenant = new TenantId(Guid.NewGuid());
        Assert.False(svc.IsTenantSuspended(tenant));
        Assert.False(svc.IsRevoked("jti", "cliente", tenant));
        await Assert.ThrowsAnyAsync<RedisException>(() => svc.SuspendTenantAsync(tenant));   // escrita exige o Valkey
    }
}

/// <summary>Fila de gravação das simulações (SDD §16.2 item 6): novas tentativas, descarte contado e drenagem no desligamento.</summary>
public sealed class SimulationWriterTests
{
    private sealed class FlakyDb(Func<int, Exception?> failOnAttempt) : ITenantDb
    {
        public int Calls;
        public ConcurrentQueue<TenantId> Tenants { get; } = new();

        public Task<T> InTenantAsync<T>(TenantId tenant, Func<TenantScope, CancellationToken, Task<T>> work, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref Calls);
            Tenants.Enqueue(tenant);
            return failOnAttempt(n) is { } ex ? Task.FromException<T>(ex) : Task.FromResult(default(T)!);
        }
    }

    private static SimulationRecord Rec(TenantId t, string req = "{}") =>
        new(t, Guid.NewGuid(), new DateOnly(2027, 1, 1), "11222333000181", "REGULAR", "b", 1, req, "{}", DateTimeOffset.UtcNow);

    private static long DroppedDuring(Action act)
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == "taxgateway_simulation_persist_dropped_total")
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref dropped, v));
        listener.Start();
        act();
        return Interlocked.Read(ref dropped);
    }

    [Fact]
    public async Task Falha_transitoria_tenta_de_novo_e_falha_persistente_descarta_contando()
    {
        var t1 = new TenantId(Guid.NewGuid());
        var t2 = new TenantId(Guid.NewGuid());
        var flaky = new FlakyDb(n => n <= 2 ? new NpgsqlException("queda") : null);
        var log = new ListLogger<SimulationWriter>();
        var writer = new SimulationWriter(flaky, log);
        var buffer = new List<SimulationRecord> { Rec(t1), Rec(t1) };
        await writer.FlushAsync(buffer, CancellationToken.None);
        Assert.Empty(buffer);
        Assert.Equal(3, flaky.Calls);   // 2 falhas + sucesso, mesmo grupo
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Warning));

        var dead = new FlakyDb(_ => new TimeoutException("lento"));
        var log2 = new ListLogger<SimulationWriter>();
        var writer2 = new SimulationWriter(dead, log2);
        var dropped = DroppedDuring(() => writer2.FlushAsync([Rec(t1), Rec(t2), Rec(t2)], CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(6, dead.Calls);    // 3 tentativas por tenant
        Assert.Equal(3, dropped);
        Assert.Equal(2, log2.Entries.Count(e => e.Level == LogLevel.Error));
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.FlushAsync(null!, CancellationToken.None));

        // Níveis desligados: mesmas tentativas, sem log.
        var quiet = new ListLogger<SimulationWriter> { Enabled = false };
        var dead2 = new FlakyDb(_ => new NpgsqlException("fora"));
        await new SimulationWriter(dead2, quiet).FlushAsync([Rec(t1)], CancellationToken.None);
        Assert.Equal(3, dead2.Calls);
        var quietWriter = new SimulationWriter(new FlakyDb(_ => null), quiet);
        quietWriter.Enqueue(Rec(t1, new string('x', 70 * 1024 * 1024)));
        Assert.Empty(quiet.Entries);
    }

    [Fact]
    public void Fila_acima_do_teto_de_bytes_descarta_sem_bloquear()
    {
        var t = new TenantId(Guid.NewGuid());
        var big = new string('x', 34 * 1024 * 1024);   // ~68 MB contados por registro (UTF-16)
        var log = new ListLogger<SimulationWriter>();
        var writer = new SimulationWriter(new FlakyDb(_ => null), log);
        var dropped = DroppedDuring(() =>
        {
            writer.Enqueue(Rec(t, big));
            writer.Enqueue(Rec(t, big));   // passaria de 128 MB
        });
        Assert.Equal(1, dropped);
        Assert.True(log.Has(LogLevel.Error, "não será gravada"));
        Assert.Throws<ArgumentNullException>(() => writer.Enqueue(null!));
    }

    [Fact]
    public async Task Laco_grava_em_lote_e_drena_no_desligamento()
    {
        var t = new TenantId(Guid.NewGuid());
        var db = new FlakyDb(_ => null);
        using var writer = new SimulationWriter(db, new ListLogger<SimulationWriter>());
        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 1500; i++)
        {
            writer.Enqueue(Rec(t));
        }

        await Dead.Until(() => db.Calls >= 2);   // 1.000 + 500
        writer.Enqueue(Rec(t));
        await writer.StopAsync(CancellationToken.None);
        Assert.True(db.Calls >= 2);
        Assert.All(db.Tenants, x => Assert.Equal(t, x));
    }
}

/// <summary>Laços de fundo que não podem morrer quando o banco cai (lotes e DF-e).</summary>
public sealed class WorkerLoopResilienceTests
{
    [Fact]
    public async Task Processador_de_lotes_registra_a_falha_e_segue_no_laco()
    {
        await using var db = Dead.Db();
        var log = new ListLogger<BatchProcessor>();
        using var processor = new BatchProcessor(db, null!, null!, null!, null!, Options.Create(new BatchOptions()), log);
        await processor.StartAsync(CancellationToken.None);
        await Dead.Until(() => log.Has(LogLevel.Error, "processador de lotes"));
        await processor.StopAsync(CancellationToken.None);
        Assert.IsAssignableFrom<NpgsqlException>(log.Entries.First().Error);
    }

    [Fact]
    public async Task Worker_de_DFe_registra_ciclo_com_falha_e_para_no_stop()
    {
        await using var db = Dead.Db();
        var sync = new DfeSyncService(db, null!, null!, null!, null!, TimeProvider.System, new ListLogger<DfeSyncService>());
        var expiry = new CertificateExpiryService(db, null!, TimeProvider.System);
        var log = new ListLogger<DfeWorker>();
        using var worker = new DfeWorker(sync, expiry, Options.Create(new DfeOptions { PollSeconds = 1 }), log);
        await worker.StartAsync(CancellationToken.None);
        await Dead.Until(() => log.Has(LogLevel.Error, "DF-e: ciclo falhou"));
        await worker.StopAsync(CancellationToken.None);
        Assert.IsAssignableFrom<NpgsqlException>(log.Entries.First().Error);
    }
}

/// <summary>Ramos menos comuns das auditorias de saída e por regime (SDD §8.1, §8.3).</summary>
public sealed class AuditorBranchTests
{
    private static readonly BaseComponents Comps = new(1000m, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    private static readonly HashSet<string> None = [];
    private static readonly EngineVersion Version = new("b", RfbEngine.Regular, "1.7.2", "V0073");

    private static DeclaredItem Reg(int n = 1, string? cls = "000001") => new(n, "84713012", "000", cls, 1000m,
        new DeclaredTax(0.05m, 0.50m, null, null, null, null, null), new DeclaredTax(0.05m, 0.50m, null, null, null, null, null), 1.00m,
        new DeclaredTax(11.2m, 112m, null, null, null, null, null), null, Comps, null, []);

    private static DeclaredDocument Doc(string crt, DeclaredTotals? totals, params DeclaredItem[] itens) => new(
        "35270611222333000181550010000045901000000000", "55", new DateTimeOffset(2027, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
        "11222333000181", crt, null, "11444777000161", null, false, 3550308, 1, true, null, itens, totals ?? Totals(itens));

    private static DeclaredTotals Totals(DeclaredItem[] itens, decimal? vIs = null, decimal? vCbsSn = null) =>
        new(itens.Sum(i => i.VBc ?? 0), itens.Sum(i => i.IbsUf?.Valor ?? 0), itens.Sum(i => i.IbsMun?.Valor ?? 0),
            itens.Sum(i => i.VIbs ?? 0), itens.Sum(i => i.Cbs?.Valor ?? 0), 0, 0, 0, 0, 0, 0, vIs ?? itens.Sum(i => i.ImpostoSeletivo?.Valor ?? 0),
            itens.Sum(i => i.Simples?.ValorIbs ?? 0), vCbsSn ?? itens.Sum(i => i.Simples?.ValorCbs ?? 0));

    private static EngineItemResult Eng(int n, TaxLine? cbs, TaxLine? ibsMun, IsLine? isLine = null) =>
        new(n, "CALCULADO", "000", "000001", 1000m, cbs, new TaxLine(0.05m, 0.50m, null, null), ibsMun, 1.00m, isLine, [], null);

    [Fact]
    public void Saida_item_sem_grupo_e_item_sem_resultado_do_motor_ficam_indeterminados()
    {
        var calc = new EngineCalculation([Eng(1, new TaxLine(11.2m, 112m, null, null), new TaxLine(0.05m, 0.5m, null, null))],
            new EngineTotals(1000m, 112m, 0.5m, 0.5m, 1m, 0), [], Version);
        var o = OutboundAuditor.Audit(Doc("3", null, Reg(1) with { Cst = null }, Reg(2)), calc);
        Assert.Equal(AuditStatus.Indeterminado, o.Status);
        var i005 = Assert.Single(o.Findings);
        Assert.Equal("I005", i005.Regra);
        Assert.Equal(1, i005.NumeroItem);
        Assert.Equal(113m - 226m, o.Diferenca);   // recálculo só tem o item 1; o declarado soma os dois

        var semClass = OutboundAuditor.Audit(Doc("3", null, Reg(1, cls: null)), null);
        Assert.Equal("I005", Assert.Single(semClass.Findings).Regra);
        Assert.Null(semClass.Recalculado);
        Assert.Throws<ArgumentNullException>(() => OutboundAuditor.Audit(null!, null));
    }

    [Fact]
    public void Saida_grupo_presente_so_de_um_lado_IS_divergente_e_total_do_IS()
    {
        var item = Reg() with { IbsMun = null, ImpostoSeletivo = new DeclaredIs("000", "000001", 1000m, 1m, null, null, null, 10m) };
        var calc = new EngineCalculation([Eng(1, cbs: null, ibsMun: new TaxLine(0.05m, 0.5m, null, null), isLine: new IsLine(1000m, 1.2m, null, 12m))],
            new EngineTotals(1000m, 0, 0.5m, 0.5m, 1m, 12m), [], Version);
        var o = OutboundAuditor.Audit(Doc("3", Totals([item], vIs: 7m), item), calc);

        Assert.Equal(AuditStatus.Divergente, o.Status);
        var r029 = o.Findings.Where(f => f.Regra == "R029").ToList();
        Assert.Equal(2, r029.Count);
        Assert.Contains(r029, f => f.ValorDeclarado == "ausente" && f.CampoXml!.EndsWith("gIBSMun", StringComparison.Ordinal));
        Assert.Contains(r029, f => f.ValorDeclarado == "presente" && f.CampoXml!.EndsWith("gCBS", StringComparison.Ordinal));
        var i003 = Assert.Single(o.Findings, f => f.Regra == "I003");
        Assert.Equal(2m, i003.ImpactoFinanceiro);
        var tot = Assert.Single(o.Findings, f => f.Regra == "R030");
        Assert.Equal("total/ISTot/vIS", tot.CampoXml);
        Assert.Equal("7.00", tot.ValorDeclarado);
        Assert.Equal("10.00", tot.ValorReferencia);
    }

    [Fact]
    public void Saida_so_com_IS_divergente_fica_indeterminada_pela_regra_informativa()
    {
        var item = Reg() with { ImpostoSeletivo = new DeclaredIs("000", "000001", 1000m, 1m, null, null, null, 10m) };
        var calc = new EngineCalculation([Eng(1, new TaxLine(11.2m, 112m, null, null), new TaxLine(0.05m, 0.5m, null, null), new IsLine(1000m, 1m, null, 10.5m))],
            new EngineTotals(1000m, 112m, 0.5m, 0.5m, 1m, 10.5m), [], Version);
        var o = OutboundAuditor.Audit(Doc("3", null, item), calc);
        Assert.Equal("I003", Assert.Single(o.Findings).Regra);
        Assert.Equal(AuditStatus.Indeterminado, o.Status);
        Assert.Equal(0.5m, o.Diferenca);

        // Dentro da tolerância publicada (lida como as ferramentas externas leem a constante): conforme.
        var tol = (decimal)typeof(OutboundAuditor).GetField(nameof(OutboundAuditor.Tolerancia))!.GetValue(null)!;
        Assert.Equal(0.01m, tol);
        var near = calc with { Itens = [calc.Itens[0] with { ImpostoSeletivo = new IsLine(1000m, 1m, null, 10m + tol) }] };
        Assert.Equal(AuditStatus.Conforme, OutboundAuditor.Audit(Doc("3", null, item), near).Status);
    }

    [Fact]
    public void Simples_sem_grupo_sem_resultado_com_erros_do_motor_e_total_divergente()
    {
        var sn = new DeclaredSimples(0.50m, 5.00m, 1.20m, 12.00m);
        DeclaredItem Sn(int n) => Reg(n) with { VBc = null, IbsUf = null, IbsMun = null, VIbs = null, Cbs = null, Simples = sn, CodigoAtividadeSn = "01" };
        var itens = new[] { Reg(1) with { Simples = null }, Sn(2), Sn(3), Sn(4), Sn(5) };
        var sim = new SimplesCalculation(
        [
            new SimplesItemResult(3, "CALCULADO", 1000m, 0.5m, 5m, 1.2m, 12m, [new EngineError("SN-009", "Atividade", "cAtivSN inválido", "/itens/3")], null, null),
            new SimplesItemResult(4, "INCONSISTENCIA_ENTRADA", null, null, null, null, null, [], "Base negativa", null),
            new SimplesItemResult(5, "NAO_CALCULADO", null, null, null, null, null, [], null, null),
        ], new SimplesTotals(1000m, 5m, 12m, 0), new EngineVersion("b", RfbEngine.Simples, "1.0.2", "V0073"));

        var o = DocumentAuditor.Audit(new AuditInput(Doc("1", Totals(itens, vCbsSn: 99m), itens), "OUTBOUND", false, true, null, sim,
            new Dictionary<string, ClassificationInfo>(), "APROPRIAVEL_POR_DESTAQUE", None));

        Assert.Equal(AuditStatus.Divergente, o.Status);
        Assert.Contains(o.Findings, f => f.Regra == "R029" && f.NumeroItem == 1 && f.ValorDeclarado == "ausente");
        Assert.DoesNotContain(o.Findings, f => f.NumeroItem == 2);   // sem resultado do motor: indeterminado, sem achado
        Assert.Contains(o.Findings, f => f.NumeroItem == 3 && f.Mensagem.Contains("(SN-009)", StringComparison.Ordinal));
        Assert.Contains(o.Findings, f => f.NumeroItem == 4 && f.Mensagem == "Base negativa" && f.ValorDeclarado == "INCONSISTENCIA_ENTRADA");
        Assert.Contains(o.Findings, f => f.NumeroItem == 5 && f.Mensagem == "Motor do Simples: NAO_CALCULADO.");
        var r030 = Assert.Single(o.Findings, f => f.Regra == "R030");
        Assert.Equal("20.00/99.00", r030.ValorDeclarado);
        Assert.Equal("20.00/48.00", r030.ValorReferencia);
        Assert.Equal(new TaxSummary(12m, 5m, 0), o.Recalculado);

        // Só o item sem resultado: indeterminado (nenhum achado determinístico).
        var only = DocumentAuditor.Audit(new AuditInput(Doc("1", null, Sn(2)), "OUTBOUND", false, true, null, sim,
            new Dictionary<string, ClassificationInfo>(), "APROPRIAVEL_POR_DESTAQUE", None));
        Assert.Equal(AuditStatus.Indeterminado, only.Status);
    }

    [Fact]
    public void Entrada_ignora_classificacao_desconhecida_e_item_sem_cClassTrib_no_credito()
    {
        var itens = new[] { Reg(1, cls: "999999"), Reg(2, cls: null) with { Cst = null }, Reg(3) };
        var calc = new EngineCalculation([Eng(1, new TaxLine(11.2m, 112m, null, null), new TaxLine(0.05m, 0.5m, null, null)),
                Eng(3, new TaxLine(11.2m, 112m, null, null), new TaxLine(0.05m, 0.5m, null, null))],
            new EngineTotals(2000m, 224m, 1m, 1m, 2m, 0), [], Version);
        var cls = new Dictionary<string, ClassificationInfo> { ["000001"] = new("000001", "Tributação integral", false, true, false) };
        var o = DocumentAuditor.Audit(new AuditInput(Doc("3", null, itens), "INBOUND", true, false, calc, null, cls, "APROPRIAVEL_POR_DESTAQUE", None));

        Assert.DoesNotContain(o.Findings, f => f.Regra == "R021");
        Assert.Equal(112m, o.CreditoPotencial);   // só a CBS do item 3 (IBS sem direito a crédito; item 1 sem classificação)
        Assert.Equal("APROPRIAVEL_POR_DESTAQUE", o.CreditoStatus);
        Assert.Equal(AuditStatus.Indeterminado, o.Status);   // item 2 sem grupo IBSCBS
    }
}
