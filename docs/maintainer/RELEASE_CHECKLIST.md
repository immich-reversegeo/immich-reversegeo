# Release Checklist

## Prepare and verify candidate artifacts

- Start from a reviewed candidate commit and keep unrelated work separate.
- Set `Version` in `src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj` to the intended SemVer in the candidate PR and update both changelogs together. The development version is `0.0.0-dev`. Run the Release workflow on that reviewed branch or tag; `release_name` only labels artifacts and cannot change the app version.
- The workflow stamps the app's version and full source SHA, runs tests and strict docs validation, includes bundled geodata, and checks required files, SQLite headers, and ZIP integrity. A release tag must match the project version. Official `actions/attest` signs the ZIP digests and their build provenance; `actions/upload-artifact` stores the packages. Python is needed only for Zensical.
- Download the web, docs, and verification artifacts from the same successful run into one directory. Compare `release-build.json` with the independently reviewed candidate SHA and project version. Run `sha256sum -c SHA256SUMS` from that directory. These checksums cover the actual inner ZIPs; upload-artifact's reported digest covers its outer artifact archive. The app ZIP includes the same `release-build.json` and the verification artifact includes the signed `attestation.json` bundle.
- Keep checksums beside the ZIPs in a draft release. If the source commit changes after review or merge, regenerate the artifacts and replace the draft assets and checksums together.
- Candidate packaging does not publish a GitHub release, create a tag, or promote Docker images. Complete the existing publishing checks below separately. Docker remains the recommended end-user installation path.

Verify each downloaded ZIP with GitHub CLI, using the expected source commit and ref from your candidate review (replace the placeholders):

```bash
gh attestation verify immich-reversegeo-web.zip --repo immich-reversegeo/immich-reversegeo \
  --source-digest <expected-sha> --source-ref refs/heads/<candidate-branch> \
  --signer-workflow immich-reversegeo/immich-reversegeo/.github/workflows/release.yml
gh attestation verify immich-reversegeo-docs.zip --repo immich-reversegeo/immich-reversegeo \
  --source-digest <expected-sha> --source-ref refs/heads/<candidate-branch> \
  --signer-workflow immich-reversegeo/immich-reversegeo/.github/workflows/release.yml
```

For a tag run, use `refs/tags/<tag>` instead. Add `--bundle attestation.json` to verify the downloaded bundle rather than fetching attestations. Provenance verifies the build source and artifact identity; it does not prove runtime behavior or database correctness. ZIPs are workflow artifacts and no GitHub release is created. Signing permissions are scoped to the Release job; app CI retains its existing permissions.

## GitHub

- Push `master`
- Confirm CI passes
- Confirm CI's publishing job succeeds. Each master push publishes `edge` and SHA tags only after the same commit passes build, test, and Docker build checks; failed or cancelled checks skip publishing.
- Confirm Pages deploy passes
- Confirm the docs site renders correctly
- Update both changelogs and publish a GitHub release with a `vMAJOR.MINOR.PATCH` tag pointing to the validated commit. This starts the Docker publishing workflow; pushing a tag alone does not publish a versioned image.
- For a prerelease, use a tag such as `v1.2.3-rc.1` and mark the GitHub release as a prerelease. It publishes full version tags without moving `latest`.
- Choose which stable release GitHub designates as latest. Confirm the publishing workflow succeeds and inspect the image tags: the full version with and without `v`, the SHA tag, and `latest` when this is GitHub's latest stable release. `edge` remains the master build channel.
- Automatic `latest` promotion runs separately from the build, in a shared queue, and checks GitHub's latest release immediately before promoting the built digest. Superseded release runs skip promotion. If the GitHub lookup or registry promotion fails, the version tags remain published; rerun the failed promotion job.
- The existing manual `publish_latest` workflow input remains an explicit override. Use it only when intentionally promoting a validated build to `latest`.
- To recover promotion without rebuilding, run Publish Docker Image with `publish_latest=true`, `existing_release` set to the stable release tag, and `existing_digest` set to its already published image digest. This skips the build, checks the version tag's registry digest, and rechecks GitHub's latest stable release before promotion.
- Confirm the published container image can be pulled and started

## Runtime

- Start the latest image with persistent `/data` and `/config` mounts
- Verify the web UI loads
- Verify `/healthz` if a health endpoint is available in the release being tested
- Verify existing settings survive a container update

## Reverse Geocoding

- Verify a lookup in a straightforward inland city
- Suggested coordinate: `47.3769, 8.5417` should resolve to Zurich / Switzerland
- Verify a lookup near a major airport
- Suggested coordinate: `47.460972, 8.553525` should resolve to Zurich Airport infrastructure in Switzerland
- Verify a lookup in a coastal or island-heavy country
- Suggested coordinate: `4.2979, 73.0111` should resolve to Maldives with useful Overture division data
- Verify processing writes city/state/country back to Immich as expected
- Verify a country with on-demand Overture division download still works from a clean `/data` directory

## Docs and Metadata

- Final README updates
- Final changelog updates
- Final installation/configuration docs
- Final privacy URL
- Final support URL
- Final terms URL
