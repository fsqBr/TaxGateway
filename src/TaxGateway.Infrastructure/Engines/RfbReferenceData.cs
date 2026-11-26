using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using TaxGateway.Core.Engines;

namespace TaxGateway.Infrastructure.Engines;

/// <summary>
/// Espelho do cadastro de entes do motor (27 UFs e 5.570 municípios, SDD §8.6) por bundle: carregado de
/// <c>/dados-abertos</c> uma vez e guardado no Valkey em <c>rfb:{bundle}:municipios</c>, então um bundle novo
/// invalida tudo sem purge manual. Consulta em memória (FrozenDictionary), sem rede no caminho da requisição.
/// </summary>
public sealed partial class RfbReferenceData(
    IHttpClientFactory httpFactory, IConnectionMultiplexer valkey, IOptions<EngineOptions> options, ILogger<RfbReferenceData> log)
    : BackgroundService, IRfbReferenceData
{
    public const string Client = "rtc-regular-reference";
    private FrozenDictionary<int, string>? _ufByMunicipio;

    public string Bundle => options.Value.Bundle;

    public bool IsLoaded => Volatile.Read(ref _ufByMunicipio) is not null;

    public int MunicipioCount => Volatile.Read(ref _ufByMunicipio)?.Count ?? 0;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateOnly, Task<IReadOnlyDictionary<string, TaxGateway.Core.Audit.ClassificationInfo>>> _classes = new();

    public Task<IReadOnlyDictionary<string, TaxGateway.Core.Audit.ClassificationInfo>> ClassificationsAsync(DateOnly onDate, CancellationToken ct)
    {
        var task = _classes.GetOrAdd(onDate, d => LoadClassesAsync(d));
        if (task.IsFaulted)
        {
            _classes.TryRemove(onDate, out _);   // nova tentativa na próxima chamada
        }

        return task.WaitAsync(ct);
    }

    private async Task<IReadOnlyDictionary<string, TaxGateway.Core.Audit.ClassificationInfo>> LoadClassesAsync(DateOnly date)
    {
        var http = httpFactory.CreateClient(Client);
        using var doc = await JsonDocument.ParseAsync(await http.GetStreamAsync(
            $"calculadora/dados-abertos/classificacoes-tributarias/cbs-ibs?data={date:yyyy-MM-dd}").ConfigureAwait(false)).ConfigureAwait(false);
        var map = new Dictionary<string, TaxGateway.Core.Audit.ClassificationInfo>(StringComparer.Ordinal);
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            var code = c.GetProperty("codigo").GetString()!;
            map[code] = new TaxGateway.Core.Audit.ClassificationInfo(code,
                c.TryGetProperty("descricaoTratamentoTributario", out var t) ? t.GetString() ?? "" : "",
                B(c, "possuiPercentualReducao"), B(c, "indicaApropriacaoCreditoAdquirenteCbs"), B(c, "indicaApropriacaoCreditoAdquirenteIbs"));
        }

        return map;
    }

    public string? UfOfMunicipio(int municipioIbge)
    {
        var map = Volatile.Read(ref _ufByMunicipio) ?? throw new EngineUnavailableException("Cadastro de municípios ainda não carregado.");
        return map.GetValueOrDefault(municipioIbge);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested && !IsLoaded)
        {
            try
            {
                var map = await LoadAsync(stoppingToken).ConfigureAwait(false);
                Volatile.Write(ref _ufByMunicipio, map.ToFrozenDictionary());
                LogLoaded(log, map.Count, Bundle);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or RedisException or InvalidOperationException
                                       && !stoppingToken.IsCancellationRequested)
            {
                LogRetry(log, ex, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task<Dictionary<int, string>> LoadAsync(CancellationToken ct)
    {
        var key = $"rfb:{Bundle}:municipios";
        var db = valkey.GetDatabase();
        try
        {
            var cached = await db.StringGetAsync(key).ConfigureAwait(false);
            if (!cached.IsNullOrEmpty)
            {
                return JsonSerializer.Deserialize(cached.ToString(), ReferenceJsonContext.Default.DictionaryInt32String)!;
            }
        }
        catch (RedisException ex)
        {
            LogRetry(log, ex, 0);   // sem L2: busca no motor
        }

        var http = httpFactory.CreateClient(Client);
        var ufs = await JsonSerializer.DeserializeAsync(
            await http.GetStreamAsync("calculadora/dados-abertos/ufs", ct).ConfigureAwait(false),
            ReferenceJsonContext.Default.ListUfDto, ct).ConfigureAwait(false) ?? [];
        var map = new Dictionary<int, string>(5600);
        foreach (var uf in ufs)
        {
            var municipios = await JsonSerializer.DeserializeAsync(
                await http.GetStreamAsync($"calculadora/dados-abertos/ufs/municipios?siglaUf={uf.Sigla}", ct).ConfigureAwait(false),
                ReferenceJsonContext.Default.ListMunicipioDto, ct).ConfigureAwait(false) ?? [];
            foreach (var m in municipios)
            {
                map[m.Codigo] = uf.Sigla;
            }
        }

        // 27 UFs + "EX" (exterior, município 9999999), como no cadastro do motor.
        if (ufs.Count(u => u.Sigla != "EX") != 27 || map.Count < 5000)
        {
            throw new InvalidOperationException($"Cadastro incompleto do motor: {ufs.Count} UFs, {map.Count} municípios.");
        }

        try
        {
            await db.StringSetAsync(key, JsonSerializer.Serialize(map, ReferenceJsonContext.Default.DictionaryInt32String), TimeSpan.FromDays(30)).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogRetry(log, ex, 0);
        }

        return map;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cadastro de entes carregado: {Count} municípios (bundle {Bundle}).")]
    private static partial void LogLoaded(ILogger logger, int count, string bundle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao carregar o cadastro de entes do motor; nova tentativa em {Seconds} s.")]
    private static partial void LogRetry(ILogger logger, Exception ex, double seconds);
}

internal sealed record UfDto(int Codigo, string Sigla);

internal sealed record MunicipioDto(int Codigo);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<UfDto>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<MunicipioDto>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<int, string>))]
internal sealed partial class ReferenceJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
