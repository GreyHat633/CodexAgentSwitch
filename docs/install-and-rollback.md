# Install, upgrade, rollback, and uninstall

## Recommended Win10 x64 installer bundle

Extract the complete `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` to an E-drive folder and run `CodexAgentSwitch.Setup.exe` without separating it from its `.dll`, `.deps.json`, `.runtimeconfig.json`, native runtime files, payload ZIP, or checksum. The setup validates the adjacent payload ZIP against its SHA-256 before changing the target. On this host the default target is `E:\Apps\Codex Agent Switch`.

An upgrade first moves the complete old installation to a timestamped `.backup-*` directory, installs the new payload, then moves the existing `data` directory back. A failure restores the old directory. Setup creates a per-user Start Menu shortcut after the payload is committed. “可恢复卸载” moves the installation to `.removed-*`; by default it does not erase user data or credentials. The UI has a separate, clearly labelled option to delete local `data`; Windows credentials always require the dedicated credential-clear command.

Silent acceptance example:

```powershell
.\CodexAgentSwitch.Setup.exe --install --payload .\CodexAgentSwitch-win10-x64.zip --target 'E:\Apps\Codex Agent Switch'
.\CodexAgentSwitch.Setup.exe --uninstall --target 'E:\Apps\Codex Agent Switch'
# Destructive local-data choice; credentials are still retained:
.\CodexAgentSwitch.Setup.exe --uninstall --delete-data --target 'E:\Apps\Codex Agent Switch'
```

The Start Menu shortcut normally lives below `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Codex Agent Switch`. Tests set `CAS_START_MENU_ROOT` to an E-drive fixture and never write the real user Start Menu.

## Runtime-aware compact bundle

`CodexAgentSwitch-compact-runtime-win10-x64.zip` contains the framework-dependent WinUI app under `App\`, the independent multi-file self-contained WinForms bootstrapper at the root, and Microsoft’s signed x64 `WindowsAppRuntimeInstall-x64.exe`. Keep the complete directory tree together and start `CodexAgentSwitch.Bootstrapper.exe`; it checks Win10 build/architecture and actual HKCU/HKLM App Runtime 1.8 package registration. It never downloads or launches the Runtime installer without explicit confirmation.

## Backup and credentials

```powershell
pwsh -File .\scripts\backup-data.ps1 -DataRoot 'E:\Apps\Codex Agent Switch\data'
pwsh -File .\scripts\clear-credential.ps1 -ReferenceId 'provider/deepseek-default' -WhatIf
```

Clearing an API Key is separate from uninstall so an accidental uninstall cannot silently destroy credentials.
