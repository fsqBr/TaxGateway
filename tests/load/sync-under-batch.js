// Gate de capacidade (SDD §12 Fase 6; D-608): p95 de /v1/calculo/simular (20 itens) de um tenant, com taxa fixa,
// medido com e sem o lote pesado de OUTRO tenant em paralelo. A base de cálculo varia por requisição para
// escapar do cache determinístico (mede o motor de verdade).
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.API_URL || 'http://api:8000';
const KEY = __ENV.API_KEY;
const RATE = parseInt(__ENV.RATE || '30', 10);
const DURATION = __ENV.DURATION || '60s';

export const options = {
  scenarios: {
    sync: { executor: 'constant-arrival-rate', rate: RATE, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 50, maxVUs: 200 },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: { http_req_failed: ['rate<0.01'] },
};

export default function () {
  const itens = [];
  for (let i = 0; i < 20; i++) {
    itens.push({
      numero: i + 1, ncm: '84713012', cst_ibs_cbs: '000', c_class_trib: '000001',
      valor_base_calculo: Math.round((100 + Math.random() * 9000) * 100) / 100,
    });
  }
  const body = JSON.stringify({
    cnpj_emitente: '11222333000181', regime_tributario: 'REGIME_REGULAR',
    data_fato_gerador: '2027-03-10T10:00:00-03:00', municipio_ibge: 3550308, uf: 'SP', itens,
  });
  const r = http.post(`${BASE}/v1/calculo/simular`, body, { headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY } });
  check(r, { '200': (x) => x.status === 200 });
}
