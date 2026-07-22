# ADR-010 — Initial production authentication and administration

**Status:** Accepted for `S35` / `IG-04P`  
**Date:** 22 July 2026

## Decision

Initial production authentication uses local Dispatcher accounts owned by `MOD-IAM`. Login names are normalized invariantly; passwords are stored only as configurable PBKDF2-SHA256 hashes with per-account random salts. Configured lockout limits bound online guessing. Successful login issues independently random access and refresh credentials whose hashes alone are durable.

The first administrator is created once from the write-only startup configuration keys `Dispatcher:Identity:Bootstrap:UserName` and `Dispatcher:Identity:Bootstrap:Password`. An advisory transaction lock and the empty-account precondition close bootstrap permanently after the first account; operators remove the bootstrap secret from configuration after successful initialization.

Every request presents `Dispatcher-Session` in the authorization header. Server middleware validates the durable session before resolving the existing `SessionSnapshot`; query parameters and the development/test session bridge are not production authentication. Refresh rotates both credentials. Expiry, explicit revoke, account disable and authorization-version changes invalidate access.

Roles and groups contribute exact permission codes, optionally classified by an owned access scope. Account overrides are explicit grants or denials. Administration permission is checked in the backend. Removing or disabling the final effective administrator is rejected. Permission updates expose an impact preview and invalidate active affected sessions through the account authorization version.

Settings resolve in the fixed order account → unambiguous group → nearest scope ancestor → global. Conflicting group overrides fail closed. The initial integration diagnostics boundary supports local Dispatcher authentication status only and exposes sanitized metadata plus a `SecretConfigured` flag; no secret value is readable.

Workspace Account/Person remain separate identities linked by reference. Full OIDC/enterprise provisioning and arbitrary integration adapters are outside this decision.
