"""Roteiro ponta a ponta contra a pilha em containers (D-Q05). Só biblioteca padrão.

Uso:
  docker compose -f docker-compose.yml -f docker-compose.capacity.yml -f docker-compose.web.yml up -d api workers rtc-regular-batch web web-backoffice
  python tests/smoke/stack_smoke.py [--public http://127.0.0.1:8090] [--backoffice http://127.0.0.1:8091]

Tudo passa pelos nginx (site/portal e backoffice), como um cliente e um operador reais. Não liga a captura de DF-e
(chamaria a SEFAZ de homologação real com um certificado de teste). Sai com código 1 se algum passo falhar.
"""
import argparse
import io
import json
import os
import random
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OPERATOR = os.environ.get('SMOKE_OPERATOR_KEY', 'tg_admin_dev_operador_fiscal_a_0000000000')  # chave SÓ de dev
results = []


def step(name):
    def deco(fn):
        def run(*a, **k):
            t0 = time.time()
            try:
                detail = fn(*a, **k) or ''
                results.append((True, name, detail, time.time() - t0))
            except Exception as e:  # noqa: BLE001 — o roteiro registra a falha e segue
                results.append((False, name, f'{type(e).__name__}: {e}', time.time() - t0))
        return run
    return deco


def http(method, url, body=None, headers=None, raw=None, ctype=None, form=None):
    h = dict(headers or {})
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        h['Content-Type'] = 'application/json'
    elif raw is not None:
        data = raw
        h['Content-Type'] = ctype
    elif form is not None:
        data = urllib.parse.urlencode(form).encode()
        h['Content-Type'] = 'application/x-www-form-urlencoded'
    req = urllib.request.Request(url, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=60, context=ssl.create_default_context()) as r:
            content = r.read()
            return r.status, dict(r.headers), content
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


def js(content):
    return json.loads(content) if content else None


def expect(cond, msg):
    if not cond:
        raise AssertionError(msg)


def cnpj():
    stem = f'{random.randint(30000000, 39999999)}0001'
    def dv(s, w):
        r = sum(int(c) * x for c, x in zip(s, w)) % 11
        return 0 if r < 2 else 11 - r
    d1 = dv(stem, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2])
    return f'{stem}{d1}{dv(stem + str(d1), [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2])}'


def rekey(xml: bytes, numero: int) -> bytes:
    """Mesma lógica do GoldenXmlTests.Rekey, sobre texto: chave nova e nNF/cDV coerentes; a assinatura fica inválida."""
    s = xml.decode('utf-8')
    i = s.index('Id="NFe') + 7
    old = s[i:i + 44]
    base = old[:25] + f'{numero:09d}' + old[34:43]
    tot, w = 0, 2
    for c in reversed(base):
        tot += (ord(c) - 48) * w
        w = 2 if w == 9 else w + 1
    dv = 0 if tot % 11 < 2 else 11 - tot % 11
    return s.replace(old, base + str(dv)).encode('utf-8')


def main():
    sys.stdout.reconfigure(encoding='utf-8')   # console do Windows (cp1252) não imprime → e acentos
    ap = argparse.ArgumentParser()
    ap.add_argument('--public', default='http://127.0.0.1:8090')
    ap.add_argument('--backoffice', default='http://127.0.0.1:8091')
    a = ap.parse_args()
    P, B = a.public, a.backoffice
    ADM = {'X-Admin-Key': OPERATOR}
    st = {}

    @step('Site, docs e OpenAPI pelo nginx público; CSP e cabeçalhos de segurança')
    def site():
        s, h, c = http('GET', P + '/')
        expect(s == 200 and b'<div id="root">' in c, f'site {s}')
        csp = h.get('Content-Security-Policy', '')
        expect("script-src 'self'" in csp and "frame-ancestors 'none'" in csp, f'CSP: {csp}')
        expect(h.get('X-Content-Type-Options') == 'nosniff', 'nosniff')
        expect(http('GET', P + '/docs/')[0] == 200, 'docs')
        oa = js(http('GET', P + '/openapi/v1.json')[2])
        expect('/v1/calculo/simular' in oa['paths'] and not any(p.startswith('/admin') for p in oa['paths']), 'openapi')
        return f'{len(oa["paths"])} rotas no OpenAPI público'
    site()

    @step('Superfície administrativa e métricas NÃO expostas no público')
    def hidden():
        expect(http('GET', P + '/admin/v1/me', headers=ADM)[0] == 404, '/admin no público deveria ser 404')
        s, _, c = http('GET', P + '/metrics')
        expect(b'http_server_request_duration' not in c, '/metrics exposto')
        expect(http('GET', B + '/v1/me')[0] == 404, '/v1 no backoffice deveria ser 404')
    hidden()

    @step('Backoffice: operador, planos e criação de tenant')
    def tenant():
        s, _, c = http('GET', B + '/admin/v1/me', headers=ADM)
        expect(s == 200, f'operador {s} {c[:200]}')
        st['root'] = '11222333000181'
        s, _, c = http('POST', B + '/admin/v1/tenants', headers=ADM, body={
            'nome': f'Smoke {uuid.uuid4().hex[:6]}', 'cnpj_raiz': cnpj(), 'plano': 'GROWTH',
            'auto_provisionar_cnpj': True, 'retencao_xml_dias': 30})
        expect(s == 201, f'criar tenant {s} {c[:300]}')
        d = js(c)
        st['tenant'], st['key'] = d['tenant_id'], d['api_key']
        return st['tenant']
    tenant()
    K = {'X-Api-Key': st.get('key', '')}

    @step('Portal: /v1/me com a API key, limites e cabeçalhos de rate limit')
    def me():
        s, h, c = http('GET', P + '/v1/me', headers=K)
        expect(s == 200 and js(c)['plano'] == 'GROWTH', f'me {s}')
        expect(any(k.lower().startswith('ratelimit') for k in h), f'sem RateLimit-*: {list(h)}')
        expect(http('GET', P + '/v1/me', headers={'X-Api-Key': 'tg_test_sk_AAAAAAAA_' + 'x' * 32})[0] == 401, 'chave falsa deveria ser 401')
    me()

    @step('OAuth: cliente criado no backoffice, token pelo nginx, chamada com Bearer, escopo negado')
    def oauth():
        s, _, c = http('POST', B + f'/admin/v1/tenants/{st["tenant"]}/oauth-clients', headers=ADM, body={'nome': 'smoke', 'scopes': ['usage:read']})
        expect(s == 201, f'cliente {s} {c[:200]}')
        d = js(c)
        s, _, c = http('POST', P + '/oauth/token', form={'grant_type': 'client_credentials', 'client_id': d['client_id'], 'client_secret': d['client_secret']})
        expect(s == 200, f'token {s} {c[:200]}')
        tok = js(c)['access_token']
        st['bearer'] = {'Authorization': f'Bearer {tok}'}
        expect(http('GET', P + '/v1/usage', headers=st['bearer'])[0] == 200, 'usage com bearer')
        s, _, c = http('POST', P + '/v1/calculo/simular', headers=st['bearer'], body={})
        expect(s == 403 and js(c)['code'] == 'ERR_FORBIDDEN_SCOPE', f'escopo deveria negar: {s}')
        jwks = js(http('GET', P + '/.well-known/jwks.json')[2])
        return f'{len(jwks["keys"])} chave(s) no JWKS'
    oauth()

    @step('Simulação pelo motor oficial (container)')
    def sim():
        body = {'cnpj_emitente': st['root'], 'regime_tributario': 'REGIME_REGULAR', 'data_fato_gerador': '2027-06-15T14:00:00-03:00',
                'municipio_ibge': 3550308, 'uf': 'SP', 'itens': [{'numero': 1, 'cst_ibs_cbs': '000', 'c_class_trib': '000001',
                'valor_base_calculo': 1000, 'ncm': '84713012', 'quantidade': 1, 'unidade': 'UN'}]}
        s, _, c = http('POST', P + '/v1/calculo/simular', headers={**K, 'Idempotency-Key': str(uuid.uuid4())}, body=body)
        expect(s == 200, f'simular {s} {c[:300]}')
        t = js(c)['totais']
        expect(abs(t['cbs_valor'] - 112.0) < 0.01 and abs(t['ibs_total_valor'] - 1.0) < 0.01, f'totais inesperados {t}')
        return f"CBS {t['cbs_valor']} · IBS {t['ibs_total_valor']}"
    sim()

    xmls = sorted(f for f in os.listdir(os.path.join(ROOT, 'tests/golden/corpus-xml/v0')) if f.endswith('.xml'))[:6]
    corpus = [open(os.path.join(ROOT, 'tests/golden/corpus-xml/v0', f), 'rb').read() for f in xmls]

    @step('Auditoria de XML do corpus (assinado pela PKI de teste confiável no compose)')
    def audit():
        s, _, c = http('POST', P + '/v1/auditoria/xml?direction=OUTBOUND', headers={**K, 'Idempotency-Key': str(uuid.uuid4())}, raw=corpus[0], ctype='application/xml')
        expect(s == 200, f'auditoria {s} {c[:300]}')
        d = js(c)
        expect(d['status'] in ('CONFORME', 'DIVERGENTE', 'INDICIO'), f"status {d['status']}: {[o['regra'] for o in d['ocorrencias']]}")
        st['chave'] = d['chave_acesso']
        s2, _, c2 = http('POST', P + '/v1/auditoria/xml?direction=OUTBOUND', headers={**K, 'Idempotency-Key': str(uuid.uuid4())}, raw=corpus[0], ctype='application/xml')
        expect(s2 == 200 and js(c2)['replay'] is True, 'replay por chave de acesso')
        r = js(http('POST', P + '/v1/auditoria/xml?direction=OUTBOUND', headers={**K, 'Idempotency-Key': str(uuid.uuid4())}, raw=rekey(corpus[0], 990001), ctype='application/xml')[2])
        expect(r['status'] == 'ASSINATURA_INVALIDA', f"XML adulterado deveria dar ASSINATURA_INVALIDA: {r['status']}")
        return f"{d['status']}; replay ok; adulterado → ASSINATURA_INVALIDA"
    audit()

    @step('Webhook cadastrado (destino público) e destino privado recusado (SSRF)')
    def webhook():
        s, _, c = http('POST', P + '/v1/webhooks', headers=K, body={'url': 'https://example.com/taxgateway-smoke', 'eventos': ['batch.audit.completed']})
        expect(s == 201 and js(c)['secret'].startswith('whsec_'), f'webhook {s} {c[:200]}')
        s, _, c = http('POST', P + '/v1/webhooks', headers=K, body={'url': 'https://10.0.0.5/hook', 'eventos': ['*']})
        expect(s in (400, 422), f'IP privado deveria ser recusado: {s}')
    webhook()

    @step('Lote ZIP processado pelos workers, relatório CSV e evento no outbox → entrega de webhook')
    def batch():
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, 'w') as z:
            for i, x in enumerate(corpus[1:6]):
                z.writestr(f'nota-{i}.xml', x)
        s, _, c = http('POST', P + '/v1/auditoria/lotes?direction=OUTBOUND', headers={**K, 'Idempotency-Key': str(uuid.uuid4())}, raw=buf.getvalue(), ctype='application/zip')
        expect(s == 202, f'lote {s} {c[:300]}')
        bid = js(c)['batch_id']
        t0 = time.time()
        while time.time() - t0 < 120:
            d = js(http('GET', P + f'/v1/auditoria/lotes/{bid}', headers=K)[2])
            if d['status'] in ('COMPLETED', 'FAILED'):
                break
            time.sleep(1)
        expect(d['status'] == 'COMPLETED', f'lote terminou em {d["status"]}')
        s, h, csv = http('GET', P + f'/v1/auditoria/lotes/{bid}/relatorio', headers=K)
        expect(s == 200 and csv.count(b'\n') >= 5, f'relatório {s}')
        t1 = time.time()
        while time.time() - t1 < 60:
            dl = js(http('GET', P + '/v1/webhooks/deliveries', headers=K)[2])
            if any(x['event'] == 'batch.audit.completed' for x in dl):
                break
            time.sleep(1)
        expect(any(x['event'] == 'batch.audit.completed' for x in dl), 'entrega de webhook não criada')
        return f"{d['processados']}/{d['total_documentos']} docs em {time.time() - t0:.0f}s; entrega {[x['status'] for x in dl]}"
    batch()

    @step('OEM: CNPJ cadastrado, listado e uso por CNPJ vindo do ledger')
    def oem():
        expect(http('POST', P + '/v1/oem/cnpjs', headers=K, body={'cnpj': '11444777000161'})[0] in (200, 201), 'add cnpj')
        lst = js(http('GET', P + '/v1/oem/cnpjs', headers=K)[2])['cnpjs']
        expect(any(x['cnpj'] == '11444777000161' for x in lst), 'cnpj na lista')
        t0 = time.time()
        while time.time() - t0 < 20:
            u = js(http('GET', P + '/v1/oem/usage', headers=K)[2])
            if u['por_cnpj']:
                break
            time.sleep(1)
        expect(u['por_cnpj'], 'uso por CNPJ vazio')
        return json.dumps(u['por_cnpj'])
    oem()

    @step('Certificado A1 (PKI de teste): envio, consulta e remoção; captura exige certificado')
    def cert():
        pfx = open(os.path.join(ROOT, 'tests/golden/pki/test-leaf-11222333000181.pfx'), 'rb').read()
        import base64
        s, _, c = http('PUT', P + f'/v1/cnpjs/{st["root"]}/certificado', headers=K, body={'pfx_base64': base64.b64encode(pfx).decode(), 'senha': 'errada'})
        expect(s == 400 and js(c)['code'] == 'ERR_CERT_INVALID', f'senha errada {s}')
        s, _, c = http('PUT', P + f'/v1/cnpjs/{st["root"]}/certificado', headers=K, body={'pfx_base64': base64.b64encode(pfx).decode(), 'senha': 'teste'})
        expect(s == 200 and 'senha' not in js(c), f'certificado {s} {c[:200]}')
        expect(http('GET', P + f'/v1/cnpjs/{st["root"]}/certificado', headers=K)[0] == 200, 'consulta')
        expect(http('DELETE', P + f'/v1/cnpjs/{st["root"]}/certificado', headers=K)[0] == 204, 'remoção')
        s, _, c = http('POST', P + f'/v1/cnpjs/{st["root"]}/dfe/sync', headers=K)
        expect(s == 409 and js(c)['code'] == 'ERR_CERT_REQUIRED', f'sync sem certificado {s}')
    cert()

    @step('LGPD: localizar titular')
    def lgpd():
        s, _, c = http('POST', P + '/v1/titulares/localizar', headers=K, body={'cpf': '52998224725'})
        expect(s == 200 and 'documentos' in js(c), f'titular {s}')
        expect(http('POST', P + '/v1/titulares/localizar', headers=K, body={'cpf': '11111111111'})[0] == 400, 'CPF inválido')
    lgpd()

    @step('Consumo da competência converge (cache de até 60 s, D-705) e bate com o ledger por CNPJ')
    def usage():
        t0 = time.time()
        while True:
            u = js(http('GET', P + '/v1/usage', headers=K)[2])
            if (u['simulacoes'] >= 1 and u['documentos_auditados'] >= 2) or time.time() - t0 > 75:
                break
            time.sleep(3)
        expect(u['simulacoes'] >= 1 and u['documentos_auditados'] >= 2, f'uso {u}')
        por_cnpj = js(http('GET', P + '/v1/oem/usage', headers=K)[2])['por_cnpj']
        expect(sum(x['documentos_auditados'] for x in por_cnpj) == u['documentos_auditados'], f'total {u} × por CNPJ {por_cnpj}')
        return f"{u['documentos_auditados']} docs · {u['simulacoes']} simulações (convergiu em {time.time() - t0:.0f}s)"
    usage()

    @step('Suspensão pelo backoffice vale em < 5 s para API key e token; reativação volta')
    def suspend():
        expect(http('POST', B + f'/admin/v1/tenants/{st["tenant"]}/suspend', headers=ADM)[0] == 200, 'suspend')
        t0 = time.time()
        while time.time() - t0 < 10:
            if http('GET', P + '/v1/me', headers=K)[0] == 403 and http('GET', P + '/v1/usage', headers=st['bearer'])[0] in (401, 403):
                break
            time.sleep(0.1)
        dt = time.time() - t0
        expect(dt < 5, f'suspensão levou {dt:.1f}s')
        expect(http('POST', B + f'/admin/v1/tenants/{st["tenant"]}/reactivate', headers=ADM)[0] == 200, 'reactivate')
        t1 = time.time()
        while time.time() - t1 < 40 and http('GET', P + '/v1/me', headers=K)[0] != 200:
            time.sleep(0.5)
        expect(http('GET', P + '/v1/me', headers=K)[0] == 200, 'reativação')
        return f'efetiva em {dt * 1000:.0f} ms'
    suspend()

    @step('Revogação de API key: nova chave criada e revogada deixa de valer')
    def revoke():
        s, _, c = http('POST', P + '/v1/api-keys', headers=K, body={'nome': 'temp', 'scopes': ['usage:read']})
        expect(s == 201, f'criar key {s}')
        d = js(c)
        expect(http('GET', P + '/v1/usage', headers={'X-Api-Key': d['api_key']})[0] == 200, 'nova key funciona')
        expect(http('DELETE', P + f'/v1/api-keys/{d["id"]}', headers=K)[0] == 204, 'revogar')
        expect(http('GET', P + '/v1/usage', headers={'X-Api-Key': d['api_key']})[0] == 401, 'key revogada ainda vale')
    revoke()

    ok = sum(1 for r in results if r[0])
    for passed, name, detail, dt in results:
        print(f"{'OK  ' if passed else 'FALHA'} {name} ({dt:.1f}s){' — ' + detail if detail else ''}")
    print(f'\n{ok}/{len(results)} passos aprovados')
    return 0 if ok == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
