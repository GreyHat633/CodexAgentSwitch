# Runtime deployment

The bootstrapper is a standalone `net8.0-windows10.0.19041.0` x64 WinForms executable. It has no Microsoft.WindowsAppSDK or WinUI dependency. Development remains self-contained; the compact framework-dependent release layout may place the bootstrapper beside the main app.

## Bundle layout

```text
CodexAgentSwitch.Bootstrapper.exe
CodexAgentSwitch.App.exe
RuntimeInstaller/WindowsAppRuntime-1.8-x64.exe
```

The installer must be an already bundled, official, signed x64 Windows App Runtime installer. The bootstrapper never downloads software and never searches outside its application directory.

At startup it separately checks Windows build/architecture and the Windows App Runtime inventory. Windows 10 22H2 (build 19045) and Windows 11 x64 are accepted. Runtime matching requires x64 Windows App Runtime 1.8 or newer; absent and same-major-but-too-old states are reported differently.

The installer button is enabled only when the OS is supported and the required runtime is not present. Clicking it opens an explicit confirmation dialog showing the exact bundled path. Declining does nothing. No installation occurs during inspection, development, or tests. After installation, restart or re-open the bootstrapper and re-check status.

If the main app previously reported `0x80670016`, do not infer success from a major-version-only check: verify the exact x64 runtime registration and deployment mode. This code reports the missing/mismatch state and starts only the bundled installer; it does not silently repair or elevate it.

## Validation

```powershell
dotnet build src/CodexAgentSwitch.Bootstrapper/CodexAgentSwitch.Bootstrapper.csproj -c Release -p:Platform=x64 --nologo
dotnet test tests/CodexAgentSwitch.Bootstrapper.Tests/CodexAgentSwitch.Bootstrapper.Tests.csproj -c Release --nologo
```
