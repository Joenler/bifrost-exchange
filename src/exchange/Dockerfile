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
COPY ["src/exchange/Bifrost.Exchange.csproj", "src/exchange/"]
RUN dotnet restore "src/exchange/Bifrost.Exchange.csproj"
COPY src/ src/
WORKDIR "/src/src/exchange"
RUN dotnet build "Bifrost.Exchange.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bifrost.Exchange.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bifrost.Exchange.dll"]
