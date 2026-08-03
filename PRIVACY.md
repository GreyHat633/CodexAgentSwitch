# Privacy

Profiles, task history, prompts, Provider responses, Usage, budgets, protocol cache, and diagnostic logs stay in the configured local data root. Calling an external Provider sends the prompt and request metadata to the configured endpoint; no external Provider is contacted during startup or local profile editing.

Uninstall preserves local data and Windows credentials by default. Deleting local `data` requires a separate explicit choice, and deleting a Credential Manager secret requires `scripts/clear-credential.ps1`. Diagnostic export is local, bounded, and redacted; the user decides whether to share the resulting ZIP.
