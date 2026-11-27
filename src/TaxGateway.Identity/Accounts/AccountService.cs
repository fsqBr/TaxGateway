using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Identity.Accounts;

public sealed class AccountOptions
{
    public const string Section = "Accounts";

    /// <summary>Endereço público do site/portal, usado nos links dos e-mails.</summary>
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:8090";

    /// <summary>Endereço do backoffice (links de operador).</summary>
    public string BackofficeBaseUrl { get; set; } = "http://127.0.0.1:8091";

    public int SessionHours { get; set; } = 12;
    public int IdleMinutes { get; set; } = 120;

    /// <summary>Cookie só por HTTPS e com prefixo __Host- (produção). Dev em http: false.</summary>
    public bool CookieSecure { get; set; } = true;

    public string Issuer { get; set; } = "TaxGateway";
    public int MaxFailedLogins { get; set; } = 5;
    public int LockMinutes { get; set; } = 15;
}

/// <summary>Erro de conta com código estável (convertido em problem+json na API).</summary>
public sealed class AccountException(int status, string code, string title, string? detail = null) : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
}

public sealed record SessionInfo(Guid SessionId, Guid UserId, string Realm, string Email, string Name, bool MfaPassed, bool MfaEnabled,
    Guid? ActiveTenant, IReadOnlyList<string> OperatorScopes, bool EmailVerified);

public sealed record Membership(Guid Tenant, string Role, string TenantName, string RootCnpj, string PlanCode, string TenantStatus);

public sealed record Member(Guid UserId, string Name, string Email, string Role, DateTimeOffset Since, bool MfaEnabled, DateTimeOffset? LastLogin);

public sealed record Invite(string Email, string Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

/// <summary>
/// Contas de pessoas (D-U01 a D-U08): cadastro com confirmação de e-mail, senha Argon2id, MFA TOTP obrigatório com
/// códigos de recuperação, sessões opacas em cookie HttpOnly (o banco só guarda o hash), bloqueio por tentativas,
/// redefinição de senha, convites e papéis por tenant, operadores do backoffice. Tudo que muda segurança vai para a
/// trilha <c>audit_log</c>. As API keys continuam sendo o acesso de máquina.
/// </summary>
public sealed partial class AccountService(
    NpgsqlDataSource appDb, ITenantDb tenantDb, IEmailSender email, IOptions<AccountOptions> options,
    IOptions<ApiKeyOptions> pepper, TimeProvider clock, ILogger<AccountService> log)
{
    public static readonly string[] CustomerRoles = ["owner", "admin", "fiscal", "developer", "finance", "viewer"];
    private static readonly (byte[] Hash, byte[] Salt) Dummy = Passwords.Hash("senha-de-referencia-para-tempo-constante");
    private readonly SecretBox _box = new(Convert.FromBase64String(pepper.Value.ApiKeyPepper));

    private AccountOptions O => options.Value;

    private DateTimeOffset Now => clock.GetUtcNow();

    public static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static bool ValidEmail(string e) => e.Length is >= 5 and <= 254 && System.Text.RegularExpressions.Regex.IsMatch(e,
        @"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    // ------------------------------------------------------------------------------------------------- cadastro

    /// <summary>
    /// Cadastro self-service: usuário + tenant no plano Sandbox + vínculo de dono, numa transação. E-mail já cadastrado
    /// recebe o mesmo retorno (sem enumerar contas) e um aviso por e-mail. CNPJ já cadastrado: 409 (peça convite).
    /// </summary>
    public async Task SignupAsync(string name, string emailAddr, string password, string company, string rootCnpj, bool acceptedTerms, string? ip, CancellationToken ct)
    {
        var e = Normalize(emailAddr);
        name = (name ?? "").Trim();
        company = (company ?? "").Trim();
        if (!acceptedTerms)
        {
            throw new AccountException(400, "ERR_VALIDATION", "Aceite os Termos de Uso e a Política de Privacidade para continuar.");
        }

        if (name.Length is < 2 or > 120 || company.Length is < 2 or > 200 || !ValidEmail(e))
        {
            throw new AccountException(400, "ERR_VALIDATION", "Dados inválidos", "Informe nome, e-mail válido e o nome da empresa.");
        }

        if (!Core.Fiscal.Cnpj.TryNormalize(rootCnpj, out var cnpj))
        {
            throw new AccountException(400, "ERR_VALIDATION", "CNPJ inválido", "Confira os dígitos do CNPJ da empresa.");
        }

        if (Passwords.Problem(password, e, name) is { } problem)
        {
            throw new AccountException(400, "ERR_SENHA_FRACA", "Senha fraca", problem);
        }

        var userId = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7();
        var (hash, salt) = Passwords.Hash(password);
        await using var conn = await appDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO users (id, realm, email, name, password_hash, password_salt) VALUES ($1, 'customer', $2, $3, $4, $5)
            ON CONFLICT (realm, email) DO NOTHING
            """, conn, tx))
        {
            ins.Parameters.Add(new() { Value = userId });
            ins.Parameters.Add(new() { Value = e });
            ins.Parameters.Add(new() { Value = name });
            ins.Parameters.Add(new() { Value = hash });
            ins.Parameters.Add(new() { Value = salt });
            if (await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                await email.SendAsync(EmailTemplates.AlreadyRegistered(e, $"{O.PublicBaseUrl}/portal/esqueci-senha"), ct).ConfigureAwait(false);
                await AuditAsync(null, "anonymous", null, "signup.email_existente", null, ip, new JsonObject(), ct).ConfigureAwait(false);
                return;
            }
        }

        await using (var fn = new NpgsqlCommand("SELECT app.signup_tenant($1, $2, $3, $4)", conn, tx))
        {
            fn.Parameters.Add(new() { Value = tenant });
            fn.Parameters.Add(new() { Value = company });
            fn.Parameters.Add(new() { Value = cnpj });
            fn.Parameters.Add(new() { Value = userId });
            if (!(bool)(await fn.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            {
                // D-R10: empresa já cadastrada — mesma resposta de um cadastro novo (não revela quem é cliente). A conta da
                // pessoa é criada sem empresa e o e-mail explica: pedir convite ou, sendo o titular, contestar com o e-CNPJ.
                var tokenExisting = await NewUserTokenAsync(conn, tx, userId, "verify_email", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await email.SendAsync(EmailTemplates.CompanyAlreadyRegistered(e, name, company,
                    $"{O.PublicBaseUrl}/portal/confirmar-email?token={tokenExisting}"), ct).ConfigureAwait(false);
                await AuditAsync(null, "user", userId.ToString(), "signup.empresa_existente", null, ip, new JsonObject(), ct).ConfigureAwait(false);
                return;
            }
        }

        var token = await NewUserTokenAsync(conn, tx, userId, "verify_email", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await email.SendAsync(EmailTemplates.VerifyEmail(e, name, $"{O.PublicBaseUrl}/portal/confirmar-email?token={token}"), ct).ConfigureAwait(false);
        await AuditAsync(new TenantId(tenant), "user", userId.ToString(), "signup", tenant.ToString(), ip, new JsonObject { ["plano"] = "SANDBOX" }, ct).ConfigureAwait(false);
    }

    public async Task ResendVerificationAsync(string emailAddr, CancellationToken ct)
    {
        var e = Normalize(emailAddr);
        await using var conn = await appDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT id, name FROM users WHERE realm = 'customer' AND email = $1 AND email_verified_at IS NULL", conn);
        cmd.Parameters.Add(new() { Value = e });
        Guid id;
        string name;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return;   // mesma resposta para quem não existe ou já confirmou
            }

            (id, name) = (r.GetGuid(0), r.GetString(1));
        }

        var token = await NewUserTokenAsync(conn, null, id, "verify_email", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        await email.SendAsync(EmailTemplates.VerifyEmail(e, name, $"{O.PublicBaseUrl}/portal/confirmar-email?token={token}"), ct).ConfigureAwait(false);
    }

    public async Task VerifyEmailAsync(string token, CancellationToken ct)
    {
        var userId = await ConsumeUserTokenAsync(token, "verify_email", ct).ConfigureAwait(false)
            ?? throw new AccountException(400, "ERR_LINK_INVALIDO", "Link inválido ou expirado", "Peça um novo e-mail de confirmação.");
        await using var cmd = appDb.CreateCommand("""
            UPDATE users SET email_verified_at = now() WHERE id = $1 AND email_verified_at IS NULL RETURNING email, name, realm
            """);
        cmd.Parameters.Add(new() { Value = userId });
        (string Email, string Name, string Realm)? first = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                first = (r.GetString(0), r.GetString(1), r.GetString(2));
            }
        }

        await AuditAsync(null, "user", userId.ToString(), "email.confirmado", null, null, new JsonObject(), ct).ConfigureAwait(false);
        // D-R32: boas-vindas só na primeira confirmação, com os primeiros passos.
        if (first is { Realm: "customer" } f)
        {
            await email.SendAsync(EmailTemplates.Welcome(f.Email, f.Name, $"{O.PublicBaseUrl}/portal", $"{O.PublicBaseUrl}/ajuda"), ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------- login e sessão

    /// <summary>Login por senha. Devolve o valor do cookie (opaco) e a sessão, ainda sem MFA (mfa_passed = false).</summary>
    public async Task<(string Cookie, SessionInfo Session)> LoginAsync(string realm, string emailAddr, string password, string? ip, string? userAgent, CancellationToken ct)
    {
        var e = Normalize(emailAddr);
        await using var cmd = appDb.CreateCommand("""
            SELECT id, name, password_hash, password_salt, email_verified_at IS NOT NULL, totp_enabled_at IS NOT NULL, status,
                   CASE WHEN locked_until > now() THEN locked_until END, failed_logins, operator_scopes
              FROM users WHERE realm = $1 AND email = $2
            """);
        cmd.Parameters.Add(new() { Value = realm });
        cmd.Parameters.Add(new() { Value = e });
        Guid id;
        string name;
        byte[] hash, salt;
        bool verified, mfa;
        string status;
        DateTimeOffset? locked;
        string[] opScopes;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                Passwords.Verify(password ?? "", Dummy.Hash, Dummy.Salt);   // mesmo custo para e-mail inexistente
                await AuditAsync(null, "anonymous", null, "login.falhou", null, ip, new JsonObject { ["motivo"] = "conta_inexistente" }, ct).ConfigureAwait(false);
                throw InvalidCredentials();
            }

            (id, name, hash, salt, verified, mfa, status) = (r.GetGuid(0), r.GetString(1), r.GetFieldValue<byte[]>(2), r.GetFieldValue<byte[]>(3),
                r.GetBoolean(4), r.GetBoolean(5), r.GetString(6));
            locked = r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7);
            opScopes = r.GetFieldValue<string[]>(9);
        }

        if (locked is not null)   // comparação feita no banco (mesmo relógio que gravou o bloqueio)
        {
            throw new AccountException(423, "ERR_CONTA_BLOQUEADA", "Conta bloqueada temporariamente",
                $"Muitas tentativas sem sucesso. Tente de novo depois de {locked.Value.ToOffset(TimeSpan.FromHours(-3)):HH:mm} (horário de Brasília) ou redefina a senha.");
        }

        if (!Passwords.Verify(password ?? "", hash, salt) || status != "ACTIVE")
        {
            await RegisterFailureAsync(id, name, e, ip, ct).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        if (!verified)
        {
            throw new AccountException(403, "ERR_EMAIL_NAO_CONFIRMADO", "Confirme seu e-mail para entrar",
                "Enviamos um link de confirmação no cadastro. Peça um novo se não encontrar.");
        }

        // D-R32: entrada por um navegador/dispositivo que esta pessoa nunca usou → aviso de segurança por e-mail.
        bool newDevice;
        await using (var seen = appDb.CreateCommand("""
            SELECT count(*) > 0 AND count(*) FILTER (WHERE user_agent IS NOT DISTINCT FROM $2) = 0 FROM user_sessions WHERE user_id = $1
            """))
        {
            seen.Parameters.Add(new() { Value = id });
            seen.Parameters.Add(new() { Value = (object?)Truncate(userAgent, 300) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            newDevice = await seen.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }

        var cookie = OpaqueToken.New();
        var sessionId = Guid.CreateVersion7();
        Guid? active = null;
        if (realm == "customer")
        {
            active = (await MembershipsAsync(id, ct).ConfigureAwait(false)).FirstOrDefault(m => m.TenantStatus == "ACTIVE")?.Tenant;
        }

        var insert = new NpgsqlBatchCommand("""
            INSERT INTO user_sessions (id, user_id, token_hash, mfa_passed, active_tenant, ip, user_agent, expires_at)
            VALUES ($1, $2, $3, FALSE, $4, $5, $6, now() + make_interval(hours => $7))
            """);
        insert.Parameters.Add(new() { Value = sessionId });
        insert.Parameters.Add(new() { Value = id });
        insert.Parameters.Add(new() { Value = OpaqueToken.Hash(cookie) });
        insert.Parameters.Add(new() { Value = (object?)active ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        insert.Parameters.Add(new() { Value = (object?)ip ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = (object?)Truncate(userAgent, 300) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = O.SessionHours });
        await BatchAsync(ct, insert, Cmd("UPDATE users SET failed_logins = 0, locked_until = NULL, last_login_at = now() WHERE id = $1", id)).ConfigureAwait(false);

        await AuditAsync(null, realm == "operator" ? "operator" : "user", id.ToString(), "login.senha_ok", null, ip, new JsonObject { ["mfa"] = mfa ? "verificar" : "configurar" }, ct).ConfigureAwait(false);
        if (newDevice)
        {
            await email.SendAsync(EmailTemplates.SecurityNotice(e, name,
                $"Houve uma entrada na sua conta por um navegador ou dispositivo novo ({DeviceLabel(userAgent)}, IP {ip ?? "desconhecido"}, " +
                $"{DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy HH:mm} no horário de Brasília)."), ct).ConfigureAwait(false);
        }

        return (cookie, new SessionInfo(sessionId, id, realm, e, name, false, mfa, active, opScopes, verified));
    }

    /// <summary>D-R33: avisos que a pessoa pode deixar de receber por e-mail (continuam no portal). Mesma lista do CHECK da V0027.</summary>
    public static readonly IReadOnlyDictionary<string, string> OptionalNotices = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RESUMO_MENSAL"] = "Resumo do mês (documentos auditados, divergências e crédito em risco)",
        ["MOTOR_ATUALIZADO"] = "Nova versão do motor oficial da Receita",
        ["RENOVACAO_PROXIMA"] = "Assinatura renova em breve",
        ["FATURA_EMITIDA"] = "Fatura emitida",
        ["PAGAMENTO_RECEBIDO"] = "Pagamento recebido",
        ["FRANQUIA_80"] = "Franquia do plano em 80%",
        ["TETO_80"] = "Teto de excedente em 80%",
    };

    public async Task<string[]> GetEmailOptOutAsync(SessionInfo s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = appDb.CreateCommand("SELECT notice_email_opt_out FROM users WHERE id = $1");
        cmd.Parameters.Add(new() { Value = s.UserId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string[] ?? [];
    }

    public async Task<string[]> SetEmailOptOutAsync(SessionInfo s, IReadOnlyCollection<string> off, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(off);
        var invalid = off.Where(x => !OptionalNotices.ContainsKey(x)).ToList();
        if (invalid.Count > 0)
        {
            throw new AccountException(400, "ERR_VALIDATION", "Aviso que não pode ser desligado",
                $"Cobrança, acesso, segurança, franquia esgotada e mudanças fiscais sempre vão por e-mail. Não reconhecidos: {string.Join(", ", invalid)}.");
        }

        var value = off.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        await using var cmd = appDb.CreateCommand("UPDATE users SET notice_email_opt_out = $2 WHERE id = $1");
        cmd.Parameters.Add(new() { Value = s.UserId });
        cmd.Parameters.Add(new() { Value = value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await AuditAsync(null, "user", s.UserId.ToString(), "preferencias_email.alteradas", null, null,
            new JsonObject { ["desligados"] = new JsonArray([.. value.Select(x => (JsonNode)x)]) }, ct).ConfigureAwait(false);
        return value;
    }

    /// <summary>"Chrome no Windows" a partir do User-Agent (só para o aviso; sem biblioteca de parsing).</summary>
    public static string DeviceLabel(string? userAgent)
    {
        var ua = userAgent ?? "";
        var browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : "navegador desconhecido";
        var os = ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : ua.Contains("Mac OS", StringComparison.Ordinal) ? "macOS"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux" : null;
        return os is null ? browser : $"{browser} no {os}";
    }

    private static AccountException InvalidCredentials() => new(401, "ERR_CREDENCIAIS", "E-mail ou senha incorretos");

    private async Task RegisterFailureAsync(Guid id, string name, string e, string? ip, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("""
            UPDATE users SET failed_logins = failed_logins + 1,
                   locked_until = CASE WHEN failed_logins + 1 >= $2 THEN now() + make_interval(mins => $3) ELSE locked_until END
             WHERE id = $1 RETURNING failed_logins, locked_until IS NOT NULL AND locked_until > now()
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = O.MaxFailedLogins });
        cmd.Parameters.Add(new() { Value = O.LockMinutes });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        var lockedNow = r.GetBoolean(1) && r.GetInt32(0) == O.MaxFailedLogins;
        await r.DisposeAsync().ConfigureAwait(false);
        await AuditAsync(null, "user", id.ToString(), "login.falhou", null, ip, new JsonObject { ["bloqueou"] = lockedNow }, ct).ConfigureAwait(false);
        if (lockedNow)
        {
            await email.SendAsync(EmailTemplates.SecurityNotice(e, name,
                $"Sua conta foi bloqueada por {O.LockMinutes} minutos depois de {O.MaxFailedLogins} tentativas de entrada com senha errada."), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Resolve o cookie. null = sessão inexistente, expirada (absoluta ou por inatividade), revogada ou usuário desativado.</summary>
    public async Task<SessionInfo?> GetSessionAsync(string? cookie, string realm, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 100)
        {
            return null;
        }

        await using var cmd = appDb.CreateCommand("""
            UPDATE user_sessions s SET last_seen_at = CASE WHEN s.last_seen_at < now() - interval '5 minutes' THEN now() ELSE s.last_seen_at END
              FROM users u
             WHERE s.token_hash = $1 AND u.id = s.user_id AND u.realm = $2 AND u.status = 'ACTIVE'
               AND s.revoked_at IS NULL AND s.expires_at > now() AND s.last_seen_at > now() - make_interval(mins => $3)
            RETURNING s.id, u.id, u.email, u.name, s.mfa_passed, u.totp_enabled_at IS NOT NULL, s.active_tenant, u.operator_scopes,
                      u.email_verified_at IS NOT NULL
            """);
        cmd.Parameters.Add(new() { Value = OpaqueToken.Hash(cookie) });
        cmd.Parameters.Add(new() { Value = realm });
        cmd.Parameters.Add(new() { Value = O.IdleMinutes });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SessionInfo(r.GetGuid(0), r.GetGuid(1), realm, r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
            r.IsDBNull(6) ? null : r.GetGuid(6), r.GetFieldValue<string[]>(7), r.GetBoolean(8));
    }

    public async Task LogoutAsync(SessionInfo s, string? ip, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("UPDATE user_sessions SET revoked_at = now() WHERE id = $1");
        cmd.Parameters.Add(new() { Value = s.SessionId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await AuditAsync(null, Actor(s), s.UserId.ToString(), "logout", null, ip, new JsonObject(), ct).ConfigureAwait(false);
    }

    public async Task<int> LogoutOthersAsync(SessionInfo s, string? ip, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("UPDATE user_sessions SET revoked_at = now() WHERE user_id = $1 AND id <> $2 AND revoked_at IS NULL");
        cmd.Parameters.Add(new() { Value = s.UserId });
        cmd.Parameters.Add(new() { Value = s.SessionId });
        var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await AuditAsync(null, Actor(s), s.UserId.ToString(), "sessoes.encerradas", null, ip, new JsonObject { ["quantidade"] = n }, ct).ConfigureAwait(false);
        return n;
    }

    public async Task<JsonArray> ListSessionsAsync(SessionInfo s, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("""
            SELECT id, ip, user_agent, created_at, last_seen_at FROM user_sessions
             WHERE user_id = $1 AND revoked_at IS NULL AND expires_at > now() ORDER BY last_seen_at DESC LIMIT 20
            """);
        cmd.Parameters.Add(new() { Value = s.UserId });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new JsonArray();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new JsonObject
            {
                ["id"] = r.GetGuid(0),
                ["ip"] = r.IsDBNull(1) ? null : r.GetString(1),
                ["navegador"] = r.IsDBNull(2) ? null : r.GetString(2),
                ["criada_em"] = r.GetFieldValue<DateTimeOffset>(3),
                ["ultimo_uso"] = r.GetFieldValue<DateTimeOffset>(4),
                ["atual"] = r.GetGuid(0) == s.SessionId,
            });
        }

        return list;
    }

    // ------------------------------------------------------------------------------------------------- MFA

    /// <summary>Gera o segredo TOTP (pendente até a confirmação com um código). Só para quem ainda não tem MFA.</summary>
    public async Task<(string Secret, string Uri)> MfaBeginAsync(SessionInfo s, CancellationToken ct)
    {
        if (s.MfaEnabled)
        {
            throw new AccountException(409, "ERR_MFA_JA_ATIVO", "O MFA já está ativo nesta conta");
        }

        var secret = Totp.NewSecret();
        await using var cmd = appDb.CreateCommand("UPDATE users SET totp_secret_enc = $2 WHERE id = $1 AND totp_enabled_at IS NULL");
        cmd.Parameters.Add(new() { Value = s.UserId });
        cmd.Parameters.Add(new() { Value = _box.Seal(secret, s.UserId) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (Totp.Base32(secret), Totp.OtpAuthUri(O.Issuer, s.Email, secret));
    }

    /// <summary>Confirma o primeiro código, ativa o MFA, aprova a sessão e devolve 10 códigos de recuperação (exibidos uma vez).</summary>
    public async Task<IReadOnlyList<string>> MfaEnableAsync(SessionInfo s, string code, string? ip, CancellationToken ct)
    {
        if (s.MfaEnabled)
        {
            throw new AccountException(409, "ERR_MFA_JA_ATIVO", "O MFA já está ativo nesta conta");
        }

        var (enc, last) = await TotpStateAsync(s.UserId, ct).ConfigureAwait(false);
        if (enc is null)
        {
            throw new AccountException(409, "ERR_MFA_NAO_INICIADO", "Gere o QR code antes de confirmar");
        }

        var step = Totp.Verify(_box.Open(enc, s.UserId), code, Now, last)
            ?? throw new AccountException(400, "ERR_CODIGO_INVALIDO", "Código inválido", "Confira o código de 6 dígitos no app autenticador (ele muda a cada 30 segundos).");

        var codes = Enumerable.Range(0, 10).Select(_ => OpaqueToken.RecoveryCode()).ToList();
        await using var conn = await appDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await BatchAsync(conn, tx, ct,
            Cmd("UPDATE users SET totp_enabled_at = now(), totp_last_step = $2 WHERE id = $1", s.UserId, step),
            Cmd("UPDATE user_sessions SET mfa_passed = TRUE WHERE id = $1", s.SessionId),
            Cmd("DELETE FROM user_recovery_codes WHERE user_id = $1", s.UserId)).ConfigureAwait(false);

        await using (var ins = new NpgsqlCommand("INSERT INTO user_recovery_codes (user_id, code_hash) SELECT $1, unnest($2::bytea[])", conn, tx))
        {
            ins.Parameters.Add(new() { Value = s.UserId });
            ins.Parameters.Add(new() { Value = codes.Select(OpaqueToken.HashRecovery).ToArray() });
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        await AuditAsync(null, Actor(s), s.UserId.ToString(), "mfa.ativado", null, ip, new JsonObject(), ct).ConfigureAwait(false);
        await email.SendAsync(EmailTemplates.SecurityNotice(s.Email, s.Name, "A verificação em duas etapas (MFA) foi ativada na sua conta."), ct).ConfigureAwait(false);
        return codes;
    }

    /// <summary>Segundo fator na entrada: código TOTP ou um código de recuperação (que deixa de valer).</summary>
    /// <summary>Erros de código seguidos numa mesma sessão antes de ela ser encerrada (D-R03).</summary>
    public const int MaxMfaFailuresPerSession = 5;

    public async Task MfaVerifyAsync(SessionInfo s, string? code, string? recoveryCode, string? ip, CancellationToken ct)
    {
        if (!s.MfaEnabled)
        {
            throw new AccountException(409, "ERR_MFA_NAO_CONFIGURADO", "Configure o MFA primeiro");
        }

        // D-R03: o bloqueio da conta vale também para o segundo fator (antes só a senha o consultava: com a senha certa,
        // o código podia ser testado à exaustão, limitado só pelo teto por IP).
        await using (var locked = appDb.CreateCommand("SELECT locked_until IS NOT NULL AND locked_until > now() FROM users WHERE id = $1"))
        {
            locked.Parameters.Add(new() { Value = s.UserId });
            if (await locked.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
            {
                throw new AccountException(429, "ERR_CONTA_BLOQUEADA", "Conta bloqueada temporariamente", "Muitas tentativas. Aguarde alguns minutos ou redefina a senha.");
            }
        }

        bool ok;
        var method = "totp";
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            method = "recuperacao";
            await using var cmd = appDb.CreateCommand("UPDATE user_recovery_codes SET used_at = now() WHERE user_id = $1 AND code_hash = $2 AND used_at IS NULL");
            cmd.Parameters.Add(new() { Value = s.UserId });
            cmd.Parameters.Add(new() { Value = OpaqueToken.HashRecovery(recoveryCode) });
            ok = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        else
        {
            var (enc, last) = await TotpStateAsync(s.UserId, ct).ConfigureAwait(false);
            var step = enc is null ? null : Totp.Verify(_box.Open(enc, s.UserId), code, Now, last);
            ok = false;
            if (step is { } st)
            {
                // Anti-replay também entre instâncias: só vence quem grava o passo maior.
                await using var upd = appDb.CreateCommand("UPDATE users SET totp_last_step = $2 WHERE id = $1 AND totp_last_step < $2");
                upd.Parameters.Add(new() { Value = s.UserId });
                upd.Parameters.Add(new() { Value = st });
                ok = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            }
        }

        if (!ok)
        {
            await RegisterFailureAsync(s.UserId, s.Name, s.Email, ip, ct).ConfigureAwait(false);
            await using var count = appDb.CreateCommand("UPDATE user_sessions SET mfa_failures = mfa_failures + 1 WHERE id = $1 RETURNING mfa_failures");
            count.Parameters.Add(new() { Value = s.SessionId });
            if (await count.ExecuteScalarAsync(ct).ConfigureAwait(false) is int failures && failures >= MaxMfaFailuresPerSession)
            {
                // A sessão "meio autenticada" morre: para tentar de novo é preciso a senha outra vez.
                await using var kill = appDb.CreateCommand("UPDATE user_sessions SET revoked_at = now() WHERE id = $1");
                kill.Parameters.Add(new() { Value = s.SessionId });
                await kill.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                throw new AccountException(401, "ERR_UNAUTHENTICATED", "Muitas tentativas de código", "Entre de novo com e-mail e senha.");
            }

            throw new AccountException(400, "ERR_CODIGO_INVALIDO", "Código inválido", "Confira o código no app autenticador ou use um código de recuperação.");
        }

        await BatchAsync(ct,
            Cmd("UPDATE user_sessions SET mfa_passed = TRUE WHERE id = $1", s.SessionId),
            Cmd("UPDATE users SET failed_logins = 0 WHERE id = $1", s.UserId)).ConfigureAwait(false);

        await AuditAsync(null, Actor(s), s.UserId.ToString(), "login.mfa_ok", null, ip, new JsonObject { ["metodo"] = method }, ct).ConfigureAwait(false);
        if (method == "recuperacao")
        {
            await email.SendAsync(EmailTemplates.SecurityNotice(s.Email, s.Name, "Um código de recuperação foi usado para entrar na sua conta."), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(SessionInfo s, string code, string? ip, CancellationToken ct)
    {
        await MfaVerifyAsync(s, code, null, ip, ct).ConfigureAwait(false);   // exige um código TOTP válido
        var codes = Enumerable.Range(0, 10).Select(_ => OpaqueToken.RecoveryCode()).ToList();
        await BatchAsync(ct,
            Cmd("DELETE FROM user_recovery_codes WHERE user_id = $1", s.UserId),
            Cmd("INSERT INTO user_recovery_codes (user_id, code_hash) SELECT $1, unnest($2::bytea[])", s.UserId, codes.Select(OpaqueToken.HashRecovery).ToArray())).ConfigureAwait(false);
        await AuditAsync(null, Actor(s), s.UserId.ToString(), "mfa.codigos_recriados", null, ip, new JsonObject(), ct).ConfigureAwait(false);
        return codes;
    }

    public async Task<int> RemainingRecoveryCodesAsync(Guid user, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("SELECT count(*) FROM user_recovery_codes WHERE user_id = $1 AND used_at IS NULL");
        cmd.Parameters.Add(new() { Value = user });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<(byte[]? Enc, long Last)> TotpStateAsync(Guid user, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("SELECT totp_secret_enc, totp_last_step FROM users WHERE id = $1");
        cmd.Parameters.Add(new() { Value = user });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return (r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0), r.GetInt64(1));
    }

    // ------------------------------------------------------------------------------------------------- senha

    public async Task ForgotPasswordAsync(string realm, string emailAddr, string? ip, CancellationToken ct)
    {
        var e = Normalize(emailAddr);
        await using var cmd = appDb.CreateCommand("SELECT id, name FROM users WHERE realm = $1 AND email = $2 AND status = 'ACTIVE'");
        cmd.Parameters.Add(new() { Value = realm });
        cmd.Parameters.Add(new() { Value = e });
        Guid id;
        string name;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return;   // mesma resposta para e-mail inexistente
            }

            (id, name) = (r.GetGuid(0), r.GetString(1));
        }

        await using var conn = await appDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        var token = await NewUserTokenAsync(conn, null, id, "reset_password", TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        var baseUrl = realm == "operator" ? $"{O.BackofficeBaseUrl}/backoffice" : $"{O.PublicBaseUrl}/portal";
        await email.SendAsync(EmailTemplates.ResetPassword(e, name, $"{baseUrl}/redefinir-senha?token={token}"), ct).ConfigureAwait(false);
        await AuditAsync(null, realm == "operator" ? "operator" : "user", id.ToString(), "senha.pedido_redefinicao", null, ip, new JsonObject(), ct).ConfigureAwait(false);
    }

    /// <summary>Nova senha por link de e-mail (redefinição ou primeiro acesso de operador). Encerra todas as sessões.</summary>
    public async Task ResetPasswordAsync(string token, string newPassword, string? ip, CancellationToken ct)
    {
        var hash = OpaqueToken.Hash(token ?? "");
        await using var find = appDb.CreateCommand("""
            SELECT u.id, u.email, u.name, t.purpose FROM user_tokens t JOIN users u ON u.id = t.user_id
             WHERE t.token_hash = $1 AND t.purpose IN ('reset_password','operator_setup') AND t.used_at IS NULL AND t.expires_at > now()
            """);
        find.Parameters.Add(new() { Value = hash });
        Guid id;
        string e, name, purpose;
        await using (var r = await find.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new AccountException(400, "ERR_LINK_INVALIDO", "Link inválido ou expirado", "Peça um novo link de redefinição.");
            }

            (id, e, name, purpose) = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3));
        }

        if (Passwords.Problem(newPassword, e, name) is { } problem)
        {
            throw new AccountException(400, "ERR_SENHA_FRACA", "Senha fraca", problem);
        }

        var (h, s) = Passwords.Hash(newPassword);
        await BatchAsync(ct,
            Cmd("UPDATE user_tokens SET used_at = now() WHERE token_hash = $1 AND used_at IS NULL", hash),
            Cmd("""
                UPDATE users SET password_hash = $2, password_salt = $3, password_changed_at = now(), failed_logins = 0, locked_until = NULL,
                       email_verified_at = coalesce(email_verified_at, now())
                 WHERE id = $1
                """, id, h, s),
            Cmd("UPDATE user_sessions SET revoked_at = now() WHERE user_id = $1 AND revoked_at IS NULL", id)).ConfigureAwait(false);
        await AuditAsync(null, "user", id.ToString(), purpose == "operator_setup" ? "operador.senha_definida" : "senha.redefinida", null, ip, new JsonObject(), ct).ConfigureAwait(false);
        await email.SendAsync(EmailTemplates.SecurityNotice(e, name, "A senha da sua conta foi alterada e todas as sessões abertas foram encerradas."), ct).ConfigureAwait(false);
    }

    public async Task ChangePasswordAsync(SessionInfo s, string current, string newPassword, string? ip, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("SELECT password_hash, password_salt FROM users WHERE id = $1");
        cmd.Parameters.Add(new() { Value = s.UserId });
        byte[] hash, salt;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await r.ReadAsync(ct).ConfigureAwait(false);
            (hash, salt) = (r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1));
        }

        if (!Passwords.Verify(current ?? "", hash, salt))
        {
            throw new AccountException(400, "ERR_CREDENCIAIS", "A senha atual está incorreta");
        }

        if (Passwords.Problem(newPassword, s.Email, s.Name) is { } problem)
        {
            throw new AccountException(400, "ERR_SENHA_FRACA", "Senha fraca", problem);
        }

        var (h, sl) = Passwords.Hash(newPassword);
        await BatchAsync(ct,
            Cmd("UPDATE users SET password_hash = $2, password_salt = $3, password_changed_at = now() WHERE id = $1", s.UserId, h, sl),
            Cmd("UPDATE user_sessions SET revoked_at = now() WHERE user_id = $1 AND id <> $2 AND revoked_at IS NULL", s.UserId, s.SessionId)).ConfigureAwait(false);
        await AuditAsync(null, Actor(s), s.UserId.ToString(), "senha.alterada", null, ip, new JsonObject(), ct).ConfigureAwait(false);
        await email.SendAsync(EmailTemplates.SecurityNotice(s.Email, s.Name, "A senha da sua conta foi alterada; as outras sessões foram encerradas."), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------- tenants e papéis

    public async Task<IReadOnlyList<Membership>> MembershipsAsync(Guid user, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("SELECT tenant_id, role, tenant_name, root_cnpj, plan_code, tenant_status FROM app.user_memberships($1)");
        cmd.Parameters.Add(new() { Value = user });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Membership>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new Membership(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        }

        return list;
    }

    public async Task<Membership> SetActiveTenantAsync(SessionInfo s, Guid tenant, CancellationToken ct)
    {
        var m = (await MembershipsAsync(s.UserId, ct).ConfigureAwait(false)).FirstOrDefault(x => x.Tenant == tenant)
            ?? throw new AccountException(404, "ERR_NOT_FOUND", "Empresa não encontrada entre os seus acessos");
        await using var cmd = appDb.CreateCommand("UPDATE user_sessions SET active_tenant = $2 WHERE id = $1");
        cmd.Parameters.Add(new() { Value = s.SessionId });
        cmd.Parameters.Add(new() { Value = tenant });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return m;
    }

    public Task<IReadOnlyList<Member>> ListMembersAsync(TenantId tenant, CancellationToken ct) =>
        tenantDb.InTenantAsync<IReadOnlyList<Member>>(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT u.id, u.name, u.email, m.role, m.created_at, u.totp_enabled_at IS NOT NULL, u.last_login_at
                  FROM user_memberships m JOIN users u ON u.id = m.user_id ORDER BY m.created_at
                """);
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var list = new List<Member>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                list.Add(new Member(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5),
                    r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)));
            }

            return list;
        }, ct);

    public Task<IReadOnlyList<Invite>> ListInvitesAsync(TenantId tenant, CancellationToken ct) =>
        tenantDb.InTenantAsync<IReadOnlyList<Invite>>(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT email, role, expires_at, created_at FROM user_invites WHERE used_at IS NULL AND expires_at > now() ORDER BY created_at DESC");
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var list = new List<Invite>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                list.Add(new Invite(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3)));
            }

            return list;
        }, ct);

    /// <summary>Convite por e-mail (7 dias). Limite de usuários do plano conta membros + convites pendentes.</summary>
    public async Task InviteAsync(TenantId tenant, string tenantName, SessionInfo inviter, string emailAddr, string role, int? maxUsers, string? ip, CancellationToken ct)
    {
        var e = Normalize(emailAddr);
        if (!ValidEmail(e) || role is not ("admin" or "fiscal" or "developer" or "finance" or "viewer"))
        {
            throw new AccountException(400, "ERR_VALIDATION", "Dados inválidos", "Informe um e-mail válido e um papel: admin, fiscal, developer, finance ou viewer.");
        }

        var token = OpaqueToken.New();
        await tenantDb.InTenantAsync(tenant, async (s, c) =>
        {
            await using (var count = s.CreateCommand("""
                SELECT (SELECT count(*) FROM user_memberships) + (SELECT count(*) FROM user_invites WHERE used_at IS NULL AND expires_at > now()),
                       EXISTS (SELECT 1 FROM user_memberships m JOIN users u ON u.id = m.user_id WHERE u.email = $1)
                """))
            {
                count.Parameters.Add(new() { Value = e });
                await using var r = await count.ExecuteReaderAsync(c).ConfigureAwait(false);
                await r.ReadAsync(c).ConfigureAwait(false);
                if (r.GetBoolean(1))
                {
                    throw new AccountException(409, "ERR_CONFLICT", "Esta pessoa já tem acesso a esta empresa");
                }

                if (maxUsers is { } max && r.GetInt64(0) >= max)
                {
                    throw new AccountException(422, "ERR_LIMITE_USUARIOS", "Limite de usuários do plano atingido",
                        $"O plano atual permite {max} usuário(s). Troque de plano ou remova um acesso.");
                }
            }

            return await BatchAsync(s, c,
                Cmd("DELETE FROM user_invites WHERE email = $1 AND used_at IS NULL", e),
                Cmd("INSERT INTO user_invites (token_hash, tenant_id, email, role, invited_by, expires_at) VALUES ($1, $2, $3, $4, $5, now() + interval '7 days')",
                    OpaqueToken.Hash(token), tenant.Value, e, role, inviter.UserId)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await email.SendAsync(EmailTemplates.Invite(e, tenantName, inviter.Name, RoleLabel(role), $"{O.PublicBaseUrl}/portal/convite?token={token}"), ct).ConfigureAwait(false);
        await AuditAsync(tenant, "user", inviter.UserId.ToString(), "membro.convidado", e, ip, new JsonObject { ["papel"] = role }, ct).ConfigureAwait(false);
    }

    public async Task RevokeInviteAsync(TenantId tenant, SessionInfo actor, string emailAddr, string? ip, CancellationToken ct)
    {
        var e = Normalize(emailAddr);
        await tenantDb.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("DELETE FROM user_invites WHERE email = $1 AND used_at IS NULL");
            cmd.Parameters.Add(new() { Value = e });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await AuditAsync(tenant, "user", actor.UserId.ToString(), "membro.convite_cancelado", e, ip, new JsonObject(), ct).ConfigureAwait(false);
    }

    /// <summary>Dados do convite para a tela de aceite (sem exigir login).</summary>
    public async Task<(Guid Tenant, string Email, string Role, string TenantName, bool HasAccount)> InviteInfoAsync(string token, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("""
            SELECT i.tenant_id, i.email, i.role, i.tenant_name, EXISTS (SELECT 1 FROM users u WHERE u.realm = 'customer' AND u.email = i.email)
              FROM app.invite_by_token($1) i WHERE i.used_at IS NULL AND i.expires_at > now()
            """);
        cmd.Parameters.Add(new() { Value = OpaqueToken.Hash(token ?? "") });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new AccountException(400, "ERR_LINK_INVALIDO", "Convite inválido ou expirado", "Peça um novo convite a quem administra a conta.");
        }

        return (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4));
    }

    /// <summary>
    /// Aceite: quem já tem conta precisa estar logado com o MESMO e-mail; quem não tem cria a conta aqui (o link prova o
    /// e-mail) e segue para configurar o MFA.
    /// </summary>
    public async Task<Guid> AcceptInviteAsync(string token, SessionInfo? session, string? name, string? password, string? ip, CancellationToken ct)
    {
        var info = await InviteInfoAsync(token, ct).ConfigureAwait(false);
        // D-R14: o limite de usuários do plano vale no aceite (o plano pode ter baixado depois do convite).
        await tenantDb.InTenantAsync(new TenantId(info.Tenant), async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT (SELECT count(*) FROM user_memberships), p.max_users
                  FROM tenants t JOIN plans p ON p.code = t.plan_code
                """);
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            if (await r.ReadAsync(c).ConfigureAwait(false) && !r.IsDBNull(1) && r.GetInt64(0) >= r.GetInt32(1))
            {
                throw new AccountException(422, "ERR_LIMITE_USUARIOS", "Limite de usuários do plano atingido",
                    $"O plano da empresa permite {r.GetInt32(1)} usuário(s). Peça a quem administra a conta para trocar de plano ou liberar um acesso.");
            }

            return 0;
        }, ct).ConfigureAwait(false);
        Guid userId;
        if (info.HasAccount)
        {
            if (session is null || !string.Equals(session.Email, info.Email, StringComparison.Ordinal))
            {
                throw new AccountException(401, "ERR_LOGIN_NECESSARIO", "Entre com a conta do e-mail convidado para aceitar",
                    $"Este convite é para {info.Email}.");
            }

            userId = session.UserId;
        }
        else
        {
            name = (name ?? "").Trim();
            if (name.Length is < 2 or > 120)
            {
                throw new AccountException(400, "ERR_VALIDATION", "Informe seu nome");
            }

            if (Passwords.Problem(password, info.Email, name) is { } problem)
            {
                throw new AccountException(400, "ERR_SENHA_FRACA", "Senha fraca", problem);
            }

            userId = Guid.CreateVersion7();
            var (h, s) = Passwords.Hash(password!);
            await using var ins = appDb.CreateCommand("""
                INSERT INTO users (id, realm, email, name, password_hash, password_salt, email_verified_at) VALUES ($1, 'customer', $2, $3, $4, $5, now())
                """);
            ins.Parameters.Add(new() { Value = userId });
            ins.Parameters.Add(new() { Value = info.Email });
            ins.Parameters.Add(new() { Value = name });
            ins.Parameters.Add(new() { Value = h });
            ins.Parameters.Add(new() { Value = s });
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tenantDb.InTenantAsync(new TenantId(info.Tenant), async (s, c) =>
        {
            return await BatchAsync(s, c,
                Cmd("UPDATE user_invites SET used_at = now() WHERE token_hash = $1 AND used_at IS NULL", OpaqueToken.Hash(token)),
                Cmd("INSERT INTO user_memberships (user_id, tenant_id, role) VALUES ($1, $2, $3) ON CONFLICT (user_id, tenant_id) DO NOTHING", userId, info.Tenant, info.Role)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await AuditAsync(new TenantId(info.Tenant), "user", userId.ToString(), "membro.entrou", info.Email, ip, new JsonObject { ["papel"] = info.Role }, ct).ConfigureAwait(false);
        return info.Tenant;
    }

    public async Task ChangeRoleAsync(TenantId tenant, SessionInfo actor, string actorRole, Guid member, string role, string? ip, CancellationToken ct)
    {
        if (!CustomerRoles.Contains(role))
        {
            throw new AccountException(400, "ERR_VALIDATION", "Papel inválido");
        }

        if (role == "owner" && actorRole != "owner")
        {
            throw new AccountException(403, "ERR_FORBIDDEN_SCOPE", "Só um dono pode transferir a posição de dono");
        }

        await tenantDb.InTenantAsync(tenant, async (s, c) =>
        {
            await EnsureOwnerOnlyByOwnerAsync(s, member, actorRole, c).ConfigureAwait(false);
            await EnsureNotLastOwnerAsync(s, member, role, c).ConfigureAwait(false);
            await using var cmd = s.CreateCommand("UPDATE user_memberships SET role = $2 WHERE user_id = $1");
            cmd.Parameters.Add(new() { Value = member });
            cmd.Parameters.Add(new() { Value = role });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false) == 1
                ? 1 : throw new AccountException(404, "ERR_NOT_FOUND", "Membro não encontrado");
        }, ct).ConfigureAwait(false);
        await AuditAsync(tenant, "user", actor.UserId.ToString(), "membro.papel_alterado", member.ToString(), ip, new JsonObject { ["papel"] = role }, ct).ConfigureAwait(false);
    }

    public async Task RemoveMemberAsync(TenantId tenant, SessionInfo actor, Guid member, string? ip, CancellationToken ct, string actorRole = "owner")
    {
        await tenantDb.InTenantAsync(tenant, async (s, c) =>
        {
            await EnsureOwnerOnlyByOwnerAsync(s, member, actorRole, c).ConfigureAwait(false);
            await EnsureNotLastOwnerAsync(s, member, null, c).ConfigureAwait(false);
            await using var cmd = s.CreateCommand("DELETE FROM user_memberships WHERE user_id = $1");
            cmd.Parameters.Add(new() { Value = member });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false) == 1
                ? 1 : throw new AccountException(404, "ERR_NOT_FOUND", "Membro não encontrado");
        }, ct).ConfigureAwait(false);

        // Sessões ativas da pessoa nesta empresa deixam de valer na hora.
        await using (var sess = appDb.CreateCommand("UPDATE user_sessions SET active_tenant = NULL WHERE user_id = $1 AND active_tenant = $2"))
        {
            sess.Parameters.Add(new() { Value = member });
            sess.Parameters.Add(new() { Value = tenant.Value });
            await sess.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await AuditAsync(tenant, "user", actor.UserId.ToString(), "membro.removido", member.ToString(), ip, new JsonObject(), ct).ConfigureAwait(false);
    }

    /// <summary>D-R29: administrador não rebaixa nem remove um dono (só outro dono pode).</summary>
    private static async Task EnsureOwnerOnlyByOwnerAsync(TenantScope s, Guid member, string actorRole, CancellationToken ct)
    {
        if (actorRole == "owner")
        {
            return;
        }

        await using var cmd = s.CreateCommand("SELECT role FROM user_memberships WHERE user_id = $1");
        cmd.Parameters.Add(new() { Value = member });
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is "owner")
        {
            throw new AccountException(403, "ERR_FORBIDDEN_SCOPE", "Só um dono pode alterar ou remover outro dono");
        }
    }

    private static async Task EnsureNotLastOwnerAsync(TenantScope s, Guid member, string? newRole, CancellationToken ct)
    {
        await using var cmd = s.CreateCommand("""
            SELECT (SELECT role FROM user_memberships WHERE user_id = $1), (SELECT count(*) FROM user_memberships WHERE role = 'owner')
            """);
        cmd.Parameters.Add(new() { Value = member });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        if (!r.IsDBNull(0) && r.GetString(0) == "owner" && r.GetInt64(1) <= 1 && newRole != "owner")
        {
            throw new AccountException(409, "ERR_ULTIMO_DONO", "A empresa precisa de pelo menos um dono",
                "Promova outra pessoa a dona antes de remover ou rebaixar este acesso.");
        }
    }

    public static string RoleLabel(string role) => role switch
    {
        "owner" => "Dono",
        "admin" => "Administrador",
        "fiscal" => "Fiscal",
        "developer" => "Desenvolvedor",
        "finance" => "Financeiro",
        "viewer" => "Leitura",
        _ => role,
    };

    // ------------------------------------------------------------------------------------------------- operadores

    /// <summary>Operador do backoffice: conta sem senha utilizável até o primeiro acesso pelo link (24 h), com MFA obrigatório.</summary>
    /// <summary>Escopos de operador. admin:security = gerir operadores e ler a trilha de auditoria (D-R04).</summary>
    public static readonly string[] OperatorScopes = ["admin:commercial", "admin:fiscal", "admin:security"];

    /// <param name="creatorScopes">Escopos de quem cria; null = linha de comando (primeiro operador, acesso ao servidor).</param>
    public async Task<string> CreateOperatorAsync(string emailAddr, string name, IReadOnlyList<string> scopes, string createdBy, CancellationToken ct,
        IReadOnlyCollection<string>? creatorScopes = null)
    {
        var e = Normalize(emailAddr);
        if (!ValidEmail(e) || string.IsNullOrWhiteSpace(name) || scopes.Count == 0 || scopes.Any(x => !OperatorScopes.Contains(x)))
        {
            throw new AccountException(400, "ERR_VALIDATION", "Dados inválidos", "E-mail, nome e escopos (admin:commercial, admin:fiscal e/ou admin:security).");
        }

        // D-R04: ninguém concede escopo que não tem (antes um operador comercial criava operadores fiscais e furava a
        // regra de duas pessoas das alíquotas).
        if (creatorScopes is not null && scopes.Except(creatorScopes, StringComparer.Ordinal).FirstOrDefault() is { } missing)
        {
            throw new AccountException(403, "ERR_FORBIDDEN_SCOPE", "Escopo não permitido", $"Você não tem '{missing}' e por isso não pode concedê-lo.");
        }

        var id = Guid.CreateVersion7();
        var (h, s) = Passwords.Hash(OpaqueToken.New());   // senha aleatória e descartada: só o link de primeiro acesso entra
        await using var conn = await appDb.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO users (id, realm, email, name, password_hash, password_salt, operator_scopes, operator_created_by) VALUES ($1, 'operator', $2, $3, $4, $5, $6, $7)
            ON CONFLICT (realm, email) DO NOTHING
            """, conn))
        {
            ins.Parameters.Add(new() { Value = id });
            ins.Parameters.Add(new() { Value = e });
            ins.Parameters.Add(new() { Value = name.Trim() });
            ins.Parameters.Add(new() { Value = h });
            ins.Parameters.Add(new() { Value = s });
            ins.Parameters.Add(new() { Value = scopes.Distinct().ToArray() });
            ins.Parameters.Add(new() { Value = createdBy });
            if (await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new AccountException(409, "ERR_CONFLICT", "Já existe operador com este e-mail");
            }
        }

        var token = await NewUserTokenAsync(conn, null, id, "operator_setup", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var link = $"{O.BackofficeBaseUrl}/backoffice/redefinir-senha?token={token}";
        await email.SendAsync(EmailTemplates.OperatorSetup(e, name, link), ct).ConfigureAwait(false);
        await AuditAsync(null, "operator", createdBy, "operador.criado", e, null, new JsonObject { ["escopos"] = string.Join(',', scopes) }, ct).ConfigureAwait(false);
        return link;
    }

    // ------------------------------------------------------------------------------------------------- utilidades

    private static async Task<string> NewUserTokenAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid user, string purpose, TimeSpan validity, CancellationToken ct)
    {
        var token = OpaqueToken.New();
        await BatchAsync(conn, tx, ct,
            Cmd("DELETE FROM user_tokens WHERE user_id = $1 AND purpose = $2 AND used_at IS NULL", user, purpose),
            Cmd("INSERT INTO user_tokens (token_hash, purpose, user_id, expires_at) VALUES ($1, $2, $3, now() + $4)", OpaqueToken.Hash(token), purpose, user, validity)).ConfigureAwait(false);
        return token;
    }

    private async Task<Guid?> ConsumeUserTokenAsync(string token, string purpose, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("""
            UPDATE user_tokens SET used_at = now() WHERE token_hash = $1 AND purpose = $2 AND used_at IS NULL AND expires_at > now() RETURNING user_id
            """);
        cmd.Parameters.Add(new() { Value = OpaqueToken.Hash(token ?? "") });
        cmd.Parameters.Add(new() { Value = purpose });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
    }

    private static string Actor(SessionInfo s) => s.Realm == "operator" ? "operator" : "user";

    /// <summary>Vários comandos com parâmetros posicionais ($1...) precisam de NpgsqlBatch (um comando só não aceita).</summary>
    private static NpgsqlBatchCommand Cmd(string sql, params object[] args)
    {
        var c = new NpgsqlBatchCommand(sql);
        foreach (var a in args)
        {
            c.Parameters.Add(new() { Value = a });
        }

        return c;
    }

    private async Task<int> BatchAsync(CancellationToken ct, params NpgsqlBatchCommand[] commands)
    {
        await using var b = appDb.CreateBatch();
        foreach (var c in commands)
        {
            b.BatchCommands.Add(c);
        }

        return await b.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> BatchAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct, params NpgsqlBatchCommand[] commands)
    {
        await using var b = new NpgsqlBatch(conn, tx);
        foreach (var c in commands)
        {
            b.BatchCommands.Add(c);
        }

        return await b.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> BatchAsync(TenantScope scope, CancellationToken ct, params NpgsqlBatchCommand[] commands)
    {
        await using var b = scope.CreateBatch();
        foreach (var c in commands)
        {
            b.BatchCommands.Add(c);
        }

        return await b.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    /// <summary>Trilha de auditoria (D-U07). Eventos de tenant vão no contexto do tenant (RLS); os demais, sem tenant.</summary>
    public async Task AuditAsync(TenantId? tenant, string actorType, string? actorId, string action, string? target, string? ip, JsonObject detail, CancellationToken ct)
    {
        try
        {
            const string Sql = "INSERT INTO audit_log (actor_type, actor_id, action, tenant_id, target, ip, trace_id, detail) VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)";
            void Bind(NpgsqlCommand cmd)
            {
                cmd.Parameters.Add(new() { Value = actorType });
                cmd.Parameters.Add(new() { Value = (object?)actorId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = action });
                cmd.Parameters.Add(new() { Value = (object?)tenant?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new() { Value = (object?)Truncate(target, 200) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = (object?)ip ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = (object?)System.Diagnostics.Activity.Current?.TraceId.ToString() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = detail.ToJsonString() });
            }

            if (tenant is { } t)
            {
                await tenantDb.InTenantAsync(t, async (s, c) =>
                {
                    await using var cmd = s.CreateCommand(Sql);
                    Bind(cmd);
                    return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            else
            {
                await using var cmd = appDb.CreateCommand(Sql);
                Bind(cmd);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (NpgsqlException ex)
        {
            LogAuditFailed(log, ex, action);   // a trilha não pode derrubar o login
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao gravar a trilha de auditoria ({Action}).")]
    private static partial void LogAuditFailed(ILogger logger, Exception ex, string action);
}
