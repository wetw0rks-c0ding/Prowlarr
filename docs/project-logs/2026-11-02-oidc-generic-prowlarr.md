# 2026-11-02 — Prowlarr OIDC-generic (Authentik-specific code removed)

Branch: `feature/oidc-app-auth`

## What changed
- Config plumbing: `OidcRoleMappings` added to `AuthOptions`, `IConfigFileProvider`, `HostConfigResource`, and `HostConfigController` validator.
- `OidcGroupMapping.IsAuthorized` now config-driven: any group listed in `OidcRoleMappings` authorizes (binary, admin-only); legacy hardcoded tier gate retained as fallback when mappings empty.
- Rename + de-hardcode: `AuthentikTokenValidationService` → `OidcTokenValidationService` (file renamed via `git mv`); fallback issuer/audience consts and substring logic removed; `GetIssuer()` returns `OidcAuthority` verbatim; audience exact-or-fail-closed; lazy-init `ConfigurationManager`; `MapInboundClaims=false`; in-call JWKS refresh+retry; no-throw on invalid token. Group parsing inlined (no `GetGroups` helper needed).
- Call sites (`/exchange`, callback) pass configured mappings.
- README banner rewritten to provider-neutral wording.

## Verification
- Build: `dotnet build src/Prowlarr.sln -c Debug -p:Platform=Posix` → 0 warnings, 0 errors.
- No OIDC test fixtures exist in Prowlarr (create-from-scratch per spec §3).

## Backward-compat config recipe
Set `OidcAuthority=https://auth.piracyhub.win/application/o/arrstack/` (exact string incl. trailing slash), `OidcClientId=arrstack`, `OidcRoleMappings=homies=Admin;family=Admin;maintenance=Admin;authentik Admins=Admin`.
