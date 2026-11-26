# LCR desatualizada e cache adulterado

Alertas `TaxGatewayLcrDesatualizada` (ticket) e `TaxGatewayCacheAdulterado` (página). Decisões D-P23 e D-P10.

**Antes de qualquer comando no cluster, confira o contexto do `kubectl`** (esta máquina tem contextos de outros projetos).

## LCR com mais de 36 h (`TaxGatewayLcrDesatualizada`)

Em produção a API lê a LCR do bucket (`Xml__CrlSource=shared`); quem baixa da ICP-Brasil e publica em `crl/` são os
workers (`CrlPublisher`, a cada minuto para URLs novas e a cada hora para renovar). Com a LCR velha, as auditorias
seguem saindo, com o aviso `REVOGACAO_NAO_VERIFICADA` quando a LCR não está no cache.

1. Logs dos workers: `Falha ao baixar a LCR {Url}` indica o ponto de distribuição com problema (fora do ar, lento,
   acima de 32 MB) — os da ICP-Brasil às vezes ficam indisponíveis por horas.
2. Métrica `taxgateway_crl_published_total` parada: confira se os workers têm saída HTTPS (não há NetworkPolicy de
   egress neles) e acesso ao bucket.
3. Na API, `Falha ao baixar/validar a LCR` com "não confere" = a LCR no bucket não foi assinada pelo emissor. Trate
   como incidente ([incidente-seguranca.md](incidente-seguranca.md)): alguém gravou no bucket.
4. Política estrita (`Xml__StrictRevocation=true`) faz a auditoria falhar sem LCR — só ligue depois de estabilizar.

## Cache com selo inválido (`TaxGatewayCacheAdulterado`)

Toda entrada de autenticação e de simulação no Valkey leva um HMAC com chave derivada do pepper (que nunca vai ao
Valkey). Selo inválido = alguém escreveu no Valkey sem o pepper. A aplicação **descarta** a entrada e consulta o banco
— o cliente não é afetado —, mas o Valkey deve ser considerado comprometido.

1. Abra incidente de segurança ([incidente-seguranca.md](incidente-seguranca.md)).
2. Isole o Valkey: confira ACL, quem tem rede até ele (NetworkPolicy) e as conexões abertas (`CLIENT LIST`).
3. Gire a senha do Valkey e, se o acesso veio de um pod da aplicação, trate o pod como comprometido e gire os segredos
   do workload (Secrets separados por workload, D-P29).
4. `FLUSHDB` no Valkey de cache é seguro (tudo é recalculado); o estado de revogação tem o banco como fonte (D-P10).
