using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TaxGateway.Api.Http;
using TaxGateway.Api.Simulation;
using TaxGateway.Contracts;
using TaxGateway.Core.Audit;
using TaxGateway.Core.Audit.Rules;
using TaxGateway.Core.Engines;
using TaxGateway.Core.Fiscal;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.Api.Audit;

/// <summary>ConfirmedIndicios: regras INDICIO que o cliente confirmou com contexto (ex.: "R021"), D-505.</summary>
public sealed record AuditRequest(byte[] Xml, string Direction, bool Reprocess, bool SignatureOptional, IReadOnlySet<string>? ConfirmedIndicios = null);

/// <summary>
/// <c>POST /v1/auditoria/xml</c> de saída (SDD §6.2): endurecimento → XSD → assinatura → mapeamento → motor →
/// regras → gravação atômica (§10.2). Replay da mesma chave de acesso, em qualquer mês, devolve a auditoria
/// existente sem cobrança; <c>reprocess=true</c> reaudita sem cobrar.
/// </summary>
public sealed partial class AuditService(
    IRfbTaxEngineAdapter engine,
    ISimplesEngine simplesEngine,
    IRfbReferenceData reference,
    IParamSnapshotProvider snapshots,
    TenantCnpjService cnpjs,
    TenantSettingsService tenantSettings,
    AuditRepository repository,
    CpfAnonymizer cpfAnonymizer,
    NfeSignatureVerifier signatures,
    IOptions<EngineOptions> engineOptions,
    TimeProvider clock,
    ILogger<AuditService> log,
    XmlVault? vault = null)
{
    public async Task<byte[]> AuditAsync(Caller caller, AuditRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(req);
        if (req.Direction is not ("OUTBOUND" or "INBOUND"))
        {
            throw new ApiProblemException(400, "ERR_DIRECTION_INVALIDA", "direction deve ser OUTBOUND ou INBOUND");
        }

        var receivedAt = clock.GetUtcNow();
        ParsedNfe parsed;
        try
        {
            parsed = NfeMapper.Map(SecureXmlLoader.Load(req.Xml));
        }
        catch (XmlRejectedException ex)
        {
            throw new ApiProblemException(400, "ERR_XML_REJEITADO", "XML recusado", ex.Message);
        }

        var d = parsed.Document;
        if (!AccessKey.IsValid(d.ChaveAcesso) || d.Modelo is not ("55" or "65"))
        {
            throw new ApiProblemException(400, "ERR_CHAVE_INVALIDA", "Chave de acesso ou modelo inválido",
                "infNFe/@Id precisa de uma chave de 44 posições com DV válido, modelo 55 ou 65.");
        }

        if (!Cnpj.TryNormalize(d.EmitenteCnpj, out var emitente))
        {
            throw new ApiProblemException(422, "ERR_EMITENTE_NAO_SUPORTADO", "Emitente sem CNPJ válido",
                "Emitente pessoa física (CPF) ainda não é suportado.");
        }

        // CNPJ sujeito (SDD §3.3): o emitente na saída; o destinatário na entrada (é ele quem toma o crédito).
        var tenant = caller.Identity.Tenant;
        string sujeito;
        if (req.Direction == "INBOUND")
        {
            if (!Cnpj.TryNormalize(d.DestinatarioCnpj, out var destino))
            {
                throw new ApiProblemException(422, "ERR_DESTINATARIO_NAO_SUPORTADO", "Destinatário sem CNPJ",
                    "A auditoria de entrada é do adquirente pessoa jurídica (dest/CNPJ).");
            }

            sujeito = destino;
        }
        else
        {
            sujeito = emitente;
        }

        caller.EnsureOnBehalfOf(sujeito);
        if (!await cnpjs.EnsureAllowedAsync(tenant, sujeito, caller.Identity.AutoProvisionCnpj, ct).ConfigureAwait(false))
        {
            throw new ApiProblemException(403, "ERR_CNPJ_NOT_PROVISIONED", $"CNPJ {(req.Direction == "INBOUND" ? "destinatário" : "emitente")} não cadastrado para este tenant");
        }

        var existing = await repository.FindLatestAsync(tenant, req.Direction, d.ChaveAcesso, ct).ConfigureAwait(false);
        if (existing is not null && !req.Reprocess)
        {
            return AsReplay(existing.ResponseJson);
        }

        var snapshot = snapshots.Current;
        var findings = new List<Finding>();
        var avisos = new List<AvisoDto>();
        EngineCalculation? calc = null;
        AuditOutcome outcome;

        var schemaErrors = XmlSecurityCache.Nfe.Validate(parsed.Nfe);
        if (schemaErrors.Count > 0)
        {
            outcome = Fixed(AuditStatus.SchemaError, d, Rule(RuleCatalog.R001, "NFe", string.Join(" | ", schemaErrors.Take(5))));
        }
        else
        {
            var sig = signatures.Verify(parsed.Nfe, parsed.InfNFe, emitente, d.DataEmissao);
            if (!sig.Valid && !(req.SignatureOptional && sig.Failure == SignatureFailure.Ausente))
            {
                outcome = Fixed(AuditStatus.AssinaturaInvalida, d, Rule(RuleCatalog.R002, "NFe/Signature", $"{sig.Failure}: {sig.Detail}"));
            }
            else
            {
                if (!sig.Valid)
                {
                    avisos.Add(new AvisoDto { Codigo = "ASSINATURA_AUSENTE", Mensagem = "XML sem assinatura auditado no modo assinatura=opcional." });
                }

                if (sig.Valid && !sig.RevocationChecked)
                {
                    avisos.Add(new AvisoDto { Codigo = "REVOGACAO_NAO_VERIFICADA", Mensagem = "LCR do certificado ainda não disponível no cache; a revogação será verificada nas próximas auditorias." });
                }

                if (!d.Autorizada)
                {
                    avisos.Add(new AvisoDto { Codigo = "SEM_AUTORIZACAO", Mensagem = "XML sem protNFe (pré-transmissão): autorização da SEFAZ não verificada." });
                }

                (outcome, calc) = await RecalculateAsync(d, req, snapshot, ct).ConfigureAwait(false);
            }
        }

        findings.AddRange(outcome.Findings);
        var status = outcome.Status;
        // Cobrança (SDD §10.1): SCHEMA_ERROR/ASSINATURA_INVALIDA cobram (processamento ocorreu); INDETERMINADO e reprocessamento não.
        var billable = !req.Reprocess && status != AuditStatus.Indeterminado
            && (caller.Limits.BillSchemaErrors || status is not (AuditStatus.SchemaError or AuditStatus.AssinaturaInvalida));   // §10.1 por plano (D-703)

        var settings = await tenantSettings.GetAsync(tenant, ct).ConfigureAwait(false);
        var id = Guid.CreateVersion7();
        var month = SimulationService.BillingMonth(receivedAt);
        string? storageKey = null;
        if (settings.XmlRetentionDays is { } days && vault is not null)
        {
            storageKey = await vault.PutAsync(tenant, settings.KmsKeyRef, month, id, req.Xml, days, ct).ConfigureAwait(false);
        }

        var response = new AuditoriaResponse
        {
            AuditId = $"aud_{id:N}",
            ChaveAcesso = d.ChaveAcesso,
            Direction = req.Direction,
            Status = StatusCode(status),
            RfbEngineVersion = calc is null ? new EngineVersionDto { Bundle = engineOptions.Value.Bundle, Engine = "REGULAR" }
                : new EngineVersionDto { Bundle = calc.Version.Bundle, Engine = "REGULAR", Tag = calc.Version.AppVersion, DbVersion = calc.Version.DbVersion },
            ParamSnapshotId = snapshot.Id,
            RegrasVersao = RuleCatalog.RulesVersion,
            IsBillable = billable,
            Reprocessamento = req.Reprocess,
            XmlArmazenado = storageKey is not null,
            ResumoFinanceiro = new ResumoFinanceiroDto
            {
                TributoDeclarado = new ValoresTributoDto { Cbs = outcome.Declarado.Cbs, Ibs = outcome.Declarado.Ibs, Is = outcome.Declarado.Is },
                TributoRecalculado = outcome.Recalculado is { } rc ? new ValoresTributoDto { Cbs = rc.Cbs, Ibs = rc.Ibs, Is = rc.Is } : null,
                Diferenca = outcome.Diferenca,
                CreditoPotencial = outcome.CreditoPotencial,
                CreditoStatus = outcome.CreditoStatus,
            },
            Ocorrencias = findings.Select(ToDto).ToList(),
            Avisos = avisos,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(response, AuditoriaJsonContext.Default.AuditoriaResponse);

        var record = new AuditRecord(
            id, month, req.Direction, d.Modelo == "65" ? "NFCE65" : "NFE55", d.ChaveAcesso, sujeito,
            req.Direction == "INBOUND" ? emitente : (Cnpj.TryNormalize(d.DestinatarioCnpj, out var dest) ? dest : null), d.ConsumidorFinal,
            await cpfAnonymizer.HashAsync(settings.KmsKeyRef, d.DestinatarioCpf, ct).ConfigureAwait(false),
            "REGULAR", response.RfbEngineVersion!.Bundle, RuleCatalog.RulesVersion, snapshot.Id, response.Status,
            DeclaredJson(d), $$"""{"resposta": {{System.Text.Encoding.UTF8.GetString(json)}}}""", outcome.Diferenca,
            JsonSerializer.Serialize(response.Ocorrencias, AuditoriaJsonContext.Default.ListOcorrenciaDto),
            storageKey, SHA256.HashData(req.Xml), billable, req.Reprocess, outcome.CreditoPotencial ?? 0, outcome.CreditoStatus);

        if (await repository.SaveAsync(tenant, record, ct).ConfigureAwait(false) == SaveResult.Replay)
        {
            // Corrida com outra requisição da mesma chave: vale a gravada primeiro.
            LogRaceReplay(log, d.ChaveAcesso);
            var winner = await repository.FindLatestAsync(tenant, req.Direction, d.ChaveAcesso, ct).ConfigureAwait(false);
            return AsReplay(winner!.ResponseJson);
        }

        return json;
    }

    /// <summary>
    /// Recalcula pelo(s) motor(es) da rota do §8.1 (tabela engine_routing): regular, Simples ou os dois (sublimite);
    /// MEI e combinações sem cálculo saem pelas regras R050/I004 (D-503).
    /// </summary>
    private async Task<(AuditOutcome, EngineCalculation?)> RecalculateAsync(DeclaredDocument d, AuditRequest req, ParamSnapshot snapshot, CancellationToken ct)
    {
        var data = DateOnly.FromDateTime(d.DataEmissao.DateTime);   // data local do offset (D-220)
        var classes = req.Direction == "INBOUND"
            ? await reference.ClassificationsAsync(data, ct).ConfigureAwait(false)
            : new Dictionary<string, ClassificationInfo>();
        var confirmed = req.ConfirmedIndicios ?? new HashSet<string>();

        if (d.Crt == "4")
        {
            return (DocumentAuditor.Audit(new AuditInput(d, req.Direction, false, false, null, null, classes, snapshot.CreditStatusOn(data), confirmed)), null);
        }

        var route = snapshot.Route(d.Crt ?? "3", d.TpRegimeIbsCbs);
        if (route is null || route.Engines.Count == 0)
        {
            return (Fixed(AuditStatus.Indeterminado, d, Rule(RuleCatalog.I004, "NFe/infNFe/emit/CRT",
                $"CRT {d.Crt} / tpRegimeIBSCBS {d.TpRegimeIbsCbs ?? "-"}: sem cálculo definido na tabela engine_routing (§8.1).")), null);
        }

        var usaRegular = route.Engines.Contains("REGULAR");
        var usaSimples = route.Engines.Contains("SIMPLES") && route.SimplesMode is "SN_PLENO" or "SN_SUBLIMITE";

        if (!reference.IsLoaded)
        {
            throw new EngineUnavailableException("Cadastro de municípios do motor ainda não carregado.");
        }

        EngineCalculation? calc = null;
        if (usaRegular)
        {
            var uf = reference.UfOfMunicipio(d.MunicipioIncidencia);
            if (uf is null)
            {
                return (Fixed(AuditStatus.Divergente, d, Rule(RuleCatalog.R029, "NFe/infNFe/ide/cMunFG",
                    $"Município {d.MunicipioIncidencia} inexistente no cadastro do bundle.")), null);
            }

            NominalRates? rates = null;
            if (data >= engineOptions.Value.Regular.AliquotasNominaisDesde)
            {
                var cbs = snapshot.Rates.Resolve(Tributo.Cbs, d.MunicipioIncidencia, data);
                var iuf = snapshot.Rates.Resolve(Tributo.IbsUf, d.MunicipioIncidencia, data);
                var imu = snapshot.Rates.Resolve(Tributo.IbsMun, d.MunicipioIncidencia, data);
                if (cbs is null || iuf is null || imu is null)
                {
                    return (Fixed(AuditStatus.Indeterminado, d, Rule(RuleCatalog.I006, "NFe/infNFe/ide/dhEmi",
                        $"Catálogo sem alíquota vigente em {data:dd/MM/yyyy}.")), null);
                }

                // Sublimite: a CBS vai no DAS, então a CBS do regime regular é zero (§8.1).
                rates = new NominalRates(usaSimples ? 0m : cbs.Aliquota, iuf.Aliquota, imu.Aliquota, null, null);
            }

            var operation = NfeMapper.ToEngineOperation(d, uf, i => rates is null ? null
                : rates with { IsAdValorem = i.ImpostoSeletivo?.Aliquota, IsAdRem = i.ImpostoSeletivo?.AdRem });
            if (operation.Itens.Count > 0)
            {
                try
                {
                    calc = await engine.CalculateAsync(operation, ct).ConfigureAwait(false);
                }
                catch (EngineRejectedException ex)
                {
                    var list = ex.Errors.Select(e => Rule(RuleCatalog.R029, "NFe/infNFe", $"{e.Titulo}: {e.Detalhe} ({e.Codigo})")).ToList();
                    return (new AuditOutcome(AuditStatus.Divergente, list, Declared(d), null, 0), null);
                }
            }
        }

        SimplesCalculation? sim = null;
        if (usaSimples)
        {
            var sop = NfeMapper.ToSimplesOperation(d);
            if (sop.Itens.Count > 0)
            {
                try
                {
                    sim = await simplesEngine.CalculateAsync(sop, ct).ConfigureAwait(false);
                }
                catch (EngineRejectedException ex)
                {
                    var list = ex.Errors.Select(e => Rule(RuleCatalog.R029, "NFe/infNFe", $"{e.Titulo}: {e.Detalhe} ({e.Codigo})")).ToList();
                    return (new AuditOutcome(AuditStatus.Divergente, list, Declared(d), null, 0), calc);
                }
            }
        }

        var outcome = DocumentAuditor.Audit(new AuditInput(d, req.Direction, usaRegular, usaSimples, calc, sim, classes,
            snapshot.CreditStatusOn(data), confirmed));
        return (outcome, calc);
    }

    private static AuditOutcome Fixed(AuditStatus status, DeclaredDocument d, Finding f) => new(status, [f], Declared(d), null, 0);

    private static TaxSummary Declared(DeclaredDocument d) =>
        new(d.Itens.Sum(i => (i.Cbs?.Valor ?? 0) + (i.Simples?.ValorCbs ?? 0)), d.Itens.Sum(i => (i.VIbs ?? 0) + (i.Simples?.ValorIbs ?? 0)),
            d.Itens.Sum(i => i.ImpostoSeletivo?.Valor ?? 0));

    private static Finding Rule(RuleDefinition r, string campo, string msg) =>
        new(r.Id, r.Versao, r.Natureza, r.Severidade, null, campo, null, null, msg, r.Fundamentacao, null);

    private static OcorrenciaDto ToDto(Finding f) => new()
    {
        Regra = f.Regra,
        Versao = f.Versao,
        Natureza = f.Natureza switch
        {
            Natureza.Deterministica => "DETERMINISTICA",
            Natureza.Indicio => "INDICIO",
            _ => "INFORMATIVA",
        },
        Severidade = f.Severidade,
        NumeroItem = f.NumeroItem,
        CampoXml = f.CampoXml,
        ValorDeclarado = f.ValorDeclarado,
        ValorReferencia = f.ValorReferencia,
        Mensagem = f.Mensagem,
        Fundamentacao = f.Fundamentacao,
        ImpactoFinanceiroEstimado = f.ImpactoFinanceiro,
    };

    private static string StatusCode(AuditStatus s) => s switch
    {
        AuditStatus.Conforme => "CONFORME",
        AuditStatus.Divergente => "DIVERGENTE",
        AuditStatus.Indicio => "INDICIO",
        AuditStatus.SchemaError => "SCHEMA_ERROR",
        AuditStatus.AssinaturaInvalida => "ASSINATURA_INVALIDA",
        _ => "INDETERMINADO",
    };

    /// <summary>Resposta guardada, marcada como replay e não cobrada.</summary>
    private static byte[] AsReplay(string storedJson)
    {
        var r = JsonSerializer.Deserialize(storedJson, AuditoriaJsonContext.Default.AuditoriaResponse)!;
        r.Replay = true;
        r.IsBillable = false;
        return JsonSerializer.SerializeToUtf8Bytes(r, AuditoriaJsonContext.Default.AuditoriaResponse);
    }

    /// <summary>Declarado em metadados: só valores fiscais por item, sem nome/endereço/CPF (SDD §4.3).</summary>
    private static string DeclaredJson(DeclaredDocument d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("modelo", d.Modelo);
            w.WriteString("data_emissao", d.DataEmissao);
            w.WriteNumber("municipio_incidencia", d.MunicipioIncidencia);
            w.WriteStartArray("itens");
            foreach (var i in d.Itens)
            {
                w.WriteStartObject();
                w.WriteNumber("numero", i.Numero);
                w.WriteString("ncm", i.Ncm);
                w.WriteString("cst", i.Cst);
                w.WriteString("c_class_trib", i.CClassTrib);
                if (i.VBc is { } vbc) { w.WriteNumber("vbc", vbc); }
                if (i.Cbs is { } c) { w.WriteNumber("pcbs", c.Aliquota); w.WriteNumber("vcbs", c.Valor); }
                if (i.IbsUf is { } u) { w.WriteNumber("pibsuf", u.Aliquota); w.WriteNumber("vibsuf", u.Valor); }
                if (i.IbsMun is { } m) { w.WriteNumber("pibsmun", m.Aliquota); w.WriteNumber("vibsmun", m.Valor); }
                if (i.ImpostoSeletivo is { } s) { w.WriteNumber("vis", s.Valor); }
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auditoria concorrente da chave {Chave}: devolvendo a gravada primeiro.")]
    private static partial void LogRaceReplay(ILogger logger, string chave);
}
