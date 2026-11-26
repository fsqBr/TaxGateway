#!/bin/sh
# Renderiza o snippet de proxy em conf.d (tmpfs no modo read-only). Extensão .inc: não é incluída no nível http.
set -e
envsubst '${API_UPSTREAM}' < /etc/nginx/snippets/proxy.conf.in > /etc/nginx/conf.d/proxy.inc
