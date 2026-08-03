# Architecture

Codex Agent Switch uses six separable projects: WinUI presentation, application policies, domain records, Windows infrastructure, a runtime bootstrapper, and a recoverable setup executable. The target is .NET 8 and WinUI 3 on Windows 10 22H2 x64, with Windows 11 compatibility kept additive.

`IWorkerAdapter` is the common lifecycle boundary for native Codex and external OpenAI-compatible workers. Native workers communicate with the currently installed Codex App Server through generated JSON-RPC Schema; external workers use typed one-shot HTTP operations. Delegation is gated by replaceability, non-overlapping Scope, concurrency, Provider availability, and budget. Results, Usage, final Thread state, and adoption are persisted before an owned Thread can be deleted.

The UI consumes explicit solid light/dark/high-contrast resources and does not initialize Mica, acrylic, system backdrops, custom title bars, or Snap Layout APIs. Runtime-dependent and self-contained delivery modes are isolated from application policy. See `docs/architecture.md`, `docs/phase0-audit.md`, and `docs/protocol-compatibility.md`.
