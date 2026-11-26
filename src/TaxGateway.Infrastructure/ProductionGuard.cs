using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TaxGateway.Infrastructure;

/// <summary>
/// Travas de produção (D-P13; revisão de segurança B7 e arquitetura 17): em <c>Production</c> a aplicação NÃO sobe com
/// configuração de desenvolvimento — cofre de chaves local, pepper/chave mestra públicos do repositório, CA de teste
/// como âncora, atalhos de teste de webhook/OAuth, cookie sem Secure ou chave estática de operador ligada. Um erro de
/// configuração vira falha de inicialização visível, nunca um sistema rodando inseguro em silêncio.
/// </summary>
public static class ProductionGuard
{
    /// <summary>Valores de dev publicados no repositório (appsettings.Development.json, compose). Nunca em produção.</summary>
    private static readonly HashSet<string> PublicDevSecrets = new(StringComparer.Ordinal)
    {
        "0k2C9K9HFb5Vq32thS/EFrvm6BXIlmD7rMLfWfpVzFI=",
        "T2rB45BDwYQ1YJuAMnRR4s1hTIh5NdC6YyZdpERfsf0=",
    };

    public static IReadOnlyList<string> Problems(IConfiguration config, bool isApi = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        var problems = new List<string>();
        void Check(bool bad, string message)
        {
            if (bad)
            {
                problems.Add(message);
            }
        }

        bool Flag(string key) => config.GetValue(key, false);

        Check(string.IsNullOrEmpty(config["OpenBao:Address"]), "OpenBao:Address ausente: o cofre local de dev cifraria os dados sem KMS (sem crypto-shredding).");
        Check(!string.IsNullOrEmpty(config["Kms:LocalMasterKey"]), "Kms:LocalMasterKey definida: chave mestra local é só de dev.");
        Check(PublicDevSecrets.Contains(config["Auth:ApiKeyPepper"] ?? ""), "Auth:ApiKeyPepper é o valor público de desenvolvimento.");
        Check(config.GetSection("Xml:ExtraTrustAnchorPemFiles").GetChildren().Any(), "Xml:ExtraTrustAnchorPemFiles definida: âncora extra (CA de teste) não pode valer em produção.");
        Check(Flag("Webhooks:AllowInsecureForTests"), "Webhooks:AllowInsecureForTests=true desliga a proteção SSRF dos webhooks.");
        Check(Flag("Identity:AllowEphemeralKeys"), "Identity:AllowEphemeralKeys=true: cada pod assinaria tokens com uma chave diferente.");
        Check(Flag("Identity:AllowHttp"), "Identity:AllowHttp=true aceita /oauth/token sem TLS.");
        Check(isApi && (!Uri.TryCreate(config["Identity:Issuer"], UriKind.Absolute, out var iss) || iss.Scheme != Uri.UriSchemeHttps),
            "Identity:Issuer ausente ou sem https: o issuer dos tokens não pode vir do header Host.");
        Check(!config.GetValue("Accounts:CookieSecure", true), "Accounts:CookieSecure=false: cookie de sessão sem Secure/__Host-.");
        Check(config.GetValue("Admin:AllowStaticKeys", true) && config.GetSection("Admin:Operators").GetChildren().Any(),
            "Admin:AllowStaticKeys=true com chaves de operador configuradas: em produção, operadores entram com conta e MFA (D-U08).");
        return problems;
    }

    /// <summary>Em Production, lança com a lista completa de problemas; nos demais ambientes não faz nada.</summary>
    public static void EnsureSafe(IConfiguration config, IHostEnvironment env, bool isApi = true)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (!env.IsProduction())
        {
            return;
        }

        var problems = Problems(config, isApi);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Configuração insegura para produção (D-P13):\n - " + string.Join("\n - ", problems));
        }
    }
}
