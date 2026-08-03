# Security

- API keys are Windows generic credentials named `CodexAgentSwitch/<reference>`; SQLite, exports, and logs store only their reference IDs.
- Provider URLs require HTTPS except explicit loopback development endpoints. Reserved headers and CR/LF injection are rejected.
- Setup validates the adjacent payload SHA-256, rejects drive-root destinations, stages before commit, retains version backups, and restores the old install when commit fails.
- Runtime installation is never implicit. The independent bootstrapper checks x64 Framework, Main, Singleton, and DDLM registrations, then requires an explicit confirmation before launching the bundled Microsoft-signed installer.
- Diagnostic exports redact Bearer values, API-key/token fields, and `sk-` tokens. Worker ownership and Scope checks prevent deletion or mutation outside registered boundaries.

The locally built application and Setup executables are not production code-signed because no publisher certificate was provided. Verify release-manifest hashes before use. See `docs/security-and-privacy.md`.
