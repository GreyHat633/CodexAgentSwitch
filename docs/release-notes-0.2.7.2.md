# CodexAgentSwitch v0.2.7.2 Release Notes

- CAS-managed projects now default to `model_auto_compact_token_limit = 150000`.
- Existing explicit project values are preserved and take priority over the CAS default.
- Unrelated project TOML content, existing backup behavior, and restore behavior are preserved.
- Unselected projects and user-level Codex configuration are not modified.
- CAS does not directly compact Desktop-owned threads.
- The setting takes effect for newly created or reloaded project threads.

CodexAgentSwitch v0.2.7.2 assigns a default native auto-compaction threshold of 150,000 tokens to selected CAS-managed projects. Codex itself remains responsible for native automatic compaction.
