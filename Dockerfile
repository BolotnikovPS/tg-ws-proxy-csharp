FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first for better restore caching
COPY TgWsProxy.Domain/TgWsProxy.Domain.csproj TgWsProxy.Domain/
COPY TgWsProxy.Application/TgWsProxy.Application.csproj TgWsProxy.Application/
COPY TgWsProxy.Infrastructure/TgWsProxy.Infrastructure.csproj TgWsProxy.Infrastructure/
COPY TgWsProxy/TgWsProxy.csproj TgWsProxy/
COPY Directory.Packages.props /
COPY Directory.Build.props /

RUN dotnet restore TgWsProxy/TgWsProxy.csproj
COPY . .
WORKDIR /src/TgWsProxy
RUN dotnet build TgWsProxy.csproj -c Release -o /app/build

FROM build AS publish
RUN dotnet publish TgWsProxy.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TgWsProxy.dll"]
