<#
  在自己的電腦上從原始碼建置並安裝 MyFences。

  為什麼要這樣做：從網路下載的 exe 會帶著「從網際網路下載」的標記，而 MyFences 沒有程式碼簽章、
  下載的人也很少，所以會被 Chrome / Edge 與 Windows SmartScreen 攔下來。
  在自己電腦上用原始碼建置出來的 exe 沒有這個標記，就不會被攔。

  用法（在 repo 資料夾裡開 PowerShell）：
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1

  會做的事：
    1. 沒有 .NET 8 SDK 就用 winget 安裝（微軟官方套件）
    2. 建置 MyFences，放到 %LOCALAPPDATA%\MyFences
    3. 在開始功能表建立捷徑，然後啟動 MyFences
  之後要更新：拿到新版原始碼後再執行一次就好（設定檔不會被動到）。
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'MyFences')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\MyFences\MyFences.csproj'

function Test-DotNet8Sdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    return [bool](dotnet --list-sdks 2>$null | Select-String '^8\.')
}

Write-Host '== MyFences 安裝 ==' -ForegroundColor Cyan

if (-not (Test-Path $project)) {
    throw "找不到 $project，請在 MyFences 的 repo 資料夾裡執行這個腳本。"
}

if (-not (Test-DotNet8Sdk)) {
    Write-Host '找不到 .NET 8 SDK，用 winget 安裝中（第一次需要幾分鐘）...'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw '這台電腦沒有 winget。請先到 https://dotnet.microsoft.com/download/dotnet/8.0 安裝 .NET 8 SDK，再執行一次。'
    }
    winget install --id Microsoft.DotNet.SDK.8 --exact --accept-source-agreements --accept-package-agreements
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    if (-not (Test-DotNet8Sdk)) { throw '.NET 8 SDK 安裝後還是找不到，請重開 PowerShell 再執行一次。' }
}

# 正在執行的 MyFences 要先結束，才能覆蓋 exe（用系統匣「結束」，桌面圖示和設定才會正確還原）
if (Get-Process -Name MyFences -ErrorAction SilentlyContinue) {
    Write-Host 'MyFences 正在執行。請先在系統匣的 MyFences 圖示按右鍵 →「結束」，再按 Enter 繼續。' -ForegroundColor Yellow
    Read-Host | Out-Null
    if (Get-Process -Name MyFences -ErrorAction SilentlyContinue) { throw 'MyFences 還在執行，已取消安裝。' }
}

Write-Host "建置中，輸出到 $InstallDir ..."
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) { throw '建置失敗，請把上面的錯誤訊息貼給我。' }

$exe = Join-Path $InstallDir 'MyFences.exe'

# 開始功能表捷徑
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'MyFences.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = 'MyFences 桌面分區整理工具'
$shortcut.Save()

Write-Host "完成！已安裝到 $exe，開始功能表也有捷徑。" -ForegroundColor Green
Start-Process $exe
