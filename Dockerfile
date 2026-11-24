# 玄铁栈道 EquiLedger · 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
# 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/EquiLedger.Api/EquiLedger.Api.csproj src/EquiLedger.Api/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore src/EquiLedger.Api/EquiLedger.Api.csproj
COPY src/EquiLedger.Api/ src/EquiLedger.Api/
COPY web/ src/EquiLedger.Api/wwwroot/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/EquiLedger.Api/EquiLedger.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY db/init.sql /app/db/init.sql
ENV ASPNETCORE_URLS=http://+:8080
ENV EQUI_INIT_SQL=/app/db/init.sql
EXPOSE 8080
ENTRYPOINT ["dotnet", "EquiLedger.Api.dll"]