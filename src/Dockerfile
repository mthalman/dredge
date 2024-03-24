FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build

ARG TARGETARCH
ARG PACKAGE_VERSION

WORKDIR /source

COPY Valleysoft.Dredge/*.csproj Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/*.csproj Valleysoft.Dredge.Analyzers/
RUN dotnet restore -a $TARGETARCH Valleysoft.Dredge/*.csproj

COPY Valleysoft.Dredge/ Valleysoft.Dredge/
COPY Valleysoft.Dredge.Analyzers/ Valleysoft.Dredge.Analyzers/
RUN dotnet publish Valleysoft.Dredge/*.csproj -f net8.0 -o /app -a $TARGETARCH --no-self-contained /p:Version=$PACKAGE_VERSION --no-restore


FROM mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./dredge"]
