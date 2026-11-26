using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using TaxGateway.Api.Http;
using TaxGateway.Contracts;
using TaxGateway.Core.Engines;
using TaxGateway.Core.Fiscal;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Simulation;

public sealed class SimulationOptions
{
    public const string Section = "Simulation";

    /// <summary>Máximo de itens por simulação síncrona (D-215). Acima disso: lote (Fase 6).</summary>
    public int MaxItems { get; set; } = 500;
}

/// <summary>Resultado pronto para o cliente + o que precisa ser persistido.</summary>
public sealed record SimulationOutcome(SimulacaoResponse Response, byte[] Json);

/// <summary>
/// <c>POST /v1/calculo/simular</c> (SDD §6.1): valida, resolve alíquotas pelo catálogo (§8.5), chama o motor,
/// mapeia a resposta e enfileira a gravação + cobrança. Cache determinístico por (bundle, snapshot, request do motor).
/// </summary>
public sealed partial class SimulationService(
    IRfbTaxEngineAdapter engine,
    ISimplesEngine simplesEngine,
    IRfbReferenceData reference,
    IParamSnapshotProvider snapshots,
    TenantCnpjService cnpjs,
    SimulationWriter writer,
    TaxGateway.Infrastructure.Auth.CacheSeal seal,
    [FromKeyedServices(TaxGateway.Infrastructure.RuntimeServiceCollectionExtensions.ValkeyCacheKey)] IConnectionMultiplexer valkey,   // D-P26
    IOptions<EngineOptions> engineOptions,
    IOptions<SimulationOptions> options,
    TimeProvider clock,
    ILogger<SimulationService> log)
{
    private static readonly TimeSpan L1Ttl = TimeSpan.FromSeconds(60);

    /// <summary>D-P19: L1 próprio limitado em BYTES (64 MB): uma resposta de 500 itens tem dezenas de KB.</summary>
    private static readonly MemoryCache L1 = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024 });

    private static MemoryCacheEntryOptions L1Entry(byte[] json) => new() { AbsoluteExpirationRelativeToNow = L1Ttl, Size = json.Length };
    private static readonly TimeSpan L2Ttl = TimeSpan.FromHours(24);
    private static readonly TimeZoneInfo Brasilia = ResolveBrasilia();

    public async Task<SimulationOutcome> SimulateAsync(Caller caller, SimulacaoRequest req, string rawRequest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(req);
        var receivedAt = clock.GetUtcNow();
        var v = Validate(req);
        var snapshot = snapshots.Current;

        caller.EnsureOnBehalfOf(v.Cnpj);
        if (!await cnpjs.EnsureAllowedAsync(caller.Identity.Tenant, v.Cnpj, caller.Identity.AutoProvisionCnpj, ct).ConfigureAwait(false))
        {
            throw new ApiProblemException(403, "ERR_CNPJ_NOT_PROVISIONED", "CNPJ não cadastrado para este tenant",
                "Cadastre o CNPJ ou habilite o auto-provisionamento.");
        }

        // Roteamento por regime (SDD §8.1; D-502): tabela engine_routing do snapshot, nunca fixa no código.
        var route = snapshot.Routing.FirstOrDefault(r => r.Crt == v.Crt && r.TpRegimeIbsCbs == v.TpRegime);
        if (route is null || route.Engines.Count == 0)
        {
            throw new ApiProblemException(422, "ERR_REGIME_INDETERMINADO", "Combinação de regime sem cálculo definido",
                $"CRT {v.Crt} / tpRegimeIBSCBS {v.TpRegime}: {(route is null ? "sem rota na tabela engine_routing" : "sem motor aplicável")}.");
        }

        var usaRegular = route.Engines.Contains("REGULAR");
        var usaSimples = route.Engines.Contains("SIMPLES") && route.SimplesMode is "SN_PLENO" or "SN_SUBLIMITE";
        var regime = route.SimplesMode ?? "REGULAR";

        if (!reference.IsLoaded)
        {
            throw new EngineUnavailableException("Cadastro de municípios do motor ainda não carregado.");
        }

        var uf = reference.UfOfMunicipio(v.Municipio);
        if (uf is null || !string.Equals(uf, v.Uf, StringComparison.Ordinal))
        {
            throw new ApiProblemException(422, "ERR_MUNICIPIO_INVALIDO", "Município IBGE inválido para a UF",
                uf is null ? $"O município {v.Municipio} não existe no cadastro do bundle {reference.Bundle}."
                           : $"O município {v.Municipio} pertence à UF {uf}, não a {v.Uf}.");
        }

        // Sublimite (CRT 2): CBS no DAS (motor do Simples) e IBS pelo regime regular com a CBS zerada (§8.1).
        var (operation, sources) = usaRegular ? BuildOperation(req, v, snapshot, zeroCbs: usaSimples) : (null, new Dictionary<int, ItemSources>());
        var simplesOp = usaSimples ? BuildSimplesOperation(req, v) : null;
        var cacheKey = CacheKey(regime, operation, simplesOp, snapshot.Id);
        var response = await FromCacheAsync(cacheKey).ConfigureAwait(false);
        if (response is null)
        {
            var calc = operation is null ? null : await engine.CalculateAsync(operation, ct).ConfigureAwait(false);
            var sim = simplesOp is null ? null : await simplesEngine.CalculateAsync(simplesOp, ct).ConfigureAwait(false);
            response = calc is not null ? Map(calc, sources, snapshot) : MapSimplesOnly(sim!, snapshot);
            if (calc is not null && sim is not null)
            {
                AttachSimples(response, sim);
            }

            response.RegimeCalculo = regime;
            await ToCacheAsync(cacheKey, response).ConfigureAwait(false);
        }

        var id = Guid.CreateVersion7();
        response.SimulationId = $"sim_{id:N}";
        var json = JsonSerializer.SerializeToUtf8Bytes(response, ContractsJsonContext.Default.SimulacaoResponse);

        writer.Enqueue(new SimulationRecord(
            caller.Identity.Tenant, id, BillingMonth(receivedAt), v.Cnpj, response.RegimeCalculo, response.RfbEngineVersion.Bundle,
            snapshot.Id, rawRequest, System.Text.Encoding.UTF8.GetString(json), receivedAt));
        return new SimulationOutcome(response, json);
    }

    /// <summary>Competência = mês de recebimento em America/Sao_Paulo (SDD §10.1).</summary>
    public static DateOnly BillingMonth(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Brasilia);
        return new DateOnly(local.Year, local.Month, 1);
    }

    private sealed record Validated(string Cnpj, string Regime, DateTimeOffset FatoGerador, int Municipio, string Uf, string Crt, string TpRegime);

    /// <summary>Fonte de cada alíquota por item: MOTOR, CATALOGO (com a linha) ou CLIENTE.</summary>
    private sealed record ItemSources(RateSource Cbs, RateSource IbsUf, RateSource IbsMun);

    private sealed record RateSource(string Fonte, ResolvedRate? Catalog);

    private Validated Validate(SimulacaoRequest r)
    {
        var errors = new List<CampoErroDto>();
        void Err(string campo, string codigo, string msg) => errors.Add(new CampoErroDto { Campo = campo, Codigo = codigo, Mensagem = msg });

        if (!Cnpj.TryNormalize(r.CnpjEmitente, out var cnpj))
        {
            Err("cnpj_emitente", "INVALIDO", "CNPJ ausente ou com dígito verificador inválido (numérico ou alfanumérico).");
        }

        if (r.RegimeTributario is not ("REGIME_REGULAR" or "SIMPLES_NACIONAL"))
        {
            Err("regime_tributario", "INVALIDO", "Use REGIME_REGULAR ou SIMPLES_NACIONAL.");
        }

        DateTimeOffset fato = default;
        if (r.DataFatoGerador is null || !OffsetDateTime().IsMatch(r.DataFatoGerador)
            || !DateTimeOffset.TryParse(r.DataFatoGerador, CultureInfo.InvariantCulture, DateTimeStyles.None, out fato))
        {
            Err("data_fato_gerador", "INVALIDO", "Data/hora ISO 8601 com offset obrigatória (ex.: 2027-03-10T10:00:00-03:00).");
        }
        else if (fato.Year is < 2026 or > 2100)
        {
            // D-J05: IBS/CBS começam em 2026; datas absurdas (ano 1 ou 9999) viravam erro do motor ou overflow.
            Err("data_fato_gerador", "FORA_DO_INTERVALO", "A reforma vale a partir de 2026; use uma data entre 2026 e 2100.");
        }

        if (r.MunicipioIbge is not ((>= 1100000 and <= 5399999) or 9999999))
        {
            Err("municipio_ibge", "INVALIDO", "Código IBGE de 7 dígitos obrigatório (9999999 = exterior, com uf EX).");
        }

        if (r.Uf is null || !UfFormat().IsMatch(r.Uf))
        {
            Err("uf", "INVALIDO", "Sigla da UF obrigatória (2 letras maiúsculas).");
        }

        if (r.CompraGovernamental is { TipoEnte: null })
        {
            Err("compra_governamental.tipo_ente", "OBRIGATORIO", "Informe o tipo de ente governamental.");
        }

        var itens = r.Itens ?? [];
        if (itens.Count == 0)
        {
            Err("itens", "OBRIGATORIO", "Informe ao menos um item.");
        }
        else if (itens.Count > options.Value.MaxItems)
        {
            throw new ApiProblemException(422, "ERR_TOO_MANY_ITEMS", "Itens demais para a simulação síncrona",
                $"Máximo de {options.Value.MaxItems} itens por chamada. Para documentos maiores, use o processamento em lote.");
        }

        var desde = engineOptions.Value.Regular.AliquotasNominaisDesde;
        var dataLocal = DateOnly.FromDateTime(fato.DateTime);
        var numeros = new HashSet<int>();
        for (var i = 0; i < itens.Count; i++)
        {
            var it = itens[i];
            var p = $"itens[{i}]";
            if (it is null)
            {
                Err(p, "OBRIGATORIO", "Item nulo.");
                continue;
            }

            var numero = it.Numero ?? (i + 1);
            if (numero <= 0 || !numeros.Add(numero))
            {
                Err($"{p}.numero", "INVALIDO", "Número do item deve ser positivo e único.");
            }

            if (it.CstIbsCbs is null || !Cst().IsMatch(it.CstIbsCbs))
            {
                Err($"{p}.cst_ibs_cbs", "INVALIDO", "CST de 3 dígitos obrigatório.");
            }

            if (it.CClassTrib is null || !CClassTrib().IsMatch(it.CClassTrib))
            {
                Err($"{p}.c_class_trib", "INVALIDO", "cClassTrib de 6 dígitos obrigatório.");
            }

            if (it.Ncm is not null && !Ncm().IsMatch(it.Ncm))
            {
                Err($"{p}.ncm", "INVALIDO", "NCM com 8 dígitos.");
            }

            if (it.Nbs is not null && !Nbs().IsMatch(it.Nbs))
            {
                Err($"{p}.nbs", "INVALIDO", "NBS com 9 dígitos.");
            }

            if (it.ValorBaseCalculo is not { } vbc || vbc < 0)
            {
                Err($"{p}.valor_base_calculo", "INVALIDO", "Valor da base de cálculo obrigatório e não negativo.");
            }

            if (it.ImpostoSeletivo is { } s && (s.Cst is null || !Cst().IsMatch(s.Cst) || s.CClassTrib is null
                                                || !CClassTrib().IsMatch(s.CClassTrib) || s.ValorBaseCalculo is not >= 0))
            {
                Err($"{p}.imposto_seletivo", "INVALIDO", "Informe cst, c_class_trib e valor_base_calculo do Imposto Seletivo.");
            }

            if (it.AliquotasNominais is { } a)
            {
                if (dataLocal < desde)
                {
                    Err($"{p}.aliquotas_nominais", "NAO_PERMITIDO",
                        $"Alíquotas nominais só podem ser informadas para fato gerador a partir de {desde:dd/MM/yyyy}: antes disso valem as alíquotas da base oficial.");
                }

                foreach (var (name, value) in new[] { ("cbs", a.Cbs), ("ibs_uf", a.IbsUf), ("ibs_mun", a.IbsMun), ("is_ad_valorem", a.IsAdValorem), ("is_ad_rem", a.IsAdRem) })
                {
                    if (value is < 0 or > 100)
                    {
                        Err($"{p}.aliquotas_nominais.{name}", "INVALIDO", "Alíquota em % entre 0 e 100.");
                    }
                }
            }
        }

        // Regime/CRT (D-502): regime regular = CRT 3; Simples = CRT 1 (padrão) ou 2. MEI (CRT 4) não destaca IBS/CBS.
        var crt = r.Crt ?? (r.RegimeTributario == "SIMPLES_NACIONAL" ? "1" : "3");
        if ((r.RegimeTributario == "REGIME_REGULAR" && crt != "3") || (r.RegimeTributario == "SIMPLES_NACIONAL" && crt is not ("1" or "2")))
        {
            Err("crt", "INVALIDO", "REGIME_REGULAR usa CRT 3; SIMPLES_NACIONAL usa CRT 1 ou 2 (MEI não destaca IBS/CBS).");
        }

        if (r.TpRegimeIbsCbs is not (null or "1" or "2"))
        {
            Err("tp_regime_ibs_cbs", "INVALIDO", "Use 1 (apuração pelo regime regular) ou 2.");
        }

        if (r.RegimeTributario == "SIMPLES_NACIONAL" && r.TpRegimeIbsCbs is null)
        {
            for (var i = 0; i < itens.Count; i++)
            {
                var it = itens[i];
                if (it is null)
                {
                    continue;
                }

                if (it.CodigoAtividadeSn is not { Length: 2 })
                {
                    Err($"itens[{i}].codigo_atividade_sn", "OBRIGATORIO", "Código de atividade do Simples (cAtivSN, 2 caracteres).");
                }

                if (it.AliquotasSimples?.Cbs is not >= 0 || (crt == "1" && it.AliquotasSimples?.Ibs is not >= 0))
                {
                    Err($"itens[{i}].aliquotas_simples", "OBRIGATORIO",
                        crt == "1" ? "Informe as alíquotas efetivas de IBS e CBS do DAS (%)." : "Informe a alíquota efetiva de CBS do DAS (%); o IBS sai do DAS no sublimite.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ApiProblemException(400, "ERR_VALIDATION", "Requisição inválida", "Ver errors.", errors);
        }

        return new Validated(cnpj!, r.RegimeTributario!, fato, r.MunicipioIbge!.Value, r.Uf!, crt, r.TpRegimeIbsCbs ?? "-");
    }

    private static SimplesOperation BuildSimplesOperation(SimulacaoRequest r, Validated v)
    {
        var itens = r.Itens!.Select((it, i) => new SimplesItemInput(
            it.Numero ?? (i + 1), it.CodigoAtividadeSn, it.CClassTrib!, it.Ncm, it.Nbs, it.ValorBaseCalculo!.Value,
            0m, v.Crt == "1" ? it.AliquotasSimples?.Ibs : null, it.AliquotasSimples?.Cbs)).ToList();
        return new SimplesOperation(DateOnly.FromDateTime(v.FatoGerador.DateTime), r.TipoDocumento ?? 55, v.Crt,
            v.TpRegime == "-" ? null : v.TpRegime, r.CompraGovernamental?.TipoEnte, itens);
    }

    private (EngineOperation, Dictionary<int, ItemSources>) BuildOperation(SimulacaoRequest r, Validated v, ParamSnapshot snapshot, bool zeroCbs = false)
    {
        // Data do fato gerador = data LOCAL do offset informado (SDD §8.6); nunca converter para UTC antes.
        var data = DateOnly.FromDateTime(v.FatoGerador.DateTime);
        var usaNominais = data >= engineOptions.Value.Regular.AliquotasNominaisDesde;
        var sources = new Dictionary<int, ItemSources>();
        var itens = new List<EngineItemInput>(r.Itens!.Count);
        var faltando = new List<CampoErroDto>();

        for (var i = 0; i < r.Itens.Count; i++)
        {
            var it = r.Itens[i];
            var numero = it.Numero ?? (i + 1);
            NominalRates? nominais = null;
            ItemSources src;
            if (!usaNominais)
            {
                var motor = new RateSource("MOTOR", null);
                src = new ItemSources(motor, motor, motor);
            }
            else
            {
                var c = it.AliquotasNominais;
                (decimal? Value, RateSource Source) Pick(decimal? client, Tributo t, string campo)
                {
                    if (client is not null)
                    {
                        return (client, new RateSource("CLIENTE", null));
                    }

                    var resolved = snapshot.Rates.Resolve(t, v.Municipio, data);
                    if (resolved is null)
                    {
                        faltando.Add(new CampoErroDto
                        {
                            Campo = $"itens[{i}].aliquotas_nominais.{campo}",
                            Codigo = "ALIQUOTA_INDEFINIDA",
                            Mensagem = $"Sem alíquota de {campo} vigente em {data:dd/MM/yyyy} no catálogo; informe aliquotas_nominais.{campo}.",
                        });
                        return (null, new RateSource("CATALOGO", null));
                    }

                    return (resolved.Aliquota, new RateSource("CATALOGO", resolved));
                }

                var cbs = zeroCbs ? (Value: (decimal?)0m, Source: new RateSource("SUBLIMITE_CBS_NO_DAS", null)) : Pick(c?.Cbs, Tributo.Cbs, "cbs");
                var ibsUf = Pick(c?.IbsUf, Tributo.IbsUf, "ibs_uf");
                var ibsMun = Pick(c?.IbsMun, Tributo.IbsMun, "ibs_mun");
                nominais = new NominalRates(cbs.Value, ibsUf.Value, ibsMun.Value, c?.IsAdValorem, c?.IsAdRem);
                src = new ItemSources(cbs.Source, ibsUf.Source, ibsMun.Source);
            }

            sources[numero] = src;
            EngineIsInput? isInput = it.ImpostoSeletivo is { } s
                ? new EngineIsInput(s.Cst!, s.CClassTrib!, s.ValorBaseCalculo!.Value, s.Quantidade, s.Unidade)
                : null;
            itens.Add(new EngineItemInput(numero, it.Ncm, it.Nbs, it.CstIbsCbs!, it.CClassTrib!, it.ValorBaseCalculo,
                it.Quantidade, it.Unidade, isInput, nominais));
        }

        if (faltando.Count > 0)
        {
            throw new ApiProblemException(422, "ERR_ALIQUOTA_INDEFINIDA", "Alíquota nominal não definida para a data",
                "O catálogo não tem alíquota vigente para algum tributo; informe aliquotas_nominais.", faltando);
        }

        CompraGovernamental? gov = r.CompraGovernamental is { TipoEnte: { } te } g ? new CompraGovernamental(te, g.TipoOperacao) : null;
        // Id fixo: o id da operação não muda o cálculo e não pode quebrar o cache determinístico.
        return (new EngineOperation("tg", v.FatoGerador, v.Municipio, v.Uf, r.TipoDocumento, gov, itens), sources);
    }

    private static SimulacaoResponse Map(EngineCalculation calc, Dictionary<int, ItemSources> sources, ParamSnapshot snapshot)
    {
        var resp = new SimulacaoResponse
        {
            RfbEngineVersion = new EngineVersionDto
            {
                Bundle = calc.Version.Bundle,
                Engine = "REGULAR",
                Tag = calc.Version.AppVersion,
                DbVersion = calc.Version.DbVersion,
            },
            ParamSnapshotId = snapshot.Id,
            OperacaoDfe = calc.OperacaoDfe,
            Totais = new TotaisDto
            {
                ValorBaseTotal = calc.Totais.BaseCalculo,
                CbsValor = calc.Totais.Cbs,
                IbsUfValor = calc.Totais.IbsUf,
                IbsMunValor = calc.Totais.IbsMun,
                IbsTotalValor = calc.Totais.Ibs,
                ImpostoSeletivoValor = calc.Totais.ImpostoSeletivo,
                CargaTributariaEfetivaPercent = calc.Totais.BaseCalculo > 0
                    ? Math.Round((calc.Totais.Cbs + calc.Totais.Ibs + calc.Totais.ImpostoSeletivo) * 100m / calc.Totais.BaseCalculo, 2, MidpointRounding.ToEven)
                    : null,
            },
        };

        var provisorias = new Dictionary<Guid, RateRow>();
        foreach (var it in calc.Itens)
        {
            var src = sources.GetValueOrDefault(it.Numero);
            resp.Itens.Add(new ItemResultadoDto
            {
                NumeroItem = it.Numero,
                Estado = it.Estado,
                CstIbsCbs = it.Cst,
                CClassTrib = it.CClassTrib,
                BaseCalculo = it.BaseCalculo,
                Cbs = Line(it.Cbs, src?.Cbs, provisorias),
                IbsUf = Line(it.IbsUf, src?.IbsUf, provisorias),
                IbsMun = Line(it.IbsMun, src?.IbsMun, provisorias),
                IbsTotalValor = it.IbsTotal,
                ImpostoSeletivo = it.ImpostoSeletivo is { } s
                    ? new ImpostoSeletivoResultadoDto { BaseCalculo = s.BaseCalculo, AliquotaAdValorem = s.AliquotaAdValorem, AliquotaAdRem = s.AliquotaAdRem, Valor = s.Valor }
                    : null,
                Erros = it.Erros.Select(e => new ErroItemDto { Codigo = e.Codigo, Titulo = e.Titulo, Detalhe = e.Detalhe, Campo = EngineFieldNames.Map(e.Ponteiro) }).ToList(),
                TributosDfe = it.TributosDfe,
            });
        }

        foreach (var w in calc.AvisosDadosSimulados)
        {
            resp.Avisos.Add(new AvisoDto
            {
                Codigo = $"DADOS_SIMULADOS_{w}",
                Mensagem = "O motor da RFB sinalizou dados simulados (header x-warning-dados-simulados). Com alíquotas do catálogo TaxGateway ou do cliente, o item sai como CALCULO_SIMULADO; isso é esperado e não é erro.",
            });
        }

        foreach (var p in provisorias.Values)
        {
            resp.Avisos.Add(new AvisoDto
            {
                Codigo = "ALIQUOTA_PROVISORIA",
                Mensagem = $"Alíquota de {TributoNome(p.Tributo)} {p.Aliquota.ToString("0.00##", CultureInfo.InvariantCulture)}% é provisória: {p.AtoLegal}.",
            });
        }

        return resp;
    }

    private static string TributoNome(Tributo t) => t switch
    {
        Tributo.Cbs => "CBS",
        Tributo.IbsUf => "IBS estadual",
        Tributo.IbsMun => "IBS municipal",
        _ => "IS",
    };

    private static TributoResultadoDto? Line(TaxLine? line, RateSource? src, Dictionary<Guid, RateRow> provisorias)
    {
        if (line is null)
        {
            return null;
        }

        if (src?.Catalog is { Provisoria: true } c)
        {
            provisorias[c.Referencia.Id] = c.Referencia;
        }

        return new TributoResultadoDto
        {
            Aliquota = line.Aliquota,
            Valor = line.Valor,
            PercentualReducao = line.PercentualReducao,
            AliquotaEfetiva = line.AliquotaEfetiva,
            FonteAliquota = src?.Fonte ?? "MOTOR",
            CatalogoId = src?.Catalog?.CatalogId,
        };
    }

    private static string CacheKey(string regime, EngineOperation? op, SimplesOperation? sop, long snapshotId)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(System.Text.Encoding.UTF8.GetBytes($"{regime}|{snapshotId}|"));
        if (op is not null)
        {
            h.AppendData(RegularEngineAdapter.CanonicalRequest(op));
        }

        h.AppendData("|"u8);
        if (sop is not null)
        {
            h.AppendData(SimplesEngineAdapter.Write(sop));
        }

        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    private static SimplesResultadoDto ToSimplesDto(SimplesItemResult s) => new()
    {
        Estado = s.Estado,
        MotivoEstado = s.MotivoEstado,
        BaseCalculo = s.BaseCalculo,
        AliquotaIbs = s.AliquotaIbs,
        ValorIbs = s.ValorIbs,
        AliquotaCbs = s.AliquotaCbs,
        ValorCbs = s.ValorCbs,
        TributosSimples = s.TributosSimples,
        Erros = s.Erros.Select(e => new ErroItemDto { Codigo = e.Codigo, Titulo = e.Titulo, Detalhe = e.Detalhe, Campo = EngineFieldNames.Map(e.Ponteiro) }).ToList(),
    };

    /// <summary>Simples pleno (CRT 1): IBS e CBS no DAS, só o motor do Simples.</summary>
    private static SimulacaoResponse MapSimplesOnly(SimplesCalculation sim, ParamSnapshot snapshot)
    {
        var t = sim.Totais;
        var resp = new SimulacaoResponse
        {
            RfbEngineVersion = new EngineVersionDto { Bundle = sim.Version.Bundle, Engine = "SIMPLES", Tag = sim.Version.AppVersion, DbVersion = sim.Version.DbVersion },
            ParamSnapshotId = snapshot.Id,
            Totais = new TotaisDto
            {
                ValorBaseTotal = t.BaseCalculo,
                CbsValor = t.Cbs,
                IbsTotalValor = t.Ibs,
                ImpostoSeletivoValor = t.ImpostoSeletivo,
                CargaTributariaEfetivaPercent = t.BaseCalculo > 0 ? Math.Round((t.Cbs + t.Ibs) * 100m / t.BaseCalculo, 2, MidpointRounding.ToEven) : null,
            },
        };
        foreach (var s in sim.Itens)
        {
            var dto = ToSimplesDto(s);
            resp.Itens.Add(new ItemResultadoDto { NumeroItem = s.Numero, Estado = s.Estado, BaseCalculo = s.BaseCalculo, Simples = dto, Erros = dto.Erros });
        }

        return resp;
    }

    /// <summary>Sublimite (CRT 2): soma a CBS do DAS à resposta do regime regular (que traz só o IBS).</summary>
    private static void AttachSimples(SimulacaoResponse resp, SimplesCalculation sim)
    {
        var byItem = sim.Itens.ToDictionary(i => i.Numero);
        foreach (var it in resp.Itens)
        {
            if (byItem.TryGetValue(it.NumeroItem, out var s))
            {
                it.Simples = ToSimplesDto(s);
                it.Erros.AddRange(it.Simples.Erros);
            }
        }

        resp.Totais.CbsValor += sim.Totais.Cbs;
        resp.Totais.CargaTributariaEfetivaPercent = resp.Totais.ValorBaseTotal > 0
            ? Math.Round((resp.Totais.CbsValor + resp.Totais.IbsTotalValor + resp.Totais.ImpostoSeletivoValor) * 100m / resp.Totais.ValorBaseTotal, 2, MidpointRounding.ToEven)
            : null;
        resp.RfbEngineVersion.Engine = "REGULAR+SIMPLES";
    }

    private async Task<SimulacaoResponse?> FromCacheAsync(string key)
    {
        if (L1.TryGetValue(key, out byte[]? json))
        {
            TaxGateway.Infrastructure.TaxGatewayMetrics.SimulationCache.Add(1, new KeyValuePair<string, object?>("camada", "l1"));
            return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.SimulacaoResponse);
        }

        try
        {
            var cacheKey = $"sim:{engineOptions.Value.Bundle}:{key}";
            var v = await valkey.GetDatabase().StringGetAsync(cacheKey).ConfigureAwait(false);
            if (v.IsNullOrEmpty)
            {
                TaxGateway.Infrastructure.TaxGatewayMetrics.SimulationCache.Add(1, new KeyValuePair<string, object?>("camada", "miss"));
                return null;
            }

            // Resultado fiscal compartilhado entre clientes: só vale com o selo (D-P10).
            json = seal.Open(cacheKey, (byte[])v!);
            if (json is null)
            {
                TaxGateway.Infrastructure.TaxGatewayMetrics.CacheTampered.Add(1, new KeyValuePair<string, object?>("cache", "simulacao"));
                return null;
            }

            TaxGateway.Infrastructure.TaxGatewayMetrics.SimulationCache.Add(1, new KeyValuePair<string, object?>("camada", "l2"));

            L1.Set(key, json, L1Entry(json));
            return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.SimulacaoResponse);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(log, ex);
            return null;
        }
    }

    private async Task ToCacheAsync(string key, SimulacaoResponse response)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, ContractsJsonContext.Default.SimulacaoResponse);
        L1.Set(key, json, L1Entry(json));
        try
        {
            var cacheKey = $"sim:{engineOptions.Value.Bundle}:{key}";
            await valkey.GetDatabase().StringSetAsync(cacheKey, seal.Seal(cacheKey, json), L2Ttl, flags: CommandFlags.FireAndForget).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(log, ex);
        }
    }

    private static TimeZoneInfo ResolveBrasilia()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        // Sem horário de verão desde 2019: UTC−03:00 fixo é equivalente.
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetDateTime();

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex UfFormat();

    [GeneratedRegex(@"^[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Cst();

    [GeneratedRegex(@"^[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CClassTrib();

    [GeneratedRegex(@"^[0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ncm();

    [GeneratedRegex(@"^[0-9]{9}$", RegexOptions.CultureInvariant)]
    private static partial Regex Nbs();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache L2 (Valkey) indisponível na simulação.")]
    private static partial void LogCacheUnavailable(ILogger logger, Exception ex);
}
