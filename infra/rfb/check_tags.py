"""Detecção de tags novas dos motores da RFB (SDD §2.3 passo 1; D-401).

Consulta GET /repos/{repo}/tags, escolhe a maior versão semântica (aceita "1.7.2" e "v1.7.2") e compara com
infra/rfb/versions.json. Com --write, atualiza tag + commit SHA e gera um bundle novo (AAAA.MM.N), para o
workflow abrir o PR. Sem dependências externas (só a biblioteca padrão).

Também acusa **tag republicada** (mesmo nome, outro commit — a RFB fez isso com a 1.7.2 em 25/11/2026; D-Q04):
vira uma atualização do tipo "retag", que passa pelo mesmo fluxo (imagens candidatas, regressão, PR com aprovação).
Tag fixada que sumiu do repositório gera aviso ("tag_ausente"), sem alterar nada.

Uso: python infra/rfb/check_tags.py [--write] [--versions caminho] [--tags-json motor=arquivo ...] | --self-test
Saída (stdout): JSON {"changed": bool, "bundle": ..., "updates": {...}}. Exit 0 sempre que a consulta funcionar.
"""
import argparse
import datetime
import json
import os
import re
import sys
import urllib.request

SEMVER = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)$")


def parse(tag: str):
    m = SEMVER.match(tag.strip())
    return tuple(int(x) for x in m.groups()) if m else None


def latest(tags: list[dict]) -> dict | None:
    """Maior versão semântica; tags fora do padrão (ex.: 'teste', 'v1.0-rc') são ignoradas."""
    valid = [(parse(t["name"]), t) for t in tags if parse(t["name"]) is not None]
    return max(valid, key=lambda x: x[0])[1] if valid else None


def fetch_tags(repo: str) -> list[dict]:
    req = urllib.request.Request(f"https://api.github.com/repos/{repo}/tags?per_page=100",
                                 headers={"Accept": "application/vnd.github+json", "User-Agent": "taxgateway-ci"})
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def compare(current: dict, tags: list[dict]) -> tuple[dict | None, str | None]:
    """(atualização, aviso) para um motor: versão maior > tag republicada > nada."""
    top = latest(tags)
    if top is not None and parse(top["name"]) > parse(current["tag"]):
        return {"kind": "nova_versao", "from": current["tag"], "to": top["name"].lstrip("v"),
                "tag_ref": top["name"], "commit": top["commit"]["sha"]}, None
    same = next((t for t in tags if t["name"] == current["tag"]), None)
    if same is None:
        return None, f"tag_ausente: {current['tag']} não existe mais no repositório (commit fixado {current['commit'][:12]})"
    if same["commit"]["sha"] != current["commit"]:
        return {"kind": "retag", "from": current["tag"], "to": current["tag"], "tag_ref": current["tag"],
                "commit": same["commit"]["sha"], "commit_anterior": current["commit"]},             f"retag: {current['tag']} foi republicada ({current['commit'][:12]} -> {same['commit']['sha'][:12]})"
    return None, None


def self_test() -> int:
    t = lambda name, sha: {"name": name, "commit": {"sha": sha}}
    cur = {"tag": "1.7.2", "commit": "a" * 40}
    assert compare(cur, [t("1.7.2", "a" * 40), t("1.6.1", "c" * 40)]) == (None, None), "sem mudança"
    u, w = compare(cur, [t("1.7.2", "b" * 40)])
    assert u["kind"] == "retag" and u["commit"] == "b" * 40 and u["to"] == "1.7.2" and w.startswith("retag"), "retag"
    u, _ = compare(cur, [t("1.7.2", "b" * 40), t("v1.8.0", "d" * 40), t("teste", "e" * 40)])
    assert u["kind"] == "nova_versao" and u["to"] == "1.8.0" and u["tag_ref"] == "v1.8.0", "versão nova vence retag"
    u, w = compare(cur, [t("1.6.1", "c" * 40)])
    assert u is None and w.startswith("tag_ausente"), "tag sumiu"
    assert next_bundle("2026.11.2", datetime.date(2026, 11, 25)) == "2026.11.3"
    assert next_bundle("2026.11.2", datetime.date(2026, 12, 1)) == "2026.12.1"
    print("check_tags self-test ok")
    return 0


def next_bundle(current: str, today: datetime.date) -> str:
    prefix = f"{today:%Y.%m}"
    if current.startswith(prefix + "."):
        return f"{prefix}.{int(current.rsplit('.', 1)[1]) + 1}"
    return f"{prefix}.1"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--versions", default=os.path.join(os.path.dirname(__file__), "versions.json"))
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--tags-json", nargs="*", default=[], help="motor=arquivo com a resposta da API (testes)")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()

    with open(a.versions, encoding="utf-8") as f:
        v = json.load(f)
    overrides = dict(x.split("=", 1) for x in a.tags_json)

    updates, warnings = {}, []
    for engine in ("regular", "simples"):
        e = v[engine]
        if engine in overrides:
            with open(overrides[engine], encoding="utf-8") as f:
                tags = json.load(f)
        else:
            tags = fetch_tags(e["repo"])
        top = latest(tags)
        if top is None:
            continue
        updates_engine, warning = compare(e, tags)
        if updates_engine:
            updates[engine] = updates_engine
        if warning:
            warnings.append(f"{engine}: {warning}")

    result = {"changed": bool(updates), "bundle": v["bundle"], "updates": updates, "warnings": warnings}
    if updates and a.write:
        for engine, u in updates.items():
            v[engine]["tag"] = u["tag_ref"]          # a tag exata do repositório (o clone usa --branch <tag>)
            v[engine]["commit"] = u["commit"]
        v["bundle"] = next_bundle(v["bundle"], datetime.date.today())
        result["bundle"] = v["bundle"]
        with open(a.versions, "w", encoding="utf-8", newline="\n") as f:
            json.dump(v, f, indent=2, ensure_ascii=False)
            f.write("\n")
    json.dump(result, sys.stdout, ensure_ascii=False)
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
