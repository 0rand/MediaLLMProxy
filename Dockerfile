# syntax=docker/dockerfile:1
# MediaLLMProxy — multi-stage build (SDK) → runtime image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore KineticLLM.sln && \
    dotnet build KineticLLM.sln -c Release --no-restore && \
    dotnet publish OAIPreRouter.Cli -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    RoutingOptions__ListenUrl=http://0.0.0.0:7071 \
    MultimodalOptions__Enabled=true
EXPOSE 7071
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fs http://localhost:7071/health || exit 1
ENTRYPOINT ["dotnet", "OAIPreRouter.Cli.dll"]
