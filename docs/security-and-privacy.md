# Security and privacy

- Provider secrets are stored as Windows generic credentials under `CodexAgentSwitch/<reference>`; database exports and logs contain only the reference.
- Provider URLs require HTTPS except loopback HTTP. Reserved transport headers and CR/LF injection are rejected.
- 401, 429, timeout, 404, and 5xx are categorized; the client does not perform an unbounded or hidden retry.
- Setup validates the payload SHA-256, blocks drive-root targets, stages extraction outside the final directory, and keeps recoverable backups.
- The official Windows App Runtime installer is bundled only by explicit release packaging, and its Authenticode signer must validate as Microsoft.
- Usage facts, estimates, and unavailable fields remain distinct. The app does not claim an exact counterfactual savings percentage.
- Local prompts, Provider responses, profile metadata, Task history, and usage remain on the configured local data root unless the user explicitly calls an external Provider.
