FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:2c9a4956a61fc45d9111fb36ec9fb86932e4842af9eb9bc9306bf8757f53674d AS build

ARG TARGETARCH
ARG PACKAGE_VERSION

WORKDIR /source

COPY Valleysoft.Dredge/*.csproj Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/*.csproj Valleysoft.Dredge.Analyzers/
RUN dotnet restore -a $TARGETARCH Valleysoft.Dredge/*.csproj

COPY Valleysoft.Dredge/ Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/ Valleysoft.Dredge.Analyzers/
RUN dotnet publish Valleysoft.Dredge/*.csproj -f net9.0 -o /app -a $TARGETARCH --no-self-contained /p:Version=$PACKAGE_VERSION --no-restore


FROM mcr.microsoft.com/dotnet/runtime:9.0-noble-chiseled@sha256:bf2eb655f67f443eb3b2827ccedb1f936bdb34dbe9d716ed1ab28cb3b34bea6f
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
