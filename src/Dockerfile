FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS build

ARG TARGETARCH
ARG PACKAGE_VERSION

WORKDIR /source

COPY Valleysoft.Dredge/*.csproj Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/*.csproj Valleysoft.Dredge.Analyzers/
RUN dotnet restore -a $TARGETARCH Valleysoft.Dredge/*.csproj

COPY Valleysoft.Dredge/ Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/ Valleysoft.Dredge.Analyzers/
RUN dotnet publish Valleysoft.Dredge/*.csproj -f net10.0 -o /app -a $TARGETARCH --no-self-contained /p:Version=$PACKAGE_VERSION --no-restore


FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled@sha256:faeea3edbaf9b13cc84dad62a5072cc96a888272087e3aa461268f612bdee702
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
