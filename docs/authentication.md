# Authentication

For registries that require authentication, Dredge resolves credentials in the following order:

1. **`DREDGE_TOKEN` environment variable** — OAuth bearer token for the registry.
2. **`DREDGE_USERNAME` and `DREDGE_PASSWORD` environment variables** — Basic credentials.
3. **Docker credential store** — Credentials stored via `docker login`.

If none of these are available, Dredge attempts anonymous access.

## Examples

### Using an environment variable

```shell
export DREDGE_TOKEN="your-oauth-token"
dredge manifest get myregistry.azurecr.io/myimage:latest
```

### Using Docker credentials

```shell
docker login myregistry.azurecr.io
dredge manifest get myregistry.azurecr.io/myimage:latest
```
