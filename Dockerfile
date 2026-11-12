FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/AssetPulse.Api/AssetPulse.Api.csproj src/AssetPulse.Api/
RUN dotnet restore src/AssetPulse.Api/AssetPulse.Api.csproj
COPY src/ src/
RUN dotnet publish src/AssetPulse.Api/AssetPulse.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "AssetPulse.Api.dll"]
