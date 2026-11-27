"""Relatório de regressão fiscal entre dois bundles (SDD §2.3 passo 3; D-403).

Roda as requisições do corpus golden (tests/golden/corpus/v0) no motor ATUAL e no CANDIDATO e gera um diff por
item: estado, CST/cClassTrib aceitos, base, alíquotas, valores e erros. O Markdown vai anexado ao PR de
atualização; o revisor fiscal aprova lendo este diff (passo 5). Sem dependências externas.

Uso:
  python infra/rfb/regression_report.py --base http://atual:8080/api/ --candidate http://novo:8080/api/ \
      [--corpus tests/golden/corpus/v0] [--out relatorio.md] [--fail-on-diff]
  python infra/rfb/regression_report.py --self-test
"""
import argparse
import glob
import json
import os
import sys
import urllib.error
import urllib.request

IGNORED = {"memoriaCalculo"}   # texto explicativo: muda de redação sem mudar o cálculo


def post(base: str, body: dict) -> tuple[int, dict]:
    req = urllib.request.Request(base.rstrip("/") + "/calculadora/observabilidade/regime-geral",
                                 data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def leaves(node, path=""):
    """Achata um JSON em {caminho: valor}, ignorando campos de texto livre."""
    out = {}
    if isinstance(node, dict):
        for k, v in node.items():
            if k not in IGNORED:
                out.update(leaves(v, f"{path}/{k}"))
    elif isinstance(node, list):
        for i, v in enumerate(node):
            out.update(leaves(v, f"{path}[{i}]"))
    else:
        out[path] = node
    return out


def diff_response(a: tuple[int, dict], b: tuple[int, dict]) -> list[str]:
    """Diferenças relevantes entre duas respostas do motor (lista vazia = iguais)."""
    (sa, ba), (sb, bb) = a, b
    if sa != sb:
        return [f"HTTP {sa} → {sb}"]
    if sa != 200:
        ea = sorted(e.get("code", "") for e in ba.get("errors", []))
        eb = sorted(e.get("code", "") for e in bb.get("errors", []))
        return [] if ea == eb else [f"erros globais {ea} → {eb}"]
    out = []
    ia = {o["nObj"]: o for o in ba.get("objetos", [])}
    ib = {o["nObj"]: o for o in bb.get("objetos", [])}
    for n in sorted(set(ia) | set(ib)):
        oa, ob = ia.get(n), ib.get(n)
        if oa is None or ob is None:
            out.append(f"item {n}: {'ausente no atual' if oa is None else 'ausente no candidato'}")
            continue
        if oa.get("estadoItem") != ob.get("estadoItem"):
            out.append(f"item {n}: estado {oa.get('estadoItem')} → {ob.get('estadoItem')}")
        ca = sorted(e["code"] for e in oa.get("erros", []))
        cb = sorted(e["code"] for e in ob.get("erros", []))
        if ca != cb:
            out.append(f"item {n}: erros {ca} → {cb}")
        la, lb = leaves(oa.get("tribCalc", {})), leaves(ob.get("tribCalc", {}))
        for k in sorted(set(la) | set(lb)):
            if la.get(k) != lb.get(k):
                out.append(f"item {n}: {k} {la.get(k, '∅')} → {lb.get(k, '∅')}")
    ta, tb = leaves(ba.get("total", {})), leaves(bb.get("total", {}))
    for k in sorted(set(ta) | set(tb)):
        if ta.get(k) != tb.get(k):
            out.append(f"total: {k} {ta.get(k, '∅')} → {tb.get(k, '∅')}")
    return out


def summary(bundle: str, total: int, changed: list) -> dict:
    """Resumo para o aviso às contas (D-R31): lido pelo RateSyncWorker em fiscal-bundles/<bundle>.json."""
    return {
        "bundle": bundle,
        "casos": total,
        "com_diferenca": len(changed),
        "destaques": [desc or cid for cid, desc, _ in changed[:5]],
    }


def run(base: str, candidate: str, corpus: str, summary_out: dict | None = None) -> tuple[str, int]:
    files = sorted(glob.glob(os.path.join(corpus, "*.json")))
    changed = []
    for f in files:
        with open(f, encoding="utf-8") as fh:
            case = json.load(fh)
        d = diff_response(post(base, case["engine_request"]), post(candidate, case["engine_request"]))
        if d:
            changed.append((case["id"], case.get("descricao", ""), d))
    if summary_out is not None:
        summary_out.update(summary(summary_out.get("bundle", ""), len(files), changed))
    lines = [
        "# Relatório de regressão fiscal",
        "",
        f"* Atual: `{base}`  ·  Candidato: `{candidate}`",
        f"* Casos do corpus: **{len(files)}** · com diferença: **{len(changed)}**",
        "",
    ]
    if not changed:
        lines.append("Nenhuma diferença de cálculo entre os bundles.")
    for cid, desc, d in changed:
        lines.append(f"## {cid}")
        lines.append(f"_{desc}_")
        lines.append("")
        lines.extend(f"* {x}" for x in d[:50])
        if len(d) > 50:
            lines.append(f"* … mais {len(d) - 50} diferenças")
        lines.append("")
    return "\n".join(lines) + "\n", len(changed)


def self_test() -> int:
    ok = (200, {"objetos": [{"nObj": 1, "estadoItem": "CALCULADO", "tribCalc": {"IBSCBS": {"gIBSCBS": {"vBC": "10.00", "gCBS": {"pCBS": "0.90", "vCBS": "0.09"}}, "memoriaCalculo": {"cbs": "a"}}}}],
                "total": {"tribCalc": {"IBSCBSTot": {"gCBS": {"vCBS": "0.09"}}}}})
    same_text = json.loads(json.dumps(ok))
    same_text[1]["objetos"][0]["tribCalc"]["IBSCBS"]["memoriaCalculo"]["cbs"] = "texto novo"
    rate = json.loads(json.dumps(ok))
    rate[1]["objetos"][0]["tribCalc"]["IBSCBS"]["gIBSCBS"]["gCBS"] = {"pCBS": "1.00", "vCBS": "0.10"}
    rate[1]["total"]["tribCalc"]["IBSCBSTot"]["gCBS"]["vCBS"] = "0.10"
    rejected = (200, {"objetos": [{"nObj": 1, "estadoItem": "INCONSISTENCIA_ENTRADA", "erros": [{"code": "REG-005"}]}]})
    assert diff_response(ok, tuple(same_text)) == [], "memoriaCalculo não pode gerar diferença"
    d = diff_response(ok, tuple(rate))
    assert any("pCBS 0.90 → 1.00" in x for x in d) and any(x.startswith("total:") for x in d), d
    d = diff_response(ok, rejected)
    assert any("estado CALCULADO → INCONSISTENCIA_ENTRADA" in x for x in d) and any("REG-005" in x for x in d), d
    assert diff_response((422, {"errors": [{"code": "X"}]}), (200, {})) == ["HTTP 422 → 200"]
    s = summary("2026.12.1", 158, [("c1", "Venda interestadual", ["x"]), ("c2", "", ["y"])])
    assert s == {"bundle": "2026.12.1", "casos": 158, "com_diferenca": 2, "destaques": ["Venda interestadual", "c2"]}, s
    print("self-test ok")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base")
    ap.add_argument("--candidate")
    ap.add_argument("--corpus", default=os.path.join(os.path.dirname(__file__), "..", "..", "tests", "golden", "corpus", "v0"))
    ap.add_argument("--out")
    ap.add_argument("--summary-json", help="grava o resumo (casos, com_diferenca, destaques) para o aviso às contas")
    ap.add_argument("--bundle", default="", help="bundle candidato, gravado no resumo")
    ap.add_argument("--fail-on-diff", action="store_true")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    resumo = {"bundle": a.bundle}
    report, changed = run(a.base, a.candidate, a.corpus, resumo)
    if a.summary_json:
        os.makedirs(os.path.dirname(a.summary_json) or ".", exist_ok=True)
        with open(a.summary_json, "w", encoding="utf-8") as f:
            json.dump(resumo, f, ensure_ascii=False, indent=2)
            f.write("\n")
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(report)
    else:
        sys.stdout.write(report)
    return 1 if changed and a.fail_on_diff else 0


if __name__ == "__main__":
    sys.exit(main())
