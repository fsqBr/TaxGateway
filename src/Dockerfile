# syntax=docker/dockerfile:1.7
# Imagens da aplicação (D-404): um build, três alvos (api, workers, migrator). Runtime "chiseled" (sem shell, sem
# gerenciador de pacotes), usuário não-root, ReadyToRun. Contexto = raiz do repositório:
#   docker build -f src/Dockerfile --target api -t taxgateway/api:dev .
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
# "-extra": inclui tzdata (America/Sao_Paulo vem do sistema) e ICU.
ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra

FROM ${SDK_IMAGE} AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig TaxGateway.slnx ./
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
