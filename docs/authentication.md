# Registry Authentication

For container registries requiring authentication, Dredge can make use of credentials stored in your environment via the `docker login` command.
Alternatively, you can set the `DREDGE_TOKEN` environment variable to an OAuth bearer token or set the `DREDGE_USERNAME` and `DREDGE_PASSWORD` environment variables if you have credentials.
Dredge will look for the environment variables first and fall back to any `docker login` credentials if they exist.
