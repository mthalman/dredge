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
