"""Compara os relatórios do surefire de um motor com a lista de exceções versionada (D-006).

Falha (exit 1) quando:
  * algum teste falha e não está na lista de exceções (regressão ou bundle novo quebrado);
  * nenhum teste rodou (build de testes quebrado não pode passar em silêncio).
Exceções que voltaram a passar só geram aviso: devem ser removidas da lista no mesmo PR.

Formato da lista: uma linha por teste, "pacote.Classe#metodo", com comentário opcional após " # ".
"""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def load_exceptions(path: Path) -> set[str]:
    if not path.exists():
        return set()
    items = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.split(" # ", 1)[0].strip()
        if line and not line.startswith("#"):
            items.add(line)
    return items


def main() -> int:
    reports, exceptions_file = Path(sys.argv[1]), Path(sys.argv[2])
    exceptions = load_exceptions(exceptions_file)
    total = skipped = 0
    failed: dict[str, str] = {}
    for f in sorted(reports.glob("TEST-*.xml")):
        root = ET.parse(f).getroot()
        for case in root.iter("testcase"):
            total += 1
            name = f"{case.get('classname')}#{case.get('name')}"
            if case.find("skipped") is not None:
                skipped += 1
            problem = case.find("failure")
            if problem is None:
                problem = case.find("error")
            if problem is not None:
                msg = (problem.get("message") or problem.get("type") or "").splitlines()
                failed[name] = (msg[0] if msg else "")[:160]

    new = sorted(set(failed) - exceptions)
    fixed = sorted(exceptions - set(failed))
    print(f"testes: {total}  falhas: {len(failed)}  ignorados: {skipped}  exceções listadas: {len(exceptions)}")
    for n in fixed:
        print(f"AVISO: exceção voltou a passar (remover da lista): {n}")
    for n in new:
        print(f"FALHA NOVA: {n}  -> {failed[n]}")
    if total == 0:
        print("ERRO: nenhum teste executado (ver test.log)")
        return 1
    return 1 if new else 0


if __name__ == "__main__":
    sys.exit(main())
