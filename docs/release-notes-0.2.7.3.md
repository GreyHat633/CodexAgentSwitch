# CodexAgentSwitch v0.2.7.3 Release Notes

- Profile editor now includes a per-Profile native auto-compaction policy.
- Available choices are `节省 · 150K`, `均衡 · 180K`, `连续 · 200K`, and `默认 · 约218K`.
- The previous Routing Mode selector is removed from the visible Profile editor.
- `RoutingMode` remains internally compatible with existing Profiles and applied snapshots.
- The selected compaction policy is written only when that Profile is explicitly applied to a project.
- Editing or saving a Profile never silently rewrites previously applied projects.
- Native-default mode omits `model_auto_compact_token_limit` and leaves the threshold to Codex.
- Existing user-owned project thresholds are preserved and take precedence without creating duplicate TOML keys.
- A new or reloaded project conversation is required before the updated threshold is expected to take effect.

CodexAgentSwitch v0.2.7.3 adds a per-Profile native context auto-compaction policy. Profiles can choose 150K, 180K, 200K, or the Codex native default (currently about 218K in the tested environment). The choice is deployed only when that Profile is explicitly applied to a project; CAS does not directly compact ordinary Codex Desktop conversations.
