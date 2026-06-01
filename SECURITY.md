# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.2.x   | yes       |
| 1.1.x   | critical fixes only |
| < 1.1   | no        |

## Reporting a Vulnerability

Report security issues privately via GitHub's "Report a vulnerability" feature on this repository. Do NOT open a public issue for security bugs.

Expected response: within 7 days. Coordinated disclosure follows.

## Scope

In scope:
- Privilege escalation via plugin endpoints
- Path traversal via configured preroll folders
- Auth bypass on admin endpoints
- Code execution via crafted sidecar metadata
- DoS via malformed preroll inputs

Out of scope:
- Issues requiring physical access to the server
- Issues in Jellyfin itself (report upstream)
- Social engineering
