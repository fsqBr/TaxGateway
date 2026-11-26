# syntax=docker/dockerfile:1.7
# Imagens da aplicação (D-404): um build, três alvos (api, workers, migrator). Runtime "chiseled" (sem shell, sem
# gerenciador de pacotes), usuário não-root, ReadyToRun. Contexto = raiz do repositório:
#   docker build -f src/Dockerfile --target api -t taxgateway/api:dev .
# D-P30: imagens base por DIGEST (a tag é só leitura humana); o Dependabot abre PR quando a Microsoft publica correções.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d
# "-extra": inclui tzdata (America/Sao_Paulo vem do sistema) e ICU.
ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:c8dd08a50d8ffd19bd8da72edee05a7ac15b486a5b1869cf46fdb370bf1fbc76
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:b003a2c0159d1791db7c310a90183fa3348d45dec848f665d8bd572ecc701a83

FROM ${SDK_IMAGE} AS build
WORKDIR /repo
ARG BUILD_COMMIT=""
ENV BUILD_COMMIT=${BUILD_COMMIT}
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig TaxGateway.slnx VERSION ./
COPY src/ src/
COPY infra/xsd-schemas/ infra/xsd-schemas/
COPY infra/icp-brasil/ infra/icp-brasil/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TaxGateway.Api -c Release -o /out/api --no-self-contained \
 && dotnet publish src/TaxGateway.Workers -c Release -o /out/workers --no-self-contained \
 && dotnet publish src/TaxGateway.Migrator -c Release -o /out/migrator --no-self-contained -p:PublishReadyToRun=false

FROM ${ASPNET_IMAGE} AS api
WORKDIR /app
COPY --from=build /out/api .
USER app
ENV ASPNETCORE_URLS="http://+:8000;http://+:9464" DOTNET_gcServer=1
EXPOSE 8000 9464
ENTRYPOINT ["dotnet", "TaxGateway.Api.dll"]

# Workers referenciam a API (AuditService): precisam do runtime ASP.NET (D-607).
FROM ${ASPNET_IMAGE} AS workers
WORKDIR /app
COPY --from=build /out/workers .
USER app
ENTRYPOINT ["dotnet", "TaxGateway.Workers.dll"]

FROM ${RUNTIME_IMAGE} AS migrator
WORKDIR /app
COPY --from=build /out/migrator .
USER app
ENTRYPOINT ["dotnet", "TaxGateway.Migrator.dll"]
