FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY DomusFlow.Api.csproj ./
RUN dotnet restore DomusFlow.Api.csproj

COPY . .
RUN dotnet publish DomusFlow.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app
RUN apk add --no-cache icu-libs tzdata \
    && addgroup -S app \
    && adduser -S app -G app

COPY --from=build /app/publish .
COPY migrations ./migrations

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    PORT=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DomusFlow.Api.dll"]
