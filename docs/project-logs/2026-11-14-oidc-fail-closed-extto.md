# Project log — 2026-11-14: OIDC fail-closed fix + ExtTo indexer

## What landed

- Interactive OIDC login no longer elevates a principal when a configured `OidcRoleMappings` mapping matches no group; the legacy `OidcUserIdentifier` fallback now only applies when no mapping is configured, matching `/api/auth/oidc/exchange`'s fail-closed behaviour.
- ExtTo indexer (`99402191b`, `a3033b0bf`, `993994fd2`, `55383f057`, `e6e1d723`): native C# Cardigann-less indexer; FlareSolverr solve through bertha; signed magnet POST. Proven in container: 100 releases / 100 magnets via FlareSolverr session flow; earlier direct-POST returned CF 403 until HMAC/cookie handling was completed.

## Audit trigger

Scoped audit (`~/security-audit-skill/Prowlarr/run-1/`): `ProwlarrOidc.IdentifierFallbackBypassesGroupMapping`, high, verified; `ExtTo.RemoteResponseTrustAndBounds` remains needs_validation (sink bounds/escaping).

## Images

Immutable tag `ghcr.io/wetw0rks-c0ding/prowlarr-oidc:fix-2a70678e` contains the fix. `:feature-oidc-app-auth` not moved.

## Hard dependency

ExtTo index: bertha FlareSolverr over HTTPS with correct SNI; ~14–75s challenge solve; no per-magnet CF solve with the session flow.
