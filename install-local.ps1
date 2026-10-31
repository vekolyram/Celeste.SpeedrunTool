# 把本地构建的 SpeedRunTool 真正装进游戏（可回滚）
#
# 为什么需要这个脚本：
#   Everest 实际加载的是 Mods\Cache\SpeedrunTool.SpeedrunTool.dll —— 它是从
#   Mods\SpeedRunTool\ 文件夹或 Mods\SpeedrunTool.zip 里"构建/复制"出来的缓存。
#   直接替换 Cache 里的 DLL 会在下次启动时被重建覆盖（而且游戏运行时该文件被锁）。
#   所以正确做法是：替换 Mods\SpeedRunTool\ 与 zip 里的 DLL + 资源，再删掉 Cache 让 Everest 重建。
#
# 注意：Dialog\ 和 Graphics\ 也必须同步！只换 DLL 会导致新增的 i18n 键缺失
#       （菜单里显示成英文或 key 原文）。
#
# 用法:
#   .\install-local.ps1                    # 立刻安装（要求游戏已关闭）
#   .\install-local.ps1 -WaitForGameExit   # 等游戏退出后自动安装
#   .\install-local.ps1 -Restore           # 还原官方版本

param(
    [switch]$Restore,
    [switch]$WaitForGameExit,
    [string]$CelestePath = "C:\Program Files (x86)\Steam\steamapps\common\Celeste",
    [int]$WaitMinutes = 30
)

$ErrorActionPreference = 'Stop'

$modFolder = Join-Path $CelestePath 'Mods\SpeedRunTool'
$zipPath = Join-Path $CelestePath 'Mods\SpeedrunTool.zip'
$cacheDir = Join-Path $CelestePath 'Mods\Cache'
$cacheFiles = @(
    (Join-Path $cacheDir 'SpeedrunTool.SpeedrunTool.dll'),
    (Join-Path $cacheDir 'SpeedrunTool.SpeedrunTool.pdb'),
    (Join-Path $cacheDir 'SpeedrunTool.SpeedrunTool.sum')
)
$assetFolders = @('Dialog', 'Graphics')

function Wait-GameClosed {
    if (-not (Get-Process Celeste -ErrorAction SilentlyContinue)) { return }
    if (-not $WaitForGameExit) {
        throw "Celeste 正在运行，Cache 里的 DLL 被占用。请先关闭游戏，或加 -WaitForGameExit 让脚本等待。"
    }
    Write-Host "等待 Celeste 退出（最多 $WaitMinutes 分钟）..." -ForegroundColor Yellow
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while (Get-Process Celeste -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw "等待超时，未安装。" }
        Start-Sleep -Seconds 3
    }
    Write-Host "游戏已退出，开始安装。" -ForegroundColor Green
    Start-Sleep -Seconds 2
}

function Clear-Cache {
    foreach ($f in $cacheFiles) {
        if (Test-Path $f) {
            Remove-Item $f -Force
            Write-Host "  删除缓存 $([IO.Path]::GetFileName($f))"
        }
    }
}

function Backup-AssetFolder([string]$name) {
    $dst = Join-Path $modFolder $name
    if ((Test-Path $dst) -and -not (Test-Path "$dst.orig")) {
        Copy-Item $dst "$dst.orig" -Recurse -Force
    }
}

function Sync-AssetFolder([string]$name) {
    $src = Join-Path $PSScriptRoot $name
    $dst = Join-Path $modFolder $name
    if (-not (Test-Path $src)) { return }
    Backup-AssetFolder $name
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    Copy-Item $src $dst -Recurse -Force
    $count = (Get-ChildItem $dst -Recurse -File | Measure-Object).Count
    Write-Host "  已同步 $name\ ($count 个文件)"
}

function Restore-AssetFolder([string]$name) {
    $orig = Join-Path $modFolder "$name.orig"
    $dst = Join-Path $modFolder $name
    if (-not (Test-Path $orig)) { return }
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    Copy-Item $orig $dst -Recurse -Force
    Write-Host "  还原 $name\"
}

function Set-ZipEntry($zip, [string]$entryName, [string]$srcPath) {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq $entryName }
    if ($entry) { $entry.Delete() }
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $srcPath, $entryName)
}

# ---------------------------------------------------------------- 还原
if ($Restore) {
    Wait-GameClosed
    foreach ($name in 'SpeedrunTool.dll', 'SpeedrunTool.pdb', 'everest.yaml') {
        $orig = Join-Path $modFolder "$name.orig"
        $dest = Join-Path $modFolder $name
        if (Test-Path $orig) {
            Copy-Item $orig $dest -Force
            Write-Host "  还原 $name"
        }
    }
    foreach ($folder in $assetFolders) { Restore-AssetFolder $folder }
    if (Test-Path "$zipPath.orig") {
        Copy-Item "$zipPath.orig" $zipPath -Force
        Write-Host "  还原 SpeedrunTool.zip"
    }
    Clear-Cache
    Write-Host "已还原官方版本（*.orig 备份保留）。" -ForegroundColor Green
    return
}

# ---------------------------------------------------------------- 安装
$localDll = Join-Path $PSScriptRoot 'SpeedrunTool.dll'
$localPdb = Join-Path $PSScriptRoot 'SpeedrunTool.pdb'
if (-not (Test-Path $localDll)) { throw "先运行 .\build.ps1 生成本地构建" }

Wait-GameClosed

# 1) 解包 mod 目录（Everest 优先用它）
if (-not (Test-Path $modFolder)) { throw "找不到 $modFolder" }
foreach ($name in 'SpeedrunTool.dll', 'SpeedrunTool.pdb', 'everest.yaml') {
    $p = Join-Path $modFolder $name
    if ((Test-Path $p) -and -not (Test-Path "$p.orig")) { Copy-Item $p "$p.orig" -Force }
}
Copy-Item $localDll (Join-Path $modFolder 'SpeedrunTool.dll') -Force
if (Test-Path $localPdb) { Copy-Item $localPdb (Join-Path $modFolder 'SpeedrunTool.pdb') -Force }
Write-Host "  已替换 Mods\SpeedRunTool\SpeedrunTool.dll"

# 1b) 同步 Dialog\ / Graphics\（i18n 和贴图都在这里，漏了菜单就会显示英文或 key 原文）
foreach ($folder in $assetFolders) { Sync-AssetFolder $folder }

# 2) 版本号标成 3.27.22，方便在日志/Mod 列表里认出本地构建
$yaml = Get-Content (Join-Path $modFolder 'everest.yaml') -Raw
$yaml = $yaml -replace 'Version:\s*3\.27\.\d+', 'Version: 3.27.22'
Set-Content (Join-Path $modFolder 'everest.yaml') $yaml -NoNewline
Write-Host "  everest.yaml 版本 -> 3.27.22"

# 3) zip 也同步替换（万一 Everest 从 zip 重建）
if (Test-Path $zipPath) {
    if (-not (Test-Path "$zipPath.orig")) { Copy-Item $zipPath "$zipPath.orig" -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Update')
    try {
        foreach ($entryName in 'SpeedrunTool.dll', 'SpeedrunTool.pdb', 'everest.yaml') {
            $src = Join-Path $modFolder $entryName
            if (Test-Path $src) { Set-ZipEntry $zip $entryName $src }
        }

        foreach ($folder in $assetFolders) {
            $root = Join-Path $modFolder $folder
            if (-not (Test-Path $root)) { continue }
            foreach ($file in Get-ChildItem $root -Recurse -File) {
                $rel = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
                Set-ZipEntry $zip "$folder/$rel" $file.FullName
            }
        }
    }
    finally { $zip.Dispose() }
    Write-Host "  已同步 zip 内的 DLL 与资源"
}

# 4) 清掉缓存，强制 Everest 重建
Clear-Cache

Write-Host ""
Write-Host "安装完成。启动游戏后确认:" -ForegroundColor Green
Write-Host "  log.txt 里应出现  'GcCompactLoh: False' 与 'ProfileSaveLoad: False'（官方版没有这两行）"
Write-Host "  版本号应显示 3.27.22"
Write-Host "  Mod 选项里应有 'Performance / 性能' 子菜单，且中文正常"
