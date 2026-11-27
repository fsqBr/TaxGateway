using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Web;

using TaxGateway.Identity.Accounts;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Contas de pessoas com MFA (D-U01 a D-U09): cadastro → confirmação → login → MFA obrigatório → portal por cookie,
/// convites e papéis, bloqueio, redefinição de senha, CSRF e operador do backoffice.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class AccountTests(GoldenFixture env)
{
    private const string Senha = "Uma frase longa e boa 2027!";
    private static readonly string[] PeopleOnlyScope = ["membros:manage"];
    private static readonly string[] FiscalScope = ["admin:fiscal"];
    private static readonly string[] UsageOnly = ["usage:read"];
    private static readonly string[] OptOut = ["RESUMO_MENSAL", "MOTOR_ATUALIZADO", "RESUMO_MENSAL"];
    private static readonly string[] Critical = ["PAGAMENTO_PENDENTE"];
    private static readonly string[] SecurityCommercialScopes = ["admin:security", "admin:commercial"];

    private static HttpRequestMessage Req(HttpMethod m, string path, object? body = null, bool csrf = true)
    {
        var r = new HttpRequestMessage(m, path);
        if (body is not null)
        {
            r.Content = JsonContent.Create(body);
        }

        if (csrf)
        {
            r.Headers.Add("X-TaxGateway-Csrf", "1");
        }

        return r;
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpClient c, HttpMethod m, string path, object? body = null, bool csrf = true)
    {
        using var resp = await c.SendAsync(Req(m, path, body, csrf));
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Token(string link) => HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;

    private string Code(string secretBase32)
    {
        var secret = FromBase32(secretBase32);
        return Totp.Code(secret, Totp.StepAt(env.Clock.GetUtcNow()));
    }

    private static byte[] FromBase32(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(s.Select(c => Convert.ToString(Alphabet.IndexOf(c, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }

    /// <summary>Cadastro completo até o portal; devolve o navegador logado (com MFA) e o segredo TOTP.</summary>
    private async Task<(HttpClient Browser, string Email, string Secret, Guid Tenant)> SignupAndLoginAsync(string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@exemplo.test";
        var browser = env.NewBrowser();
        var signup = await Send(browser, HttpMethod.Post, "/v1/conta/cadastro", new
        {
            nome = "Maria Fiscal",
            email,
            senha = Senha,
            empresa = new { nome = $"Empresa {label}", cnpj = GoldenFixture.NewCnpj() },
            aceite_termos = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, signup.Status);

        // Sem confirmar o e-mail não entra.
        var early = await Send(browser, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha });
        Assert.Equal("ERR_EMAIL_NAO_CONFIRMADO", early.Body!["code"]!.GetValue<string>());

        var link = env.Emails.LastLinkTo(email)!;
        Assert.Contains("/portal/confirmar-email?token=", link, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await Send(browser, HttpMethod.Post, "/v1/conta/confirmar-email", new { token = Token(link) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(browser, HttpMethod.Post, "/v1/conta/confirmar-email", new { token = Token(link) })).Status);   // uso único

        var login = await Send(browser, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha });
        Assert.Equal("configurar", login.Body!["mfa"]!.GetValue<string>());

        // Antes do MFA, nada da API: o MFA é obrigatório.
        var blocked = await Send(browser, HttpMethod.Get, "/v1/me");
        Assert.Equal("ERR_MFA_NECESSARIO", blocked.Body!["code"]!.GetValue<string>());

        var begin = await Send(browser, HttpMethod.Post, "/v1/conta/mfa/iniciar");
        var secret = begin.Body!["segredo"]!.GetValue<string>();
        Assert.StartsWith("otpauth://totp/TaxGateway:", begin.Body["otpauth_uri"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(browser, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = "000000" })).Status);
        var enable = await Send(browser, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = Code(secret) });
        Assert.Equal(10, enable.Body!["codigos_recuperacao"]!.AsArray().Count);

        var session = await Send(browser, HttpMethod.Get, "/v1/conta/sessao");
        Assert.Equal("ok", session.Body!["mfa"]!.GetValue<string>());
        Assert.Equal("owner", session.Body["papel"]!.GetValue<string>());
        Assert.Equal("SANDBOX", session.Body["plano"]!.GetValue<string>());
        var tenant = session.Body["empresa_ativa"]!.GetValue<Guid>();
        return (browser, email, secret, tenant);
    }

    /// <summary>D-R32/D-R33: boas-vindas na confirmação, aviso de entrada por navegador novo e preferências de e-mail.</summary>
    [Fact]
    public async Task Boas_vindas_navegador_novo_e_preferencias_de_email()
    {
        var (browser, email, _, _) = await SignupAndLoginAsync("ciclo");
        Assert.Single(env.Emails.Sent, m => m.To == email && m.Subject == "Boas-vindas ao TaxGateway");

        // Entrada pelo mesmo navegador: sem aviso. Por outro navegador: aviso de segurança com o dispositivo.
        var before = env.Emails.Sent.Count(m => m.To == email && m.Subject.StartsWith("Aviso de segurança", StringComparison.Ordinal));
        using (var same = env.NewBrowser())
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(same, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Status);
        }

        Assert.Equal(before, env.Emails.Sent.Count(m => m.To == email && m.Subject.StartsWith("Aviso de segurança", StringComparison.Ordinal)));
        using (var other = env.NewBrowser())
        {
            other.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
            Assert.Equal(HttpStatusCode.OK, (await Send(other, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Status);
        }

        Assert.Contains(env.Emails.Sent, m => m.To == email && m.Text.Contains("navegador ou dispositivo novo (Chrome no Windows", StringComparison.Ordinal));

        // Preferências: só informativos podem ser desligados.
        var prefs = await Send(browser, HttpMethod.Get, "/v1/conta/preferencias-email");
        Assert.Empty(prefs.Body!["desligados"]!.AsArray());
        Assert.Contains(prefs.Body["opcionais"]!.AsArray(), o => o!["tipo"]!.GetValue<string>() == "RESUMO_MENSAL");
        var off = await Send(browser, HttpMethod.Put, "/v1/conta/preferencias-email", new { desligados = OptOut });
        Assert.Equal(["MOTOR_ATUALIZADO", "RESUMO_MENSAL"], off.Body!["desligados"]!.AsArray().Select(x => x!.GetValue<string>()));
        var bad = await Send(browser, HttpMethod.Put, "/v1/conta/preferencias-email", new { desligados = Critical });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(2, (await Send(browser, HttpMethod.Get, "/v1/conta/preferencias-email")).Body!["desligados"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(env.NewBrowser(), HttpMethod.Get, "/v1/conta/preferencias-email")).Status);
    }

    [Fact]
    public async Task Cadastro_confirmacao_login_e_MFA_obrigatorio_liberam_o_portal_por_cookie()
    {
        var (browser, email, secret, tenant) = await SignupAndLoginAsync("fluxo");

        // Portal pelo cookie (sem API key): a mesma API que o ERP usa, com os escopos do papel.
        var me = await Send(browser, HttpMethod.Get, "/v1/me");
        Assert.Equal(tenant, me.Body!["tenant_id"]!.GetValue<Guid>());
        Assert.Equal("sessao", me.Body["credencial"]!.GetValue<string>());
        Assert.Equal("SANDBOX", me.Body["plano"]!.GetValue<string>());

        // CSRF: sem o header, ação que muda estado é recusada mesmo com cookie válido.
        var noCsrf = await Send(browser, HttpMethod.Post, "/v1/api-keys", new { nome = "erp" }, csrf: false);
        Assert.Equal("ERR_CSRF", noCsrf.Body!["code"]!.GetValue<string>());

        // Chave criada pela pessoa: padrão mínimo (simular + auditar), nunca escopos só de pessoas.
        var key = await Send(browser, HttpMethod.Post, "/v1/api-keys", new { nome = "erp" });
        Assert.Equal(HttpStatusCode.Created, key.Status);
        Assert.Equal(["simulacao:write", "auditoria:write"], key.Body!["scopes"]!.AsArray().Select(x => x!.GetValue<string>()));
        var forbidden = await Send(browser, HttpMethod.Post, "/v1/api-keys", new { nome = "x", scopes = PeopleOnlyScope });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);

        // Sair encerra a sessão no servidor.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(browser, HttpMethod.Post, "/v1/conta/sair")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(browser, HttpMethod.Get, "/v1/me")).Status);

        // Novo login pede o código; código repetido não vale (anti-replay); código de recuperação vale uma vez.
        var again = env.NewBrowser();
        Assert.Equal("verificar", (await Send(again, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        env.Clock.Advance(TimeSpan.FromSeconds(31));
        var code = Code(secret);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(again, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = code })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(again, HttpMethod.Get, "/v1/me")).Status);
        var third = env.NewBrowser();
        await Send(third, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(third, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = code })).Status);
    }

    [Fact]
    public async Task Senha_errada_bloqueia_e_redefinicao_encerra_sessoes()
    {
        var (browser, email, _, _) = await SignupAndLoginAsync("bloqueio");
        var other = env.NewBrowser();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("ERR_CREDENCIAIS", (await Send(other, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = "errada errada errada" })).Body!["code"]!.GetValue<string>());
        }

        Assert.Equal("ERR_CONTA_BLOQUEADA", (await Send(other, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["code"]!.GetValue<string>());
        Assert.Contains(env.Emails.Sent, m => m.To == email && m.Text.Contains("bloqueada", StringComparison.Ordinal));

        // E-mail inexistente: mesma resposta (sem enumerar contas).
        Assert.Equal(HttpStatusCode.Accepted, (await Send(other, HttpMethod.Post, "/v1/conta/senha/esqueci", new { email = "ninguem@exemplo.test" })).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(other, HttpMethod.Post, "/v1/conta/senha/esqueci", new { email })).Status);
        var link = env.Emails.LastLinkTo(email)!;
        Assert.Contains("/portal/redefinir-senha?token=", link, StringComparison.Ordinal);
        Assert.Equal("ERR_SENHA_FRACA", (await Send(other, HttpMethod.Post, "/v1/conta/senha/redefinir", new { token = Token(link), senha = "curta" })).Body!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(other, HttpMethod.Post, "/v1/conta/senha/redefinir", new { token = Token(link), senha = "Outra frase longa e boa 2028!" })).Status);

        // A sessão antiga morreu; a nova senha entra (o bloqueio foi zerado) e o MFA continua exigido.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(browser, HttpMethod.Get, "/v1/me")).Status);
        Assert.Equal("verificar", (await Send(other, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = "Outra frase longa e boa 2028!" })).Body!["mfa"]!.GetValue<string>());
    }

    [Fact]
    public async Task Convite_papel_e_isolamento_entre_empresas()
    {
        var (owner, _, _, tenant) = await SignupAndLoginAsync("dono");
        var invitedEmail = $"convidado-{Guid.NewGuid():N}@exemplo.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Send(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = invitedEmail, papel = "fiscal" })).Status);

        // Sandbox permite 2 usuários: dono + 1 convite pendente = limite.
        var over = await Send(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = "mais@exemplo.test", papel = "viewer" });
        Assert.Equal("ERR_LIMITE_USUARIOS", over.Body!["code"]!.GetValue<string>());

        var link = env.Emails.LastLinkTo(invitedEmail)!;
        var guest = env.NewBrowser();
        var info = await Send(guest, HttpMethod.Get, $"/v1/conta/convite?token={Uri.EscapeDataString(Token(link))}");
        Assert.False(info.Body!["tem_conta"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await Send(guest, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = Token(link), nome = "João Fiscal", senha = Senha })).Status);

        // Entra, configura MFA, e o papel fiscal limita o que pode fazer.
        await Send(guest, HttpMethod.Post, "/v1/conta/entrar", new { email = invitedEmail, senha = Senha });
        var secret = (await Send(guest, HttpMethod.Post, "/v1/conta/mfa/iniciar")).Body!["segredo"]!.GetValue<string>();
        await Send(guest, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = Code(secret) });
        var session = await Send(guest, HttpMethod.Get, "/v1/conta/sessao");
        Assert.Equal(tenant, session.Body!["empresa_ativa"]!.GetValue<Guid>());
        Assert.Equal("fiscal", session.Body["papel"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(guest, HttpMethod.Post, "/v1/api-keys", new { nome = "x" })).Status);   // fiscal não cria chaves
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(guest, HttpMethod.Get, "/v1/conta/membros")).Status);                  // nem gerencia acessos

        // Dono vê o membro e não pode ser removido sendo o único dono.
        var members = (await Send(owner, HttpMethod.Get, "/v1/conta/membros")).Body!["membros"]!.AsArray();
        Assert.Equal(2, members.Count);
        var ownerId = members.First(m => m!["papel"]!.GetValue<string>() == "owner")!["user_id"]!.GetValue<Guid>();
        Assert.Equal("ERR_ULTIMO_DONO", (await Send(owner, HttpMethod.Delete, $"/v1/conta/membros/{ownerId}")).Body!["code"]!.GetValue<string>());

        // Removido, perde o acesso na hora.
        var guestId = members.First(m => m!["papel"]!.GetValue<string>() == "fiscal")!["user_id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(owner, HttpMethod.Delete, $"/v1/conta/membros/{guestId}")).Status);
        Assert.Equal("ERR_SEM_EMPRESA", (await Send(guest, HttpMethod.Get, "/v1/me")).Body!["code"]!.GetValue<string>());

        // Outra empresa: o dono A não troca para a empresa B.
        var (_, _, _, tenantB) = await SignupAndLoginAsync("outra");
        Assert.Equal(HttpStatusCode.NotFound, (await Send(owner, HttpMethod.Post, "/v1/conta/empresa-ativa", new { tenant_id = tenantB })).Status);
    }

    [Fact]
    public async Task Cadastro_nao_enumera_contas_e_empresa_existente_pede_convite()
    {
        var (_, email, _, _) = await SignupAndLoginAsync("enum");
        var browser = env.NewBrowser();
        var dup = await Send(browser, HttpMethod.Post, "/v1/conta/cadastro", new
        {
            nome = "Outra Pessoa",
            email,
            senha = Senha,
            empresa = new { nome = "Empresa Y", cnpj = GoldenFixture.NewCnpj() },
            aceite_termos = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, dup.Status);   // mesma resposta de um cadastro novo
        Assert.Contains(env.Emails.Sent, m => m.To == email && m.Subject.Contains("já tem uma conta", StringComparison.Ordinal));

        var weak = await Send(browser, HttpMethod.Post, "/v1/conta/cadastro", new
        {
            nome = "Fulano",
            email = "fulano@exemplo.test",
            senha = "fulano12345678",
            empresa = new { nome = "Empresa Z", cnpj = GoldenFixture.NewCnpj() },
            aceite_termos = true,
        });
        Assert.Equal("ERR_SENHA_FRACA", weak.Body!["code"]!.GetValue<string>());

        var noCsrf = await Send(browser, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha }, csrf: false);
        Assert.Equal("ERR_CSRF", noCsrf.Body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Operador_entra_no_backoffice_com_senha_e_MFA()
    {
        var accounts = (AccountService)env.Services.GetService(typeof(AccountService))!;
        var email = $"op-{Guid.NewGuid():N}@taxgateway.test";
        var link = await accounts.CreateOperatorAsync(email, "Operadora", ["admin:commercial"], "teste", CancellationToken.None);
        var bo = env.NewBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bo, HttpMethod.Post, "/admin/v1/conta/senha/redefinir", new { token = Token(link), senha = Senha })).Status);
        Assert.Equal("configurar", (await Send(bo, HttpMethod.Post, "/admin/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        Assert.Equal("ERR_MFA_NECESSARIO", (await Send(bo, HttpMethod.Get, "/admin/v1/me")).Body!["code"]!.GetValue<string>());
        var secret = (await Send(bo, HttpMethod.Post, "/admin/v1/conta/mfa/iniciar")).Body!["segredo"]!.GetValue<string>();
        await Send(bo, HttpMethod.Post, "/admin/v1/conta/mfa/ativar", new { codigo = Code(secret) });

        Assert.Equal(HttpStatusCode.OK, (await Send(bo, HttpMethod.Get, "/admin/v1/tenants")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bo, HttpMethod.Get, "/admin/v1/params/rates")).Status);   // sem admin:fiscal

        // A conta de cliente não serve no backoffice (realms separados), e a de operador não serve no portal.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(bo, HttpMethod.Get, "/v1/me")).Status);

        // D-R04: a trilha e a gestão de operadores exigem admin:security; um operador comercial não cria operador fiscal.
        var trail = await Send(bo, HttpMethod.Get, $"/admin/v1/auditoria-acoes?ator={Uri.EscapeDataString("operador")}");
        Assert.Equal(HttpStatusCode.Forbidden, trail.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bo, HttpMethod.Post, "/admin/v1/operadores",
            new { email = $"x-{Guid.NewGuid():N}@taxgateway.test", nome = "X", escopos = FiscalScope })).Status);
    }

    /// <summary>D-R04: ninguém concede escopo que não tem — nem pela API nem pelo serviço.</summary>
    [Fact]
    public async Task Operador_nao_concede_escopo_que_nao_tem()
    {
        var accounts = (AccountService)env.Services.GetService(typeof(AccountService))!;
        var ex = await Assert.ThrowsAsync<AccountException>(() => accounts.CreateOperatorAsync($"y-{Guid.NewGuid():N}@taxgateway.test", "Y",
            FiscalScope, "comercial@taxgateway.test", CancellationToken.None, SecurityCommercialScopes));
        Assert.Equal(403, ex.Status);
        Assert.NotNull(await accounts.CreateOperatorAsync($"z-{Guid.NewGuid():N}@taxgateway.test", "Z", ["admin:commercial"], "seg@taxgateway.test",
            CancellationToken.None, SecurityCommercialScopes));
    }

    /// <summary>
    /// D-R03: com a senha certa, o segundo fator não pode ser testado à exaustão — 5 códigos errados encerram a sessão e
    /// bloqueiam a conta (antes o bloqueio não valia para o MFA e a sessão seguia aceitando tentativas).
    /// </summary>
    [Fact]
    public async Task Codigos_MFA_errados_encerram_a_sessao_e_bloqueiam_a_conta()
    {
        var (_, email, secret, _) = await SignupAndLoginAsync("mfa-forca-bruta");
        var attacker = env.NewBrowser();
        Assert.Equal("verificar", (await Send(attacker, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        for (var i = 0; i < AccountService.MaxMfaFailuresPerSession - 1; i++)
        {
            Assert.Equal("ERR_CODIGO_INVALIDO", (await Send(attacker, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = "000000" })).Body!["code"]!.GetValue<string>());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(attacker, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = "000000" })).Status);
        // A sessão morreu: nem o código certo vale nela.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(attacker, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo = Code(secret) })).Status);
        // E a conta ficou bloqueada (as falhas de MFA contam no mesmo limite da senha).
        Assert.Equal("ERR_CONTA_BLOQUEADA", (await Send(attacker, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["code"]!.GetValue<string>());
    }

    private string Pfx(string cnpj, TestKit.TestPki? pki = null)
    {
        var now = env.Clock.GetUtcNow();
        using var leaf = TestKit.TestPki.IssueLeaf((pki ?? env.Pki).Root, cnpj, now.AddDays(-400), now.AddYears(3));
        return Convert.ToBase64String(leaf.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "senha-a1"));
    }

    private static async Task<string> RootAsync(HttpClient browser) => (await Send(browser, HttpMethod.Get, "/v1/me")).Body!["cnpj_raiz"]!.GetValue<string>();

    /// <summary>
    /// D-R10: empresa do autocadastro nasce sem verificação — a captura de DF-e (e o plano pago) exigem verificar.
    /// Certificado de outra raiz ou fora da ICP-Brasil é recusado; o e-CNPJ da mesma raiz verifica.
    /// </summary>
    [Fact]
    public async Task Titularidade_por_certificado_libera_dfe_e_recusa_certificado_alheio()
    {
        var (browser, _, _, tenant) = await SignupAndLoginAsync("verif-cert");
        var root = await RootAsync(browser);
        Assert.False((await Send(browser, HttpMethod.Get, "/v1/verificacao-cnpj")).Body!["verificada"]!.GetValue<bool>());
        // Sandbox não tem DF-e (D-R13); num plano com DF-e, o que falta é a verificação.
        Assert.Equal("ERR_PLANO_SEM_DFE", (await Send(browser, HttpMethod.Post, $"/v1/cnpjs/{root}/dfe/sync", new { c_uf_autor = 35 })).Body!["code"]!.GetValue<string>());
        using (var change = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano") { Content = JsonContent.Create(new { plano = "PROFISSIONAL" }) })
        {
            change.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(change)).StatusCode);
        }

        for (var i = 0; i < 40 && (await Send(browser, HttpMethod.Get, "/v1/me")).Body?["plano"]?.GetValue<string>() != "PROFISSIONAL"; i++)
        {
            await Task.Delay(250);
        }
        Assert.Equal("ERR_EMPRESA_NAO_VERIFICADA", (await Send(browser, HttpMethod.Post, $"/v1/cnpjs/{root}/dfe/sync", new { c_uf_autor = 35 })).Body!["code"]!.GetValue<string>());

        var other = await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/certificado", new { pfx_base64 = Pfx(GoldenFixture.NewCnpj()), senha = "senha-a1" });
        Assert.Equal("ERR_CERT_CNPJ_MISMATCH", other.Body!["code"]!.GetValue<string>());
        var untrusted = TestKit.TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"tg-untrusted-{Guid.NewGuid():N}"));
        var fake = await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/certificado", new { pfx_base64 = Pfx(root, untrusted), senha = "senha-a1" });
        Assert.Equal("ERR_CERT_NAO_ICP_BRASIL", fake.Body!["code"]!.GetValue<string>());
        Assert.Equal("ERR_CERT_INVALID", (await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/certificado", new { pfx_base64 = Pfx(root), senha = "errada" })).Body!["code"]!.GetValue<string>());

        var ok = await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/certificado", new { pfx_base64 = Pfx(root), senha = "senha-a1" });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var status = (await Send(browser, HttpMethod.Get, "/v1/verificacao-cnpj")).Body!;
        Assert.True(status["verificada"]!.GetValue<bool>());
        Assert.Equal("CERTIFICADO", status["metodo"]!.GetValue<string>());
        Assert.NotEqual("ERR_EMPRESA_NAO_VERIFICADA", (await Send(browser, HttpMethod.Post, $"/v1/cnpjs/{root}/dfe/sync", new { c_uf_autor = 35 })).Body?["code"]?.GetValue<string>());
    }

    /// <summary>D-R10: código enviado só ao e-mail do cadastro público do CNPJ (mascarado na tela), com tentativas limitadas.</summary>
    [Fact]
    public async Task Titularidade_por_codigo_no_email_do_cadastro_da_Receita()
    {
        var (browser, _, _, _) = await SignupAndLoginAsync("verif-email");
        var root = await RootAsync(browser);
        var sent = await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/email");
        Assert.Equal(HttpStatusCode.Accepted, sent.Status);
        var masked = sent.Body!["enviado_para"]!.GetValue<string>();
        Assert.EndsWith("@empresa.test", masked, StringComparison.Ordinal);
        Assert.DoesNotContain(root, masked, StringComparison.Ordinal);

        var mail = env.Emails.Sent.Last(m => m.To == $"titular-{root}@empresa.test");
        var code = System.Text.RegularExpressions.Regex.Match(mail.Subject, "[0-9]{6}").Value;
        Assert.Equal("ERR_CODIGO_INVALIDO", (await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/email/confirmar", new { codigo = code == "000000" ? "000001" : "000000" })).Body!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Send(browser, HttpMethod.Post, "/v1/verificacao-cnpj/email/confirmar", new { codigo = code })).Status);
        Assert.Equal("EMAIL_RECEITA", (await Send(browser, HttpMethod.Get, "/v1/verificacao-cnpj")).Body!["metodo"]!.GetValue<string>());
        Assert.Contains((await Send(browser, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray(), a => a!["tipo"]!.GetValue<string>() == "EMPRESA_VERIFICADA");
    }

    /// <summary>
    /// D-R10: o cadastro com CNPJ já existente não revela quem é cliente (mesmo 202); quem tem o e-CNPJ contesta, a conta
    /// atual é avisada e o backoffice transfere a empresa — e, verificada, ela não pode mais ser contestada.
    /// </summary>
    [Fact]
    public async Task Disputa_de_titularidade_com_certificado_transfere_a_empresa()
    {
        var (owner, _, _, tenant) = await SignupAndLoginAsync("squatter");
        var root = await RootAsync(owner);

        var claimant = env.NewBrowser();
        var email = $"titular-real-{Guid.NewGuid():N}@exemplo.test";
        var signup = await Send(claimant, HttpMethod.Post, "/v1/conta/cadastro", new
        {
            nome = "Titular Real",
            email,
            senha = Senha,
            empresa = new { nome = "Empresa de verdade", cnpj = root },
            aceite_termos = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, signup.Status);   // mesma resposta de um cadastro novo
        Assert.Contains(env.Emails.Sent, m => m.To == email && m.Text.Contains("já tem cadastro", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await Send(claimant, HttpMethod.Post, "/v1/conta/confirmar-email", new { token = Token(env.Emails.LastLinkTo(email)!) })).Status);
        Assert.Equal("configurar", (await Send(claimant, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        var secret = (await Send(claimant, HttpMethod.Post, "/v1/conta/mfa/iniciar")).Body!["segredo"]!.GetValue<string>();
        await Send(claimant, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = Code(secret) });

        var dispute = await Send(claimant, HttpMethod.Post, "/v1/conta/disputa-cnpj", new { pfx_base64 = Pfx(root), senha = "senha-a1" });
        Assert.Equal(HttpStatusCode.Accepted, dispute.Status);
        Assert.Contains((await Send(owner, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray(), a => a!["tipo"]!.GetValue<string>() == "DISPUTA_CNPJ");

        using var list = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/disputas");
        list.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        var disputes = System.Text.Json.Nodes.JsonNode.Parse(await (await env.Api.SendAsync(list)).Content.ReadAsStringAsync())!["disputas"]!.AsArray();
        var id = disputes.First(d => d!["requerente"]!.GetValue<string>() == email)!["id"]!.GetValue<Guid>();
        using var resolve = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/disputas/{id}/resolver") { Content = System.Net.Http.Json.JsonContent.Create(new { acao = "transferir" }) };
        resolve.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(resolve)).StatusCode);

        var session = (await Send(claimant, HttpMethod.Get, "/v1/conta/sessao")).Body!;
        Assert.Contains(session["empresas"]!.AsArray(), e => e!["tenant_id"]!.GetValue<Guid>() == tenant && e["papel"]!.GetValue<string>() == "owner");
        Assert.DoesNotContain((await Send(owner, HttpMethod.Get, "/v1/conta/sessao")).Body!["empresas"]!.AsArray(), e => e!["tenant_id"]!.GetValue<Guid>() == tenant);

        // Verificada pela disputa: uma nova contestação é recusada.
        var again = await Send(owner, HttpMethod.Post, "/v1/conta/disputa-cnpj", new { pfx_base64 = Pfx(root), senha = "senha-a1" });
        Assert.Equal("ERR_EMPRESA_VERIFICADA", again.Body!["code"]!.GetValue<string>());
    }


    /// <summary>
    /// Rotas da "Minha conta": sessões abertas, sair das outras, novos códigos de recuperação (pede TOTP) e troca de
    /// senha (confere a atual, encerra as outras sessões, a antiga deixa de entrar).
    /// </summary>
    [Fact]
    public async Task Minha_conta_sessoes_codigos_de_recuperacao_e_troca_de_senha()
    {
        var (browser, email, secret, _) = await SignupAndLoginAsync("minha-conta");

        // Segunda sessão (outro navegador) entra com código de recuperação — o TOTP do passo atual já foi usado.
        var other = env.NewBrowser();
        Assert.Equal("verificar", (await Send(other, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["mfa"]!.GetValue<string>());
        var codes = (await Send(browser, HttpMethod.Post, "/v1/conta/mfa/novos-codigos", new { codigo = "000000" }));
        Assert.Equal(HttpStatusCode.BadRequest, codes.Status);
        var step = Totp.StepAt(env.Clock.GetUtcNow()) + 1;   // janela de ±1 passo; anti-replay exige passo maior
        var fresh = await Send(browser, HttpMethod.Post, "/v1/conta/mfa/novos-codigos", new { codigo = Totp.Code(FromBase32(secret), step) });
        Assert.Equal(HttpStatusCode.OK, fresh.Status);
        var recovery = fresh.Body!["codigos_recuperacao"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal(10, recovery.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(other, HttpMethod.Post, "/v1/conta/mfa/verificar", new { codigo_recuperacao = recovery[0] })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(other, HttpMethod.Get, "/v1/me")).Status);
        Assert.Equal(9, (await Send(other, HttpMethod.Get, "/v1/conta/sessao")).Body!["codigos_recuperacao_restantes"]!.GetValue<int>());

        var sessions = (await Send(browser, HttpMethod.Get, "/v1/conta/sessoes")).Body!.AsArray();
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s!["atual"]!.GetValue<bool>());

        Assert.Equal(1, (await Send(browser, HttpMethod.Post, "/v1/conta/sair-outras")).Body!["encerradas"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(other, HttpMethod.Get, "/v1/me")).Status);
        Assert.Single((await Send(browser, HttpMethod.Get, "/v1/conta/sessoes")).Body!.AsArray());

        // Troca de senha: a atual errada é recusada; a certa troca e a antiga deixa de entrar.
        const string Nova = "Nova frase longa e segura 2029!";
        var wrong = await Send(browser, HttpMethod.Post, "/v1/conta/senha/alterar", new { atual = "nao e esta a senha", nova = Nova });
        Assert.True(wrong.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{wrong.Status}");
        Assert.Equal("ERR_SENHA_FRACA", (await Send(browser, HttpMethod.Post, "/v1/conta/senha/alterar", new { atual = Senha, nova = "curta" })).Body!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(browser, HttpMethod.Post, "/v1/conta/senha/alterar", new { atual = Senha, nova = Nova })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(browser, HttpMethod.Get, "/v1/me")).Status);   // a sessão de quem trocou continua
        var third = env.NewBrowser();
        Assert.Equal("ERR_CREDENCIAIS", (await Send(third, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Senha })).Body!["code"]!.GetValue<string>());
        Assert.Equal("verificar", (await Send(third, HttpMethod.Post, "/v1/conta/entrar", new { email, senha = Nova })).Body!["mfa"]!.GetValue<string>());
    }


    /// <summary>D-R14: o limite de usuários vale também no aceite — convite feito antes de um rebaixamento não passa do limite.</summary>
    [Fact]
    public async Task Convite_aceito_depois_de_rebaixar_o_plano_respeita_o_limite_de_usuarios()
    {
        var (owner, _, _, tenant) = await SignupAndLoginAsync("limite-aceite");
        var invitedEmail = $"convidado-tarde-{Guid.NewGuid():N}@exemplo.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Send(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = invitedEmail, papel = "fiscal" })).Status);

        var code = $"TUSR{Random.Shared.Next(10000, 99999)}";
        using (var plan = new HttpRequestMessage(HttpMethod.Put, $"/admin/v1/planos/{code}")
        {
            Content = JsonContent.Create(new
            {
                name = "Um usuário",
                price_cents = 0,
                included_docs = 10,
                included_simulations = 10,
                hard_quota = true,
                max_users = 1,
                rpm = 60,
                burst = 20,
                concurrency = 3,
                is_public = false,
                self_service = false,
                active = true
            }),
        })
        {
            plan.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(plan)).StatusCode);
        }

        using (var change = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano") { Content = JsonContent.Create(new { plano = code }) })
        {
            change.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(change)).StatusCode);
        }

        var guest = env.NewBrowser();
        var accept = await Send(guest, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = Token(env.Emails.LastLinkTo(invitedEmail)!), nome = "Chegou Tarde", senha = Senha });
        Assert.Equal("ERR_LIMITE_USUARIOS", accept.Body!["code"]!.GetValue<string>());
        Assert.Single((await Send(owner, HttpMethod.Get, "/v1/conta/membros")).Body!["membros"]!.AsArray());
    }


    private static object Sim(string cnpj, object? nominais = null) => new
    {
        cnpj_emitente = cnpj,
        regime_tributario = "REGIME_REGULAR",
        data_fato_gerador = "2027-06-15T14:00:00-03:00",
        municipio_ibge = 3550308,
        uf = "SP",
        itens = new[] { new { numero = 1, cst_ibs_cbs = "000", c_class_trib = "000001", valor_base_calculo = 1000, ncm = "84713012", quantidade = 1, unidade = "UN", aliquotas_nominais = nominais } },
    };

    /// <summary>
    /// D-R20: alíquota informada pelo cliente = cenário NÃO OFICIAL; exceção do contribuinte (liminar) exige documento e
    /// duas pessoas, e gera um cálculo ajustado AO LADO do oficial — os totais oficiais não mudam.
    /// </summary>
    [Fact]
    public async Task Excecao_do_contribuinte_gera_calculo_ajustado_ao_lado_do_oficial()
    {
        var (owner, _, _, _) = await SignupAndLoginAsync("excecao");
        var root = await RootAsync(owner);
        var oficial = await Send(owner, HttpMethod.Post, "/v1/calculo/simular", Sim(root));
        Assert.True(oficial.Body!["oficial"]!.GetValue<bool>());
        Assert.Null(oficial.Body["cenario_ajustado"]);
        var cbsOficial = oficial.Body["totais"]!["cbs_valor"]!.GetValue<decimal>();

        var hipotetico = await Send(owner, HttpMethod.Post, "/v1/calculo/simular", Sim(root, new { cbs = 5.0 }));
        Assert.False(hipotetico.Body!["oficial"]!.GetValue<bool>());
        Assert.Contains(hipotetico.Body["avisos"]!.AsArray(), a => a!["codigo"]!.GetValue<string>() == "ALIQUOTA_INFORMADA_PELO_CLIENTE");

        var pdf = Convert.ToBase64String("%PDF-1.4\n% decisao liminar de teste\n"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(owner, HttpMethod.Post, "/v1/excecoes-aliquota", new
        {
            tributo = "CBS",
            aliquota = 5,
            vigencia_inicio = "2027-01-01",
            motivo = "LIMINAR",
            processo = "5000123-45.2027.4.03.6100",
            documento_base64 = Convert.ToBase64String("nao e pdf"u8.ToArray())
        })).Status);
        var proposed = await Send(owner, HttpMethod.Post, "/v1/excecoes-aliquota", new
        {
            tributo = "CBS",
            aliquota = 5,
            vigencia_inicio = "2027-01-01",
            motivo = "LIMINAR",
            processo = "5000123-45.2027.4.03.6100",
            documento_nome = "liminar.pdf",
            documento_base64 = pdf
        });
        Assert.Equal(HttpStatusCode.Created, proposed.Status);
        var id = proposed.Body!["id"]!.GetValue<Guid>();

        // Quem propôs não aprova a própria exceção (duas pessoas).
        Assert.Equal(HttpStatusCode.Conflict, (await Send(owner, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/aprovar")).Status);
        var fiscalEmail = $"fiscal-exc-{Guid.NewGuid():N}@exemplo.test";
        Assert.Equal(HttpStatusCode.Accepted, (await Send(owner, HttpMethod.Post, "/v1/conta/membros/convites", new { email = fiscalEmail, papel = "fiscal" })).Status);
        var fiscal = env.NewBrowser();
        Assert.Equal(HttpStatusCode.OK, (await Send(fiscal, HttpMethod.Post, "/v1/conta/convite/aceitar", new { token = Token(env.Emails.LastLinkTo(fiscalEmail)!), nome = "Fiscal Dois", senha = Senha })).Status);
        await Send(fiscal, HttpMethod.Post, "/v1/conta/entrar", new { email = fiscalEmail, senha = Senha });
        var secret = (await Send(fiscal, HttpMethod.Post, "/v1/conta/mfa/iniciar")).Body!["segredo"]!.GetValue<string>();
        await Send(fiscal, HttpMethod.Post, "/v1/conta/mfa/ativar", new { codigo = Code(secret) });
        Assert.Equal("ATIVA", (await Send(fiscal, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/aprovar")).Body!["situacao"]!.GetValue<string>());

        var list = (await Send(owner, HttpMethod.Get, "/v1/excecoes-aliquota")).Body!["excecoes"]!.AsArray();
        Assert.Contains(list, e => e!["id"]!.GetValue<Guid>() == id && e["proposta_por"]!.GetValue<string>() == "Maria Fiscal" && e["decidida_por"]!.GetValue<string>() == "Fiscal Dois");
        using (var doc = await owner.GetAsync(new Uri($"/v1/excecoes-aliquota/{id}/documento", UriKind.Relative)))
        {
            Assert.Equal("application/pdf", doc.Content.Headers.ContentType!.MediaType);
        }

        var ajustado = (await Send(owner, HttpMethod.Post, "/v1/calculo/simular", Sim(root))).Body!;
        Assert.True(ajustado["oficial"]!.GetValue<bool>());
        Assert.Equal(cbsOficial, ajustado["totais"]!["cbs_valor"]!.GetValue<decimal>());   // o oficial não muda
        var cenario = ajustado["cenario_ajustado"]!;
        Assert.Equal(50m, cenario["totais"]!["cbs_valor"]!.GetValue<decimal>());
        Assert.True(cenario["diferenca"]!.GetValue<decimal>() < 0);
        Assert.Equal("LIMINAR", cenario["excecoes"]![0]!["motivo"]!.GetValue<string>());
        Assert.Contains(ajustado["avisos"]!.AsArray(), a => a!["codigo"]!.GetValue<string>() == "EXCECAO_DO_CONTRIBUINTE");

        // Revogada, o cálculo ajustado some.
        Assert.Equal("REVOGADA", (await Send(owner, HttpMethod.Post, $"/v1/excecoes-aliquota/{id}/revogar")).Body!["situacao"]!.GetValue<string>());
        Assert.Null((await Send(owner, HttpMethod.Post, "/v1/calculo/simular", Sim(root))).Body!["cenario_ajustado"]);
    }


    /// <summary>D-R23: a pessoa no portal tem limite próprio — o ERP saturando o limite do plano não trava o portal.</summary>
    [Fact]
    public async Task Pessoa_no_portal_nao_disputa_o_limite_com_o_ERP()
    {
        var (owner, _, _, tenant) = await SignupAndLoginAsync("limite-pessoa");
        var key = (await Send(owner, HttpMethod.Post, "/v1/api-keys", new { nome = "erp", scopes = UsageOnly })).Body!["api_key"]!.GetValue<string>();
        var code = $"TRLP{Random.Shared.Next(10000, 99999)}";
        using (var plan = new HttpRequestMessage(HttpMethod.Put, $"/admin/v1/planos/{code}")
        {
            Content = JsonContent.Create(new
            {
                name = "Limite baixo",
                price_cents = 0,
                included_docs = 10,
                included_simulations = 10,
                hard_quota = true,
                rpm = 6,
                burst = 2,
                concurrency = 5,
                is_public = false,
                self_service = false,
                active = true
            }),
        })
        {
            plan.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(plan)).StatusCode);
        }

        using (var change = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano") { Content = JsonContent.Create(new { plano = code }) })
        {
            change.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(change)).StatusCode);
        }

        async Task<HttpStatusCode> Erp()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/usage");
            req.Headers.Add("X-Api-Key", key);
            using var resp = await env.Api.SendAsync(req);
            return resp.StatusCode;
        }

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 6 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = await Erp();
            if (last == HttpStatusCode.OK)
            {
                await Task.Yield();
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);   // o ERP esgotou o limite do plano
        Assert.Equal(HttpStatusCode.OK, (await Send(owner, HttpMethod.Get, "/v1/usage")).Status);   // a pessoa continua
    }

}
