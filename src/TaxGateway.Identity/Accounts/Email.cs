using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TaxGateway.Identity.Accounts;

public sealed record EmailMessage(string To, string Subject, string Text, string Html);

/// <summary>E-mail transacional (D-U06): confirmação de cadastro, redefinição de senha, convite, avisos de segurança.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Servidor SMTP. Vazio = só registra no log (dev sem Mailpit). Em dev/compose: Mailpit (127.0.0.1:1025).</summary>
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpTls { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string From { get; set; } = "TaxGateway <nao-responda@taxgateway.local>";
}

public sealed partial class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost))
        {
            LogNotSent(log, message.To, message.Subject, message.Text);
            return;
        }

        using var mail = new MailMessage { From = new MailAddress(ParseAddress(o.From).Address, ParseAddress(o.From).DisplayName), Subject = message.Subject, Body = message.Text };
        mail.To.Add(message.To);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, System.Text.Encoding.UTF8, "text/html"));
        using var smtp = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.SmtpTls };
        if (o.SmtpUser.Length > 0)
        {
            smtp.Credentials = new NetworkCredential(o.SmtpUser, o.SmtpPassword);
        }

        await smtp.SendMailAsync(mail, ct).ConfigureAwait(false);
    }

    private static MailAddress ParseAddress(string from) => new(from);

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail (SMTP não configurado) para {To}: {Subject}\n{Text}")]
    private static partial void LogNotSent(ILogger logger, string to, string subject, string text);
}

/// <summary>Para testes: guarda os e-mails em memória.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Último link enviado para o endereço (o primeiro http... do texto).</summary>
    public string? LastLinkTo(string email) => Sent.Reverse().Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase))
        .Select(m => m.Text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.StartsWith("http", StringComparison.Ordinal)))
        .FirstOrDefault(l => l is not null);
}

/// <summary>Textos dos e-mails (pt-BR). HTML simples e sem recursos externos.</summary>
public static class EmailTemplates
{
    private static string Html(string title, string body, string? link, string? linkLabel, string? footer = null) =>
        $"""
        <!doctype html><html lang="pt-BR"><body style="font-family:Segoe UI,Arial,sans-serif;background:#f6f7f9;padding:24px;color:#0b1220">
        <div style="max-width:520px;margin:auto;background:#fff;border:1px solid #e3e7ed;border-radius:12px;padding:28px">
        <div style="font-weight:700;color:#0e7a5f;font-size:18px">TaxGateway</div>
        <h1 style="font-size:20px;margin:16px 0 8px">{WebUtility.HtmlEncode(title)}</h1>
        <p style="line-height:1.6;color:#3b4556">{body}</p>
        {(link is null ? "" : $"<p><a href=\"{WebUtility.HtmlEncode(link)}\" style=\"display:inline-block;background:#0e7a5f;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none\">{WebUtility.HtmlEncode(linkLabel)}</a></p><p style=\"font-size:12px;color:#667085\">Se o botão não funcionar, copie: {WebUtility.HtmlEncode(link)}</p>")}
        <p style="font-size:12px;color:#667085;margin-top:24px">{WebUtility.HtmlEncode(footer ?? "Você recebeu este e-mail porque alguém usou este endereço no TaxGateway. Se não foi você, ignore — nada será feito sem o link.")}</p>
        </div></body></html>
        """;

    public static EmailMessage VerifyEmail(string to, string name, string link) => new(to, "Confirme seu e-mail no TaxGateway",
        $"Olá, {name}.\n\nConfirme seu e-mail para ativar a conta:\n{link}\n\nO link vale por 24 horas.",
        Html("Confirme seu e-mail", $"Olá, {WebUtility.HtmlEncode(name)}. Confirme seu e-mail para ativar a conta. O link vale por 24 horas.", link, "Confirmar e-mail"));

    public static EmailMessage AlreadyRegistered(string to, string link) => new(to, "Você já tem uma conta no TaxGateway",
        $"Alguém tentou criar uma conta com este e-mail, mas ela já existe.\nSe foi você e esqueceu a senha, redefina aqui:\n{link}",
        Html("Você já tem uma conta", "Alguém tentou criar uma conta com este e-mail, mas ela já existe. Se foi você e esqueceu a senha, redefina pelo botão abaixo.", link, "Redefinir senha"));

    public static EmailMessage ResetPassword(string to, string name, string link) => new(to, "Redefinição de senha do TaxGateway",
        $"Olá, {name}.\n\nPara criar uma nova senha, use o link:\n{link}\n\nO link vale por 1 hora. Depois da troca, todas as sessões abertas são encerradas.",
        Html("Redefinir sua senha", $"Olá, {WebUtility.HtmlEncode(name)}. O link vale por 1 hora. Depois da troca, todas as sessões abertas são encerradas e o MFA continua exigido.", link, "Criar nova senha"));

    public static EmailMessage Invite(string to, string tenant, string inviter, string role, string link) => new(to, $"Convite para {tenant} no TaxGateway",
        $"{inviter} convidou você para acessar {tenant} no TaxGateway com o papel {role}.\n\nAceite aqui:\n{link}\n\nO convite vale por 7 dias.",
        Html($"Convite para {tenant}", $"{WebUtility.HtmlEncode(inviter)} convidou você para acessar <b>{WebUtility.HtmlEncode(tenant)}</b> com o papel <b>{WebUtility.HtmlEncode(role)}</b>. O convite vale por 7 dias.", link, "Aceitar convite"));

    public static EmailMessage OperatorSetup(string to, string name, string link) => new(to, "Acesso ao backoffice do TaxGateway",
        $"Olá, {name}.\n\nDefina sua senha e configure o MFA para acessar o backoffice:\n{link}\n\nO link vale por 24 horas.",
        Html("Acesso ao backoffice", $"Olá, {WebUtility.HtmlEncode(name)}. Defina sua senha e configure o MFA para acessar o backoffice. O link vale por 24 horas.", link, "Definir senha"));

    /// <summary>Aviso operacional ao cliente (D-J03): franquia, webhook pausado, certificado, mudança fiscal.</summary>
    public static EmailMessage Notice(string to, string tenant, string title, string body, string? link) => new(to, $"[{tenant}] {title}",
        $"{title}\n\n{body}\n" + (link is null ? "" : $"\nVer no portal: {link}\n") + "\nVocê recebe este aviso por ter acesso a esta empresa no TaxGateway.",
        Html(title, $"<b>{WebUtility.HtmlEncode(tenant)}</b><br>{WebUtility.HtmlEncode(body)}", link, "Ver no portal",
            "Você recebe este aviso por ter acesso a esta empresa no TaxGateway. Os avisos também ficam no sino do portal."));

    /// <summary>Cadastro com CNPJ que já tem conta (D-R10): sem revelar quem é cliente para quem não confirmou o e-mail.</summary>
    public static EmailMessage CompanyAlreadyRegistered(string to, string name, string company, string link) => new(to, "Confirme seu e-mail no TaxGateway",
        $"Olá, {name}.\n\nConfirme seu e-mail para ativar a conta:\n{link}\n\nA empresa {company} já tem cadastro no TaxGateway. Depois de entrar, "
        + "peça a quem administra a conta da empresa um convite para o seu e-mail. Se você é o responsável legal e não reconhece esse cadastro, "
        + "use o certificado digital e-CNPJ da empresa em Minha conta → Contestar titularidade.",
        Html("Confirme seu e-mail", $"Olá, {WebUtility.HtmlEncode(name)}. Confirme seu e-mail para ativar a conta.<br><br>A empresa <b>{WebUtility.HtmlEncode(company)}</b> já tem cadastro no TaxGateway: depois de entrar, peça um convite a quem administra a conta. Se você é o responsável legal e não reconhece esse cadastro, conteste a titularidade com o certificado e-CNPJ da empresa.", link, "Confirmar e-mail"));

    /// <summary>Código de verificação da titularidade enviado ao e-mail do cadastro da Receita (D-R10).</summary>
    public static EmailMessage CnpjVerificationCode(string to, string company, string code) => new(to, $"Código de verificação: {code}",
        $"Alguém pediu, no TaxGateway, a verificação de que a empresa {company} é cliente titular deste CNPJ. Código: {code}\n\n"
        + "O código vale por 30 minutos. Se você não reconhece o pedido, ignore este e-mail.",
        Html($"Código {code}", $"Alguém pediu, no TaxGateway, a confirmação de titularidade do CNPJ da empresa <b>{WebUtility.HtmlEncode(company)}</b>. Informe o código <b style=\"font-size:20px;letter-spacing:4px\">{code}</b> na tela de verificação. Vale por 30 minutos.", null, null,
            "Este e-mail foi enviado para o endereço do cadastro público do CNPJ na Receita Federal. Se você não reconhece o pedido, ignore."));

    /// <summary>D-R32: boas-vindas depois da confirmação do e-mail, com os primeiros passos.</summary>
    public static EmailMessage Welcome(string to, string name, string portal, string help) => new(to, "Boas-vindas ao TaxGateway",
        $"Olá, {name}.\n\nSua conta está ativa. Primeiros passos:\n"
        + "1. Ative a verificação em duas etapas (pedida no primeiro acesso).\n"
        + "2. Em Empresa, confirme a titularidade do CNPJ (exigida para plano pago e DF-e).\n"
        + "3. Faça uma simulação ou envie um XML para auditar — o Sandbox já permite testar.\n"
        + "4. Para integrar, crie uma chave de API em Credenciais.\n\n"
        + $"Portal: {portal}\nCentral de ajuda: {help}\n\nVocê vai receber aqui os avisos importantes: cobrança, uso da franquia, "
        + "mudanças de alíquota que afetam a sua empresa e alertas de segurança. Os informativos podem ser desligados em Minha conta.",
        Html("Boas-vindas ao TaxGateway", $"Olá, {WebUtility.HtmlEncode(name)}. Sua conta está ativa.<br><br>"
            + "<b>Primeiros passos</b><br>1. Ative a verificação em duas etapas.<br>2. Em <b>Empresa</b>, confirme a titularidade do CNPJ.<br>"
            + "3. Faça uma simulação ou envie um XML para auditar.<br>4. Para integrar, crie uma chave de API em <b>Credenciais</b>.<br><br>"
            + $"Dúvidas? Veja a <a href=\"{WebUtility.HtmlEncode(help)}\">central de ajuda</a>.", portal, "Abrir o portal",
            "Você vai receber aqui os avisos importantes (cobrança, franquia, mudanças de alíquota e segurança). Os informativos podem ser desligados em Minha conta."));

    public static EmailMessage SecurityNotice(string to, string name, string what) => new(to, "Aviso de segurança do TaxGateway",
        $"Olá, {name}.\n\n{what}\n\nSe não foi você, redefina sua senha imediatamente e fale com o administrador da sua conta.",
        Html("Aviso de segurança", $"Olá, {WebUtility.HtmlEncode(name)}. {WebUtility.HtmlEncode(what)} Se não foi você, redefina sua senha imediatamente.", null, null));
}
