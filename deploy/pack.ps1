# ============================================================
# wargame 部署打包脚本
# 打包三个发布物（zip，直接上传到 Linux 服务器即可）：
#   1. webapi  ->  wargame.WebApi.<rid>.zip   (dotnet publish)
#   2. web     ->  web.<stamp>.zip               (前端 dist)
#
# 用法:
#   ./pack.ps1                     # 全部打包（web + admin + webapi）
#   ./pack.ps1 -TraceScript        # 执行同时打印脚本每一行源码
#   ./pack.ps1 -SkipFrontend       # 只打包 webapi
#   ./pack.ps1 -SkipWebApi         # 只打包 web/admin
#   ./pack.ps1 -Target framework-dependent|self-contained
# ============================================================

[CmdletBinding()]
param(
    [switch]$SkipFrontend,
    [switch]$SkipWebApi,
    [ValidateSet("framework-dependent", "self-contained")]
    [string]$Target = "framework-dependent",
    [switch]$TraceScript  # 开启：运行时打印脚本源代码行
)

# 如果开启跟踪，显示脚本执行源码
if ($TraceScript) {
    Set-PSDebug -Trace 1
}

$ErrorActionPreference = "Stop"
$repoRoot      = Split-Path -Parent $PSScriptRoot
Write-Host "repoRoot: $repoRoot" -ForegroundColor Yellow
$webDir        = Join-Path $repoRoot "webfront"
Write-Host "webDir: $webDir" -ForegroundColor Yellow
$webapiDir     = Join-Path $repoRoot "server/HexFront.Api"
Write-Host "webapiDir: $webapiDir" -ForegroundColor Yellow
$outDir        = Join-Path $PSScriptRoot "dist"
Write-Host "outDir: $outDir" -ForegroundColor Yellow
$rid           = "linux-x64"
$stamp         = Get-Date -Format "yyyyMMdd_HHmmss"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Remove-IfExists([string]$Path) {
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function New-Zip($sourceDir, $zipPath) {
    Remove-IfExists $zipPath
    # 兼容 PowerShell 5.1：Compress-Archive 把目录内容打进去（末尾 * 取目录内全部）
    $parent = Split-Path -Parent $sourceDir
    $leaf   = Split-Path -Leaf $sourceDir
    Compress-Archive -Path (Join-Path $parent "$leaf\*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $size = [math]::Round(((Get-Item $zipPath).Length)/1MB, 2)
    Write-Host "  [OK] $zipPath  ($size MB)" -ForegroundColor Green
}

# ---------- 1. HexFront.Api ----------
if (-not $SkipWebApi) {
    Write-Host "`n[1/3] 发布 HexFront.Api ($rid, $Target) ..." -ForegroundColor Cyan
    $publishDir = Join-Path $outDir "webapi-publish"
    Write-Host "publishDir: $publishDir" -ForegroundColor Yellow
    Remove-IfExists $publishDir

    $selfContained = "false"
    if ($Target -eq "self-contained") { $selfContained = "true" }

    Write-Host 1.正在打包：Linux独立部署（包含运行时）
    $publishDir1 = Join-Path $publishDir "webapi-publish-linux-x64"
    dotnet publish "$webapiDir\HexFront.Api.csproj" -c Release -r linux-x64 --self-contained true -o $publishDir1
    $zip1 = Join-Path $outDir "WebApi.linux-x64.zip"
    New-Zip $publishDir1 $zip1

    Write-Host 2.正在打包：Linux框架依赖部署64
    $publishDir2 = Join-Path $publishDir "webapi-publish-linux-x64-fd"
    dotnet publish "$webapiDir\HexFront.Api.csproj" -c Release -r linux-x64 --self-contained false -o $publishDir2
    $zip2 = Join-Path $outDir "WebApi.linux-x64-fd.zip"
    New-Zip $publishDir2 $zip2

    Write-Host 3.正在打包：Linux框架依赖部署arm64
    $publishDir3 = Join-Path $publishDir "webapi-publish-linux-arm64-fd"
    dotnet publish "$webapiDir\HexFront.Api.csproj" -c Release -r linux-arm64 --self-contained false -o $publishDir3
    $zip3 = Join-Path $outDir "WebApi.linux-arm64-fd.zip"
    New-Zip $publishDir3 $zip3

    Write-Host 4.正在打包：macOS独立部署
    $publishDir4 = Join-Path $publishDir "webapi-publish-osx-x64"
    dotnet publish "$webapiDir\HexFront.Api.csproj" -c Release -r osx-x64 --self-contained true -o $publishDir4
    $zip4 = Join-Path $outDir "WebApi.osx-x64.zip"
    New-Zip $publishDir4 $zip4

    Write-Host 5.正在打包：Windows独立部署
    $publishDir5 = Join-Path $publishDir "webapi-publish-win-x64"
    dotnet publish "$webapiDir\HexFront.Api.csproj" -c Release -r win-x64 --self-contained true -o $publishDir5
    $zip5 = Join-Path $outDir "WebApi.win-x64.zip"
    New-Zip $publishDir5 $zip5

    Write-Host 6.正在打包：Windows框架依赖部署
    $publishDir6 = Join-Path $publishDir "webapi-publish-win-x64-fd"
    dotnet publish "$webapiDir\HexFront.Api.csproj" -c Release -r win-x64 --self-contained false -o $publishDir6
    $zip6 = Join-Path $outDir "webapi.win-x64-fd.zip"
    New-Zip $publishDir6 $zip6

    if ($LASTEXITCODE -ne 0) { throw "webapi publish 失败 (exit=$LASTEXITCODE)" }

    # 清理 dotnet publish（RID 模式）可能额外生成的嵌套 publish 目录，仅保留根产物
    $nested = "$publishDir\publish"
    Remove-IfExists $nested

}

# ---------- 2. web ----------
if (-not $SkipFrontend) {
    Write-Host "`n[2/3] 构建 web (公众站) ..." -ForegroundColor Cyan
    Push-Location $webDir
    try {
        npm run build
        Write-Host "npm run build" -ForegroundColor Cyan
        if ($LASTEXITCODE -ne 0) { throw "web 构建失败" }
    } finally { Pop-Location }

    $stage = Join-Path $outDir "web-dist"
    Remove-IfExists $stage
    Copy-Item -Path (Join-Path $webDir "dist") -Destination $stage -Recurse

    $zip = Join-Path $outDir "web.$stamp.zip"
    New-Zip $stage $zip
}

Write-Host "`n打包完成, 产物目录: $outDir" -ForegroundColor Green
Get-ChildItem $outDir -Filter "*.zip" | ForEach-Object { Write-Host "  - $($_.Name)" }

# 清理中间目录
Remove-IfExists (Join-Path $outDir "webapi-publish")
Remove-IfExists (Join-Path $outDir "web-dist")

# 关闭调试跟踪
if ($TraceScript) {
    Set-PSDebug -Trace 0
}