using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TaxGateway.Api.Http;

/// <summary>
/// Documentação da API (D-W01, D-W02, D-W08). Dois documentos: <c>v1</c> (público: <c>/v1</c> e <c>/oauth</c>) e
/// <c>admin</c> (backoffice, só com <c>Docs:IncludeAdmin</c>). Tudo o que o gerador não deduz sozinho fica aqui:
/// grupos (com descrição e ordem), resumos, escopo exigido, corpo de exemplo, código de sucesso correto com resposta de
/// exemplo (copiada de respostas reais, com IDs e segredos fictícios) e os erros padrão em <c>application/problem+json</c>.
/// O teste <c>UiSupportTests</c> confere que os exemplos têm os mesmos campos das respostas reais.
/// </summary>
public static partial class OpenApiDocs
{
    public const string ApiKey = "ApiKey";
    public const string OAuth = "OAuth2";
    public const string AdminKey = "AdminKey";
    private const string Problem = "Problem";

    /// <param name="Errors">Erros específicos além dos padrão (401, 403 com escopo, 429, 400 com corpo, 404 com id na rota).</param>
    public sealed record Doc(string Tag, string Summary, string? Body = null, string? Description = null, string? RawContentType = null,
        int Status = 200, string? Response = null, string? ResponseDescription = null, int[]? Errors = null);

    /// <summary>Grupos na ordem em que aparecem na referência.</summary>
    private static readonly (string Name, string Description)[] Tags =
    [
        ("Plataforma", "Versão da API e do bundle fiscal. Toda resposta traz o header `X-TaxGateway-Version`; as mudanças de cada versão ficam em /ajuda/novidades."),
        ("Simulação", "Cálculo de CBS, IBS e Imposto Seletivo de uma operação, antes da emissão, pelos motores oficiais da Receita Federal."),
        ("Auditoria", "Recalcula um XML de NF-e/NFC-e autorizado e aponta divergências com fundamentação legal, na saída ou na entrada."),
        ("Documentos", "Histórico do que foi auditado (portal, API, lotes e DF-e): filtros, detalhe por item e exportação CSV."),
        ("Lotes", "Auditoria assíncrona de muitos documentos por arquivo ZIP, com relatório CSV e aviso por webhook."),
        ("Classificação", "Consulta de CST/cClassTrib candidatos por NCM ou NBS nos dados abertos do motor oficial."),
        ("Avisos", "Avisos da conta (franquia, webhook pausado, certificado, mudança fiscal) e primeiros passos."),
        ("Webhooks", "Notificações assinadas (HMAC-SHA256) sobre auditorias, lotes, DF-e e vencimento de certificado."),
        ("Credenciais", "API keys, token OAuth 2.0 (client credentials) e dados da credencial em uso."),
        ("Consumo", "Uso da competência e prévia da fatura."),
        ("OEM", "Para software houses: CNPJs atendidos e uso separado por CNPJ."),
        ("DF-e", "Certificado A1 e captura automática das notas em que o CNPJ é destinatário."),
        ("LGPD", "Atendimento a pedidos de titulares (o cliente é o controlador)."),
        ("Admin: tenants", "Backoffice: clientes, planos e situação."),
        ("Admin: OAuth", "Backoffice: clientes OAuth e revogação de tokens."),
        ("Admin: parâmetros", "Backoffice: catálogo de alíquotas com aprovação por duas pessoas."),
        ("Admin: faturamento", "Backoffice: fechamento, reconciliação e notas de ajuste."),
    ];

    private const string Id = "01938a6e-5b1c-7d2a-9e4f-0a1b2c3d4e5f";
    private const string Ts = "2026-11-25T14:30:00+00:00";

    private static readonly Dictionary<string, Doc> Docs = new(StringComparer.Ordinal)
    {
        ["GET /version"] = new("Plataforma", "Versão da API, commit, bundle fiscal e snapshot de parâmetros",
            Description: "Sem autenticação. Use para detectar mudança de versão (compare com o header `X-TaxGateway-Version`).",
            Response: """{"versao":"0.11.0","commit":"66eb2cf5aed5","api":"v1","bundle_fiscal":"2026.11.2","parametros_snapshot":11,"novidades":"/ajuda/novidades"}"""),
        ["POST /v1/calculo/simular"] = new("Simulação", "Simula IBS/CBS/IS de uma operação",
            """{"cnpj_emitente":"11222333000181","regime_tributario":"REGIME_REGULAR","data_fato_gerador":"2027-06-15T14:00:00-03:00","municipio_ibge":3550308,"uf":"SP","itens":[{"numero":1,"cst_ibs_cbs":"000","c_class_trib":"000001","valor_base_calculo":1000,"ncm":"84713012","quantidade":1,"unidade":"UN"}]}""",
            "Aceita `Idempotency-Key`. A resposta é determinística para a mesma entrada e o mesmo snapshot de parâmetros (`param_snapshot_id`). O exemplo de resposta é real, com o catálogo de 2027 (CBS provisória de 11,2%).",
            Response: """{"simulation_id":"sim_01938a6e5b1c7d2a9e4f0a1b2c3d4e5f","regime_calculo":"REGULAR","rfb_engine_version":{"bundle":"2026.11.2","engine":"REGULAR","tag":"1.7.2","db_version":"V0073"},"param_snapshot_id":11,"totais":{"valor_base_total":1000.0,"cbs_valor":112.0,"ibs_uf_valor":0.5,"ibs_mun_valor":0.5,"ibs_total_valor":1.0,"imposto_seletivo_valor":0,"carga_tributaria_efetiva_percent":11.3},"itens":[{"numero_item":1,"estado":"CALCULO_SIMULADO","cst_ibs_cbs":"000","c_class_trib":"000001","base_calculo":1000.0,"cbs":{"aliquota":11.2,"valor":112.0,"fonte_aliquota":"CATALOGO"},"ibs_uf":{"aliquota":0.05,"valor":0.5,"fonte_aliquota":"CATALOGO"},"ibs_mun":{"aliquota":0.05,"valor":0.5,"fonte_aliquota":"CATALOGO"},"ibs_total_valor":1.0,"erros":[]}],"avisos":[]}""",
            Errors: [403, 422, 503]),
        ["POST /v1/auditoria/xml"] = new("Auditoria", "Audita um XML de NF-e/NFC-e", null,
            "Corpo: o XML bruto (`NFe` ou `nfeProc`), assinado. Query: `direction=OUTBOUND` (você é o emitente) ou `INBOUND` (destinatário). Exige `Idempotency-Key`. A mesma chave de acesso auditada de novo devolve o resultado anterior (`replay: true`).",
            "application/xml", Errors: [403, 422, 503],
            ResponseDescription: "Resultado da auditoria. `status`: CONFORME, DIVERGENTE, INDICIO, SCHEMA_ERROR, ASSINATURA_INVALIDA ou INDETERMINADO. Exemplo ilustrativo (valores fictícios, estrutura real).",
            Response: """{"audit_id":"aud_01938a6e5b1c7d2a9e4f0a1b2c3d4e5f","chave_acesso":"35270611222333000181550010000000011000000010","direction":"OUTBOUND","status":"DIVERGENTE","rfb_engine_version":{"bundle":"2026.11.2","engine":"REGULAR","tag":"1.7.2","db_version":"V0073"},"param_snapshot_id":11,"regras_versao":"2026.11-fase5","is_billable":true,"replay":false,"reprocessamento":false,"xml_armazenado":false,"resumo_financeiro":{"tributo_declarado":{"cbs":94.08,"ibs":0.84,"is":0},"tributo_recalculado":{"cbs":112.0,"ibs":1.0,"is":0},"diferenca":18.08},"ocorrencias":[{"regra":"R012","versao":1,"natureza":"DETERMINISTICA","severidade":"ERROR","numero_item":1,"campo_xml":"det/imposto/IBSCBS/gIBSCBS/gCBS/pCBS","valor_declarado":"9.40","valor_referencia":"11.20","mensagem":"Alíquota declarada ≠ alíquota do Catálogo ajustada pela redução do cClassTrib","fundamentacao":"LC 214/2025 arts. 14 a 19 e 343 a 347"}],"avisos":[],"aviso_legal":"Indícios não constituem parecer tributário. Achados determinísticos refletem as regras oficiais vigentes na data de emissão."}"""),
        ["POST /v1/auditoria/lotes"] = new("Lotes", "Envia um lote para auditoria assíncrona", null,
            "Corpo `application/zip` (só `.xml`, sem ZIP aninhado, até 100 MB) ou JSON `{\"storage_object_key\"}` de um envio pela URL pré-assinada. Query `direction`. Exige `Idempotency-Key`. Conclusão por webhook `batch.audit.completed`.",
            "application/zip", Status: 202,
            Response: $$"""{"batch_id":"{{Id}}","status_url":"/v1/auditoria/lotes/{{Id}}","documentos":3000,"chunks":6,"estimated_completion_seconds":60}""",
            Errors: [403, 413, 422]),
        ["POST /v1/auditoria/lotes/upload-url"] = new("Lotes", "Gera URL pré-assinada para enviar um lote grande",
            Description: "Envie o ZIP com `PUT` na `upload_url` (válida por 15 min) e depois chame `POST /v1/auditoria/lotes` com `{\"storage_object_key\"}`.",
            Response: """{"upload_url":"https://storage.exemplo/taxgateway-xml/uploads/01938a6e-.../....zip?X-Amz-Signature=...","storage_object_key":"uploads/01938a6e-.../....zip","expira_em_segundos":900,"metodo":"PUT","content_type":"application/zip"}"""),
        ["GET /v1/auditoria/lotes"] = new("Lotes", "Lista os lotes (50 mais recentes)",
            Response: $$"""{"lotes":[{"batch_id":"{{Id}}","status":"COMPLETED","direction":"OUTBOUND","total":3000,"processados":3000,"falhas":2,"conformes":2950,"criado_em":"{{Ts}}","concluido_em":"2026-11-25T14:31:10+00:00"}]}"""),
        ["GET /v1/auditoria/lotes/{id}"] = new("Lotes", "Situação de um lote",
            Response: $$"""{"batch_id":"{{Id}}","status":"RUNNING","direction":"OUTBOUND","total_documentos":3000,"processados":1500,"falhas":0,"conformes":1490,"chunks":6,"chunks_concluidos":3,"relatorio_url":null,"criado_em":"{{Ts}}","concluido_em":null}"""),
        ["GET /v1/auditoria/lotes/{id}/relatorio"] = new("Lotes", "Baixa o relatório CSV do lote concluído",
            ResponseDescription: "Arquivo `text/csv` com uma linha por documento (chave, status, ocorrências, diferença, erro)."),

        ["GET /v1/auditoria/documentos"] = new("Documentos", "Lista os documentos auditados",
            Description: "Filtros: `de` e `ate` (AAAA-MM-DD, horário de Brasília; padrão: últimos 90 dias; até 400 dias), `status` (lista separada por vírgula), `direcao` (`saida` ou `entrada`), `cnpj` (emitente ou contraparte), `chave` (44 dígitos), `lote` (batch_id). Paginação por `cursor` (use `proximo_cursor`) e `limite` (1–200, padrão 50). Escopo `auditoria:read` ou `auditoria:write`.",
            Response: $$$"""{"documentos":[{"id":"{{{Id}}}","chave_acesso":"35270611222333000181550010000000011000000010","direcao":"saida","modelo":"NFE55","status":"DIVERGENTE","cnpj":"11222333000181","cnpj_contraparte":"11444777000161","diferenca":12.5,"credito_potencial":0,"credito_status":null,"ocorrencias":2,"lote_id":null,"competencia":"2026-11","criado_em":"{{{Ts}}}","reprocessamento":false,"xml_armazenado":true}],"proximo_cursor":null,"periodo":{"de":"2026-08-27","ate":"2026-11-25"}}"""),
        ["GET /v1/auditoria/documentos/{id}"] = new("Documentos", "Detalhe de um documento auditado",
            Description: "Itens como declarados no XML (valores fiscais, sem dados pessoais) e a resposta completa da auditoria, com cada ocorrência por item e campo (declarado × referência × fundamento).",
            Response: $$$"""{"documento":{"id":"{{{Id}}}","chave_acesso":"35270611222333000181550010000000011000000010","direcao":"saida","modelo":"NFE55","status":"CONFORME","cnpj":"11222333000181","cnpj_contraparte":"11444777000161","diferenca":0,"credito_potencial":0,"credito_status":null,"ocorrencias":0,"lote_id":null,"competencia":"2026-11","criado_em":"{{{Ts}}}","reprocessamento":false,"xml_armazenado":true},"itens_declarados":[{"numero":1,"ncm":"84713012","cst":"000","c_class_trib":"000001","vbc":1000.0,"pcbs":0.9,"vcbs":9.0}],"data_emissao":"2027-06-15T11:00:00-04:00","municipio_incidencia":1100205,"auditoria":{"audit_id":"aud_01938a6e5b1c7d2a9e4f0a1b2c3d4e5f","status":"CONFORME","ocorrencias":[],"avisos":[]}}"""),
        ["GET /v1/auditoria/documentos/exportar"] = new("Documentos", "Exporta os documentos em CSV",
            Description: "Mesmos filtros da listagem. `nivel=documento` (padrão, uma linha por documento) ou `nivel=ocorrencia` (uma linha por achado: regra, item, campo, declarado, referência e fundamento). Separador `;` e vírgula decimal (abre direto no Excel em pt-BR); até 50 mil linhas.",
            ResponseDescription: "Arquivo `text/csv` (UTF-8 com BOM)."),
        ["GET /v1/auditoria/resumo"] = new("Documentos", "Totais por situação no período",
            Description: "Mesmos filtros da listagem. Contagem por `status` (só as situações presentes no período), diferença total e crédito potencial (entradas).",
            Response: """{"total":1240,"por_status":{"CONFORME":1180},"diferenca_total":3120.55,"credito_potencial_total":890.1,"periodo":{"de":"2026-08-27","ate":"2026-11-25"}}"""),
        ["GET /v1/cnpjs/{cnpj}/dfe/documentos"] = new("DF-e", "Notas recebidas pela captura DF-e",
            Description: "Resumos, notas completas e eventos recebidos da SEFAZ para o CNPJ, com a situação (ciência, auditoria). Filtro `status` e `limite` (até 500).",
            Response: $$"""{"documentos":[{"nsu":1532,"schema":"procNFe_v4.00.xsd","tipo":"COMPLETO","chave_acesso":"35270611444777000161550010000000011000000010","situacao":"AUDITADO","status_auditoria":"CONFORME","detalhe":null,"recebido_em":"{{Ts}}"}]}"""),
        ["GET /v1/classificacao"] = new("Classificação", "Consulta CST/cClassTrib candidatos por NCM ou NBS",
            Description: "Informe `ncm` (8 dígitos) **ou** `nbs` (9 dígitos); opcionais `q` (palavras do produto, filtra), `data` (AAAA-MM-DD, padrão hoje) e `modelo` (55 ou 65). Devolve a descrição do código e se há Imposto Seletivo, e as classificações compatíveis ordenadas — **atalho de pesquisa, não parecer**: reduções dos anexos da LC 214/2025 dependem do enquadramento do produto.",
            Response: """{"consulta":{"tipo":"NCM","codigo":"22021000","data":"2027-01-01","modelo":55},"item":{"descricao":"Bebidas, líquidos alcoólicos e vinagres. — Águas, incluindo as águas minerais...","imposto_seletivo":true,"is_aliquota_ad_valorem":true,"is_aliquota_ad_rem":false},"sugestoes":[{"c_class_trib":"000001","cst":"000","descricao":"Situações tributadas integralmente pelo IBS e CBS.","tratamento":"Tributação integral","tipo_aliquota":"Padrão","reducao_cbs_percentual":0,"reducao_ibs_uf_percentual":0,"reducao_ibs_mun_percentual":0,"credito_adquirente":true,"motivo":"Regra geral: tributação integral, quando nenhuma situação específica se aplica."}],"aviso":"Sugestões por compatibilidade com a nomenclatura e com a descrição do item..."}""",
            Errors: [503]),
        ["GET /v1/avisos"] = new("Avisos", "Avisos da conta",
            Description: "Os 50 mais recentes; `nao_lidos=true` filtra. Tipos: FRANQUIA_80, FRANQUIA_ESGOTADA, WEBHOOK_PAUSADO, CERTIFICADO_VENCENDO, MUDANCA_FISCAL. Os mesmos avisos vão por e-mail aos papéis responsáveis.",
            Response: $$"""{"avisos":[{"id":"{{Id}}","tipo":"FRANQUIA_80","severidade":"aviso","titulo":"80% da franquia de auditorias usada","mensagem":"O plano ESSENCIAL inclui 2000 auditorias por mês...","link":"/portal/cobranca","criado_em":"{{Ts}}","lido":false}],"nao_lidos":1}"""),
        ["POST /v1/avisos/{id}/lido"] = new("Avisos", "Marca um aviso como lido", Status: 204),
        ["POST /v1/avisos/lidos"] = new("Avisos", "Marca todos os avisos como lidos", Status: 204),
        ["GET /v1/primeiros-passos"] = new("Avisos", "Checklist de primeiros passos da conta",
            Description: "O que a empresa já fez (simular, auditar, chave de API, webhook, equipe, plano) — usado pela Visão geral do portal.",
            Response: """{"passos":[{"id":"simular","titulo":"Simule um cálculo","descricao":"Veja CBS, IBS e IS de um item com o motor oficial da RFB.","link":"/portal/simular","feito":true}],"concluidos":1,"total":6}"""),

        ["GET /v1/webhooks"] = new("Webhooks", "Lista os webhooks",
            Response: $$"""[{"id":"{{Id}}","url":"https://erp.exemplo.com.br/hooks/taxgateway","events":["document.audited"],"active":true,"created_at":"{{Ts}}"}]"""),
        ["POST /v1/webhooks"] = new("Webhooks", "Cadastra um webhook HTTPS",
            """{"url":"https://erp.exemplo.com.br/hooks/taxgateway","eventos":["document.audited","batch.audit.completed","dfe.document.audited","certificate.expiring"]}""",
            "Destinos em rede privada ou interna são recusados (proteção SSRF). O segredo de assinatura aparece **só nesta resposta**. Cada entrega traz `TaxGateway-Signature: t=<unix>,v1=<hex HMAC-SHA256 de \"t.corpo\">`.",
            Status: 201, Response: $$"""{"id":"{{Id}}","secret":"whsec_exemplo_nao_use_9f8e7d6c5b4a","aviso":"Guarde o segredo: ele é exibido uma única vez. Assinatura em TaxGateway-Signature (t=..,v1=HMAC-SHA256)."}"""),
        ["DELETE /v1/webhooks/{id}"] = new("Webhooks", "Desativa um webhook", Status: 204),
        ["POST /v1/webhooks/{id}/rotate-secret"] = new("Webhooks", "Gira o segredo de assinatura",
            Response: """{"secret":"whsec_exemplo_nao_use_1a2b3c4d5e6f","aviso":"O segredo anterior continua assinando por 24 h."}"""),
        ["GET /v1/webhooks/deliveries"] = new("Webhooks", "Lista entregas", Description: "Filtro opcional `status=PENDING|RETRYING|DELIVERED|DEAD`.",
            Response: $$"""[{"id":"{{Id}}","webhook_id":"{{Id}}","event":"document.audited","status":"DELIVERED","attempts":1,"last_status_code":200,"created_at":"{{Ts}}"}]"""),
        ["POST /v1/webhooks/deliveries/{id}/retry"] = new("Webhooks", "Reenvia uma entrega", Status: 202),

        ["GET /v1/me"] = new("Credenciais", "Dados da credencial em uso",
            Description: "Tenant, CNPJ raiz, plano, escopos e limites efetivos. Não exige escopo específico.",
            Response: $$$"""{"tenant_id":"{{{Id}}}","nome":"Empresa Exemplo Ltda","cnpj_raiz":"11222333000181","plano":"GROWTH","situacao":"ACTIVE","auto_provisionar_cnpj":false,"credencial":"api_key","scopes":["simulacao:write","auditoria:write"],"limites":{"rpm":600,"concorrencia":20}}"""),
        ["GET /v1/api-keys"] = new("Credenciais", "Lista as API keys (sem o segredo)",
            Response: $$"""{"api_keys":[{"id":"{{Id}}","nome":"erp-producao","prefixo":"AbCd1234","scopes":["simulacao:write"],"expira_em":null,"revogada_em":null,"ultimo_uso":"{{Ts}}","criada_em":"{{Ts}}","atual":true}]}"""),
        ["POST /v1/api-keys"] = new("Credenciais", "Cria uma API key",
            """{"nome":"erp-producao","scopes":["simulacao:write","auditoria:write"],"expira_em":"2027-12-31T23:59:59Z"}""",
            "A chave aparece **só nesta resposta**. Uma chave não pode ter escopos que a chave que a criou não tem.",
            Status: 201, Response: $$"""{"id":"{{Id}}","api_key":"tg_live_sk_AbCd1234_exemploNaoUse0000000000000000","prefixo":"AbCd1234","scopes":["simulacao:write","auditoria:write"],"expira_em":"2027-12-31T23:59:59+00:00"}"""),
        ["DELETE /v1/api-keys/{id}"] = new("Credenciais", "Revoga uma API key", Description: "Efeito imediato em todas as instâncias.", Status: 204),

        ["GET /v1/usage"] = new("Consumo", "Consumo e prévia da fatura", Description: "Competência corrente ou `mes=AAAA-MM`. Atualizado com cache de até 60 s.",
            Response: $$"""{"competencia":"2026-11","plano":"GROWTH","documentos_auditados":12450,"simulacoes":88310,"cnpjs_ativos":3,"franquia_documentos":10000,"excedente_documentos":2450,"atualizado_em":"{{Ts}}"}"""),

        ["GET /v1/oem/cnpjs"] = new("OEM", "Lista os CNPJs atendidos",
            Response: $$"""{"cnpjs":[{"cnpj":"11444777000161","ativo":true,"cobranca_encerrada_a_partir_de":null,"criado_em":"{{Ts}}"}]}"""),
        ["POST /v1/oem/cnpjs"] = new("OEM", "Cadastra ou reativa um CNPJ atendido", """{"cnpj":"11444777000161"}""",
            "`201` quando o CNPJ passou a ativo agora; `200` quando já estava ativo.", Status: 201,
            Response: """{"cnpj":"11444777000161","ativo":true}"""),
        ["DELETE /v1/oem/cnpjs/{cnpj}"] = new("OEM", "Desativa um CNPJ", Description: "As chamadas com o CNPJ passam a ser recusadas; a cobrança termina na competência seguinte.", Status: 204),
        ["GET /v1/oem/usage"] = new("OEM", "Uso por CNPJ", Description: "Competência `month=AAAA-MM` (padrão: corrente), lida do ledger de cobrança.",
            Response: """{"competencia":"2026-11","cnpjs_ativos":1,"por_cnpj":[{"cnpj":"11444777000161","documentos_auditados":420,"simulacoes":5310}]}"""),

        ["PUT /v1/cnpjs/{cnpj}/certificado"] = new("DF-e", "Envia o certificado A1 do CNPJ", """{"pfx_base64":"MIIK...","senha":"senha-do-pfx"}""",
            "PFX até 64 KB. Validados: senha, chave privada RSA, vencimento e raiz do CNPJ do certificado. Guardado cifrado; o PFX e a senha nunca voltam pela API.",
            Response: $$"""{"cnpj":"11444777000161","thumbprint":"A1B2C3D4E5F60718293A4B5C6D7E8F9012345678","titular":"CN=EMPRESA EXEMPLO LTDA:11444777000161, O=ICP-Brasil","vence_em":"2027-11-25T00:00:00+00:00"}""",
            Errors: [422]),
        ["GET /v1/cnpjs/{cnpj}/certificado"] = new("DF-e", "Dados do certificado",
            Response: """{"cnpj":"11444777000161","thumbprint":"A1B2C3D4E5F60718293A4B5C6D7E8F9012345678","titular":"CN=EMPRESA EXEMPLO LTDA:11444777000161","vence_em":"2027-11-25T00:00:00+00:00"}"""),
        ["DELETE /v1/cnpjs/{cnpj}/certificado"] = new("DF-e", "Remove o certificado", Status: 204),
        ["POST /v1/cnpjs/{cnpj}/dfe/sync"] = new("DF-e", "Liga ou reconfigura a captura de DF-e", """{"c_uf_autor":35,"manifestar_ciencia":true}""",
            "Corpo opcional. Nunca antecipa uma consulta bloqueada (a SEFAZ bloqueia por 1 h após \"nenhum documento\"). Exige certificado enviado.",
            Response: $$$"""{"cnpj":"11444777000161","ativo":true,"ult_nsu":0,"max_nsu":null,"proxima_consulta":"{{{Ts}}}","ultimo_cstat":null,"ultima_consulta":null,"ultimo_656":null,"manifestar_ciencia":true,"documentos":{}}""",
            Errors: [409]),
        ["GET /v1/cnpjs/{cnpj}/dfe/status"] = new("DF-e", "Situação da captura",
            Response: $$$"""{"cnpj":"11444777000161","ativo":true,"ult_nsu":1532,"max_nsu":1532,"proxima_consulta":"2026-11-25T15:30:00+00:00","ultimo_cstat":"137","ultima_consulta":"{{{Ts}}}","ultimo_656":null,"manifestar_ciencia":true,"documentos":{"AUDITADO":41,"CIENCIA_ENVIADA":3,"IGNORADO":12}}"""),

        ["POST /v1/titulares/localizar"] = new("LGPD", "Localiza documentos de um titular", """{"cpf":"52998224725"}""",
            "Busca pelo HMAC do CPF (chave do seu tenant no cofre); o CPF não é guardado. Cada pedido fica registrado.",
            Response: $$"""{"documentos":[{"id":"{{Id}}","direcao":"OUTBOUND","chave_acesso":"35270611222333000181550010000000011000000010","xml_armazenado":true,"criado_em":"{{Ts}}"}]}"""),
        ["POST /v1/titulares/expurgar"] = new("LGPD", "Expurga os dados de um titular", """{"cpf":"52998224725"}""",
            "Apaga o XML e o vínculo com o CPF; o registro fiscal (valores e achados) fica, sem dado pessoal.",
            Response: $$"""{"documentos":[{"id":"{{Id}}","direcao":"OUTBOUND","chave_acesso":"35270611222333000181550010000000011000000010","xml_armazenado":false,"criado_em":"{{Ts}}"}],"documentos_expurgados":1,"xmls_apagados":1}"""),

        ["GET /admin/v1/me"] = new("Admin: tenants", "Operador autenticado", Response: """{"operador":"fiscal-a","scopes":["admin:fiscal","admin:commercial"]}"""),
        ["GET /admin/v1/plans"] = new("Admin: tenants", "Planos e limites",
            Response: """{"planos":[{"codigo":"GROWTH","rpm":600,"concorrencia":20,"lote_max_documentos":5000}]}"""),
        ["GET /admin/v1/tenants/{id}"] = new("Admin: tenants", "Detalhe do tenant",
            Description: "Assinatura, pessoas com acesso (papel e MFA), CNPJs, API keys (sem segredo), consumo dos últimos 3 meses e as 12 últimas faturas.",
            Response: $$$"""{"tenant":{"tenant_id":"{{{Id}}}","nome":"Empresa Exemplo Ltda","cnpj_raiz":"11222333000181","plano":"PROFISSIONAL","situacao":"ACTIVE","cobranca":{"provedor":"STRIPE","situacao_assinatura":"active"}},"pessoas":[{"nome":"Maria","email":"maria@exemplo.com.br","papel":"owner","mfa":true,"situacao":"ACTIVE","ultimo_acesso":"{{{Ts}}}"}],"cnpjs":[{"cnpj":"11222333000181","ativo":true,"criado_em":"{{{Ts}}}"}],"api_keys":[],"consumo":[{"competencia":"2026-11","documentos":1240,"simulacoes":8800}],"faturas":[]}"""),
        ["GET /admin/v1/tenants"] = new("Admin: tenants", "Lista tenants", Description: "Filtro opcional `busca` (nome ou início do CNPJ); paginação por `pagina` (a partir de 1) e `limite` (1–200, padrão 50), com `total`. Clientes OAuth só com id, nome e datas.",
            Response: $$"""{"tenants":[{"tenant_id":"{{Id}}","nome":"Empresa Exemplo Ltda","cnpj_raiz":"11222333000181","plano":"GROWTH","situacao":"ACTIVE","auto_provisionar_cnpj":false,"retencao_xml_dias":null,"criado_em":"{{Ts}}","clientes_oauth":[]}],"total":1,"pagina":1,"limite":50}"""),
        ["POST /admin/v1/tenants"] = new("Admin: tenants", "Cria tenant",
            """{"nome":"Empresa Exemplo Ltda","cnpj_raiz":"11222333000181","plano":"GROWTH","auto_provisionar_cnpj":false,"retencao_xml_dias":90,"cnpjs":[]}""",
            "Devolve a primeira API key (exibida uma única vez). Sem `retencao_xml_dias` o XML não é guardado.",
            Status: 201, Response: $$"""{"tenant_id":"{{Id}}","api_key":"tg_live_sk_AbCd1234_exemploNaoUse0000000000000000","scopes":["simulacao:write","auditoria:write"]}""",
            Errors: [409]),
        ["POST /admin/v1/tenants/{id}/suspend"] = new("Admin: tenants", "Suspende o tenant", Description: "Vale em menos de 5 s em todas as instâncias, para API key e OAuth.",
            Response: $$"""{"tenant_id":"{{Id}}","status":"SUSPENDED"}"""),
        ["POST /admin/v1/tenants/{id}/reactivate"] = new("Admin: tenants", "Reativa o tenant", Response: $$"""{"tenant_id":"{{Id}}","status":"ACTIVE"}"""),
        ["POST /admin/v1/tenants/{id}/oauth-clients"] = new("Admin: OAuth", "Cria cliente OAuth",
            """{"nome":"integracao-erp","scopes":["simulacao:write","usage:read"]}""", "O `client_secret` aparece só nesta resposta.",
            Status: 201, Response: """{"client_id":"tgc_AbCdEf0123456789wxyz","client_secret":"exemplo-nao-use-0000000000000000000000","scopes":["simulacao:write","usage:read"]}"""),
        ["DELETE /admin/v1/oauth-clients/{clientId}"] = new("Admin: OAuth", "Revoga um cliente OAuth", Description: "Tokens já emitidos param de valer na hora.", Status: 204),
        ["POST /admin/v1/oauth/revoked-tokens"] = new("Admin: OAuth", "Revoga um token pelo jti", """{"jti":"...","expira_em":"2026-12-10T15:15:00Z"}""", Status: 204),
        ["GET /admin/v1/params/rates"] = new("Admin: parâmetros", "Alíquotas vigentes do catálogo",
            Response: """[{"id":"01938000-0000-7000-8001-000000000006","tributo":"CBS","tipo":"REFERENCIA","esfera":"UNIAO","ente_codigo":null,"forma_aplicacao":null,"aliquota":11.2,"valid_from":"2027-01-01","valid_to":"2028-01-01","ato_legal":"Acórdão TCU; LC 214/2025 art. 347","fonte":"TCU","provisoria":true,"change_id":"01938000-0000-7000-8000-000000000005"}]"""),
        ["POST /admin/v1/params/rates/changes"] = new("Admin: parâmetros", "Propõe mudança de alíquota",
            """{"justificativa":"Resolução do Senado nº X/2026","vigente_em":"2027-01-01T00:00:00-03:00","linhas":[{"tributo":"CBS","tipo":"REFERENCIA","esfera":"UNIAO","aliquota":8.8,"valid_from":"2027-01-01","valid_to":"2028-01-01","ato_legal":"Resolução do Senado nº X/2026","fonte":"SENADO","provisoria":false}],"substitui":[]}""",
            "Outra pessoa precisa aprovar (fluxo de duas pessoas).", Status: 201, Response: $$"""{"change_id":"{{Id}}","status":"PROPOSED"}"""),
        ["GET /admin/v1/params/changes"] = new("Admin: parâmetros", "Lista propostas e mudanças", Description: "Filtro opcional `status`.",
            Response: """[{"id":"01938a6e-5b1c-7d2a-9e4f-0a1b2c3d4e5f","dominio":"RATE_CATALOG","status":"PROPOSED","proposto_por":"fiscal-a","aprovado_por":null,"justificativa":"Resolução do Senado nº X/2026","vigente_em":"2027-01-01T03:00:00+00:00","criado_em":"2026-11-25T14:30:00+00:00","payload":{}}]"""),
        ["POST /admin/v1/params/changes/{id}/approve"] = new("Admin: parâmetros", "Aprova e aplica uma proposta", Description: "Propaga para todas as instâncias em até 5 s.",
            Response: $$"""{"change_id":"{{Id}}","status":"APPLIED"}""", Errors: [409]),
        ["POST /admin/v1/billing/close"] = new("Admin: faturamento", "Fecha e fatura a competência", Description: "Query `mes=AAAA-MM`. Repetir não duplica cobrança.",
            Response: """{"competencia":"2026-11","periodos_fechados":42,"faturados":40}"""),
        ["GET /admin/v1/billing/reconcile"] = new("Admin: faturamento", "Reconcilia ledger, períodos e provedor", Description: "Query `mes=AAAA-MM`.",
            Response: """{"competencia":"2026-11","tenants":42,"divergencias":[]}"""),
        ["POST /admin/v1/billing/adjustments"] = new("Admin: faturamento", "Cria nota de ajuste (v2, v3…)",
            """{"tenant_id":"01938a6e-5b1c-7d2a-9e4f-0a1b2c3d4e5f","competencia":"2026-11","valor_centavos":2500,"motivo":"..."}""",
            "Valor negativo vira crédito a abater (o provedor não emite cobrança negativa)."),
    };

    /// <summary>Catálogo exposto aos testes (exemplos × respostas reais).</summary>
    public static IReadOnlyDictionary<string, Doc> Catalog => Docs;

    private static readonly Dictionary<int, string> ErrorText = new()
    {
        [400] = "Requisição inválida (`ERR_VALIDATION`, `ERR_BAD_REQUEST`).",
        [401] = "Credencial ausente, inválida, revogada ou expirada (`ERR_UNAUTHENTICATED`).",
        [403] = "Escopo insuficiente (`ERR_FORBIDDEN_SCOPE`), tenant suspenso (`ERR_TENANT_SUSPENDED`) ou CNPJ fora do cadastro/escopo (`ERR_CNPJ_NOT_PROVISIONED`, `ERR_CNPJ_SCOPE`).",
        [404] = "Recurso não encontrado (`ERR_NOT_FOUND`).",
        [409] = "Conflito com o estado atual (ex.: `ERR_CERT_REQUIRED`, `ERR_CONFLICT`, `ERR_PARAM_CONFLICT`).",
        [413] = "Arquivo acima do limite do plano.",
        [422] = "Conteúdo recusado pelas regras (ex.: XML fora do schema, assinatura inválida, regime sem cálculo, certificado recusado).",
        [429] = "Limite do plano excedido (`ERR_RATE_LIMITED`). Veja `Retry-After` e os headers `RateLimit-*`.",
        [503] = "Motor de cálculo indisponível no momento (`ERR_ENGINE_UNAVAILABLE`). Tente de novo.",
    };

    public static IServiceCollection AddTaxGatewayOpenApi(this IServiceCollection services, bool includeAdmin)
    {
        services.AddOpenApi("v1", o =>
        {
            // Rotas de conta de pessoas (/v1/conta, D-U01) servem ao navegador do portal, não à integração: fora da referência.
            o.ShouldInclude = d => d.RelativePath is { } p && !p.StartsWith("admin", StringComparison.Ordinal) && !p.StartsWith("health", StringComparison.Ordinal)
                && !p.StartsWith("v1/conta", StringComparison.Ordinal);
            Configure(o, "TaxGateway API", """
                Cálculo e auditoria de **IBS, CBS e Imposto Seletivo** sobre os motores oficiais da Receita Federal.

                * **Autenticação:** `X-Api-Key` ou `Authorization: Bearer` (OAuth 2.0 client credentials em `/oauth/token`).
                * **Erros:** sempre `application/problem+json` com o campo `code` estável (ex.: `ERR_FORBIDDEN_SCOPE`) e `trace_id`.
                * **Idempotência:** envie `Idempotency-Key` nas operações que geram cobrança ou efeito.
                * **Valores:** em reais, com ponto decimal; datas em ISO 8601.
                """, publicDoc: true);
        });
        if (includeAdmin)
        {
            services.AddOpenApi("admin", o =>
            {
                o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("admin", StringComparison.Ordinal) && !p.StartsWith("admin/v1/conta", StringComparison.Ordinal);
                Configure(o, "TaxGateway Admin API", "Backoffice: tenants, OAuth, parâmetros fiscais (duas pessoas) e faturamento. **Uso interno.**", publicDoc: false);
            });
        }

        return services;
    }

    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    private static partial Regex RouteConstraint();

    private static void Configure(OpenApiOptions o, string title, string description, bool publicDoc)
    {
        o.AddOperationTransformer((op, ctx, _) =>
        {
            var d = ctx.Description;
            // RelativePath traz as restrições de rota ({id:guid}); o catálogo usa o caminho público ({id}).
            var path = "/" + RouteConstraint().Replace(d.RelativePath ?? "", "{$1}");
            var key = $"{d.HttpMethod} {path}";
            var scope = d.ActionDescriptor.EndpointMetadata.OfType<RequiredScope>().FirstOrDefault()?.Scope;
            Docs.TryGetValue(key, out var doc);
            if (doc is not null)
            {
                Apply(op, doc, ctx.Document!);
            }

            if (scope is not null)
            {
                op.Description = (op.Description is null ? "" : op.Description + "\n\n") + $"**Escopo exigido:** `{scope}`.";
            }

            AddErrors(op, ctx.Document!, path, scope is not null, doc);
            if (path.StartsWith("/v1/", StringComparison.Ordinal))
            {
                op.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKey, ctx.Document)] = [] },
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(OAuth, ctx.Document)] = scope is null ? [] : [scope] },
                ];
            }
            else if (path.StartsWith("/admin/", StringComparison.Ordinal))
            {
                op.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(AdminKey, ctx.Document)] = [] }];
            }

            return Task.CompletedTask;
        });

        // Roda depois das operações: componentes, /oauth/token e a lista de grupos só com os usados (sem os nomes de
        // classe que o gerador cria e deixava grupos vazios na referência).
        o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new OpenApiInfo { Title = title, Version = AppVersion.Version, Description = description };
            doc.Components ??= new OpenApiComponents();
            doc.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
            doc.Components.Schemas[Problem] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Erro no padrão RFC 9457 (problem+json). Use `code` para tratar o erro; `title`/`detail` são para pessoas.",
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Examples = [JsonValue.Create("urn:taxgateway:erro:ERR_FORBIDDEN_SCOPE")] },
                    ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Examples = [JsonValue.Create("ERR_FORBIDDEN_SCOPE")] },
                    ["trace_id"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["errors"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.Object } },
                },
            };
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes[ApiKey] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Key",
                Description = "API key do tenant: `tg_live_sk_<prefixo>_<segredo>`. Crie e revogue pelo portal ou por `/v1/api-keys`.",
            };
            doc.Components.SecuritySchemes[OAuth] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "OAuth 2.0 client credentials; token JWT de 15 min em `Authorization: Bearer`.",
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri("/oauth/token", UriKind.Relative),
                        Scopes = Scopes.TenantDefault.ToDictionary(s => s, s => s),
                    },
                },
            };
            if (!publicDoc)
            {
                doc.Components.SecuritySchemes[AdminKey] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Admin-Key",
                    Description = "Chave individual de operador do backoffice.",
                };
            }

            if (publicDoc)
            {
                AddTokenEndpoint(doc);
            }

            var used = (doc.Paths ?? new OpenApiPaths()).Values
                .SelectMany(p => (IEnumerable<OpenApiOperation>?)p.Operations?.Values ?? [])
                .SelectMany(op => op.Tags ?? new HashSet<OpenApiTagReference>())
                .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            doc.Tags = new HashSet<OpenApiTag>(Tags.Where(t => used.Contains(t.Name)).Select(t => new OpenApiTag { Name = t.Name, Description = t.Description }));
            return Task.CompletedTask;
        });
    }

    private static void Apply(OpenApiOperation op, Doc doc, OpenApiDocument document)
    {
        op.Summary = doc.Summary;
        op.Tags = new HashSet<OpenApiTagReference> { new(doc.Tag, document) };
        if (doc.Description is not null)
        {
            op.Description = doc.Description;
        }

        if (doc.RawContentType is not null)
        {
            op.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    [doc.RawContentType] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = doc.RawContentType.EndsWith("zip", StringComparison.Ordinal) ? "binary" : null } },
                },
            };
        }
        else if (doc.Body is not null)
        {
            if (op.RequestBody is OpenApiRequestBody rb && rb.Content is not null && rb.Content.TryGetValue("application/json", out var media))
            {
                media.Example = JsonNode.Parse(doc.Body);
            }
            else
            {
                op.RequestBody = new OpenApiRequestBody
                {
                    Required = !doc.Summary.StartsWith("Liga", StringComparison.Ordinal),
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object }, Example = JsonNode.Parse(doc.Body) },
                    },
                };
            }
        }

        // Resposta de sucesso: mantém o schema gerado (rotas tipadas) e acrescenta o exemplo; corrige o código quando o
        // gerador só sabia "200".
        var status = doc.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        op.Responses ??= new OpenApiResponses();
        IOpenApiResponse? existing = null;
        if (op.Responses.TryGetValue(status, out var same))
        {
            existing = same;
        }
        else if (op.Responses.TryGetValue("200", out var generic) && status != "200")
        {
            op.Responses.Remove("200");
            existing = generic is OpenApiResponse g && g.Content is { Count: > 0 } ? generic : null;
        }

        var response = existing as OpenApiResponse ?? new OpenApiResponse();
        response.Description = doc.ResponseDescription ?? (doc.Status switch { 201 => "Criado.", 202 => "Aceito para processamento.", 204 => "Feito; sem corpo.", _ => "Sucesso." });
        if (doc.Status == 204)
        {
            response.Content = null;
        }
        else if (doc.Response is not null)
        {
            response.Content ??= new Dictionary<string, OpenApiMediaType>();
            if (!response.Content.TryGetValue("application/json", out var m))
            {
                m = new OpenApiMediaType { Schema = new OpenApiSchema { Type = doc.Response.TrimStart().StartsWith('[') ? JsonSchemaType.Array : JsonSchemaType.Object } };
                response.Content["application/json"] = m;
            }

            m.Example = JsonNode.Parse(doc.Response);
        }
        else if (doc.ResponseDescription is not null && doc.ResponseDescription.Contains("text/csv", StringComparison.Ordinal))
        {
            response.Content = new Dictionary<string, OpenApiMediaType> { ["text/csv"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String } } };
        }

        op.Responses[status] = response;
    }

    private static void AddErrors(OpenApiOperation op, OpenApiDocument document, string path, bool hasScope, Doc? doc)
    {
        op.Responses ??= new OpenApiResponses();
        var codes = new SortedSet<int> { 401 };
        if (path.StartsWith("/v1/", StringComparison.Ordinal))
        {
            codes.Add(429);
        }

        if (hasScope || path.StartsWith("/admin/", StringComparison.Ordinal))
        {
            codes.Add(403);
        }

        if (doc?.Body is not null || doc?.RawContentType is not null)
        {
            codes.Add(400);
        }

        if (path.Contains('{', StringComparison.Ordinal))
        {
            codes.Add(404);
        }

        foreach (var c in doc?.Errors ?? [])
        {
            codes.Add(c);
        }

        foreach (var c in codes)
        {
            var key = c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (op.Responses.ContainsKey(key))
            {
                continue;
            }

            op.Responses[key] = new OpenApiResponse
            {
                Description = ErrorText[c],
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(Problem, document) },
                },
            };
        }
    }

    /// <summary>/oauth/token é atendido pelo OpenIddict (middleware), não por um endpoint: documentado à mão.</summary>
    private static void AddTokenEndpoint(OpenApiDocument doc)
    {
        doc.Paths ??= new OpenApiPaths();
        doc.Paths["/oauth/token"] = new OpenApiPathItem
        {
            Operations = new Dictionary<HttpMethod, OpenApiOperation>
            {
                [HttpMethod.Post] = new()
                {
                    Summary = "Obtém um access token (OAuth 2.0 client credentials)",
                    Description = "Corpo `application/x-www-form-urlencoded`: `grant_type=client_credentials`, `client_id`, `client_secret` e, opcionalmente, `scope` (separados por espaço). Token JWT RS256 de 15 min; os limites do plano não vão no token. Erros no padrão OAuth.",
                    Tags = new HashSet<OpenApiTagReference> { new("Credenciais", doc) },
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/x-www-form-urlencoded"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Object,
                                    Required = new HashSet<string> { "grant_type", "client_id", "client_secret" },
                                    Properties = new Dictionary<string, IOpenApiSchema>
                                    {
                                        ["grant_type"] = new OpenApiSchema { Type = JsonSchemaType.String, Examples = [JsonValue.Create("client_credentials")] },
                                        ["client_id"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                        ["client_secret"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                        ["scope"] = new OpenApiSchema { Type = JsonSchemaType.String, Examples = [JsonValue.Create("simulacao:write usage:read")] },
                                    },
                                },
                            },
                        },
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = "Token emitido.",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new()
                                {
                                    Schema = new OpenApiSchema { Type = JsonSchemaType.Object },
                                    Example = JsonNode.Parse("""{"access_token":"eyJhbGciOiJSUzI1NiIsImtpZCI6Im9hdXRoLTIwMjcwMSIsInR5cCI6ImF0K2p3dCJ9...","token_type":"Bearer","expires_in":899}"""),
                                },
                            },
                        },
                        ["400"] = new OpenApiResponse
                        {
                            Description = "Erro OAuth: `invalid_client`, `invalid_scope` ou `unsupported_grant_type`.",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object }, Example = JsonNode.Parse("""{"error":"invalid_client","error_description":"Credenciais inválidas."}""") },
                            },
                        },
                    },
                },
            },
        };
    }
}
