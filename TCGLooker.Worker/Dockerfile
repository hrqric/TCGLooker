# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["global.json", "."]
COPY ["Directory.Packages.props", "."]
COPY ["TCGLooker.Worker/TCGLooker.Worker.csproj", "TCGLooker.Worker/"]
COPY ["TCGLooker.Application/TCGLooker.Application.csproj", "TCGLooker.Application/"]
COPY ["TCGLooker.Domain/TCGLooker.Domain.csproj", "TCGLooker.Domain/"]
COPY ["TCGLooker.Infra/TCGLooker.Infra.csproj", "TCGLooker.Infra/"]

RUN dotnet restore "TCGLooker.Worker/TCGLooker.Worker.csproj"

COPY . .

RUN dotnet publish "TCGLooker.Worker/TCGLooker.Worker.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    Scraping__CooldownStatePath=/tmp/tcglooker/scraping-cooldowns.json

COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "TCGLooker.Worker.dll"]
