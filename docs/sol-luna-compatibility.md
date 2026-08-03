# Existing Sol-Luna compatibility

The existing project-level `tools/luna-orchestrator`, Worker Policy, schema files, registry, and Task/Result contracts are not modified by this desktop application. Native Luna remains reachable through the generic native Codex adapter, while Luna-specific policy stays behind the compatibility boundary instead of leaking into common Provider or Worker records.

Single-agent mode uses zero workers. The fallback path selects native Luna only when the Profile explicitly permits it and preserves the original delegation, ownership, final-read, Usage-before-delete, and adoption semantics. Removing Codex Agent Switch or selecting the original Sol-Luna Profile does not migrate or delete existing orchestrator Threads.
