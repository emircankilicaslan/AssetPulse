FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/AssetPulse.Web/AssetPulse.Web.csproj src/AssetPulse.Web/
RUN dotnet restore src/AssetPulse.Web/AssetPulse.Web.csproj
COPY src/AssetPulse.Web/ src/AssetPulse.Web/
RUN dotnet publish src/AssetPulse.Web/AssetPulse.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "AssetPulse.Web.dll"]
