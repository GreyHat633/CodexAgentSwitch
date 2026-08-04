# Runtime deployment

The bootstrapper is a standalone, multi-file, self-contained `net8.0-windows10.0.19041.0` x64 WinForms application. It has no Microsoft.WindowsAppSDK or WinUI dependency. On the Windows 10 22H2 primary acceptance host, the main WinUI app is deployed as a stable multi-file framework-dependent application because WinUI self-contained deployment fails during XAML initialization. The compact release keeps the main app in a child directory so its files remain separate from the bootstrapper.

## Bundle layout

```text
CodexAgentSwitch.Bootstrapper.exe
CodexAgentSwitch.Bootstrapper.dll
CodexAgentSwitch.Bootstrapper.deps.json
CodexAgentSwitch.Bootstrapper.runtimeconfig.json
App/CodexAgentSwitch.App.exe
App/CodexAgentSwitch.App.dll
RuntimeInstaller/WindowsAppRuntimeInstall-x64.exe
```

The bootstrapper also accepts the legacy layout where `CodexAgentSwitch.App.exe` is beside it. None of the 0.1.3 entry points is a .NET single-file bundle, so ordinary startup does not extract the application payload into `%TEMP%\.net`.

The main app requires the .NET 8 x64 Desktop Runtime and the Windows App Runtime 1.8 x64. The installer must be an already bundled, official, signed x64 Windows App Runtime installer. The bootstrapper never downloads software and never searches outside its application directory.

At startup it separately checks Windows build/architecture and the Windows App Runtime inventory. Windows 10 22H2 (build 19045) and Windows 11 x64 are accepted. Runtime matching requires a complete x64 Windows App Runtime 1.8 set: Framework, Main, Singleton, and DDLM packages with the same package version. A Framework-only registration is reported as incomplete instead of producing a false-positive. Absent, incomplete, and same-major-but-too-old states are reported differently.

The installer button is enabled only when the OS is supported and the required runtime is not present. Clicking it opens an explicit confirmation dialog showing the exact bundled path. Declining does nothing. No installation occurs during inspection, development, or tests. After installation, restart or re-open the bootstrapper and re-check status.

If the main app previously reported `0x80670016`, do not infer success from a major-version-only check: verify the exact x64 runtime registration and deployment mode. This code reports the missing/mismatch state and starts only the bundled installer; it does not silently repair or elevate it.

## Validation

```powershell
dotnet build src/CodexAgentSwitch.Bootstrapper/CodexAgentSwitch.Bootstrapper.csproj -c Release -p:Platform=x64 --nologo
dotnet test tests/CodexAgentSwitch.Bootstrapper.Tests/CodexAgentSwitch.Bootstrapper.Tests.csproj -c Release --nologo
```
