FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
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
COPY ["src/dah-auction/Bifrost.DahAuction.csproj", "src/dah-auction/"]
RUN dotnet restore "src/dah-auction/Bifrost.DahAuction.csproj"
COPY contracts/ contracts/
COPY src/ src/
WORKDIR "/src/src/dah-auction"
RUN dotnet build "Bifrost.DahAuction.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bifrost.DahAuction.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bifrost.DahAuction.dll"]
