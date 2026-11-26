# Marca TaxGateway (guia rápido)

Identidade criada em 25/11/2026 (D-W09). Arquivos em `web/public/brand/`.

## Conceito
**Portal com check — "passagem verificada".** O arco é o *gateway* por onde o documento fiscal passa; o check é a
conferência contra o motor oficial. Forma geométrica simples, legível a 16 px (favicon) e em uma cor só.

| Arquivo | Uso |
|:--|:--|
| `logo-mark.svg` / `favicon.svg` | Ícone (app, favicon, avatar) |
| `logo.svg` | Marca + nome em fundo claro |
| `logo-on-dark.svg` | Marca + nome em fundo escuro |
| `logo-mono.svg` | Uma cor (`currentColor`): carimbos, documentos impressos |
| `og.svg` | Imagem de compartilhamento (1200×630) |

No app, use o componente `Logo` / `LogoMark` (`web/src/components/Logo.tsx`), que desenha o mesmo SVG inline.
Nome: "Tax" em peso 500 + "Gateway" em peso 750, sem espaço. Área de respiro mínima: metade da altura do ícone.

## Cores
| Token | Hex | Papel |
|:--|:--|:--|
| `--brand-500` | `#14A37A` | Início do degradê do ícone; destaques |
| `--brand-600` | `#0E7A5F` | **Cor primária** (botões, links, foco) |
| `--brand-700` | `#0A5F4A` | Hover; fim do degradê do ícone (`#0A4F3E`) |
| `--brand-300` | `#7CF0C1` | Check do ícone; destaques em fundo escuro |
| `--ink-950` | `#060B14` | Fundos escuros (hero, barra da documentação) |
| `--ink-900` | `#0B1220` | Texto principal |

Estados: ok `#0E7A5F`, atenção `#9A6200`, erro `#C0352B`, informação `#2251CC` — sempre com fundo suave correspondente.
Tema escuro automático (`prefers-color-scheme`), com os mesmos papéis.

**Por que verde-esmeralda:** o mercado fiscal/fintech brasileiro é dominado por azul; o verde escuro comunica
conformidade e "aprovado" sem cair no verde-dinheiro, e diferencia o produto.

## Tipografia
* **Inter** (variável), auto-hospedada via `@fontsource-variable/inter` — compatível com a CSP `font-src 'self'`.
* **JetBrains Mono** para código, chaves e IDs.
* Títulos com tracking negativo (−0,015 a −0,035em); números tabulares em valores e tabelas.

## Ícones
`lucide-react` (traço 2 px, cantos arredondados), sempre com `aria-hidden` quando acompanham texto.

## Voz
Direta, técnica e verificável. Só fatos do produto: **nunca** inventar clientes, números de mercado, certificações
ou preços. Valores de exemplo reais quando possível (o hero usa a resposta real do motor com o catálogo de 2027).

## Documentação da API
A página `/docs` (Scalar) usa os mesmos tokens (`src/TaxGateway.Api/Http/Brand.cs`), com faixa da marca no topo,
sem telemetria, sem o agente de IA e sem a barra promocional do Scalar.
