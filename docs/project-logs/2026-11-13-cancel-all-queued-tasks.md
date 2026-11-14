# 2026-11-13 — Cancel All queued tasks (System -> Tasks)

Branch: `feature/oidc-app-auth`

## What changed

- Added a "Cancel All" control for queued tasks on System -> Tasks. The existing per-row cancel action is retained.
- Scope is frontend only.

Files changed:
- `frontend/src/System/Tasks/Queued/QueuedTasks.tsx`
- `src/NzbDrone.Core/Localization/Core/en.json` (keys `CancelAll`, `CancelAllQueuedTasksMessage`)

## Why queued-only

The backend `DELETE /api/v1/command/{id}` calls `CommandQueueManager.Cancel(id)` -> `RemoveIfQueued(id)` and throws `NzbDroneClientException(Conflict, "Unable to cancel task")` for anything already started. Mid-run cancellation would require a `CancellationToken` plumbed through `CommandExecutor` and is explicitly out of scope.

## Commits on the branch

| Commit | Summary |
| --- | --- |
| `cffb8e606` | Merge of upstream `origin/develop` (12 commits: .NET 10.0.15 bump, STJson/UTF-8 JSON deserialization, HttpClient/Happy Eyeballs, API-key log cleansing, release 3.0.2). No conflicts; migrations checked, `045_oidc_user_subject_and_groups` does not collide. |
| `27e6c6901` | New: Add Cancel All control for queued tasks (final, after prettier formatting fix). |
| `3984bd5bd` | Fixed: Pin .NET SDK image to 10.0.404 to match `global.json` (upstream bumped `global.json` to 10.0.404 while the Dockerfile still pinned `sdk:10.0.403`; SDK resolution reads `/src/global.json` inside the build, so the build would have failed). |

## Verification performed

| Check | Result |
| --- | --- |
| `dotnet build src/Prowlarr.sln -c Debug -p:Platform=Posix` | 0 Warning(s), 0 Error(s) |
| Frontend `yarn build` (Dockerfile `frontend` stage) | webpack 5.112.0 compiled successfully |
| ESLint on `frontend/src/System/Tasks/Queued/QueuedTasks.tsx` inside the verify image (`npx eslint --config frontend/.eslintrc.js --ignore-path frontend/.eslintignore ...`) | Clean. Two prettier errors were found and fixed before commit. |
| `npx tsc --noEmit -p tsconfig.json` | Zero errors in `frontend/src` (remaining output is pre-existing `node_modules` typedef noise upstream). |
| Full image build | `ghcr.io/wetw0rks-c0ding/prowlarr-oidc:cancelall-3984bd5b` (image id `611baaa80bcc`), runtime reports .NET 10.0.15 |
| Container proof against the built image (real API calls) | Queued `ApplicationUpdate` command -> `DELETE /api/v1/command/{id}` -> HTTP 200 and the row disappeared from `GET /api/v1/command`; a `started` command -> HTTP 409 "Unable to cancel task"; a `completed` command -> HTTP 409. The built UI bundle contains the `CancelAll` translation key. |

## Known limitations / notes

- Cancelling is queued-only by backend design; true mid-run cancellation was not implemented (would require `CancellationToken` plumbing through `CommandExecutor`).
- A command that starts between render and confirm will return 409: the UI closes the modal first, dispatches one DELETE per queued id, then refetches the command list so a failed cancel reappears.
- Unrelated pre-existing behavior observed: POSTing an unknown command name returns 500 "Sequence contains no matching element" from `CommandController.StartCommand` instead of a 400.
- `docs/project-logs/2026-11-02-prowlarr-oidc-rfc.md` remains an uncommitted draft unrelated to this change and was not staged or committed.
