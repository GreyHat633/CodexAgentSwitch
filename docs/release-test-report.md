# Codex Agent Switch 0.1.0 release test report

## Host and target

- Primary live host: Microsoft Windows 10 Home China, 22H2, build 19045, x64.
- Framework: .NET 8; UI: WinUI 3 / Windows App SDK 1.8.260710003.
- Windows 11 is an enhanced-compatibility target; no Windows 11-only API or visual material is required.

## Build and automated verification

- `scripts/build.ps1 -Configuration Release`: passed, 0 warnings, 0 errors.
- Main test suite: 45/45 passed.
- Bootstrapper suite: 18/18 passed, including current Win10 runtime inventory integration.
- Current Codex App Server integration: 43/43 passed with the E-drive Codex executable explicitly selected; includes generated Schema, model discovery, worker lifecycle, terminal read/delete ordering, and three parallel independent workers.
- Forbidden visual API audit: no source hits for Mica/Acrylic/SystemBackdrop/custom title bar/Snap Layout APIs; documentation mentions are explanatory only.

## Win10 UI acceptance

- Live solid light and dark captures completed at 1024x720 on the primary Win10 host at its actual 125% scale.
- The final installed self-contained application generated the dark diagnostics capture `artifacts/acceptance/installed-dashboard-win10-v4.png` and exited 0.
- UI Automation acceptance found 38 elements, 15 focusable elements, 0 unnamed focusable elements, and 12 named Tab stops.
- Segoe UI resources fall back from Segoe UI Variable to Segoe UI. High Contrast uses explicit system-color resources.
- Static layout/resource review covers 100%, 150%, and 200%; live switching was only performed at 125%. High Contrast and Windows 11 were not live-tested on this host.

## Packaging, install, and rollback

- Final Setup bundle was extracted into a clean E-drive fixture. SHA-256 validation, fresh install, installed-app launch, install-manifest hash comparison, and recoverable uninstall all exited 0.
- Upgrade tests preserve the existing `data` directory and keep the old program in a timestamped backup. Failure before commit leaves the target unchanged.
- Start Menu shortcut creation/removal is implemented and passed the v4 install/uninstall acceptance. The Programs directory was redirected to E: with `CAS_START_MENU_ROOT`; the test did not write the real C: Start Menu.
- Uninstall preserves data by default. `--delete-data` and the matching UI checkbox are separately labelled destructive choices. Credentials are never silently deleted.
- Locally built app/Setup executables are unsigned because no publisher certificate was available; SHA-256 release manifests are supplied.

## Windows App Runtime

- The compact bundle contains the official x64 Windows App Runtime 1.8.10 installer. Packaging requires a valid Microsoft Authenticode signer.
- The current host has 1.8 Framework registrations but lacks a matching complete Main/Singleton/DDLM set. The bootstrapper correctly reports `incomplete`, enables explicit installation, and disables framework-dependent app launch.
- No runtime installation was performed during acceptance; the confirmation boundary was preserved.

Microsoft deployment references: <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview>, <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app>, and <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deployment-architecture>.

## Release boundaries

- Live Windows 11, 100%/150%/200% DPI switching, High Contrast, screen-reader narration, real DeepSeek billing/429/insufficient-balance responses, and production certificate/SmartScreen reputation remain environment-dependent manual coverage.
- A first-party app update service is intentionally deferred by the Plan. Versioned Setup payloads, hashes, backups, and install manifests are the reserved manual-update boundary for 0.1.0.
