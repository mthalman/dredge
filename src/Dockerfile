FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:39ca82d31517e698dcd47f668d53041ec95cca8b783d8134c3053a9769492603 AS build

ARG TARGETARCH
ARG PACKAGE_VERSION

WORKDIR /source

COPY Valleysoft.Dredge/*.csproj Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/*.csproj Valleysoft.Dredge.Analyzers/
RUN dotnet restore -a $TARGETARCH Valleysoft.Dredge/*.csproj

COPY Valleysoft.Dredge/ Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/ Valleysoft.Dredge.Analyzers/
RUN dotnet publish Valleysoft.Dredge/*.csproj -f net9.0 -o /app -a $TARGETARCH --no-self-contained /p:Version=$PACKAGE_VERSION --no-restore


FROM mcr.microsoft.com/dotnet/runtime:9.0-noble-chiseled@sha256:713e055f224af4f9cff3f3316e45e9d3e8ee563255474eddf2e407046187ba73
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
