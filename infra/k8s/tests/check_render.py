"""Testes de render do chart (D-P01/D-P02). Uso: helm template tg infra/k8s/taxgateway | python infra/k8s/tests/check_render.py

Falha se a topologia renderizada contradiz o que o código assume — o caso real: o pod da API sem o motor do Simples
(todo cálculo de CRT 1/2 e MEI daria 503 em Kubernetes, e o compose escondia isso).
"""
import re
import sys

import yaml

docs = [d for d in yaml.safe_load_all(sys.stdin) if d]
errors = []


def find(kind, name):
    return next((d for d in docs if d.get('kind') == kind and d['metadata']['name'] == name), None)


def env_of(container):
    return {e['name']: e.get('value') for e in container.get('env', [])}


def check_engines(pod_spec, owner):
    # Sidecars nativos (D-P25): initContainers com restartPolicy Always e startupProbe (prontos antes da API).
    sidecars = {c['name']: c for c in pod_spec.get('initContainers', []) if c.get('restartPolicy') == 'Always'}
    for n, c in sidecars.items():
        if 'startupProbe' not in c:
            errors.append(f'{owner}: sidecar {n} sem startupProbe (a API subiria antes do motor)')
    if any(c['name'].startswith('rtc-') for c in pod_spec['containers']):
        errors.append(f'{owner}: motor como contêiner comum (deve ser sidecar nativo: para depois da API)')
    containers = {c['name']: c for c in pod_spec['containers']}
    containers.update(sidecars)
    app = next((c for n, c in containers.items() if not n.startswith('rtc-')), None)
    env = env_of(app)
    for engine in ('Regular', 'Simples'):
        url = env.get(f'Engines__{engine}__BaseUrl')
        if not url:
            errors.append(f'{owner}: sem Engines__{engine}__BaseUrl (o adapter cairia no padrão e daria 503)')
            continue
        m = re.match(r'http://([^:/]+):(\d+)/', url)
        host, port = m.group(1), int(m.group(2))
        if host == '127.0.0.1':
            side = containers.get(f'rtc-{engine.lower()}')
            if side is None:
                errors.append(f'{owner}: {engine} em localhost mas sem sidecar rtc-{engine.lower()}')
            elif port not in [p['containerPort'] for p in side['ports']]:
                errors.append(f'{owner}: sidecar rtc-{engine.lower()} não expõe a porta {port}')
            elif env_of(side).get('SERVER_ADDRESS') != '127.0.0.1':
                errors.append(f'{owner}: sidecar rtc-{engine.lower()} escuta fora de localhost')
        elif find('Service', host) is None:
            errors.append(f'{owner}: {engine} aponta para o Service {host}, que não existe')


rollout = find('Rollout', 'api')
check_engines(rollout['spec']['template']['spec'], 'Rollout/api')
for d in docs:
    if d.get('kind') == 'Deployment' and d['metadata']['name'] in ('workers', 'api-admin'):
        check_engines(d['spec']['template']['spec'], f'Deployment/{d["metadata"]["name"]}')


def upstream_of(name):
    d = find('Deployment', name)
    if d is None:
        return None
    return next((e['value'] for c in d['spec']['template']['spec']['containers'] for e in c.get('env', []) if e['name'] == 'API_UPSTREAM'), None)


# Canário: o upstream do site precisa alcançar estáveis E canário (D-P02). O backoffice fala só com a api-admin (D-P29).
pub = upstream_of('web')
if pub is not None and not pub.startswith('api-all:'):
    errors.append(f'web: API_UPSTREAM={pub} — o Service estável não alcança o canário (use api-all)')
bo = upstream_of('web-backoffice')
if bo is not None and find('Deployment', 'api-admin') is not None and not bo.startswith('api-admin:'):
    errors.append(f'web-backoffice: API_UPSTREAM={bo} — o backoffice deve usar a api-admin (a API pública não tem /admin)')
api_all = find('Service', 'api-all')
if api_all is None or any(p['name'] == 'metrics' for p in api_all['spec']['ports']):
    errors.append('Service api-all ausente ou com porta metrics (coleta duplicada)')
analysis = find('AnalysisTemplate', 'api-slo')
for m in analysis['spec']['metrics']:
    q = m['provider']['prometheus']['query']
    if 'and on()' not in q or 'len(result) > 0' not in m['successCondition'] or 'failureCondition' not in m:
        errors.append(f'AnalysisTemplate {m["name"]}: sem guarda de volume mínimo / inconclusivo')

# Plano administrativo separado (D-P29): Secrets diferentes; só o backoffice entra na api-admin; entrada negada por padrão.
admin = find('Deployment', 'api-admin')
if admin is not None:
    def secrets_of(spec):
        return {r['secretRef']['name'] for c in spec['containers'] for r in c.get('envFrom', []) if 'secretRef' in r}
    pub_secrets = secrets_of(rollout['spec']['template']['spec'])
    adm_secrets = secrets_of(admin['spec']['template']['spec'])
    if pub_secrets & adm_secrets:
        errors.append(f'api e api-admin compartilham Secret {pub_secrets & adm_secrets} (a pública teria a credencial tg_admin)')
    pol = find('NetworkPolicy', 'api-admin-ingress')
    if pol is None:
        errors.append('api-admin sem NetworkPolicy de entrada')
    else:
        sources = [f for r in pol['spec']['ingress'] for f in r.get('from', [])]
        if not any(f.get('podSelector', {}).get('matchLabels', {}).get('app.kubernetes.io/name') == 'web-backoffice' for f in sources) \
                or any('podSelector' in f and f['podSelector'].get('matchLabels', {}).get('app.kubernetes.io/name') != 'web-backoffice' for f in sources):
            errors.append('api-admin-ingress: a entrada HTTP deve vir só do web-backoffice')
deny = find('NetworkPolicy', 'default-deny-ingress')
if deny is None or deny['spec'].get('podSelector') != {} or 'ingress' in deny['spec']:
    errors.append('sem NetworkPolicy default-deny de entrada no namespace')

# Desligamento (D-P25): API com preStop e grace period; workers com sonda e PDB.
for owner, spec in [('Rollout/api', rollout['spec']['template']['spec'])] + ([('Deployment/api-admin', admin['spec']['template']['spec'])] if admin else []):
    api_c = next(c for c in spec['containers'] if c['name'] == 'api')
    if 'preStop' not in api_c.get('lifecycle', {}):
        errors.append(f'{owner}: sem preStop (5xx a cada deploy/scale-in)')
    if spec.get('terminationGracePeriodSeconds', 30) <= api_c['lifecycle']['preStop'].get('sleep', {}).get('seconds', 0) + 20:
        errors.append(f'{owner}: grace period curto para preStop + drenagem')
workers = find('Deployment', 'workers')
wc = workers['spec']['template']['spec']['containers'][0]
if 'livenessProbe' not in wc:
    errors.append('Deployment/workers: sem livenessProbe')
if find('PodDisruptionBudget', 'workers') is None:
    errors.append('workers sem PodDisruptionBudget')

for e in errors:
    print('ERRO:', e)
print(f'{len(errors)} erro(s)')
sys.exit(1 if errors else 0)
