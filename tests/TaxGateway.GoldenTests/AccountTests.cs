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

        var trail = await Send(bo, HttpMethod.Get, $"/admin/v1/auditoria-acoes?ator={Uri.EscapeDataString("operador")}");
        Assert.Equal(HttpStatusCode.OK, trail.Status);
    }
}
