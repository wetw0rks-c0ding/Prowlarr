# Prowlarr OIDC App-Auth — Implementation Log (§5 artifact)

Type: implementation log (NOT spec file). Date: 2026-10-29. Author: subagent (spawned by sibling session).

## 1. Base commit & build
- Base: `78669b994` `fix(auth): align app-auth contract to spec (UI.* permissions, prowlarr_role claim)`
- Prior: `89859cd32` JwtBearer+OpenIdConnect packages added; `b03a5645e` OIDC group/auth + endpoints.
- Other subagent's fix: not yet landed as new commit in this worktree (build run before any external commit); no code changes bundled with this log.
- Build (`dotnet build src/Prowlarr.sln -c Debug -p:Platform=Posix`): BLOCKED — SDK 10.0.400 installed, `global.json` requires 10.0.403. No compiler warnings/errs recorded (did not compile). Re-run required once SDK matches.

## 2. Wire smoke-check (no secrets)
- `src/Prowlarr.Http/Authentication/AuthenticationController.cs`: `POST api/auth/oidc/exchange` (ln 71), `GET api/auth/me` (ln 152) — present.
- `ProwlarrServiceTokenService.cs`: `Issuer = "prowlarr"`, `Audience = "prowlarr"` (ln 24-25, 89-90) — matches spec.
- `AuthentikTokenValidationService.cs`: `FallbackIssuer = "https://auth.piracyhub.win/application/o/arrstack/"` (ln 25); `RequiredAudienceSubstring = "arrstack"` (ln 29); audience contains check ln 139 — arrstack compat verified.
- `Prowlarr.Http.csproj`: `Microsoft.AspNetCore.Authentication.JwtBearer` v10.0.14 (ln 8) — verified.

## 3. Contract (exchange / me shapes + status codes — from code + spec)
- Exchange (`POST api/auth/oidc/exchange`): accepts OIDC code/token exchange; returns app token. Status: 200 (success), 401/403 (invalid/unauthorized), 400 (bad request). Claim `prowlarr_role` enforced.
- Me (`GET api/auth/me`): returns current user + permissions (UI.*). Status: 200, 401 (no token).
- Token: JwtBearer, `iss`/`aud` == `prowlarr`; fallback issuer `.../o/arrstack/` for Authentik.

## 4. Arrstack compat behavior
- Fallback issuer points at Authentik `arrstack` application; audience substring check allows `arrstack` (case-insensitive). This lets Seekarr / arrstack clients authenticate against Prowlarr's app-auth without re-issuing tokens.
- No secret material in this log; no tokens, no keys, no `HostConfig` values exposed.

## 5. Build result
- BLOCKED (SDK version mismatch). Source compiles structurally (no syntax errors visible; all referenced files exist); full verification deferred to SDK 10.0.403 installation.
- Warnings/errors: none recorded (no compilation performed).

## 6. Known out-of-spec extras — require reviewer sign-off
Per user directive / spec cross-check, these are NOT fully in-spec and must be signed off:
1. **FindUser scoping** (`AuthenticationService.cs:36`): `FindUser(username, password)` — needs confirmation whether scope is restricted to app-auth users only, or pulls full directory.
2. **HostConfig secret masking**: no evidence of masking of secrets in `HostConfig` / config output; needs review to confirm no leakage in logs/admin endpoints.
3. **AllowedHosts rule**: not verified in controller / middleware; needs explicit allowlist rule for `api/auth/oidc/exchange` origins / redirect URIs.

These are documented here, NOT resolved. No code edited to address them (log-only artifact).

## 7. Commit record
- Log commit (this file) will be separate from any code fix; message to reference base commit and SDK-block.
- No source files modified by this subagent; only docs/project-logs/2026-10-29-prowlarr-oidc-app-auth.md added.
