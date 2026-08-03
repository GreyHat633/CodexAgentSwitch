# Windows 10 UI acceptance

Primary host: Windows 10 Home China 22H2, build 19045, x64. Display: 1920×1080; `GetDpiForMonitor` returned 119 DPI (approximately 125%). All paths, temporary data, NuGet packages, protocol schemas, and screenshots used an E-drive root.

## Live checks

- Solution build: 0 warnings, 0 errors.
- 1024×720 minimum-window captures completed on the Windows 10 host at its real 125% display scale.
- Light and dark solid-color themes rendered without Mica, acrylic, system backdrop, custom title bar, or Snap Layout dependency.
- UI Automation on the Provider workflow found 38 elements and 15 keyboard-focusable controls; every focusable control had a non-empty accessible name.
- Twelve sequential Tab operations moved through named controls, including Provider test/configuration and navigation actions.
- Long Chinese labels and mixed Chinese/English Provider, Model ID, Token, App Server, and `unavailable` states wrapped or scrolled without clipping in the accepted captures.
- The manifest explicitly declares `PerMonitorV2, PerMonitor`; the main pages use scroll containers and support 1024×720 through 3840×2160 window bounds.

## Captures

- `phase7-dashboard-light-win10-1024x720.png`
- `phase7-providers-dark-win10-1024x720.png`
- `phase7-usage-light-win10-1024x720.png`
- `phase7-diagnostics-dark-win10-1024x720.png`

## Compatibility boundaries

- Live scale coverage is 125%, because changing the user's Windows scale requires a sign-out and was not performed. The 100%, 150%, and 200% rows are covered by Per-Monitor V2 declaration, scalable XAML units, minimum-size/scroll layout review, but are not claimed as live host proof.
- High Contrast has a complete system-color resource dictionary; the host was not switched into High Contrast during automated capture.
- Windows 11 was not the primary host. Compatibility is supported through the shared Windows 10 API floor and the absence of Windows 11-only visual/title-bar dependencies; it is not claimed as a live Windows 11 run.
