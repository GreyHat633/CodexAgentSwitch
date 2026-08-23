# Codex Agent Switch 0.2.7.0 acceptance

Status: **GO** on 2026-08-22 (Asia/Shanghai).

## Product semantics

- Managed Context Economy is an intrinsic CAS capability. There is no project-level Context Economy switch and no new project UI entry.
- Eligibility requires an active registered project with a valid Applied Snapshot plus a CAS-created task whose project, canonical root, thread, App Server session, connection instance, and ownership lease all match.
- CAS-managed tasks rooted at the registered project root or below it are eligible. Ordinary Codex Desktop tasks have no CAS task binding and remain unobserved even when App Server emits events for them.
- Hooks and Hard Gate remain frozen and are not a production dependency.

## Automated and build evidence

- Core tests: 388 passed, 0 failed, 0 skipped.
- Bootstrapper tests: 19 passed, 0 failed, 0 skipped.
- Release solution build: 0 warnings, 0 errors.
- C-drive release audit: passed. Evidence: `E:\AISPace\Codex Agent Switch Project\.tmp\c-drive-release-audit-20260822-030301\result.json`.

## Real managed App Server acceptance

Evidence: `E:\AISPace\CAS-test-root-0270\live-auto\managed-context-live-eaf26bc2b98b44f388c7ec14635133e3\managed-context-acceptance.json`.

- Managed thread: `01a025a4-5087-77f2-8bc3-31682cef6e81`.
- Compaction request: `04b6db3d-7238-4739-9c21-cffefe5b85de`.
- Same-thread native `contextCompaction` started and completed.
- Pre-compaction input median: 133,007 tokens.
- Post-compaction input median: 21,643.5 tokens.
- Input reduction: 83.7%; classification `Effective`; final state `Cooldown`.
- The same process ran unmanaged thread `01a025a6-e37a-7760-8525-d1d387e315b3`; its binding and Context Economy state were both null.

The event contract follows the official Codex App Server lifecycle for token usage, thread status, turns/items, approvals, and native compaction: <https://learn.chatgpt.com/docs/app-server>.

## Windows UI evidence

- Actual DPI: 119 (Windows 125% scaling class); XAML rasterization scale: 1.2395833.
- 1024 DIP long-text dashboard layout passed with zero title/action overlap.
- Project menu retained the original four entries and contained zero Context Economy entries.
- Layout report: `E:\AISPace\CAS-test-root-0270\ui-final-package\layout-ui-smoke-light-1024.json`.
- Layout screenshot: `E:\AISPace\CAS-test-root-0270\ui-final-package\environment-light-1024.png`.
- Project-menu report: `E:\AISPace\CAS-test-root-0270\ui-final-package\native-project-original-menu.json`.

## Release artifacts

Release directory: `E:\AISPace\Codex Agent Switch Project\artifacts\release\0.2.7.0`.

| File | SHA-256 |
|---|---|
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | `1f69225388cce39320667da3984548e9db8eb55a638b7884a75a6b6de3d03f76` |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | `390546030baabfafbba01154f3407bde19284a713a797a862058a4d7830ef1d2` |
| `CodexAgentSwitch-win10-x64.zip` | `132806cc6b7b83ebd84e2234ff7d616c1315e7bb8403a077c04bdaa741cdc249` |
| `CodexAgentSwitch-win10-x64.zip.sha256` | `14ae5bc14ed3247559984ef2339ca13b416b3235771870e6df2e4d7a336534c9` |

Independent verification passed for every manifest hash, normalized ZIP separators, required ZIP entries, portable sidecar checksum, and file versions of App, ToolHost, CredentialBroker, Bootstrapper, and Setup (`0.2.7.0`). The bundled Windows App Runtime installer has a valid Microsoft Corporation Authenticode signature. Evidence: `E:\AISPace\CAS-test-root-0270\release-verification.json`.

Older release directories `0.2.6.2`, `0.2.6.3`, and `0.2.6.4` were retained unchanged. They are superseded, not deleted or repackaged.
