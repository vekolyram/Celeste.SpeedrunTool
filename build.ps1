# 构建 SpeedRunTool（本地可复现）
# 用法:
#   .\build.ps1
#   .\build.ps1 -CelestePath "D:\Games\Celeste" -Configuration Debug

param(
    [string]$CelestePath = "C:\Program Files (x86)\Steam\steamapps\common\Celeste",
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $CelestePath 'Celeste.dll'))) {
    throw "找不到 Celeste.dll，请用 -CelestePath 指定 Celeste 安装目录: $CelestePath"
}

$tasDll = Join-Path $root '..\CelesteTAS-EverestInterop\bin\CelesteTAS-EverestInterop.dll'
if (-not (Test-Path $tasDll)) {
    throw @"
缺少编译期引用: $tasDll
先执行:
  git clone https://github.com/EverestAPI/CelesteTAS-EverestInterop.git ..\CelesteTAS-EverestInterop
  dotnet build ..\CelesteTAS-EverestInterop\CelesteTAS-EverestInterop\CelesteTAS-EverestInterop.csproj -c Release -p:CelestePrefix="$CelestePath"
"@
}

Write-Host "building SpeedrunTool ($Configuration) against $CelestePath ..." -ForegroundColor Cyan
dotnet build (Join-Path $root 'SpeedrunTool\SpeedrunTool.csproj') `
    -c $Configuration `
    -p:CelestePrefix="$CelestePath" `
    -v m `
    -nowarn:CS8632,CS0618,CS0649,CS0436,MSB3277

if ($LASTEXITCODE -ne 0) {
    throw "构建失败 (exit $LASTEXITCODE)"
}

Write-Host "`n输出: $(Join-Path $root 'SpeedrunTool.dll')" -ForegroundColor Green
