# Ext.to indexer — live proof and fix (2026-11-13)

## Summary

Ext.to is Cloudflare-protected. The indexer now solves Cloudflare via FlareSolverr,
parses 100 releases per search, and enriches every release with a magnet URL.
Live proof (built container, host networking): `dexter` search returned **100 releases
with 100 magnetUrl populated**.

## What worked

- FlareSolverr at `https://bertha.tail4a794.ts.net:8191/v1` (HTTPS + SNI, self-signed
  cert accepted). Plain `http://bertha:8191` fails ("Client sent an HTTP request to an
  HTTPS server"); TLS without SNI fails (tlsv1 alert).
- Search page: `GET /browse/?page=N&q=TERM&sort=age&order=desc` solved via FlareSolverr
  `request.get` (~14-75s depending on challenge). Response contains `searchPageToken`
  (`window.searchPageToken`) and `csrf-token` meta.
- Magnet enrichment: FlareSolverr **session** (`sessions.create` -> `request.get` page ->
  `request.post` `/ajax/getSearchMagnet.php` -> `sessions.destroy`). The POST returns
  JSON wrapped in an HTML `<pre>...</pre>` preview; unwrap + `HtmlDecode` before parsing.
- HMAC signing: `SHA256(torrent_id|timestamp|searchPageToken)` hex, `sessid` = csrf token.

## What did NOT work (and why)

- `request.post` via FlareSolverr **without** a session / as a stateless navigation
  returns a Cloudflare challenge (403) — the POST must reuse the solved session.
- Direct native POST from Prowlarr with solved cookies+UA now returns 403 (cf_clearance
  is bound to FlareSolverr's browser/TLS context); session-based POST is required.
- FlareSolverr `request.post` responses are HTML-wrapped (`<pre>{json}</pre>`), not
  raw JSON — must unwrap.

## Timings

- FlareSolverr solve: ~14s (non-interactive) to ~75s (interactive challenge).
- Magnet POST per release: ~0.25-0.4s through the FlareSolverr session.
- Full 100-release search: ~160s end-to-end.

## Dependency

Hard dependency on bertha's FlareSolverr (HTTPS + SNI). Configure
`FlareSolverr URL = https://bertha.tail4a794.ts.net:8191/` in the ExtTo indexer
settings (not the indexer-proxy tag mechanism).

## Live proof

- Image: `prowlarr-oidc:extto-final` (3b8ac5a75bdd)
- Search `dexter`: 100 releases, 100 magnetUrl populated.
- Sample magnet prefix: `urn:btih:09304B0F334FDF69B0F873E344F549E50CD27BC3`
- Log: `/tmp/opencode/extto-final-config/logs/prowlarr.txt`
- Container config/data: `/tmp/opencode/extto-final-config`
