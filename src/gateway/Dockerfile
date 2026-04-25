FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
USER $APP_UID
EXPOSE 7007
ENV ASPNETCORE_URLS=http://0.0.0.0:7007

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["build/BannedSymbols.txt", "build/"]
COPY ["src/common/Bifrost.Time/Bifrost.Time.csproj", "src/common/Bifrost.Time/"]
COPY ["src/contracts-internal/Bifrost.Contracts.Internal/Bifrost.Contracts.Internal.csproj", "src/contracts-internal/Bifrost.Contracts.Internal/"]
COPY ["src/exchange/Exchange.Infrastructure.RabbitMq/Bifrost.Exchange.Infrastructure.RabbitMq.csproj", "src/exchange/Exchange.Infrastructure.RabbitMq/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Strategy/Bifrost.Contracts.Strategy.csproj", "contracts/dotnet/Bifrost.Contracts.Strategy/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Round/Bifrost.Contracts.Round.csproj", "contracts/dotnet/Bifrost.Contracts.Round/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Events/Bifrost.Contracts.Events.csproj", "contracts/dotnet/Bifrost.Contracts.Events/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Auction/Bifrost.Contracts.Auction.csproj", "contracts/dotnet/Bifrost.Contracts.Auction/"]
COPY ["contracts/dotnet/Bifrost.Contracts.Market/Bifrost.Contracts.Market.csproj", "contracts/dotnet/Bifrost.Contracts.Market/"]
COPY ["src/gateway/Bifrost.Gateway.csproj", "src/gateway/"]
RUN dotnet restore "src/gateway/Bifrost.Gateway.csproj"
COPY contracts/ contracts/
COPY src/ src/
WORKDIR "/src/src/gateway"
RUN dotnet build "Bifrost.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bifrost.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bifrost.Gateway.dll"]
