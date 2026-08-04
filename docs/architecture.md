# Architecture

Codex Agent Switch has exactly two production modes.

1. **原生 Codex 模式** writes the active Profile into a short-lived native Codex launch configuration and starts the original Codex CLI. Agent Switch controls the launch model, reasoning effort, approval/sandbox policy, Worker configuration and external Provider environment variable. The original Codex interface owns its own conversation lifecycle, delegation decisions and main-thread usage; Agent Switch does not claim to monitor or block those sessions.
2. **CodexAgentSwitch 模式** owns a persistent project → conversation → main Thread hierarchy. Each turn stores an immutable Profile snapshot, delegation decision, Worker result, provider/model endpoint metadata, Usage and message history before it is rendered in WinUI.

The managed execution path is:

```text
ActiveProfile → TaskProfileSnapshot → DelegationDecision
  → WorkerOrchestrator → ExternalProviderResolver
  → OpenAICompatibleWorkerAdapter → DeepSeek API
  → Sol review → messages / Usage ledger / SQLite archive
```

External Provider resolution uses the snapshot only. It never asks the native Codex model list whether DeepSeek exists, and it never silently substitutes Luna. A failure follows the selected Profile fallback rule or stops. The explicit Worker test button bypasses economic routing but still records the real Provider, model and endpoint.

Sol, Terra and Luna are stable **roles**, not a promise that every ChatGPT account has three matching model IDs. The App Server model catalog is read when a Profile editor opens and again before a managed thread or native launch is created. Unavailable roles are removed from the editor choices; a legacy Profile that still refers to one displays a clear warning and cannot be saved, launched or executed until the user selects an available role. No role is silently mapped to another model.

Profiles persist an approval mode:

| Profile choice | Codex approval policy | sandbox |
|---|---|---|
| 安全模式 | `untrusted` | `read-only` |
| 自动模式 | `on-request` | `workspace-write` |
| 完全自动 | `never` | `danger-full-access` |

External Workers remain HTTP text requests even in complete-auto mode; they do not acquire local filesystem or shell access through the Provider API.

The solution separates WinUI presentation, application policies, domain records, Windows infrastructure, a Runtime bootstrapper, and a recoverable setup executable. SQLite changes are additive and profiles/providers/credentials remain independent. Credentials remain in Windows Credential Manager; no API key is serialized into profiles, task history, logs, generated native launch TOML or audit JSON.
