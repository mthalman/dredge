# Build environment for the linux-musl Native AOT release executables.
# Release jobs run this image on native x64 and arm64 runners because
# JavaScript actions cannot run inside Alpine job containers on arm64.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:3cc3bbbbf93d82104892f42aa9106b6be4d120346dea0649643a97c801525256

RUN apk add --no-cache clang build-base zlib-dev git
