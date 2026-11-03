# RFC: Generic OIDC provider support for Prowlarr

Hey everyone — before I build anything, I wanted to check whether this is in scope.

Right now Prowlarr only supports local logins (Forms) and API keys. I'm proposing **generic OpenID Connect** support, so operators can point Prowlarr at whatever identity provider they already run — Authentik, Keycloak, Okta, Entra, Google, whatever — and configure it through the normal settings page. Nothing vendor-specific anywhere in the code.

## Why

If you self-host with a central IdP, there's no clean way to put Prowlarr behind SSO today. People use reverse-proxy auth, but that lives outside the app and the app has no idea who's actually logged in.

SSO also gives you MFA-backed logins and group-based admin access for free, instead of one shared local password. Sonarr and Radarr already have OIDC; Prowlarr doesn't.

## What I'd build

I'd split it into two PRs, since they're separable:

**PR 1 — OIDC login + group-based authorization**
- A new `AuthenticationType.Oidc` wired through the same config plumbing as the other settings (authority, client id/secret, scopes, groups claim, user identifier).
- Standard OIDC flow with PKCE, and the token's issuer/audience checked exactly against what you configured. If things aren't configured, it just fails closed — Forms and API keys keep working exactly as they do today.
- First login creates the user (keyed on `sub`, not email/username — deliberately, so nothing gets linked to an existing local account by accident). Emails were never the thing granting access.
- Group → role mapping via a settings field (`OidcRoleMappings`, format `group=Role;…`). Unmapped or empty mapping denies. Prowlarr is admin-only so any mapped group makes you an admin. The existing single-user allowlist stays around as a backcompat option for folks already using it.
- Group claims from `groups` or `memberOf` are only used when the configured claim comes back empty — so a fallback claim can't quietly promote someone the configured claim didn't approve.

**PR 2 — app-auth endpoints** (mobile clients)
- `POST /api/auth/oidc/exchange` + `GET /api/auth/me`, handing out short-lived (5-minute) service tokens for clients that can't do browser cookies. Rate-limited. This one's independent — PR 1 doesn't need it.

**Not doing:** SAML/LDAP, multi-provider at once, touching Forms/API-key behavior or their error codes.

## Testing

I know the 80% coverage bar — I'd ship fixtures with it: token validation (valid/expired/wrong issuer, audience mismatch, missing subject, key rotation), the mapping resolver (case-insensitive, highest-wins, unmapped/empty/malformed), and the exchange/me endpoints end to end. I'd test it live against Google and against a self-hosted Authentik before asking for a merge.

One behavior worth calling out: when a client id is set, the audience check is **exact match only** (no substring matching) — fails closed otherwise. That's a security choice, just flagging it.

## A few questions
1. Is generic OIDC something you'd take at all, or should SSO stay reverse-proxy territory?
2. Okay to land it as two PRs (login first, app-auth second)?
3. Settings key name — `OidcRoleMappings` work, or you'd prefer something else?
4. Keep the single-user allowlist as backcompat, or mappings-only going forward?
5. Happy to sign the CLA and link whatever issue you want me to use.

For context: there's a working fork (`wetw0rks-c0ding/Prowlarr`, `feature/oidc-app-auth`) with roughly the above plus the app-auth endpoints, already built into OCI-labeled images. It's a superset built for a specific mobile client, not the shape I'd propose upstream — but the OIDC pieces could be lifted into proper PRs if there's appetite.
