// Baseline do motor RFB isolado (SDD §16.4, Fase 0).
// Executar via: ./tests/load/run-baseline.ps1
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.RTC_URL || 'http://rtc-regular:8080/api/calculadora';
const ITEMS = parseInt(__ENV.ITEMS || '1', 10);
const VUS = parseInt(__ENV.VUS || '8', 10);
const DURATION = __ENV.DURATION || '30s';

// Itens válidos do docs/exemplos.md do repositório oficial (cesta básica com redução e serviço NBS).
const TEMPLATES = [
  { ncm: '10063021', cst: '200', cClassTrib: '200003', baseCalculo: 100.0, quantidade: 20, unidade: 'KG' },
  { nbs: '122011200', cst: '200', cClassTrib: '200028', baseCalculo: 1200.0 },
];

function payload(n) {
  const itens = [];
  for (let i = 0; i < n; i++) {
    const t = TEMPLATES[i % TEMPLATES.length];
    itens.push(Object.assign({ numero: i + 1 }, t, {
      aliquotasNominais: { cbs: 8.8, ibsEstadual: 0.05, ibsMunicipal: 0.05 },
    }));
  }
  return JSON.stringify({
    id: 'baseline', versao: '1.0.0',
    dhFatoGerador: '2027-03-10T10:00:00-03:00',
    municipio: 4314902, uf: 'RS', itens,
  });
}

const BODY = payload(ITEMS);
const PARAMS = { headers: { 'Content-Type': 'application/json' } };

export const options = {
  scenarios: {
    warmup: { executor: 'constant-vus', vus: VUS, duration: '15s', gracefulStop: '0s', tags: { phase: 'warmup' } },
    measure: { executor: 'constant-vus', vus: VUS, duration: DURATION, startTime: '15s', tags: { phase: 'measure' } },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: { 'http_req_duration{phase:measure}': ['p(95)<100000'] }, // força o resumo por fase
};

export default function () {
  const r = http.post(`${BASE}/observabilidade/regime-geral`, BODY, PARAMS);
  check(r, { '200': (x) => x.status === 200 });
}
