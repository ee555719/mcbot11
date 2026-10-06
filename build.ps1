# ============================================================
#  McBot11 构建脚本（Windows 10 x64 / .NET 8 SDK）
#  用法：
#    .\build.ps1            # 编译（Debug）
#    .\build.ps1 -Release   # 编译（Release）
#    .\build.ps1 -Run       # 编译后启动程序
# ============================================================
param(
    [switch]$Release,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$config = if ($Release) { "Release" } else { "Debug" }
$solution = Join-Path $PSScriptRoot "McBot11.sln"

Write-Host "==> dotnet build ($config)" -ForegroundColor Cyan
dotnet build $solution -c $config
if ($LASTEXITCODE -ne 0) {
    Write-Host "构建失败 (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $PSScriptRoot "src\McBot11.App\bin\$config\net8.0-windows\McBot11.App.exe"
Write-Host "构建成功: $exe" -ForegroundColor Green

if ($Run) {
    Write-Host "==> 启动 McBot11" -ForegroundColor Cyan
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
}
