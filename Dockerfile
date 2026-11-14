# ---- 编译阶段 ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NumberBomb.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ---- 运行阶段 ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# 云平台的反向代理会把访客 IP / https 信息转进来
ENV NB_TRUST_PROXY=1
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

EXPOSE 10000
ENTRYPOINT ["dotnet", "NumberBomb.dll"]
