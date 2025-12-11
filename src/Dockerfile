FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:7331fb5fb8798c58df1f263dc8cc843459965c00cfb29ebeb53bf4cc572d592d AS build

ARG TARGETARCH
ARG PACKAGE_VERSION

WORKDIR /source

COPY Valleysoft.Dredge/*.csproj Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/*.csproj Valleysoft.Dredge.Analyzers/
RUN dotnet restore -a $TARGETARCH Valleysoft.Dredge/*.csproj

COPY Valleysoft.Dredge/ Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/ Valleysoft.Dredge.Analyzers/
RUN dotnet publish Valleysoft.Dredge/*.csproj -f net9.0 -o /app -a $TARGETARCH --no-self-contained /p:Version=$PACKAGE_VERSION --no-restore


FROM mcr.microsoft.com/dotnet/runtime:9.0-noble-chiseled@sha256:c62b424f671d1367e78f08deaa18d167afe7696dc76ede30cebca8663871f6de
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
