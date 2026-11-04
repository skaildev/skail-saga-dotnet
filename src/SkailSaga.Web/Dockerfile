# Build context: repository root.
#   podman build -f src/SkailSaga.Web/Dockerfile -t saga-web .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailSaga.Contracts/SkailSaga.Contracts.csproj src/SkailSaga.Contracts/
COPY src/SkailSaga.Web/SkailSaga.Web.csproj src/SkailSaga.Web/
RUN dotnet restore src/SkailSaga.Web/SkailSaga.Web.csproj
COPY src/SkailSaga.Contracts src/SkailSaga.Contracts
COPY src/SkailSaga.Web src/SkailSaga.Web
RUN dotnet publish src/SkailSaga.Web/SkailSaga.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SkailSaga.Web.dll"]
