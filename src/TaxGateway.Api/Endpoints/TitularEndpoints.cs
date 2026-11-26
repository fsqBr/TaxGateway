using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Direitos do titular (SDD §4.3; LGPD art. 18; D-H05). O tenant é o controlador e pede; o TaxGateway (operador)
/// localiza pelo HMAC do CPF (chave do tenant no cofre) e expurga: apaga o XML do S3 e o vínculo com o CPF. O registro
/// fiscal continua (valores e achados, sem dado pessoal). Toda solicitação fica em <c>titular_requests</c> (sem o CPF).
/// </summary>
public static class TitularEndpoints
{
    public static RouteGroupBuilder MapTitulares(this RouteGroupBuilder v1)
    {
        v1.MapPost("/titulares/localizar", (HttpContext http, TitularDeps deps) => RunAsync(http, deps, expurgar: false))
            .WithMetadata(new RequiredScope(Scopes.TitularesManage));
        v1.MapPost("/titulares/expurgar", (HttpContext http, TitularDeps deps) => RunAsync(http, deps, expurgar: true))
            .WithMetadata(new RequiredScope(Scopes.TitularesManage));
        return v1;
    }

    /// <summary>Dependências agrupadas; o XmlVault é opcional (sem S3, todo tenant é zero_retention).</summary>
    public sealed class TitularDeps(ITenantDb db, TenantSettingsService settings, CpfAnonymizer anonymizer, IServiceProvider sp)
    {
        public ITenantDb Db { get; } = db;
        public TenantSettingsService Settings { get; } = settings;
        public CpfAnonymizer Anonymizer { get; } = anonymizer;
        public XmlVault? Vault { get; } = sp.GetService<XmlVault>();
    }

    private static async Task<IResult> RunAsync(HttpContext http, TitularDeps deps, bool expurgar)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var cpf = body?["cpf"]?.GetValue<string>();
        if (!IsValidCpf(cpf))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "cpf (11 dígitos, válido) é obrigatório");
        }

        var caller = http.GetCaller();
        var tenant = caller.Identity.Tenant;
        var ct = http.RequestAborted;
        var settings = await deps.Settings.GetAsync(tenant, ct);
        var hmac = (await deps.Anonymizer.HashAsync(settings.KmsKeyRef, cpf, ct))!;

        var docs = await deps.Db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT id, kind, access_key, xml_storage_key, created_at FROM fiscal_documents
                 WHERE cpf_hmac = $1 ORDER BY created_at
                """);
            cmd.Parameters.Add(new() { Value = hmac });
            await using var r = await cmd.ExecuteReaderAsync(c);
            var list = new List<(Guid Id, string Kind, string? Chave, string? Key, DateTimeOffset At)>();
            while (await r.ReadAsync(c))
            {
                list.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetFieldValue<DateTimeOffset>(4)));
            }

            return list;
        }, ct);

        var xmlDeleted = 0;
        if (expurgar)
        {
            // S3 primeiro: apagar é idempotente. Se o UPDATE falhar depois, repetir o pedido termina o serviço;
            // na ordem inversa, uma falha no S3 deixaria XML com dado pessoal sem referência no banco.
            foreach (var key in docs.Where(d => d.Key is not null).Select(d => d.Key!))
            {
                if (deps.Vault is null)
                {
                    return ApiProblem.Result(503, "ERR_STORAGE_INDISPONIVEL", "Armazenamento de XML não configurado nesta instância");
                }

                await deps.Vault.DeleteAsync(key, ct);
                xmlDeleted++;
            }
        }

        var requestedBy = SessionAuthenticator.ActorOf(http, caller.Identity);
        await deps.Db.InTenantAsync(tenant, async (s, c) =>
        {
            if (expurgar)
            {
                await using var upd = s.CreateCommand("UPDATE fiscal_documents SET cpf_hmac = NULL, xml_storage_key = NULL WHERE cpf_hmac = $1");
                upd.Parameters.Add(new() { Value = hmac });
                await upd.ExecuteNonQueryAsync(c);
            }

            await using var log = s.CreateCommand("""
                INSERT INTO titular_requests (id, tenant_id, cpf_hmac, action, documents, xml_deleted, requested_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """);
            log.Parameters.Add(new() { Value = Guid.CreateVersion7() });
            log.Parameters.Add(new() { Value = tenant.Value });
            log.Parameters.Add(new() { Value = hmac });
            log.Parameters.Add(new() { Value = expurgar ? "EXPURGAR" : "LOCALIZAR" });
            log.Parameters.Add(new() { Value = docs.Count });
            log.Parameters.Add(new() { Value = xmlDeleted });
            log.Parameters.Add(new() { Value = requestedBy });
            return await log.ExecuteNonQueryAsync(c);
        }, ct);

        var result = new JsonObject
        {
            ["documentos"] = new JsonArray([.. docs.Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["direcao"] = d.Kind, ["chave_acesso"] = d.Chave, ["xml_armazenado"] = !expurgar && d.Key is not null,
                ["criado_em"] = d.At,
            })]),
        };
        if (expurgar)
        {
            result["documentos_expurgados"] = docs.Count;
            result["xmls_apagados"] = xmlDeleted;
        }

        return Results.Ok(result);
    }

    /// <summary>CPF com 11 dígitos e DVs válidos (módulo 11); sequências repetidas são inválidas.</summary>
    public static bool IsValidCpf(string? cpf)
    {
        var d = cpf is null ? "" : new string(cpf.Where(char.IsAsciiDigit).ToArray());
        if (d.Length != 11 || d.Distinct().Count() == 1)
        {
            return false;
        }

        for (var n = 9; n <= 10; n++)
        {
            var sum = 0;
            for (var i = 0; i < n; i++)
            {
                sum += (d[i] - '0') * (n + 1 - i);
            }

            var dv = sum * 10 % 11 % 10;
            if (dv != d[n] - '0')
            {
                return false;
            }
        }

        return true;
    }
}
