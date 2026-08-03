# Architecture

The solution separates WinUI presentation, application policies, domain records, Windows infrastructure, a Runtime bootstrapper, and a recoverable setup executable. `IWorkerAdapter` provides the shared native/external lifecycle. Native Codex uses the installed CLI’s generated App Server Schema and JSON-RPC stdio. External providers use one-shot OpenAI-compatible HTTP requests with typed failures and Credential Manager lookups.

Delegation passes through replaceability, Scope, concurrency, Provider, and budget gates. Active Scope writes cannot overlap. Adoption is immutable after review, and full takeover requires `rejected`. Usage/result/adoption persistence precedes Thread deletion.
