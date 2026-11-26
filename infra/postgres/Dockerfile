# PostgreSQL 18 + pg_partman (SDD §9, decisão D-102). A imagem oficial não traz o pg_partman.
ARG PG_IMAGE=postgres:18
FROM ${PG_IMAGE}
ARG PARTMAN_VERSION=5.5.1-1.pgdg13+1
RUN apt-get update \
 && apt-get install -y --no-install-recommends "postgresql-18-partman=${PARTMAN_VERSION}" \
 && rm -rf /var/lib/apt/lists/*
# Bootstrap de cluster (roles + extensões). Em produção é executado uma vez pelo DBA (decisão D-104).
COPY init/ /docker-entrypoint-initdb.d/
