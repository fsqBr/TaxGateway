using System.Text;

using TaxGateway.Infrastructure.Webhooks;

namespace TaxGateway.SecurityTests.Batches;

/// <summary>
/// Vetor de teste publicado na central de ajuda (/ajuda/webhooks, D-W12): quem integra valida a própria implementação
/// com ele. Este teste garante que o vetor continua batendo com a verificação real do servidor.
/// </summary>
public sealed class WebhookSignatureVectorTests
{
    public const string Secret = "whsec_vetor_de_teste_nao_use";
    public const string Timestamp = "1795651200";
    public const string Body = """{"id":"9f1c2e4a-0000-4000-8000-000000000001","event":"document.audited","created_at":"2026-11-25T12:00:00Z","data":{"chave_acesso":"35270611222333000181550010000000011000000010","status":"CONFORME"}}""";
    public const string ExpectedV1 = "20c4d2aac7cefd4a7418877853c004d8c11fafafd0588e9f836b3fb5929fc069";

    [Fact]
    public void Vetor_publicado_na_ajuda_confere_com_o_servidor()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Timestamp, System.Globalization.CultureInfo.InvariantCulture));
        var body = Encoding.UTF8.GetBytes(Body);
        Assert.True(WebhookSignature.Verify($"t={Timestamp},v1={ExpectedV1}", body, Secret, TimeSpan.FromMinutes(5), now));
        // Durante a rotação chegam duas assinaturas; basta uma valer.
        Assert.True(WebhookSignature.Verify($"t={Timestamp},v1={new string('0', 64)},v1={ExpectedV1}", body, Secret, TimeSpan.FromMinutes(5), now));
        // Corpo alterado, segredo errado ou horário fora da tolerância: recusa.
        Assert.False(WebhookSignature.Verify($"t={Timestamp},v1={ExpectedV1}", Encoding.UTF8.GetBytes(Body + " "), Secret, TimeSpan.FromMinutes(5), now));
        Assert.False(WebhookSignature.Verify($"t={Timestamp},v1={ExpectedV1}", body, Secret + "x", TimeSpan.FromMinutes(5), now));
        Assert.False(WebhookSignature.Verify($"t={Timestamp},v1={ExpectedV1}", body, Secret, TimeSpan.FromMinutes(5), now.AddMinutes(10)));
    }
}
