using TaxGateway.Infrastructure.Xml;
using TaxGateway.TestKit;

namespace TaxGateway.SecurityTests.Xml;

/// <summary>Leiaute do optante do Simples (gTribSN) e do MEI no XSD PL_010h (D-503).</summary>
public sealed class SimplesLayoutTests
{
    private static readonly TestPki Pki = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), "taxgateway-test-pki"));

    private static byte[] Signed(NfeSpec spec)
    {
        var doc = NfeBuilder.Build(spec);
        NfeBuilder.Sign(doc, Pki.Leaf);
        return NfeBuilder.Bytes(doc);
    }

    public const string SnItem = "<IBSCBS><CST>000</CST><cClassTrib>000001</cClassTrib><gTribSN><gIBSSN><pIBSSN>0.50</pIBSSN><vIBSSN>5.00</vIBSSN></gIBSSN><gCBSSN><pCBSSN>1.20</pCBSSN><vCBSSN>12.00</vCBSSN></gCBSSN></gTribSN></IBSCBS>";
    public const string SnTotal = "<IBSCBSTot><vBCIBSCBS>0.00</vBCIBSCBS><gTribSN><vIBSSN>5.00</vIBSSN><vCBSSN>12.00</vCBSSN></gTribSN></IBSCBSTot>";

    public static NfeSpec Spec(string crt = "1", string rtc = SnItem, string total = SnTotal) =>
        XmlSecurityTests.Spec() with { Crt = crt, Itens = [new NfeItem(1, "84713012", 1000m, 0m, rtc, CAtivSn: crt is "1" or "2" ? "01" : null)], TotalRtcXml = total };

    [Fact]
    public void Simples_document_with_gTribSN_is_valid_and_mapped()
    {
        var p = NfeMapper.Map(SecureXmlLoader.Load(Signed(Spec())));
        Assert.Empty(XmlSecurityCache.Nfe.Validate(p.Nfe));
        var it = Assert.Single(p.Document.Itens);
        Assert.Equal("01", it.CodigoAtividadeSn);
        Assert.Equal(12.00m, it.Simples!.ValorCbs);
        Assert.Equal(0.50m, it.Simples.AliquotaIbs);
        Assert.Equal(1000m, it.BaseSimples);
        Assert.Equal(12.00m, p.Document.Totais!.VCbsSn);
        Assert.Empty(it.GruposNaoSuportados);
    }

    [Fact]
    public void Mei_document_without_ibscbs_is_valid()
    {
        var p = NfeMapper.Map(SecureXmlLoader.Load(Signed(Spec("4", "", ""))));
        Assert.Empty(XmlSecurityCache.Nfe.Validate(p.Nfe));
        Assert.Equal("4", p.Document.Crt);
        Assert.Null(Assert.Single(p.Document.Itens).Cst);
    }
}
