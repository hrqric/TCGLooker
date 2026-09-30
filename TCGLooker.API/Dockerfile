# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["global.json", "."]
COPY ["Directory.Packages.props", "."]
COPY ["TCGLooker.API/TCGLooker.API.csproj", "TCGLooker.API/"]
COPY ["TCGLooker.Application/TCGLooker.Application.csproj", "TCGLooker.Application/"]
COPY ["TCGLooker.Domain/TCGLooker.Domain.csproj", "TCGLooker.Domain/"]
COPY ["TCGLooker.Infra/TCGLooker.Infra.csproj", "TCGLooker.Infra/"]

RUN dotnet restore "TCGLooker.API/TCGLooker.API.csproj"

COPY . .

RUN dotnet publish "TCGLooker.API/TCGLooker.API.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=10000 \
    Scraping__CooldownStatePath=/tmp/tcglooker/scraping-cooldowns.json

EXPOSE 10000

COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "TCGLooker.API.dll"]
