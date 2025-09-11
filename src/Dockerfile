FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:d599d654aa6fd7baabe54b4272dc475768307ef4d8a07d6e9f9b50b7b1758059 AS build

ARG TARGETARCH
ARG PACKAGE_VERSION

WORKDIR /source

COPY Valleysoft.Dredge/*.csproj Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/*.csproj Valleysoft.Dredge.Analyzers/
RUN dotnet restore -a $TARGETARCH Valleysoft.Dredge/*.csproj

COPY Valleysoft.Dredge/ Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/ Valleysoft.Dredge.Analyzers/
RUN dotnet publish Valleysoft.Dredge/*.csproj -f net9.0 -o /app -a $TARGETARCH --no-self-contained /p:Version=$PACKAGE_VERSION --no-restore


FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled@sha256:edc829adf6e3bb44c120f4c5e9fc39273359b03e6414cb7b5835de6ad618c77e
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
