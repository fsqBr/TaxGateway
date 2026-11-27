# App web (D-W03/D-W05/D-W11). Dois alvos do mesmo código. Contexto = RAIZ do repositório (VERSION e CHANGELOG.md):
#   docker build -f web/Dockerfile --target public     --build-arg BUILD_COMMIT=$(git rev-parse HEAD) -t taxgateway/web .
#   docker build -f web/Dockerfile --target backoffice --build-arg BUILD_COMMIT=$(git rev-parse HEAD) -t taxgateway/web-backoffice .
# Imagens base por digest (D-P30); o Dependabot abre PR com as atualizações.
FROM node:22.12.0-alpine@sha256:51eff88af6dff26f59316b6e356188ffa2c422bd3c3b76f2556a2e7e89d080bd AS build
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY VERSION CHANGELOG.md /src/
COPY web/ ./
ARG BUILD_COMMIT=""
ENV BUILD_COMMIT=${BUILD_COMMIT}
RUN npm test && npx tsc -b \
 && npx vite build --outDir /out/public \
 && npx vite build --mode backoffice --outDir /out/backoffice \
 && rm -rf /out/backoffice/ajuda

FROM nginxinc/nginx-unprivileged:1.31.7-alpine@sha256:b5047b9b76845eac124358373c6852377c52e4b05834a47647e955dd91cb8f1e AS base
ENV API_UPSTREAM=api:8000
USER root
COPY web/nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY web/nginx/proxy.conf /etc/nginx/snippets/proxy.conf.in
# proxy.conf usa ${API_UPSTREAM}: renderizado no start (o template do site é renderizado pela própria imagem).
COPY --chmod=755 web/nginx/05-proxy-snippet.sh /docker-entrypoint.d/05-proxy-snippet.sh
RUN chown -R 101:101 /etc/nginx/snippets && rm -f /etc/nginx/conf.d/default.conf
USER 101
EXPOSE 8080

FROM base AS public
COPY web/nginx/public.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /out/public /usr/share/nginx/html

FROM base AS backoffice
COPY web/nginx/backoffice.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /out/backoffice /usr/share/nginx/html
