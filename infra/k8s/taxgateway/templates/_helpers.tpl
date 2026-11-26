{{- define "tg.labels" -}}
app.kubernetes.io/part-of: taxgateway
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
rtc.bundle: {{ .Values.engines.bundle | quote }}
{{- end }}

{{- define "tg.secure" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
runAsNonRoot: true
capabilities: { drop: ["ALL"] }
seccompProfile: { type: RuntimeDefault }
{{- end }}

{{/* Contêiner de motor RFB: root somente leitura, /tmp com exec (sqlite-jdbc), sem egress (SDD §2.1). */}}
{{- define "tg.engine" -}}
- name: rtc-{{ .name }}
  image: "{{ .root.Values.image.registry }}/{{ .e.image }}:{{ .e.tag }}"
  imagePullPolicy: {{ .root.Values.image.pullPolicy }}
  ports:
    - { name: {{ .name }}, containerPort: {{ .e.port }} }
    - { name: {{ .name }}-mgmt, containerPort: {{ .e.mgmtPort }} }
  env:
    - { name: VERSAO_OFFLINE_URL, value: "http://127.0.0.1:9/disabled" }
    - { name: VERSAO_URL, value: "http://127.0.0.1:9/disabled" }
    - { name: SPRING_DATASOURCE_HIKARI_MAXIMUM_POOL_SIZE, value: {{ .root.Values.engines.hikariPoolSize | quote }} }
    - { name: JAVA_TOOL_OPTIONS, value: {{ .root.Values.engines.javaToolOptions | quote }} }
  resources: {{- toYaml .root.Values.engines.resources | nindent 4 }}
  readinessProbe: { httpGet: { path: /health, port: {{ .e.mgmtPort }} }, periodSeconds: 5 }
  livenessProbe: { httpGet: { path: /health, port: {{ .e.mgmtPort }} }, initialDelaySeconds: 60, periodSeconds: 20 }
  securityContext: {{- include "tg.secure" . | nindent 4 }}
    runAsUser: 10001
  volumeMounts:
    - { name: tmp-{{ .name }}, mountPath: /tmp }
{{- end }}
