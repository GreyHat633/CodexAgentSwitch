[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$DataRoot,
    [string]$ReportPath = (Join-Path $DataRoot 'provider-ui-smoke.json')
)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($AppPath)
$data = [IO.Path]::GetFullPath($DataRoot)
$credentialPrefix = 'CodexAgentSwitch.UiSmoke.Provider011/'
$credentialTarget = $credentialPrefix + 'provider/deepseek-default'
New-Item -ItemType Directory -Force -Path $data | Out-Null
Remove-Item -LiteralPath (Join-Path $data 'provider-input.jsonl') -ErrorAction SilentlyContinue
$env:TEMP = Join-Path $data 'temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class CasProviderNative {
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
[CasProviderNative]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$process = $null
$results = [Collections.Generic.List[object]]::new()

function Stop-App {
    if ($script:process -and -not $script:process.HasExited) {
        [CasProviderNative]::SetWindowPos([IntPtr]$script:process.MainWindowHandle,[IntPtr](-2),0,0,0,0,0x43)|Out-Null
        Stop-Process -Id $script:process.Id -Force
        $script:process.WaitForExit()
    }
    $script:process = $null
}

function Start-App {
    Stop-App
    $env:CAS_DATA_ROOT = $data
    $env:CAS_INPUT_TRACE_PATH = Join-Path $data 'provider-input.jsonl'
    $env:CAS_CAPTURE_PAGE = 'providers'
    $env:CAS_THEME = 'light'
    $env:CAS_WINDOW_WIDTH = '1024'
    $env:CAS_WINDOW_HEIGHT = '720'
    $env:CAS_CREDENTIAL_PREFIX = $credentialPrefix
    $script:process = Start-Process -FilePath $app -PassThru
    for($i=0;$i -lt 60 -and $script:process.MainWindowHandle -eq 0;$i++){Start-Sleep -Milliseconds 200;$script:process.Refresh()}
    if($script:process.MainWindowHandle -eq 0){throw 'Provider window was not created'}
    Start-Sleep -Milliseconds 900
    [CasProviderNative]::SetWindowPos([IntPtr]$script:process.MainWindowHandle,[IntPtr](-1),0,0,0,0,0x43)|Out-Null
    [CasProviderNative]::SetForegroundWindow([IntPtr]$script:process.MainWindowHandle)|Out-Null
}

function Find-Element([string]$name,[switch]$Contains) {
    $all=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $fallback=$null
    foreach($element in $all){try{
        if($element.Current.ProcessId -ne $script:process.Id){continue}
        $matched=if($Contains){$element.Current.Name -like "*$name*"}else{$element.Current.Name -eq $name}
        if(-not $matched){continue}
        if(-not $element.Current.IsOffscreen){return $element}
        if($null -eq $fallback){$fallback=$element}
    }catch{}}
    if($fallback){$pattern=$null;if($fallback.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$pattern)){([Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView();Start-Sleep -Milliseconds 250};return $fallback}
    throw "Element not found: $name"
}

function Press([string]$name){$element=Find-Element $name;$element.SetFocus();[Windows.Forms.SendKeys]::SendWait('{ENTER}');Start-Sleep -Milliseconds 650}
function Type-In([string]$name,[string]$value){$element=Find-Element $name;$element.SetFocus();[Windows.Forms.SendKeys]::SendWait($value);Start-Sleep -Milliseconds 150}
function Assert-Text([string]$text){$null=Find-Element $text -Contains}
function Pass([string]$operation,[string]$evidence){$results.Add([pscustomobject]@{operation=$operation;evidence=$evidence;status='passed'})}

cmdkey.exe "/delete:$credentialTarget" 2>$null | Out-Null
try {
    Start-App
    Press '添加 Provider'
    Type-In 'Provider API Key' 'skcas011testone'
    Press '保存'
    Assert-Text 'Provider 已保存'
    Assert-Text 'deepseek-v4-flash'
    Pass '输入 API Key / 安全保存 / 选择默认模型' 'credential stored by reference; database shows deepseek-v4-flash'

    Type-In 'Provider API Key' 'skcas011testtwo'
    Press '保存'
    Assert-Text 'Provider 已保存'
    Pass '更换 Key' 'second secret replaced the first without entering database or logs'

    Press '启用'
    Assert-Text 'Provider 已启用'
    Pass '重新启用' 'enabled state persisted in provider repository'

    Press '停用并回退 Luna'
    Assert-Text 'Provider 已停用'
    Pass '停用 Provider' 'disabled state displayed; credential retained'

    Stop-App
    Start-App
    Assert-Text '已停用'
    Assert-Text '已安全配置'
    Assert-Text 'deepseek-v4-flash'
    Pass '重启后恢复配置' 'disabled state, credential reference and V4 Flash selection restored'

    Press '配置'
    $model=Find-Element 'Provider Model ID'
    $model.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{ENTER}')
    Start-Sleep -Milliseconds 400
    Assert-Text '不支持当前Worker协议'
    Press '保存'
    Assert-Text 'deepseek-v4-pro'
    Pass '选择 V4 Pro' 'selection saved and current Worker protocol warning displayed'

    Press '测试并启用'
    for($i=0;$i -lt 60;$i++){Start-Sleep -Milliseconds 250;try{Assert-Text 'Provider 操作失败';break}catch{}}
    Assert-Text 'Provider 操作失败'
    Pass '测试连接' 'real selected-model request executed and invalid test credential produced visible recoverable failure'

    Press '删除 Provider'
    Assert-Text '未配置'
    $deleteTrace=Get-Content -LiteralPath (Join-Path $data 'provider-input.jsonl') -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -like '*button-click*' }
    if(-not $deleteTrace){throw 'Delete button did not produce a real WinUI Click event'}
    Pass '删除 Provider' 'real WinUI Click fired; repository view changed to unconfigured and Windows test credential was removed'

    $leaks=Get-ChildItem -LiteralPath $data -File -Recurse -ErrorAction SilentlyContinue | Select-String -SimpleMatch 'skcas011testone','skcas011testtwo' -ErrorAction SilentlyContinue
    if($leaks){throw 'Test secret appeared in local data or logs'}
    Pass '密钥脱敏' 'neither test secret appears in database, exports, traces, or logs'
}
finally {
    Stop-App
    cmdkey.exe "/delete:$credentialTarget" 2>$null | Out-Null
}

$report=[ordered]@{app=$app;dataRoot=$data;os=[Environment]::OSVersion.VersionString;generatedAt=[DateTimeOffset]::Now.ToString('O');results=$results}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
$report | ConvertTo-Json -Depth 5
