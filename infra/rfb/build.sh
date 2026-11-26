#!/usr/bin/env bash
# Equivalente Linux/CI do build.ps1 (SDD §2.2; D-009): constrói as imagens dos motores a partir de versions.json,
# grava a label rtc.db_sha256 e falha se os dois motores do bundle não usarem o mesmo banco.
# Uso: infra/rfb/build.sh [registry_prefix]   (ex.: ghcr.io/org/taxgateway; padrão: taxgateway)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
prefix="${1:-taxgateway}"
py=python3; "$py" -c 1 >/dev/null 2>&1 || py=python
field() { "$py" -c "import json,sys; v=json.load(sys.stdin); print(v$1)" < "$here/versions.json"; }
bundle="$(field "['bundle']")"

# 1) banco SQLite gerado uma vez (stage db) -> sha256 que vira label das duas imagens
db_sha="$(DOCKER_BUILDKIT=1 docker build "$here" -q --file "$here/Dockerfile" --target db \
  --build-arg "REGULAR_TAG=$(field "['regular']['tag']")" --build-arg "REGULAR_SHA=$(field "['regular']['commit']")" \
  | MSYS_NO_PATHCONV=1 xargs -I{} docker run --rm --entrypoint cat {} /db.sha256)"   # MSYS_NO_PATHCONV: Git Bash no Windows
echo "db_sha256=$db_sha"

for engine in regular simples; do
  tag="$(field "['$engine']['tag']")"
  DOCKER_BUILDKIT=1 docker build "$here" --file "$here/Dockerfile" \
    --build-arg "REGULAR_TAG=$(field "['regular']['tag']")" --build-arg "REGULAR_SHA=$(field "['regular']['commit']")" \
    --build-arg "ENGINE_REPO=$(field "['$engine']['repo']")" --build-arg "ENGINE_TAG=$tag" \
    --build-arg "ENGINE_SHA=$(field "['$engine']['commit']")" --build-arg "ENGINE_JAR=$(field "['$engine']['jar']")" \
    --build-arg "BUNDLE=$bundle" --build-arg "DB_SHA256=$db_sha" \
    --tag "$prefix/rtc-$engine:$tag" --tag "$prefix/rtc-$engine:bundle-$bundle"
  inside="$(MSYS_NO_PATHCONV=1 docker run --rm --entrypoint cat "$prefix/rtc-$engine:$tag" /opt/rtc/db.sha256)"
  [ "$inside" = "$db_sha" ] || { echo "db_sha256 diverge em $engine: $inside != $db_sha"; exit 1; }
done
echo "OK: bundle $bundle, db_sha256 $db_sha nas duas imagens"
