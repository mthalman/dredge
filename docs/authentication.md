# Authenticate to a registry

For each registry request, Dredge selects credentials in this order:

1. The `DREDGE_TOKEN` environment variable.
2. The `DREDGE_USERNAME` and `DREDGE_PASSWORD` environment variables. Dredge
   uses these credentials only when both variables are set.
3. Credentials saved by `docker login`.
4. Anonymous access when no credentials are available.

`DREDGE_TOKEN` takes precedence over every other credential source.

## Use an access token

Set `DREDGE_TOKEN` to a registry access token, and then run Dredge in the same
shell.

```shell
export DREDGE_TOKEN="your-oauth-token"
dredge manifest get myregistry.azurecr.io/myimage:latest
```

In PowerShell:

```powershell
$env:DREDGE_TOKEN = "your-oauth-token"
dredge manifest get myregistry.azurecr.io/myimage:latest
```

## Use a username and password

Set both environment variables in the shell that runs Dredge:

```shell
export DREDGE_USERNAME="your-username"
export DREDGE_PASSWORD="your-password"
dredge manifest get myregistry.example.com/myimage:latest
```

## Use saved Docker credentials

Authenticate with Docker before running Dredge:

```shell
docker login myregistry.azurecr.io
dredge manifest get myregistry.azurecr.io/myimage:latest
```

Dredge reads the credential store configured by Docker. You do not need to
keep Docker running.

## Deletion permissions

`tag delete` and `manifest delete` use the same credential sources and priority
as read commands. Successful authentication or pull access does not imply
permission to delete. The registry must grant deletion access to the supplied
credentials or token and must enable the requested deletion API.

Manifest deletion also reads the target manifest. When deleting an artifact
with a subject on a registry without native referrers support, maintaining the
referrers fallback index can require push permission. A failure during this
maintenance is reported even if the manifest deletion has already succeeded.

Tag deletion is optional in the OCI Distribution specification. A registry
can support manifest deletion while rejecting tag deletion. Dredge reports
these failures and does not substitute the more destructive operation.

See [`tag delete`](commands/tags.md#delete) and
[`manifest delete`](commands/manifests.md#delete) for confirmation behavior
and the effects of each operation.
