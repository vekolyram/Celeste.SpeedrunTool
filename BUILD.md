# 构建 SpeedRunTool（本地可复现）

本仓库从 `https://github.com/DemoJameson/Celeste.SpeedrunTool` (master, v3.27.22) 克隆，含 `DeepCloner` 子模块。

## 前置条件

| 依赖 | 说明 |
|---|---|
| .NET SDK | 9/10 均可（工程 target `net8.0`） |
| Celeste + Everest 安装 | 提供 `Celeste.dll` / `FNA.dll` / `MMHOOK_Celeste.dll` / `Mono.Cecil.dll` / `MonoMod.*.dll` |
| `..\CelesteTAS-EverestInterop\bin\CelesteTAS-EverestInterop.dll` | 工程把它作为**编译期**引用（`TasUtils.cs` 里的 `TAS.Module.CelesteTasSettings`）。从 `EverestAPI/CelesteTAS-EverestInterop` 克隆后 `dotnet build` 一次即可生成 |
| NuGet 包 | `JetBrains.Profiler.Api` / `Krafs.Publicizer` / `KeraLua` / `NLua` / `YamlDotNet`，首次 restore 需要能访问 nuget.org |

## 构建

```powershell
.\build.ps1                                        # 默认路径 + Release
.\build.ps1 -CelestePath "D:\Games\Celeste" -Configuration Debug
```

产物：仓库根目录 `SpeedrunTool.dll`（`OutputAsModStructure` target 会把 dll/pdb/everest.yaml/Dialog/Graphics 复制到仓库根，可直接当 Mod 目录用）。

## 装到游戏里试（会改 Mods\Cache，先备份）

```powershell
.\install-test.ps1           # 备份官方 dll 并换成本地构建
.\install-test.ps1 -Restore  # 还原官方 dll
```

Everest 从 `Mods\Cache\SpeedrunTool.SpeedrunTool.dll` 加载；`Mods\SpeedrunTool.zip` 保持原样，删掉 Cache 里的文件就会自动从 zip 重建，所以这个替换是可回滚的。

## 已知的构建注意点

- 工程里 `CelestePrefix` 默认猜 `../../../Celeste.dll`，本机不存在，必须用 `-p:CelestePrefix=...` 传进去。
- 编译会有若干 `MSB3277`（KeraLua 1.3.2 vs 1.4.7）与 `CS0618` 之类的告警，属正常，构建脚本已 `-nowarn`。
- `DeepCloner` 是 git submodule，克隆时要 `--recurse-submodules`。
- 仓库里部分源文件是**混合换行符**（作者本地编辑器残留）。改动 `SaveLoadAction.cs` / `SpeedrunToolSettings.cs` 时按 `.editorconfig`（`charset=utf-8-bom`, `end_of_line=crlf`）统一成了 CRLF+BOM，因此这两个文件的 `git diff` 看起来很大，用
  `git diff --ignore-cr-at-eol` 看真实改动（约 30 行）。
