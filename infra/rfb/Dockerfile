# syntax=docker/dockerfile:1.7
# Imagem de um motor oficial da RFB (regime regular ou Simples Nacional), construída a partir
# de tag + commit verificados. O código oficial não é alterado (SDD §2.1, regra 3).
# Uso: ver infra/rfb/build.ps1 (lê versions.json).

# O repositório não versiona .mvn/wrapper/maven-wrapper.jar: usamos o Maven da imagem oficial.
ARG JDK_IMAGE=maven:3.9-eclipse-temurin-25
ARG JRE_IMAGE=eclipse-temurin:25-jre

# ---------------------------------------------------------------------------------------------
# Stage 1: banco SQLite gerado pelas migrations Flyway do repositório do REGIME REGULAR.
#          Os dois motores usam o mesmo banco (README do Simples, passo 3).
# ---------------------------------------------------------------------------------------------
FROM ${JDK_IMAGE} AS db
# D-Q03: pipe com falha propagada (o hash do banco vira label de rastreabilidade; vazio não pode passar).
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
ARG REGULAR_TAG
ARG REGULAR_SHA
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
# D-Q04: busca pelo COMMIT fixado em versions.json, não pela tag — a RFB já republicou a tag 1.7.2 em outro commit
# (25/11/2026); clonar por tag tornaria impossível reconstruir o bundle homologado.
RUN git init -q /src \
 && git -C /src fetch -q --depth 1 https://github.com/ReceitaFederal/calculadora-rtc-regime-regular "${REGULAR_SHA}" \
 && git -C /src checkout -q --detach FETCH_HEAD \
 && test "$(git -C /src rev-parse HEAD)" = "${REGULAR_SHA}"
WORKDIR /src
RUN --mount=type=cache,target=/root/.m2 \
    mkdir -p ./calculadora/db \
 && mvn flyway:migrate -Dflyway.configFiles=./flyway/flyway-pro.conf --batch-mode --errors -q \
 && sha256sum calculadora/db/calculadora-pro.db | cut -d' ' -f1 > /db.sha256 \
 && grep -Eq '^[0-9a-f]{64}$' /db.sha256

# ---------------------------------------------------------------------------------------------
# Stage 2: build do motor. Testes oficiais rodam em job separado (exigem o banco).
# ---------------------------------------------------------------------------------------------
FROM ${JDK_IMAGE} AS build
ARG ENGINE_REPO
ARG ENGINE_TAG
ARG ENGINE_SHA
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
RUN git init -q /app \
 && git -C /app fetch -q --depth 1 "https://github.com/${ENGINE_REPO}" "${ENGINE_SHA}" \
 && git -C /app checkout -q --detach FETCH_HEAD \
 && test "$(git -C /app rev-parse HEAD)" = "${ENGINE_SHA}"
WORKDIR /app
RUN --mount=type=cache,target=/root/.m2 \
    mvn -DskipTests --batch-mode -q package

# ---------------------------------------------------------------------------------------------
# Stage opcional "test-reports": testes oficiais do motor contra o banco gerado (Fase 0, D-006).
# Falhas não interrompem o build: os relatórios do surefire são exportados e comparados com a
# lista de exceções versionada (infra/rfb/test-exceptions/<motor>.txt) por check_engine_tests.py.
#   docker build --target test-reports --output type=local,dest=out/regular ...
# ---------------------------------------------------------------------------------------------
FROM build AS test
COPY --from=db /src/calculadora/db/calculadora-pro.db calculadora/db/calculadora-pro.db
RUN --mount=type=cache,target=/root/.m2     mvn --batch-mode -Dmaven.test.failure.ignore=true test > /test.log 2>&1 ;     mkdir -p /reports && cp /test.log /reports/ && cp -r target/surefire-reports/. /reports/ 2>/dev/null ; true

FROM scratch AS test-reports
COPY --from=test /reports /

# ---------------------------------------------------------------------------------------------
# Stage 3: runtime mínimo, usuário não-root.
# ---------------------------------------------------------------------------------------------
FROM ${JRE_IMAGE}
ARG ENGINE_JAR
ARG BUNDLE
ARG ENGINE_TAG
ARG ENGINE_SHA
ARG DB_SHA256=unknown
RUN useradd -r -u 10001 rtc
# A URL do datasource é relativa ao diretório de trabalho: ./calculadora/db/calculadora-pro.db?mode=ro
WORKDIR /opt/rtc
COPY --from=build /app/target/${ENGINE_JAR} app.jar
COPY --from=db /src/calculadora/db/calculadora-pro.db calculadora/db/calculadora-pro.db
COPY --from=db /db.sha256 db.sha256
LABEL rtc.bundle="${BUNDLE}" rtc.tag="${ENGINE_TAG}" rtc.commit="${ENGINE_SHA}" rtc.db_sha256="${DB_SHA256}"
USER 10001
ENV JAVA_TOOL_OPTIONS="-XX:MaxRAMPercentage=75"
ENTRYPOINT ["java", "-Dspring.profiles.active=offline", "-jar", "app.jar"]
