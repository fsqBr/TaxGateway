namespace TaxGateway.Api.Http;

/// <summary>
/// Marca na página de documentação (D-W09). Mesmos tokens do app web (web/src/styles.css, docs/marca.md).
/// O favicon também é servido pela API, para quem acessa /docs direto (sem o nginx do web).
/// </summary>
public static class Brand
{
    public const string Mark = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><defs><linearGradient id="tg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#14A37A"/><stop offset="1" stop-color="#0A4F3E"/></linearGradient></defs><rect width="48" height="48" rx="12" fill="url(#tg)"/><path d="M14.5 36V21.5a9.5 9.5 0 0 1 19 0V36" fill="none" stroke="#fff" stroke-width="3.6" stroke-linecap="round"/><path d="M19 27.2l3.6 3.6 6.6-7.2" fill="none" stroke="#7CF0C1" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round"/></svg>""";

    public const string DocsHeader = $$"""
        <div class="tg-bar"><a class="tg-brand" href="/">{{Mark}}<span><b>Tax</b>Gateway</span><em>Referência da API</em></a><nav><a href="/portal">Portal</a><a href="/openapi/v1.json">OpenAPI JSON</a></nav></div>
        """;

    public const string DocsCss = """
        .light-mode {
          --scalar-color-accent: #0e7a5f;
          --scalar-background-1: #ffffff;
          --scalar-background-2: #f6f7f9;
          --scalar-background-3: #eef1f4;
          --scalar-background-accent: #e9f8f2;
          --scalar-color-1: #0b1220;
          --scalar-color-2: #3b4556;
          --scalar-color-3: #667085;
          --scalar-border-color: #e3e7ed;
        }
        .dark-mode {
          --scalar-color-accent: #2fc493;
          --scalar-background-1: #090e18;
          --scalar-background-2: #101826;
          --scalar-background-3: #162033;
          --scalar-background-accent: #0f2a22;
          --scalar-color-1: #e9edf3;
          --scalar-color-2: #c0c8d4;
          --scalar-color-3: #8d98aa;
          --scalar-border-color: #222d42;
        }
        :root { --scalar-font: 'Inter', 'Segoe UI', system-ui, -apple-system, Roboto, sans-serif; --scalar-radius: 8px; --scalar-radius-lg: 12px; --tg-bar: 52px; }
        .tg-bar { position: sticky; top: 0; z-index: 100; height: var(--tg-bar); display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 0 20px; background: #060b14; border-bottom: 1px solid rgba(255,255,255,.07); font-family: var(--scalar-font); }
        .tg-brand { display: flex; align-items: center; gap: 10px; color: #fff; text-decoration: none; font-size: 15px; letter-spacing: -.02em; }
        .tg-brand svg { width: 26px; height: 26px; }
        .tg-brand b { font-weight: 500; }
        .tg-brand span { font-weight: 750; }
        .tg-brand em { font-style: normal; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .06em; color: #8d9bb0; border-left: 1px solid #2e3b55; padding-left: 10px; }
        .tg-bar nav { display: flex; gap: 18px; }
        .tg-bar nav a { color: #c3cdd9; text-decoration: none; font-size: 13px; font-weight: 500; }
        .tg-bar nav a:hover { color: #fff; }
        """;
}
