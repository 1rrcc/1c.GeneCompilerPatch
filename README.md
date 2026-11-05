# UniversalGeneCompiler Patch

针对工坊模组 **UniversalGeneCompiler**（`Chezhou.UniversalGeneCompiler`，创意工坊 3787321674）
的修复 / 增强补丁包。

原模组只有编译后的 DLL 和 XML、没有源码，所以本仓库对它**没有任何编译期依赖**：
所有对原模组类型的访问都走 `AccessTools.TypeByName` 反射，并且每一项独立 try/catch ——
原模组更新后即使内部实现变了，失效的也只是对应那一条修复（打一条日志），不会拖垮整个补丁包。

- packageId：`1c.GeneCompilerPatch`
- 目标版本：RimWorld 1.6（Biotech）
- 依赖：UniversalGeneCompiler、Harmony（`brrainz.harmony`）

## 改了什么

| # | 内容 | 实现方式 |
|---|---|---|
| 1 | 内置营养池：建筑吃食物，按舱内人员的饱食度缺口扣营养（仿原版培育仓，搬运工自动补料） | XML 挂原版 `CompRefuelable` + 自研 comp |
| 2 | 提取冷却结束后自动把人送回舱（殖民者自己走，囚犯由搬运工搬入），建筑上可开关 | 自研 comp |
| 3 | 建筑转到南向时，舱内人员位置 / 朝向不跟着转 | 补 `_south` 贴图 + Harmony 修正偏移与角度 |
| 4 | 两个 StatDef 出现在所有物品上 | XML patch |
| 5 | 把人搬进机器的 WorkGiver 优先级 90 → 160 | XML patch |
| 6 | 定向提取冷却时长可调（默认 1 天，0 = 不冷却） | Harmony postfix |
| 7 | 提取改成计时作业（默认 6 游戏内小时，可调，0 = 关闭），到点入库并**自动弹出**；中途手动弹出会先弹确认窗；**断电暂停** | 自研 comp + Harmony prefix |
| 8 | 超凡基因注入单价可调（默认 10000 白银 / 个，0 = 不额外收费）；普通基因仍按复杂度计费 | Harmony postfix（改 `GetInjectionPrice`） |

## 仓库结构

```
About/                     模组元数据
1.6/Patches/               XML patch（挂 comp、改 StatDef / WorkGiver）
1.6/Languages/             中英文 Keyed + DefInjected
1.6/Textures/              补齐的南向贴图（= 北向贴图旋转 180°）
1.6/Assemblies/            构建产物（随仓库提交，下载即可加载）
src/                       C# 源码（csproj + 7 个 .cs）
LoadFolders.xml
```

`src/` 下的文件与职责：

| 文件 | 职责 |
|---|---|
| `GeneCompilerPatchMod.cs` / `GeneCompilerPatchSettings.cs` | Mod 入口与设置（冷却天数、作业小时数、超凡基因白银单价、恢复默认） |
| `UGCDefs.cs` | 按 defName 取原模组的 hediff、取舱内人员（避免引用其程序集） |
| `Comp_UGCNutritionFeeder.cs` | 需求 1：用营养池换饱食度 |
| `Comp_UGCAutoExtraction.cs` | 需求 2：冷却结束自动入舱 + gizmo 开关 |
| `Comp_UGCExtractionTimer.cs` | 需求 7：提取计时作业、进度条、断电暂停、到点入库并弹出 |
| `Patches.cs` | 所有 Harmony 补丁与反射句柄（朝向 / 冷却 / 计时 / 弹出确认 / 超凡基因单价） |

## 工作方式

1. **XML patch**（`1.6/Patches/`）：给 `UniversalGeneCompiler` 的 `comps` 追加
   `CompProperties_Refuelable` 和本补丁的三个 `CompProperties`；改两个 StatDef 与一个 WorkGiverDef。
2. **自研 comp**：挂在该 ThingDef 上，`tickerType` 是 `Normal`，所以走每 tick 的 `CompTick`。
   状态都在 comp 里，用 `PostExposeData` 存档。
3. **Harmony**（`Patches.cs`）：`[StaticConstructorOnStartup]` 里逐项挂，
   目标类型 / 方法全部按名字反射解析。目前五个 patch 点：
   - `get_PawnDrawOffset` / `get_HeldPawnBodyAngle` / `GetArmMotionOffset`（南向 180° 变换，只在南向且贴图存在时生效）
   - `GeneOperationService.TryExtract`（计时器：合法则启动作业并拦下；不合法则放行让原方法报错）
   - `Building_UniversalGeneCompiler.EjectPawn`（作业中先弹确认窗）
   - `GeneOperationService.TryExtract` 的 postfix（改冷却时长）
   - `GeneOperationService.GetInjectionPrice` 的 postfix（改超凡基因单价；付款与界面报价同走此方法）

## 如何并入源模组

源模组拥有这些类，所以**大部分补丁不需要 Harmony**，直接改源码更干净。

### 纯 XML，可直接抄进 Defs
- StatDef：加 `<showIfUndefined>false</showIfUndefined>`，`<showOnDefaultValue>` 改 `false`。
- WorkGiverDef：`priorityInType` 90 → 160。
- ThingDef 的 `comps`：按 `1.6/Patches/1c.GeneCompilerPatch_Defs.xml` 里那段 `CompProperties_Refuelable`
  原样搬（营养池容量 / 补料阈值 / 食物过滤都在里面）。
- 贴图：`1.6/Textures/Things/Building/UniversalGeneCompiler/*_south.png` 直接放进贴图目录
  （就是 `_north` 旋转 180°，可以自己重新导一张更好的）。
- 本地化：`Keyed` 里的 `UGCPatch_*` 键并入原模组 Keyed；`fuelLabel` 直接写进 ThingDef。

### 需要 C#
| 本仓库 | 并入源模组时的做法 |
|---|---|
| 三个 comp（营养 / 自动入舱 / 提取计时） | 类拷进 `UniversalGeneCompilerLib`、命名空间改回自己的；把 `UGCDefs.OccupantOf()` 换成自带的 `ContainedPawn`、`UGCDefs.GeneExtractionCooldown` 换成 `DefOfRefs.UGC_GeneExtractionCooldown`，去掉这层间接 |
| 朝向修复（`Patch_PawnFacing`） | 在 `DirectionalVisualLayout.Create` 的 South 分支直接写
  `PawnOffsetX = -0.52f`、`PawnOffsetZ = 1.3f`、`PawnBodyAngle = 90f`（即 North 的偏移取反、角度 +180）。
  另外南向贴图转了 180°，`ApplyNorthSouthArmLayout` 要给 South 单独一份 x/z 取反后的机械臂起止点 |
| 冷却时长（`Patch_ExtractionCooldown`） | `TryExtract` 挂完 hediff 后按设置值 `HediffComp_Disappears.SetDuration()`，设 0 就不挂 |
| 提取计时（`Comp_UGCExtractionTimer` + prefix） | 把 `GeneOperationService.TryExtract` 拆成「开始作业 / 完成作业」两段，
  进度条照抄已有的 `geneInjectionTicksRemaining` 那套；作业期间挡住第二次提取 |
| 弹出确认（`EjectPawnPrefix`） | `EjectPawn` 的两个入口（gizmo、控制台页脚）改成
  `Dialog_MessageBox.CreateConfirmation(..., destructive: true)`，确认后再 `EjectPawn()` |
| 设置（`GeneCompilerPatchSettings`） | 并进 `UniversalGeneCompilerSettings` / `UniversalGeneCompilerMod`，不用单独的 Mod 类 |

### 构建

```bash
dotnet build src/1c.GeneCompilerPatch.csproj
```

### 已知取舍
- 对原模组类型全部走反射，是因为没有源码可引用；并入源模组后可以全部改成直呼。
- 作业进行中，控制台里「提取基因」按钮仍然可点（点了会提示作业中）。按钮的禁用状态是原模组
  `DrawExtractionPage` 里的局部变量，不打 transpiler 改不了。
- 断电暂停不推进计时，也不设「断电太久自动取消」；原版基因提取器是断电满 1 天自动取消并弹人，
  如果想要同样行为，在 `CompTick` 的断电分支里加个计数即可。

## License

MIT，见 [LICENSE](LICENSE)。
