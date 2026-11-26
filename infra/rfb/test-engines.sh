#!/usr/bin/env bash
# Roda os testes oficiais de um motor contra o banco gerado no build (SDD §12, Fase 0; D-006)
# e compara as falhas com a lista de exceções versionada.
# Uso: infra/rfb/test-engines.sh regular|simples [dir_saida]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
engine="${1:?informe regular ou simples}"
out="${2:-$here/../../out/engine-tests/$engine}"
py=python3; "$py" -c 1 >/dev/null 2>&1 || py=python
# versions.json vai por stdin: no Windows o Python não entende caminhos /e/... do Git Bash.
field() { "$py" -c "import json,sys; v=json.load(sys.stdin); print(v$1)" < "$here/versions.json"; }

rm -rf "$out" && mkdir -p "$out"
DOCKER_BUILDKIT=1 docker build "$here" \
  --file "$here/Dockerfile" --target test-reports --output "type=local,dest=$out" \
  --build-arg "REGULAR_TAG=$(field "['regular']['tag']")" \
  --build-arg "REGULAR_SHA=$(field "['regular']['commit']")" \
  --build-arg "ENGINE_REPO=$(field "['$engine']['repo']")" \
  --build-arg "ENGINE_TAG=$(field "['$engine']['tag']")" \
  --build-arg "ENGINE_SHA=$(field "['$engine']['commit']")"

"$py" "$here/check_engine_tests.py" "$out" "$here/test-exceptions/$engine.txt"
