FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:7.0-jammy AS build

ARG TARGETARCH
ARG TARGETOS

RUN arch=$TARGETARCH \
    && if [ "$arch" = "amd64" ]; then arch="x64"; fi \
    && echo $TARGETOS-$arch > /tmp/rid

WORKDIR /source

COPY *.csproj .
RUN dotnet restore -r $(cat /tmp/rid) /p:PublishReadyToRun=true

COPY . .
RUN dotnet publish -c Release -f net7.0 -o /app -r $(cat /tmp/rid) --self-contained true --no-restore /p:PublishTrimmed=true /p:PublishReadyToRun=true /p:PublishSingleFile=true /p:TrimMode=partial


FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:7.0-jammy-chiseled
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
