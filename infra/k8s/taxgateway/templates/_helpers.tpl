{{- define "tg.labels" -}}
app.kubernetes.io/part-of: taxgateway
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
rtc.bundle: {{ .Values.engines.bundle | quote }}
{{- end }}

{{/* Imagem da aplicação: por digest quando image.digests.<nome> vier do pipeline (D-P30), senão pela tag. */}}
{{- define "tg.image" -}}
{{- $d := index (.root.Values.image.digests | default dict) .name -}}
{{- if $d -}}{{ .root.Values.image.registry }}/{{ .name }}@{{ $d }}{{- else -}}{{ .root.Values.image.registry }}/{{ .name }}:{{ .root.Values.image.tag }}{{- end -}}
{{- end }}

{{- define "tg.secure" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
runAsNonRoot: true
capabilities: { drop: ["ALL"] }
seccompProfile: { type: RuntimeDefault }
{{- end }}

{{/*
Contêiner de motor RFB: root somente leitura, /tmp com exec (sqlite-jdbc), sem egress (SDD §2.1).
Como sidecar (D-P25) é um sidecar NATIVO (initContainer com restartPolicy Always, Kubernetes >= 1.29): sobe e fica
pronto ANTES da API (startupProbe) e só para DEPOIS dela — antes, no desligamento o motor podia morrer primeiro e as
requisições em voo davam 503.
*/}}
{{- define "tg.engine" -}}
- name: rtc-{{ .name }}
  {{- if .sidecar }}
  restartPolicy: Always
  {{- end }}
  image: "{{ .root.Values.image.registry }}/{{ .e.image }}{{ if .e.digest }}@{{ .e.digest }}{{ else }}:{{ .e.tag }}{{ end }}"
  imagePullPolicy: {{ .root.Values.image.pullPolicy }}
  ports:
    - { name: {{ .name }}, containerPort: {{ .e.port }} }
    - { name: {{ .name }}-mgmt, containerPort: {{ .e.mgmtPort }} }
  env:
    - { name: VERSAO_OFFLINE_URL, value: "http://127.0.0.1:9/disabled" }
    - { name: VERSAO_URL, value: "http://127.0.0.1:9/disabled" }
    - { name: SPRING_DATASOURCE_HIKARI_MAXIMUM_POOL_SIZE, value: {{ .root.Values.engines.hikariPoolSize | quote }} }
    - { name: JAVA_TOOL_OPTIONS, value: {{ .root.Values.engines.javaToolOptions | quote }} }
    {{- if .sidecar }}
    # Sidecar: a porta de cálculo só em localhost (só a API do pod chama); a de gestão segue no IP do pod para as probes.
    - { name: SERVER_ADDRESS, value: "127.0.0.1" }
    {{- end }}
  resources: {{- toYaml (.e.resources | default .root.Values.engines.resources) | nindent 4 }}
  {{- if .sidecar }}
  startupProbe: { httpGet: { path: /health, port: {{ .e.mgmtPort }} }, periodSeconds: 2, failureThreshold: 90 }
  {{- end }}
  readinessProbe: { httpGet: { path: /health, port: {{ .e.mgmtPort }} }, periodSeconds: 5 }
  livenessProbe: { httpGet: { path: /health, port: {{ .e.mgmtPort }} }, initialDelaySeconds: 60, periodSeconds: 20 }
  securityContext: {{- include "tg.secure" . | nindent 4 }}
    runAsUser: 10001
  volumeMounts:
    - { name: tmp-{{ .name }}, mountPath: /tmp }
{{- end }}

{{/*
Contêiner da API, comum ao Rollout público e ao Deployment api-admin (D-P29). Desligamento (D-P25): o preStop segura
10 s para o endpoint sair dos balanceadores antes do SIGTERM; o processo tem até o fim do grace period para terminar
as requisições em voo (o Kestrel para de aceitar e drena).
*/}}
{{- define "tg.apiContainer" -}}
- name: api
  image: {{ include "tg.image" (dict "root" .root "name" "api") | quote }}
  imagePullPolicy: {{ .root.Values.image.pullPolicy }}
  ports: [{ name: http, containerPort: {{ .root.Values.api.port }} }, { name: metrics, containerPort: {{ .root.Values.api.metricsPort }} }]
  envFrom: [{ secretRef: { name: {{ .secret }} } }]
  env:
    - { name: Engines__Bundle, value: {{ .root.Values.engines.bundle | quote }} }
    - { name: Engines__Regular__BaseUrl, value: {{ .regular | quote }} }
    - { name: Engines__Simples__BaseUrl, value: {{ .simples | quote }} }
    - { name: Xml__CrlSource, value: "shared" }          # LCR publicada pelos workers no bucket (D-P23)
    {{- range $i, $n := .root.Values.api.trustedProxyNetworks }}
    - { name: Http__TrustedProxyNetworks__{{ $i }}, value: {{ $n | quote }} }
    {{- end }}
  resources: {{- toYaml .resources | nindent 4 }}
  # Liveness não toca dependências; readiness só com parâmetros e cadastro do motor (D-112/D-218), sem o banco (D-P18).
  livenessProbe: { httpGet: { path: /health/live, port: http }, periodSeconds: 10 }
  readinessProbe: { httpGet: { path: /health/ready, port: http }, periodSeconds: 5, failureThreshold: 3 }
  lifecycle:
    preStop: { sleep: { seconds: {{ .root.Values.api.preStopSeconds }} } }
  securityContext: {{- include "tg.secure" . | nindent 4 }}
    runAsUser: 1654
  volumeMounts: [{ name: tmp-api, mountPath: /tmp }]
{{- end }}
