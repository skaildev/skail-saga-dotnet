# Builds the console app and runs it with the repository folder mounted on /data (scripts/run.sh does it):
# the app reads .env from there, so the key never goes into the image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY --from=build /app /app
WORKDIR /data
ENTRYPOINT ["dotnet", "/app/SkailSaga.dll"]
