# App web (D-W03/D-W05). Dois alvos do mesmo código:
#   docker build --target public     -t taxgateway/web .
#   docker build --target backoffice -t taxgateway/web-backoffice .
FROM node:22.12.0-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm test && npx tsc -b \
 && npx vite build --outDir /out/public \
 && npx vite build --mode backoffice --outDir /out/backoffice

FROM nginxinc/nginx-unprivileged:1.27.3-alpine AS base
ENV API_UPSTREAM=api:8000
USER root
COPY nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY nginx/proxy.conf /etc/nginx/snippets/proxy.conf.in
# proxy.conf usa ${API_UPSTREAM}: renderizado no start (o template do site é renderizado pela própria imagem).
COPY --chmod=755 nginx/05-proxy-snippet.sh /docker-entrypoint.d/05-proxy-snippet.sh
RUN chown -R 101:101 /etc/nginx/snippets && rm -f /etc/nginx/conf.d/default.conf
USER 101
EXPOSE 8080

FROM base AS public
COPY nginx/public.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /out/public /usr/share/nginx/html

FROM base AS backoffice
COPY nginx/backoffice.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /out/backoffice /usr/share/nginx/html
