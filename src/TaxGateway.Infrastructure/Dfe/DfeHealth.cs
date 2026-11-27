using System.Globalization;

namespace TaxGateway.Infrastructure.Dfe;

/// <summary>Situação da captura em linguagem de gente (D-R37). Ok = está funcionando, sem ação do cliente.</summary>
public sealed record DfeHealthStatus(string Situacao, bool Ok, string Mensagem, int? CertificadoVenceEmDias);

/// <summary>
/// D-R37: resume o estado da captura de DF-e de um CNPJ numa situação só, para o portal e para a API. Regras: sem consulta
/// há mais de 6 h com a captura ligada = PARADO (o normal é no máximo 1 h entre consultas); erro de rede ou bloqueio 656
/// na última consulta = COM_PROBLEMA (tenta de novo sozinho); certificado ausente ou vencido para tudo.
/// </summary>
public static class DfeHealth
{
    public static readonly TimeSpan StalledAfter = TimeSpan.FromHours(6);

    private static string Br(DateTimeOffset d) => d.ToOffset(TimeSpan.FromHours(-3)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static DfeHealthStatus Evaluate(bool configured, bool enabled, DateTimeOffset? certNotAfter, DateTimeOffset? lastQuery, string? lastCStat,
        DateTimeOffset? last656, DateTimeOffset nextAllowed, DateTimeOffset now)
    {
        int? days = certNotAfter is { } na ? (int)Math.Floor((na - now).TotalDays) : null;
        if (certNotAfter is null)
        {
            return new("SEM_CERTIFICADO", false, "Envie o certificado A1 do CNPJ para a captura funcionar.", null);
        }

        if (certNotAfter <= now)
        {
            return new("CERTIFICADO_VENCIDO", false, $"O certificado venceu em {Br(certNotAfter.Value)}: a captura parou. Envie o certificado novo.", days);
        }

        if (!configured || !enabled)
        {
            return new("DESLIGADO", false, "A captura está desligada. Ligue para buscar as notas dos fornecedores.", days);
        }

        if (lastQuery is null)
        {
            return new("AGUARDANDO", true, "Captura ligada: a primeira consulta à SEFAZ acontece nos próximos minutos.", days);
        }

        if (now - lastQuery > StalledAfter && now - nextAllowed > TimeSpan.FromHours(1))
        {
            return new("PARADO", false, $"Nenhuma consulta à SEFAZ desde {Br(lastQuery.Value)}. Use \"Testar conexão\"; se continuar, fale com o suporte.", days);
        }

        if (lastCStat == "656" || (last656 is { } b && now - b < TimeSpan.FromHours(1)))
        {
            return new("COM_PROBLEMA", false, $"A SEFAZ pediu uma pausa (consumo indevido). Voltamos a consultar sozinhos às {Br(nextAllowed)}.", days);
        }

        if (lastCStat is not null and not ("137" or "138"))
        {
            var what = lastCStat == "ERRO" ? "falhou (SEFAZ fora do ar ou sem rede)" : $"teve o retorno {lastCStat} da SEFAZ";
            return new("COM_PROBLEMA", false, $"A última consulta, às {Br(lastQuery.Value)}, {what}. Nova tentativa automática às {Br(nextAllowed)}.", days);
        }

        var vence = days is < 30 ? $" Atenção: o certificado vence em {days} dia(s)." : "";
        return new("FUNCIONANDO", true, $"Funcionando: última consulta à SEFAZ em {Br(lastQuery.Value)}.{vence}", days);
    }
}
