# Live Immich database checks

## Automated disposable checks

Run the same database-contract checks against fresh Immich 3.1.0, 3.2.0, and 3.3.1 stacks:

```bash
npm run test:database
```

Requires the .NET 10 SDK and a running Docker engine configured for Linux containers.
Testcontainers creates an isolated network, database and media volumes, and fresh synthetic
accounts for each version. Images are pinned by digest, host ports are assigned dynamically,
and configuration is copied into the containers without host bind mounts. Existing database
environment variables and persistent Compose fixtures are not used.

Each test verifies the server version and its unique database marker before writes, then runs
the same eight-asset contract checks described below. Containers, volumes, and networks are
disposed after success or failure; leave the Testcontainers resource reaper enabled for cleanup
after an interrupted process. Image downloads remain cached by Docker.

This project is deliberately outside the default solution test run. The separate CI database
job runs it on pull requests and master pushes, and automatic master image publication requires it to pass.
TRX reports and attached JSON results or failure logs are saved under
`_out/testcontainers/reversegeo/`.
Account files and passwords are not attached to reports. These checks do not test geographic
data accuracy, external Overture downloads, or the production container image.

## Persistent manual checks

For a separate few-thousand-asset worldwide GPS library, see [Worldwide geographic workload](geographic-workload.md). Keep that workload separate from these eight-asset contract fixtures.

The opt-in harness validates ReverseGeo's actual SQL against pinned Immich 3.1.0, 3.2.0, and 3.3.1 servers. It creates separate synthetic libraries; it accepts no production connection settings.

| Target | Immich UI | PostgreSQL | Docker project |
|---|---|---|---|
| `v31` | `127.0.0.1:15283` | `127.0.0.1:15431` | `immich-reversegeo-db-v31` |
| `v32` | `127.0.0.1:16283` | `127.0.0.1:16432` | `immich-reversegeo-db-v32` |
| `v331` | `127.0.0.1:17283` | `127.0.0.1:17433` | `immich-reversegeo-db-v331` |

Requires local Docker, PowerShell 7, Git, and the .NET SDK. From the repository or a worktree:

```powershell
npm run test:immich-db
pwsh -NoProfile -File tests/LiveImmichDb/Run-LiveImmichDb.ps1 -Action Test -Target v331
pwsh -NoProfile -File tests/LiveImmichDb/Run-LiveImmichDb.ps1 -Action Start -Target v331
pwsh -NoProfile -File tests/LiveImmichDb/Run-LiveImmichDb.ps1 -Action Status
pwsh -NoProfile -File tests/LiveImmichDb/Run-LiveImmichDb.ps1 -Action Stop
```

`Test` runs targets sequentially and stops each stack afterward, including on failure. `Start` leaves the selected stack running for inspection. Neither action deletes volumes. Ports bind only to loopback; remote Docker contexts are rejected.

Credentials, fixture IDs, Compose configuration, and per-version result reports live in the primary checkout's gitignored `.local/immich-db/`, surviving worktree removal. Account files contain secrets: do not commit or share them. Do not upload personal photos or manually alter fixture assets. Preserve these files and the named volumes together; deleting either can break fixture identity and login.

Before any data writes, the runner verifies the exact server version and a database marker created by the dedicated PostgreSQL bootstrap. Global-clear checks additionally require exactly the eight saved fixture assets. Existing host database environment variables cannot redirect the harness.

The runner uploads eight generated PNGs through Immich, waits for metadata extraction, and seeds deterministic GPS/location states in the real schema. Checks cover eligibility, tied-timestamp keyset pagination, Unicode/parameterized writes, nullable fields, location options, all reset scopes, and preservation of unrelated metadata. Immich's automatically maintained `updatedAt` and `updateId` fields are excluded from preservation comparisons.

CI compiles the persistent runner and runs the disposable Testcontainers variant above. It does not use the persistent Compose stacks or accounts. Normal `npm run test` and integration-data-source tests are unchanged. This is database-contract evidence for these pinned versions, not a guarantee for every Immich release or deployed library.

The live harness also checks Location Management apply and guarded undo, exact saved proposals, preservation of unrelated metadata, stale-scan skips, and visibility through Immich's asset API and metadata search. Undo uses the PostgreSQL row revision to reject later metadata edits even when names remain identical. The fixtures contain only generated images; all writes remain subject to their local database identity guards.
