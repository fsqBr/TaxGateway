using System.Text.RegularExpressions;

namespace TaxGateway.Core.Fiscal;

/// <summary>
/// Chave de acesso de 44 posições (SDD §3.3; NT 2026.004): posições 7–18 alfanuméricas (CNPJ do emitente),
/// demais numéricas. DV em módulo 11, pesos 2..9 da direita para a esquerda, valor do caractere = ASCII − 48.
/// </summary>
public static partial class AccessKey
{
    public static bool IsValid(string? key)
    {
        if (key is null || !Format().IsMatch(key))
        {
            return false;
        }

        var sum = 0;
        var weight = 2;
        for (var i = 42; i >= 0; i--)
        {
            sum += (key[i] - 48) * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        var rest = sum % 11;
        var dv = rest < 2 ? 0 : 11 - rest;
        return key[43] - '0' == dv;
    }

    /// <summary>Modelo (posições 21–22): "55" NF-e, "65" NFC-e.</summary>
    public static string Model(string key) => key.Substring(20, 2);

    /// <summary>CNPJ do emitente embutido na chave (posições 7–20).</summary>
    public static string EmitterCnpj(string key) => key.Substring(6, 14);

    [GeneratedRegex("^[0-9]{6}[A-Z0-9]{12}[0-9]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
