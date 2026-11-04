# Build context: repository root.
#   podman build -f src/SkailSaga.Worker/Dockerfile -t saga-worker .
# dotnet publish runs in the SDK image; the final image only needs the .NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailSaga.Contracts/SkailSaga.Contracts.csproj src/SkailSaga.Contracts/
COPY src/SkailSaga.Worker/SkailSaga.Worker.csproj src/SkailSaga.Worker/
RUN dotnet restore src/SkailSaga.Worker/SkailSaga.Worker.csproj
COPY src/SkailSaga.Contracts src/SkailSaga.Contracts
COPY src/SkailSaga.Worker src/SkailSaga.Worker
RUN dotnet publish src/SkailSaga.Worker/SkailSaga.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SkailSaga.Worker.dll"]
