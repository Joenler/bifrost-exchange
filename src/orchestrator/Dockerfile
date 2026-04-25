FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
USER $APP_UID
EXPOSE 5006
HEALTHCHECK --interval=5s --timeout=3s --retries=5 --start-period=10s \
    CMD test -f /tmp/bifrost-ready || exit 1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["build/BannedSymbols.txt", "build/"]
COPY ["src/common/Bifrost.Time/Bifrost.Time.csproj", "src/common/Bifrost.Time/"]
COPY ["src/contracts-internal/Bifrost.Contracts.Internal/Bifrost.Contracts.Internal.csproj", "src/contracts-internal/Bifrost.Contracts.Internal/"]
COPY ["src/exchange/Exchange.Domain/Bifrost.Exchange.Domain.csproj", "src/exchange/Exchange.Domain/"]
COPY ["src/exchange/Exchange.Application/Bifrost.Exchange.Application.csproj", "src/exchange/Exchange.Application/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Mc/Bifrost.Contracts.Mc.csproj", "contracts/dotnet/Bifrost.Contracts.Mc/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Round/Bifrost.Contracts.Round.csproj", "contracts/dotnet/Bifrost.Contracts.Round/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Events/Bifrost.Contracts.Events.csproj", "contracts/dotnet/Bifrost.Contracts.Events/"]
COPY ["src/orchestrator/Bifrost.Orchestrator.csproj", "src/orchestrator/"]
RUN dotnet restore "src/orchestrator/Bifrost.Orchestrator.csproj"
COPY contracts/ contracts/
COPY src/ src/
WORKDIR "/src/src/orchestrator"
RUN dotnet build "Bifrost.Orchestrator.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bifrost.Orchestrator.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# News library ship fixture - 1-entry baseline; Phase 11 populates the full
# library. Read once at orchestrator boot from the path resolved by
# OrchestratorOptions.NewsLibraryPath (default /app/config/news-library.json).
COPY config/ /app/config/

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /app/config/ ./config/
ENTRYPOINT ["dotnet", "Bifrost.Orchestrator.dll"]
