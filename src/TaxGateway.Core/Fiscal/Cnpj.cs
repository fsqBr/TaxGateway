using System.Diagnostics.CodeAnalysis;

namespace TaxGateway.Core.Fiscal;

/// <summary>
/// CNPJ numérico ou alfanumérico (IN RFB 2.229/2024; NT Conjunta DFe 2025.001; SDD §3.3).
/// Formato <c>^[A-Z0-9]{12}[0-9]{2}$</c>; DV em módulo 11 com valor do caractere = ASCII − 48.
/// Nenhuma letra é vedada até a RFB confirmar a vedação de I, O, U, Q e F (§14.2, D-207).
/// </summary>
public static class Cnpj
{
    private static readonly int[] Weights1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] Weights2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    /// <summary>Normaliza (remove máscara, maiúsculas) e valida. Devolve o CNPJ com 14 caracteres.</summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? cnpj)
    {
        cnpj = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 18)
        {
            return false;
        }

        Span<char> buf = stackalloc char[14];
        var n = 0;
        foreach (var raw in input)
        {
            if (raw is '.' or '/' or '-' or ' ')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw);
            if (n == 14 || !(c is >= '0' and <= '9' || c is >= 'A' and <= 'Z'))
            {
                return false;
            }

            buf[n++] = c;
        }

        if (n != 14 || !IsValidNormalized(buf))
        {
            return false;
        }

        cnpj = new string(buf);
        return true;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _);

    private static bool IsValidNormalized(ReadOnlySpan<char> c)
    {
        // DVs sempre numéricos; base toda zero é inválida.
        if (!char.IsAsciiDigit(c[12]) || !char.IsAsciiDigit(c[13]) || c[..12].IndexOfAnyExcept('0') < 0)
        {
            return false;
        }

        return CheckDigit(c[..12], Weights1) == c[12] - '0'
            && CheckDigit(c[..13], Weights2) == c[13] - '0';
    }

    private static int CheckDigit(ReadOnlySpan<char> chars, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            sum += (chars[i] - 48) * weights[i];
        }

        var rest = sum % 11;
        return rest < 2 ? 0 : 11 - rest;
    }
}
