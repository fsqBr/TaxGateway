using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

namespace TaxGateway.Identity.Accounts;

/// <summary>TOTP (RFC 6238): HMAC-SHA1, passo de 30 s, 6 dígitos — o que todo app autenticador aceita (D-U03).</summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // HMAC-SHA1 é o algoritmo do TOTP (RFC 6238/4226) e o padrão dos apps autenticadores.
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Confere o código na janela de ±1 passo (relógio do celular adiantado/atrasado) e devolve o passo aceito, que deve
    /// ser maior que o último usado (anti-replay: o mesmo código não vale duas vezes).
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        for (var delta = -1; delta <= 1; delta++)
        {
            var step = current + delta;
            if (step > lastUsedStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    public static string Base32(byte[] data)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
}

/// <summary>
/// Cifra de segredos de conta (TOTP) com AES-256-GCM e chave derivada do pepper por HKDF (D-U03): o banco só guarda
/// texto cifrado; o id do usuário é o dado associado (um segredo copiado para outra conta não decifra).
/// </summary>
public sealed class SecretBox
{
    private readonly byte[] _key;

    public SecretBox(byte[] pepper) =>
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, pepper, 32, info: "taxgateway/account-secrets/v1"u8.ToArray());

    public byte[] Seal(byte[] plaintext, Guid owner)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[12 + 16 + plaintext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(28), output.AsSpan(12, 16), owner.ToByteArray());
        nonce.CopyTo(output, 0);
        return output;
    }

    public byte[] Open(byte[] sealedData, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(sealedData);
        var plaintext = new byte[sealedData.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plaintext, owner.ToByteArray());
        return plaintext;
    }
}

/// <summary>Senhas: Argon2id (mesmos parâmetros dos segredos OAuth) e política mínima de qualidade (D-U04).</summary>
public static class Passwords
{
    // Lista curta das senhas mais vazadas (amostra das listas públicas); o principal é o tamanho mínimo.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456789012", "senha1234567", "password1234", "qwertyuiop12", "admin1234567", "taxgateway123", "123456123456",
        "abcdefghijkl", "aaaaaaaaaaaa", "iloveyou1234", "brasil123456", "mudar123456", "mudar1234567", "senhasegura1",
    };

    public static (byte[] Hash, byte[] Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Compute(password, salt), salt);
    }

    public static bool Verify(string password, byte[] hash, byte[] salt) =>
        CryptographicOperations.FixedTimeEquals(Compute(password, salt), hash);

    /// <summary>null = aceita; senão, o motivo em português.</summary>
    public static string? Problem(string? password, string email, string name)
    {
        if (password is null || password.Length < 12)
        {
            return "A senha precisa ter pelo menos 12 caracteres.";
        }

        if (password.Length > 128)
        {
            return "A senha pode ter no máximo 128 caracteres.";
        }

        if (Common.Contains(password) || password.Distinct().Count() < 5)
        {
            return "Essa senha é fácil de adivinhar. Use uma frase ou combinação menos comum.";
        }

        var local = email.Split('@')[0];
        if ((local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            || (name.Length >= 4 && password.Contains(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
        {
            return "A senha não pode conter seu nome ou seu e-mail.";
        }

        return null;
    }

    private static byte[] Compute(string password, byte[] salt)
    {
        using var a = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, MemorySize = 19456, Iterations = 2, DegreeOfParallelism = 1 };
        return a.GetBytes(32);
    }
}

/// <summary>Tokens opacos (sessão, links de e-mail, códigos de recuperação): só o SHA-256 vai para o banco.</summary>
public static class OpaqueToken
{
    public static string New(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Código de recuperação legível: XXXX-XXXX (sem caracteres ambíguos).</summary>
    public static string RecoveryCode()
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var raw = RandomNumberGenerator.GetString(Alphabet, 8);
        return $"{raw[..4]}-{raw[4..]}";
    }

    public static byte[] HashRecovery(string code) => Hash(code.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal));
}
