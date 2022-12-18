FROM mcr.microsoft.com/dotnet/sdk:7.0-jammy AS build
WORKDIR /source

COPY *.csproj .
RUN dotnet restore --use-current-runtime

COPY . .
RUN dotnet publish -c Release -f net7.0 -o /app --use-current-runtime --self-contained true --no-restore /p:IsPublish=true


FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:7.0-jammy-chiseled
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
