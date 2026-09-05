# CodexAgentSwitch v0.2.7.4 Release Notes

- Added GPT-6 Astra as both a main-agent role and a native Worker role.
- New Profiles default to Astra for the main agent and retain Luna as the economic Worker; existing Profiles are not rewritten.
- Profile and onboarding selectors now intersect Astra, Sol, Terra and Luna with the current account's live Codex App Server `model/list` response.
- Reasoning-effort choices come from the selected model's live capability record, including `max` or `ultra` only when the account advertises them.
- Unavailable persisted models and efforts are preserved, shown as unavailable, and rejected without silent fallback.
- Native Astra delegation generates `cas_astra_worker` with `agents/cas-astra-worker.toml` and model `gpt-6-astra`.
- Launch and execution paths validate both the exact model and exact reasoning effort immediately before use.
- Economic audit rates cover Astra, Sol, Terra and Luna. Sol-equivalent cost is recomputed from token dimensions rather than a scalar multiplier; API list-price estimates are explicitly not Codex subscription quota measurements.

This release keeps Profile schema v5 and the existing context-compaction behavior. Historical release reports and artifacts are unchanged.
