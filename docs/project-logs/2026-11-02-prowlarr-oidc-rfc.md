# RFC: Generic OIDC provider support for Prowlarr

**Author:** wetw0rks-c0ding
**Status:** Draft RFC — for pre-implementation discussion per the contributing guide
**Date:** 2026-11-03

## TL;DR

Prowlarr currently supports local (Forms) auth and API keys. This RFC proposes adding **generic, standards-compliant OIDC** (OpenID Connect) authentication so Prowlarr can be fronted by whatever identity provider an operator already runs — Authentik, Keycloak, Okta, Entra ID, Google, etc. — configured entirely through the existing `config/host` settings. No vendor is hardcoded; no provider-specific behavior is baked in.

## Motivation

Self-hosters with a central IdP (homelab or enterprise) have no way to put Prowlarr behind SSO today. Sonarr/Radarr already shipped OIDC upstream; Prowlarr has none (the code base has zero `AuthenticationType.Oidc` and no OIDC discovery/validation). Users work around it with reverse-proxy auth, but that's outside the app and invisible to its audit/health surface.

Security-wise this also matters going forward: credentials managed by an IdP with MFA are stronger than a single shared local password, and group-based authorization maps cleanly to "who may administer indexers" without maintaining a parallel user list.

## Proposal

### Scope — two PRs, split deliberately
1. **PR 1 — OIDC authentication + authorization (the ask):**
   - `AuthenticationType.Oidc` + full config plumbing through `AuthOptions` → `IConfigFileProvider` → `HostConfig` resource/mapper/validators (mirrors the existing six OIDC keys' pattern).
   - OIDC client flow with PKCE (`UsePkce = true`, `offline_access` added) and discovery/JWKS validation at the configured `OidcAuthority`. The issuer is validated **verbatim** as configured (no normalization surprises); the audience must match `OidcClientId` exactly, failing closed otherwise.
   - Subject-first JIT user provisioning (`UpsertOidcUser`): users are created on first successful login and keyed by OIDC `sub`. **No auto-linking by username/email** — that's a deliberate security choice (IdP-controlled `preferred_username` is not proof of ownership of the existing local account).
   - Config-driven group → role mapping via `OidcRoleMappings` (`group=Role;…` pairs), parsed case-insensitively, highest role wins. Prowlarr is admin-only, so any mapped group authorizes as admin; unmapped/empty mapping denies. This keeps the mapping operator-defined rather than code-defined.
   - Existing `OidcUserIdentifier` allowlist retained as a backcompat fallback.
   - Fallback group claims (`groups`, `memberOf`) are consulted only when the configured claim yields zero groups (prevents a fallback claim from overriding a present configured claim).
2. **PR 2 — App-auth token exchange** (mobile clients / Seekarr):
   - `POST /api/auth/oidc/exchange` (per-IP rate-limited) and `GET /api/auth/me`, minting short-lived (5-min) HS256 service tokens (`iss`/`aud` = `prowlarr`) for mobile clients that can't hold browser cookies.
   - This is separable — PR 1 does not depend on it.

### Non-goals
- SAML, LDAP, multi-provider simultaneous login.
- Any provider-specific configuration or hardcoded IdP addressing (none exists in the code).
- Changing the existing Forms/API-key paths or their 401/403 behavior.

### Backward compatibility
No DB migration (config-only). Existing installs are untouched: if `OidcAuthority`/`OidcClientId` are unset, OIDC validation fails closed and Forms/ApiKey continue exactly as before. The one behavior to flag: when `OidcClientId` is configured, audience validation is *exact-match only* (no substring matching) — documented in the settings UI and wiki.

## Testing plan
- **80%+ coverage on new code** per the contributing guide: validation-service fixture (valid/expired/wrong-issuer/audience-mismatch/missing-subject/JWKS-rotation), mapping-resolver fixtures (exact/case-insensitive/highest-wins/unmapped/empty/malformed), controller fixtures for `/exchange` and `/me` (status matrix + rate limit). Our fork carries this suite green (37/37 auth fixtures, 0 build warnings/errors) and will be contributed with the PR.
- Manual verification against both a public IdP (Google) and a self-hosted one (Authentik), including group-mapped roles and the configured-user fallback.

## Questions for maintainers
1. Is generic OIDC in-scope at all (vs. SSO remaining reverse-proxy territory)?
2. One feature per PR: okay to land auth+authZ first and app-auth second, or should app-auth live downstream entirely?
3. Naming: `AuthenticationType.Oidc` (+ `OidcRoleMappings`)? Any preference on the settings key (e.g. `oidc_role_mappings`)?
4. Is a single `OidcUserIdentifier` allowlist sufficient as backcompat, or should that be removed in favor of mappings-only?
5. CLA + linked issue: I'll sign the CLA and link this thread once it exists.

## Fork status (for context, not part of the ask)
A working fork exists (`wetw0rks-c0ding/Prowlarr`, `feature/oidc-app-auth`) implementing the above plus the app-auth endpoints, with OCI-labeled images (`ghcr.io/wetw0rks-c0ding/prowlarr-oidc`). It is *not* the proposed upstream shape — it's a superset built for a specific mobile client — but the core OIDC pieces can be lifted into upstream-form PRs on your say-so.
