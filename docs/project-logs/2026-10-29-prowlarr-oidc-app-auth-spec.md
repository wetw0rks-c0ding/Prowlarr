# Prowlarr OIDC App-Auth Spec (Seekarr Integration)

Date: 2026-10-29  
Branch: develop (fork context)  
Goal: Add `/api/auth/oidc/exchange` and `/api/auth/me` for Seekarr mobile app, matching the Sonarr/Radarr pattern. Prowlarr is **admin-only** (all authorized OIDC users are admins).

## Current State
Prowlarr already has:
- User model with `OidcSubject`, `OidcGroups` (migration 045)
- OIDC config (`OidcAuthority`, `OidcClientId`, `OidcClientSecret`, `OidcScopes`, `OidcUserIdentifier`, `OidcGroupsClaim`)
- `OidcGroupMapping` with tier gates (pirates→T1 blocked, homies+ → authorized as admin)
- `AuthentikTokenValidationService` (JWKS validation, group extraction from configurable claim, array/comma/single support)
- OIDC callback flow with PKCE + `offline_access`, group-aware `TryAuthorizeWithGroups`, `UserService.UpsertOidcUser()` (subject-first, no username auto-link)

Missing (vs Radarr/Sonarr):
- Service token minting/validation for app auth
- `POST /api/auth/oidc/exchange`
- `GET /api/auth/me`
- JWT Bearer scheme for service tokens
- Per-IP rate limiting on exchange
- Unified `arrstack` provider backward compatibility

## API Contract

### `POST /api/auth/oidc/exchange`
- **Auth:** AllowAnonymous
- **Rate Limit:** 10 requests/min/IP
- **Input:** `subject_token` from JSON body (`OidcExchangeRequest.ResolvedSubjectToken`/`SubjectToken`) or `Authorization: Bearer ...`
- **Validation:**
  - Validate Authentik-issued token via JWKS (issuer from `OidcAuthority`, audience must match `OidcClientId` or `arrstack`/per-service names; exact match when configured, substring fallback otherwise)
  - Fail closed if OIDC not configured
- **Authorization:**
  - Extract groups from claim `OidcGroupsClaim` (default `groups`). Support JSON array or comma-delimited
  - `OidcGroupMapping.IsAuthorized(groups)` must be true (pirates/T1 → 403 `insufficient_group`, homies+ → allowed)
- **Provisioning:**
  - JIT provision via `UserService.UpsertOidcUser(subject, username, groupsCsv)` by subject (no username auto-link)
- **Response (200):**
```json
{
  "token": "<prowlarr service jwt>",
  "token_type": "Bearer",
  "expires_in": 300,
  "role": "Admin",
  "permissions": ["UI.Read", "UI.Add", "UI.Manage", "UI.Admin"]
}
```
- **Errors:** `400 subject_token is required`, `401 invalid_token`, `403 insufficient_group`, `429 rate_limit_exceeded`, `500 service_unavailable`/`User provisioning failed`

### `GET /api/auth/me`
- **Auth:** `Authorization: Bearer <prowlarr service token>`
- **Validation:** Validate HS256 service token `iss=prowlarr`, `aud=prowlarr`, 2 min clock skew
- **Response (200):**
```json
{
  "username": "...",
  "role": "Admin",
  "groups": ["..."],
  "permissions": ["UI.Read", "UI.Add", "UI.Manage", "UI.Admin"]
}
```
- **Errors:** `401 invalid_token`, `404 not_found`

## Implementation Plan

### 1) Service Token Service
Create `src/Prowlarr.Http/Authentication/ProwlarrServiceTokenService.cs`:
- `IProwlarrServiceTokenService { string MintToken(string subject); ClaimsPrincipal ValidateToken(string token); SecurityKey SigningKey { get; } }`
- `ProwlarrServiceTokenService` HS256, `Issuer="prowlarr"`, `Audience="prowlarr"`, 5 min TTL, `sub` claim
- Key stored at `{AppDataFolder}/service-token.key`, auto-generate 32 bytes base64 if missing, throw if < 32 bytes
- `ProwlarrRolePermissions.ForRole()` or static helper returning all 4 permissions (admin-only)

### 2) Update Authentik Token Validation (arrstack compat)
Update `src/Prowlarr.Http/Authentication/AuthentikTokenValidationService.cs`:
- Default/fallback issuer → `https://auth.piracyhub.win/application/o/arrstack/`
- Audience validation: if `OidcClientId` configured, accept exact match OR `arrstack` (case-insensitive); else accept any audience containing `arrstack`, `prowlarr`, `sonarr`, `radarr`, `lidarr`, or `prowlarr` (substring fallback for tests)

### 3) Add Endpoints to Controller
Update `src/Prowlarr.Http/Authentication/AuthenticationController.cs`:
- Add `using System.Collections.Concurrent; using System.Linq;` as needed
- Add per-IP rate limiting (ConcurrentDictionary + lock) - 10 req/min window
- Add `OidcExchangeRequest` class (Code, GrantType, SubjectToken, SubjectTokenType, ResolvedSubjectToken)
- Add `POST api/auth/oidc/exchange`:
  - Extract subject token from body or `Authorization: Bearer`
  - Rate limit check → 429 if exceeded
  - Get `IAuthentikTokenValidationService` from DI, validate → 401 if failed
  - Check `OidcGroupMapping.IsAuthorized(validation.Groups)` → 403 if not
  - Get `IUserService`, call `UpsertOidcUser(validation.Subject, validation.Username, string.Join(",", validation.Groups))`
  - Get `IProwlarrServiceTokenService`, mint token → return 200 with token/role/permissions/expires_in
- Add `GET api/auth/me`:
  - Extract Bearer token, validate via service token service → 401 if invalid
  - Get user by `OidcSubject` via `IUserRepository` → 404 if not found
  - Return username/role/groups/permissions

### 4) DI Wiring + JWT Bearer
Update `src/Prowlarr.Http/Authentication/AuthenticationBuilderExtensions.cs`:
- Add usings for JWT Bearer, IdentityModel, Authorization
- Register `IProwlarrServiceTokenService` and `IAuthentikTokenValidationService` as singletons
- Add JWT Bearer scheme `Bearer` configured with `ProwlarrServiceTokenService` (iss/aud `prowlarr`, HS256, clock skew 2 min), normalize claims (`oidc_sub`, `prowlarr_role` = `Admin` on valid token)
- Add package reference to `Microsoft.AspNetCore.Authentication.JwtBearer` in `Prowlarr.Http.csproj` if missing

### 5) Build, Test, Commit, Push
- Build: `dotnet build src/Prowlarr.sln -c Debug -p:Platform=Posix`
- Create project log `docs/project-logs/2026-10-29-prowlarr-oidc-app-auth.md` documenting changes
- Commit and push to fork if one exists

## Notes
- Prowlarr is admin-only; no role enum/permission handler needed like Sonarr/Radarr
- `UserService.UpsertOidcUser` signature in Prowlarr takes `(subject, username, groups)` and assigns `Role = ProwlarrRole.Admin` internally (per subagent findings)
- Must not break existing OIDC callback flow
- Follow existing code style (tabs/spaces as in file, error messages match pattern)
