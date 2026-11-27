"""Cliente sintético (D-R11): a jornada de uma empresa nova e do ERP dela, contra a pilha em containers. Só biblioteca padrão.

Uso (pilha do compose no ar, com o Mailpit):
  python tests/smoke/cliente_sintetico.py [--public http://127.0.0.1:8090] [--backoffice http://127.0.0.1:8091] [--mailpit http://127.0.0.1:8025]

Faz o que uma pessoa faria pelo portal (cadastro, e-mail de confirmação lido no Mailpit, MFA com TOTP, sessão por cookie e
cabeçalho anti-CSRF) e o que o ERP faria pela API (chave criada no portal, simulação, consumo, avisos), mais o lado das
regras de cobrança e titularidade (D-R08/D-R10): empresa nasce sem verificação, teto de excedente, cadastro repetido do
mesmo CNPJ sem enumerar, verificação manual pelo backoffice liberando a captura de DF-e. E o caminho de falha do webhook
num destino real que não aceita o POST: nova tentativa agendada e retry manual. Sai com código 1 se algum passo falhar.
"""
import argparse
import base64
import hashlib
import hmac
import http.cookiejar
import json
import os
import random
import re
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

OPERATOR = os.environ.get('SMOKE_OPERATOR_KEY', 'tg_admin_dev_operador_fiscal_a_0000000000')  # chave SÓ de dev
SENHA = 'Uma frase longa e boa para o teste 2027!'
results = []


def step(name):
    def deco(fn):
        def run(*a, **k):
            t0 = time.time()
            try:
                detail = fn(*a, **k) or ''
                results.append((True, name, detail, time.time() - t0))
            except Exception as e:  # noqa: BLE001 — registra a falha e segue
                results.append((False, name, f'{type(e).__name__}: {e}', time.time() - t0))
        return run
    return deco


def expect(cond, msg):
    if not cond:
        raise AssertionError(msg)


class Client:
    """Navegador mínimo: guarda cookies e manda o cabeçalho anti-CSRF em toda escrita (como o portal)."""

    def __init__(self, base, headers=None):
        self.base = base
        self.headers = dict(headers or {})
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, method, path, body=None, headers=None):
        h = {**self.headers, **(headers or {})}
        data = None
        if body is not None:
            data = json.dumps(body).encode()
            h['Content-Type'] = 'application/json'
        if method != 'GET':
            h.setdefault('X-TaxGateway-Csrf', '1')
        req = urllib.request.Request(self.base + path, data=data, method=method, headers=h)
        try:
            with self.opener.open(req, timeout=60) as r:
                raw = r.read()
                return r.status, (json.loads(raw) if raw and r.headers.get_content_type().endswith('json') else raw)
        except urllib.error.HTTPError as e:
            raw = e.read()
            try:
                return e.code, json.loads(raw)
            except ValueError:
                return e.code, raw


def cnpj():
    stem = f'{random.randint(40000000, 49999999)}0001'

    def dv(s, w):
        r = sum(int(c) * x for c, x in zip(s, w)) % 11
        return 0 if r < 2 else 11 - r
    d1 = dv(stem, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2])
    return f'{stem}{d1}{dv(stem + str(d1), [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2])}'


def totp(secret_b32, step_offset=0):
    key = base64.b32decode(secret_b32 + '=' * (-len(secret_b32) % 8))
    counter = int(time.time()) // 30 + step_offset
    h = hmac.new(key, struct.pack('>Q', counter), hashlib.sha1).digest()
    o = h[-1] & 0x0F
    return f'{(struct.unpack(">I", h[o:o + 4])[0] & 0x7FFFFFFF) % 1_000_000:06d}'


def mail_text(mailpit, to, contains=None, timeout=30):
    """Último e-mail para `to` no Mailpit (opcionalmente contendo um trecho)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        q = urllib.parse.quote(f'to:"{to}"')
        with urllib.request.urlopen(f'{mailpit}/api/v1/search?query={q}', timeout=10) as r:
            msgs = json.loads(r.read()).get('messages') or []
        for m in msgs:
            with urllib.request.urlopen(f'{mailpit}/api/v1/message/{m["ID"]}', timeout=10) as r:
                text = json.loads(r.read()).get('Text', '')
            if contains is None or contains in text:
                return text
        time.sleep(1)
    raise AssertionError(f'e-mail para {to} não chegou ao Mailpit')


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser()
    ap.add_argument('--public', default='http://127.0.0.1:8090')
    ap.add_argument('--backoffice', default='http://127.0.0.1:8091')
    ap.add_argument('--mailpit', default='http://127.0.0.1:8025')
    a = ap.parse_args()
    P, B, M = a.public, a.backoffice, a.mailpit
    admin = Client(B, {'X-Admin-Key': OPERATOR})
    st = {'cnpj': cnpj(), 'email': f'sintetico-{uuid.uuid4().hex[:10]}@exemplo.test'}
    pessoa = Client(P)

    @step('Cadastro pelo portal, confirmação pelo link do e-mail e MFA obrigatório com app autenticador')
    def cadastro():
        s, b = pessoa.call('POST', '/v1/conta/cadastro', {'nome': 'Cliente Sintético', 'email': st['email'], 'senha': SENHA,
                                                         'empresa': {'nome': 'Sintética Ltda', 'cnpj': st['cnpj']}, 'aceite_termos': True})
        expect(s == 202, f'cadastro {s} {b}')
        link = re.search(r'https?://\S+confirmar-email\?token=([\w\-]+)', mail_text(M, st['email']))
        expect(link, 'link de confirmação no e-mail')
        expect(pessoa.call('POST', '/v1/conta/confirmar-email', {'token': link.group(1)})[0] == 200, 'confirmar e-mail')
        s, b = pessoa.call('POST', '/v1/conta/entrar', {'email': st['email'], 'senha': SENHA})
        expect(s == 200 and b['mfa'] == 'configurar', f'login {s} {b}')
        expect(pessoa.call('GET', '/v1/me')[1].get('code') == 'ERR_MFA_NECESSARIO', 'sem MFA não entra')
        st['totp'] = pessoa.call('POST', '/v1/conta/mfa/iniciar')[1]['segredo']
        s, b = pessoa.call('POST', '/v1/conta/mfa/ativar', {'codigo': totp(st['totp'])})
        expect(s == 200 and len(b['codigos_recuperacao']) == 10, f'MFA {s} {b}')
        s, b = pessoa.call('GET', '/v1/conta/sessao')
        expect(b['plano'] == 'SANDBOX' and b['papel'] == 'owner', f'sessão {b}')
        return f"empresa {st['cnpj']} no SANDBOX"
    cadastro()

    @step('Escrita sem o cabeçalho anti-CSRF é recusada')
    def csrf():
        s, _ = pessoa.call('POST', '/v1/api-keys', {'nome': 'x', 'scopes': ['usage:read']}, headers={'X-TaxGateway-Csrf': ''})
        expect(s in (400, 403), f'sem CSRF deveria falhar: {s}')
    csrf()

    @step('Cobrança: empresa nasce sem verificação, SANDBOX tem franquia rígida e sem DF-e, e o teto é guardado')
    def cobranca():
        s, b = pessoa.call('GET', '/v1/cobranca')
        expect(s == 200, f'cobrança {s} {b}')
        expect(b['verificacao']['verificada'] is False and b['excedente']['franquia_rigida'] is True and b['inadimplencia'] is None, f'{b}')
        expect(pessoa.call('PUT', '/v1/cobranca/teto', {'teto_centavos': -1})[0] == 400, 'teto negativo')
        s, b = pessoa.call('PUT', '/v1/cobranca/teto', {'teto_centavos': 5000})
        expect(s == 200 and pessoa.call('GET', '/v1/cobranca')[1]['excedente']['teto_centavos'] == 5000, f'teto {s} {b}')
        s, b = pessoa.call('POST', f'/v1/cnpjs/{st["cnpj"]}/dfe/sync', {'c_uf_autor': 35})
        expect(s == 403 and b['code'] == 'ERR_PLANO_SEM_DFE', f'Sandbox não tem DF-e (D-R13): {s} {b}')
        return 'teto R$ 50,00'
    cobranca()

    @step('Cadastro repetido do mesmo CNPJ: mesma resposta (sem enumerar) e e-mail explicando convite/contestação')
    def repetido():
        outro = f'outro-{uuid.uuid4().hex[:10]}@exemplo.test'
        s, b = Client(P).call('POST', '/v1/conta/cadastro', {'nome': 'Outra Pessoa', 'email': outro, 'senha': SENHA,
                                                            'empresa': {'nome': 'Sintética', 'cnpj': st['cnpj']}, 'aceite_termos': True})
        expect(s == 202, f'deveria responder 202 como um cadastro novo: {s} {b}')
        expect('já tem cadastro' in mail_text(M, outro), 'e-mail de empresa já cadastrada')
    repetido()

    @step('ERP: API key criada no portal, simulação pelo motor oficial, consumo e avisos')
    def erp():
        s, b = pessoa.call('POST', '/v1/api-keys', {'nome': 'erp-sintetico', 'scopes': ['simulacao:write', 'usage:read']})
        expect(s == 201, f'api key {s} {b}')
        st['key'] = b['api_key']
        erp = Client(P, {'X-Api-Key': st['key']})
        body = {'cnpj_emitente': st['cnpj'], 'regime_tributario': 'REGIME_REGULAR', 'data_fato_gerador': '2027-06-15T14:00:00-03:00',
                'municipio_ibge': 3550308, 'uf': 'SP', 'itens': [{'numero': 1, 'cst_ibs_cbs': '000', 'c_class_trib': '000001',
                'valor_base_calculo': 1000, 'ncm': '84713012', 'quantidade': 1, 'unidade': 'UN'}]}
        s, r = erp.call('POST', '/v1/calculo/simular', body, headers={'Idempotency-Key': str(uuid.uuid4())})
        expect(s == 200 and abs(r['totais']['cbs_valor'] - 112.0) < 0.01, f'simulação {s} {r}')
        t0 = time.time()
        while time.time() - t0 < 75:
            u = erp.call('GET', '/v1/usage')[1]
            if u['simulacoes'] >= 1:
                break
            time.sleep(3)
        expect(u['simulacoes'] >= 1, f'consumo {u}')
        expect(pessoa.call('GET', '/v1/avisos')[0] == 200, 'avisos')
        return f"CBS {r['totais']['cbs_valor']}; consumo {u['simulacoes']} simulação(ões)"
    erp()

    @step('Backoffice verifica a titularidade manualmente → aviso no portal; no plano com DF-e, a captura pede o A1')
    def verificar():
        tid = pessoa.call('GET', '/v1/conta/sessao')[1]['empresa_ativa']
        expect(admin.call('POST', f'/admin/v1/tenants/{tid}/verificar', {'observacao': 'x'})[0] == 400, 'observação curta')
        s, b = admin.call('POST', f'/admin/v1/tenants/{tid}/verificar', {'observacao': 'Contrato social conferido (teste sintético)'})
        expect(s == 200, f'verificar {s} {b}')
        t0 = time.time()
        while time.time() - t0 < 10 and not pessoa.call('GET', '/v1/cobranca')[1]['verificacao']['verificada']:
            time.sleep(0.5)
        v = pessoa.call('GET', '/v1/verificacao-cnpj')[1]
        expect(v['verificada'] and v['metodo'] == 'MANUAL', f'verificação {v}')
        avisos = pessoa.call('GET', '/v1/avisos')[1]['avisos']
        expect(any(x['tipo'] == 'EMPRESA_VERIFICADA' for x in avisos), f'aviso de empresa verificada: {[x["tipo"] for x in avisos]}')
        expect(admin.call('POST', f'/admin/v1/tenants/{tid}/plano', {'plano': 'PROFISSIONAL'})[0] == 200, 'plano com DF-e')
        t0 = time.time()
        while time.time() - t0 < 10:
            s, b = pessoa.call('POST', f'/v1/cnpjs/{st["cnpj"]}/dfe/sync', {'c_uf_autor': 35})
            if b.get('code') != 'ERR_PLANO_SEM_DFE':
                break
            time.sleep(0.5)
        expect(s == 409 and b['code'] == 'ERR_CERT_REQUIRED', f'DF-e verificado e no plano certo pede o certificado: {s} {b}')
    verificar()

    @step('Sessões: segunda sessão com código de recuperação, listar e sair das outras')
    def sessoes():
        outro = Client(P)
        expect(outro.call('POST', '/v1/conta/entrar', {'email': st['email'], 'senha': SENHA})[1]['mfa'] == 'verificar', 'login 2')
        s, b = pessoa.call('POST', '/v1/conta/mfa/novos-codigos', {'codigo': totp(st['totp'], 1)})
        expect(s == 200, f'novos códigos {s} {b}')
        expect(outro.call('POST', '/v1/conta/mfa/verificar', {'codigo_recuperacao': b['codigos_recuperacao'][0]})[0] == 204, 'MFA por recuperação')
        expect(len(pessoa.call('GET', '/v1/conta/sessoes')[1]) == 2, 'duas sessões')
        expect(pessoa.call('POST', '/v1/conta/sair-outras')[1]['encerradas'] == 1, 'sair das outras')
        expect(outro.call('GET', '/v1/me')[0] == 401, 'sessão encerrada ainda vale')
    sessoes()

    @step('Alíquotas por município (site público), apuração do mês, OAuth pelo portal e log de chamadas')
    def novidades():
        anon = Client(P)
        s, b = anon.call('GET', '/v1/publico/municipios?busca=' + urllib.parse.quote('sao paulo'))
        expect(s == 200 and b[0]['codigo'] == 3550308, f'busca de município {s} {b}')
        s, b = anon.call('GET', '/v1/publico/aliquotas?municipio=3550308&data=2027-03-01')
        cbs = next(t for t in b['tributos'] if t['tributo'] == 'CBS')
        expect(s == 200 and cbs['provisoria'] and b['total'] is not None, f'alíquotas {s} {b}')
        s, b = pessoa.call('GET', '/v1/apuracao?competencia=2027-06')
        expect(s == 200 and 'saldo' in b, f'apuração {s} {b}')
        s, c = pessoa.call('POST', '/v1/oauth-clients', {'nome': 'ERP sintético', 'scopes': ['usage:read']})
        expect(s == 201 and c['client_secret'], f'cliente OAuth {s} {c}')
        req = urllib.request.Request(P + '/oauth/token', method='POST', data=urllib.parse.urlencode(
            {'grant_type': 'client_credentials', 'client_id': c['client_id'], 'client_secret': c['client_secret']}).encode(),
            headers={'Content-Type': 'application/x-www-form-urlencoded'})
        with urllib.request.urlopen(req, timeout=30) as r:
            expect(r.status == 200, 'token OAuth')
        expect(pessoa.call('DELETE', f'/v1/oauth-clients/{c["client_id"]}')[0] == 204, 'revogar cliente OAuth')
        expect(pessoa.call('GET', '/v1/apuracao?competencia=errada')[0] == 400, 'erro proposital')
        t0 = time.time()
        while time.time() - t0 < 15:
            logs = pessoa.call('GET', '/v1/logs?status=erros')[1]['chamadas']
            if any(x['rota'] == '/v1/apuracao' for x in logs):
                break
            time.sleep(1)
        expect(any(x['rota'] == '/v1/apuracao' and x['status'] == 400 for x in logs), f'log de chamadas {logs[:3]}')
        return f"CBS 2027 {cbs['aliquota']}% (provisória); OAuth e log ok"
    novidades()

    @step('Webhook num destino que recusa o POST: nova tentativa agendada e retry manual (integrador OEM)')
    def webhook():
        s, b = admin.call('POST', '/admin/v1/tenants', {'nome': f'Sintético OEM {uuid.uuid4().hex[:6]}', 'cnpj_raiz': cnpj(), 'plano': 'GROWTH',
                                                       'auto_provisionar_cnpj': True, 'retencao_xml_dias': 30})
        expect(s == 201, f'tenant {s} {b}')
        erp = Client(P, {'X-Api-Key': b['api_key']})
        s, h = erp.call('POST', '/v1/webhooks', {'url': 'https://example.com/taxgateway-sintetico', 'eventos': ['document.audited']})
        expect(s == 201, f'webhook {s} {h}')
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        corpus = os.path.join(root, 'tests/golden/corpus-xml/v0')
        xml = open(os.path.join(corpus, sorted(f for f in os.listdir(corpus) if f.endswith('.xml'))[0]), 'rb').read()
        req = urllib.request.Request(P + '/v1/auditoria/xml?direction=OUTBOUND', data=xml, method='POST',
                                     headers={'X-Api-Key': b['api_key'], 'Idempotency-Key': str(uuid.uuid4()), 'Content-Type': 'application/xml'})
        with urllib.request.urlopen(req, timeout=60) as r:
            expect(r.status == 200, 'auditoria')
        t0 = time.time()
        d = None
        while time.time() - t0 < 60:
            lst = [x for x in erp.call('GET', '/v1/webhooks/deliveries')[1] if x['webhook_id'] == h['id']]
            if lst and lst[0]['attempts'] >= 1:
                d = lst[0]
                break
            time.sleep(1)
        expect(d is not None, 'nenhuma tentativa de entrega em 60 s')
        if d['status'] == 'DELIVERED':
            expect(erp.call('POST', f'/v1/webhooks/deliveries/{d["id"]}/retry')[0] == 404, 'entregue não reenvia')
            return f'destino aceitou (HTTP {d["last_status_code"]}); entrega não reenviada'
        expect(d['status'] in ('RETRYING', 'DEAD'), f'status {d}')
        expect(erp.call('POST', f'/v1/webhooks/deliveries/{d["id"]}/retry')[0] == 202, 'retry manual')
        t1 = time.time()
        while time.time() - t1 < 60:
            again = next(x for x in erp.call('GET', '/v1/webhooks/deliveries')[1] if x['id'] == d['id'])
            if again['attempts'] > d['attempts']:
                break
            time.sleep(1)
        expect(again['attempts'] > d['attempts'], 'retry manual não gerou nova tentativa')
        return f"destino respondeu {d['last_status_code']}; {d['status']} → retry manual → tentativa {again['attempts']} ({again['status']})"
    webhook()

    ok = sum(1 for r in results if r[0])
    for passed, name, detail, dt in results:
        print(f"{'OK  ' if passed else 'FALHA'} {name} ({dt:.1f}s){' — ' + detail if detail else ''}")
    print(f'\n{ok}/{len(results)} passos aprovados')
    return 0 if ok == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
