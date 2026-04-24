FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
USER $APP_UID

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
COPY ["src/exchange/Exchange.Infrastructure.RabbitMq/Bifrost.Exchange.Infrastructure.RabbitMq.csproj", "src/exchange/Exchange.Infrastructure.RabbitMq/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Events/Bifrost.Contracts.Events.csproj", "contracts/dotnet/Bifrost.Contracts.Events/"]
COPY ["src/quoter/Bifrost.Quoter.csproj", "src/quoter/"]
RUN dotnet restore "src/quoter/Bifrost.Quoter.csproj"
COPY contracts/ contracts/
COPY src/ src/
WORKDIR "/src/src/quoter"
RUN dotnet build "Bifrost.Quoter.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bifrost.Quoter.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# D-19: scenarios seeded into the image so appsettings Quoter:ScenarioPath
# (=/scenarios/calm-drift.json) resolves on cold boot without a required volume.
# docker-compose.yml additionally bind-mounts the repo scenarios dir for dev swap.
COPY --from=build /src/src/quoter/scenarios/ /scenarios/
ENTRYPOINT ["dotnet", "Bifrost.Quoter.dll"]
