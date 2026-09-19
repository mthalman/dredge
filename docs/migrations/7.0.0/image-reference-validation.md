# Pass repository-only tag-list inputs and disambiguate localhost

**Version introduced:** 7.0.0

`dredge tag list` now rejects tag and digest qualifiers that it previously
ignored. Remove those qualifiers from tag-list arguments. References beginning
with `localhost/` now select a local registry; specify Docker Hub explicitly
if you intended its `localhost` namespace.

## Previous behavior

`dredge tag list` parsed its argument as an image reference but used only the
registry and repository to list tags. In v6.0.2, inputs such as `ubuntu:latest`
or `ubuntu@sha256:<digest>` were accepted and their tag or digest qualifiers
were ignored; they did not filter the returned tags.

The image reference parser treated a first path component as a registry only
when it contained a period or colon. Consequently, `localhost/image` meant
repository `localhost/image` on Docker Hub, not repository `image` on a local
registry.

## New behavior

`dredge tag list` requires a repository-only argument. A tag or digest qualifier
is rejected during argument parsing, before registry access. Registry ports
remain valid: `registry.example:5000/team/image` is a repository reference, not
a tagged image.

`localhost/image` now targets registry `localhost` and repository `image`.
`localhost:5000/image` continues to identify an explicit local registry.

Image-taking commands still accept a bare image, an image with a tag, or an image
with a digest. They now validate reference syntax before making registry
requests, including lowercase repository components, tag syntax, registry
hosts and ports, and digest syntax. Dredge rejects references containing both a
tag and a digest. The old parser's acceptance of malformed text did not
guarantee that the resulting registry request would succeed.

## Type of breaking change

This is a command-line input and registry-selection compatibility change.
Previously working tag-list calls with ignored qualifiers now fail.
Unqualified references in Docker Hub's `localhost` namespace now select a
different registry unless that registry is made explicit.

Rejection of malformed inputs, such as empty repository components or invalid
digest encodings, is validation hardening rather than a migration away from
valid image references.

## Reason for change

[PR #332](https://github.com/mthalman/dredge/pull/332) validates references at
the CLI boundary, gives actionable errors before registry requests, and
distinguishes repository arguments from image arguments. Recognizing
`localhost` also makes local-registry selection explicit in parsing.

## Recommended action

Remove the tag or digest from arguments to `tag list`:

```sh
dredge tag list ubuntu
dredge tag list registry.example:5000/team/image
```

For example, replace `dredge tag list ubuntu:latest` with
`dredge tag list ubuntu`. Remove `@sha256:<digest>` in the same way; the old
qualifier never restricted the tag list. Keep tags or digests on commands that
operate on a particular image.

If you intended Docker Hub's `localhost` namespace, use its explicit registry,
for example `registry-1.docker.io/localhost/image` instead of `localhost/image`.
If you intended a local registry, keep `localhost/image` or specify its port.

When validation reports an invalid image reference, use a repository name,
optionally followed by a tag or a digest. Do not use a URL with a scheme or
combine a tag and a digest. Use the full registry-provided digest; for `sha256`,
Dredge requires 64 lowercase hexadecimal characters after `sha256:`.

## Affected APIs

The repository-only restriction affects the `repository` argument of
`dredge tag list`. Registry selection and image validation affect image
arguments on `image`, `image compare`, `manifest`, and `referrer` subcommands,
as well as repository parsing for `tag list`. Registry HTTP APIs are unchanged.
