# ============================================================
# HanJianNet 部署打包脚本
# 打包三个发布物（zip，直接上传到 Linux 服务器即可）：
#   1. webapi  ->  HanJianNet.WebApi.<rid>.zip   (dotnet publish)
#   2. web     ->  web.<stamp>.zip               (前端 dist)
#   3. admin   ->  admin.<stamp>.zip             (前端 dist)
#
# 用法:
#   ./pack.ps1                     # 全部打包（web + admin + webapi）
#   ./pack.ps1 -TraceScript        # 执行同时打印脚本每一行源码
#   ./pack.ps1 -SkipFrontend       # 只打包 webapi
#   ./pack.ps1 -SkipWebApi         # 只打包 web/admin
#   ./pack.ps1 -Target framework-dependent|self-contained
#
# 前置条件:
#   - .NET 8 SDK (用于 webapi 发布)
#   - Node.js + npm (admin/web 已 install 过 node_modules)
#   - 产物输出到 ./deploy/dist/ 目录, 文件名带时间戳
#
# 服务器托管: webapi 由 systemd 托管，单元模板见 ./hanjian-webapi.service
#   （含 ASPNETCORE_ENVIRONMENT=Production 与崩溃自动重启配置）
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
$adminDir      = Join-Path $repoRoot "admin"
$webDir        = Join-Path $repoRoot "web"
$webapiDir     = Join-Path $repoRoot "webapi"
$outDir        = Join-Path $PSScriptRoot "dist"
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

# ---------- 1. webapi ----------
if (-not $SkipWebApi) {
    Write-Host "`n[1/3] 发布 webapi ($rid, $Target) ..." -ForegroundColor Cyan
    $publishDir = Join-Path $outDir "webapi-publish"
    Remove-IfExists $publishDir

    $selfContained = "false"
    if ($Target -eq "self-contained") { $selfContained = "true" }

    dotnet publish "$webapiDir\HanJianNet.WebApi.csproj" -c Release -r $rid --self-contained $selfContained -o $publishDir
    Write-Host "dotnet publish $webapiDir\HanJianNet.WebApi.csproj -c Release -r $rid --self-contained $selfContained -o $publishDir" -ForegroundColor Cyan

    if ($LASTEXITCODE -ne 0) { throw "webapi publish 失败 (exit=$LASTEXITCODE)" }

    # 清理 dotnet publish（RID 模式）可能额外生成的嵌套 publish 目录，仅保留根产物
    $nested = "$publishDir\publish"
    Remove-IfExists $nested

    $zip = Join-Path $outDir "HanJianNet.WebApi.$rid.$stamp.zip"
    New-Zip $publishDir $zip
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

    # ---------- 3. admin ----------
    Write-Host "`n[3/3] 构建 admin (后台管理) ..." -ForegroundColor Cyan
    Push-Location $adminDir
    try {
        npm run build
        Write-Host "npm run build" -ForegroundColor Cyan
        if ($LASTEXITCODE -ne 0) { throw "admin 构建失败" }
    } finally { Pop-Location }

    $stage = Join-Path $outDir "admin-dist"
    Remove-IfExists $stage
    Copy-Item -Path (Join-Path $adminDir "dist") -Destination $stage -Recurse

    $zip = Join-Path $outDir "admin.$stamp.zip"
    New-Zip $stage $zip
}

Write-Host "`n打包完成, 产物目录: $outDir" -ForegroundColor Green
Get-ChildItem $outDir -Filter "*.zip" | ForEach-Object { Write-Host "  - $($_.Name)" }

# 清理中间目录
Remove-IfExists (Join-Path $outDir "webapi-publish")
Remove-IfExists (Join-Path $outDir "web-dist")
Remove-IfExists (Join-Path $outDir "admin-dist")

# 关闭调试跟踪
if ($TraceScript) {
    Set-PSDebug -Trace 0
}