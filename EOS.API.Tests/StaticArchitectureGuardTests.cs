using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 静态架构不变式：只读源码，不连库，因此**进 CI**。
///
/// 这三条原先各由一条连库脚本门禁守着（`check-effect-module-branches.ps1`、
/// `check-inventory-read-hosts.ps1` 的代码侧、`check-stock-depot-level-consistency.ps1` 的 ⑤ 代码侧），
/// 三条门禁都已退役（见 `scripts/README.md` 退役清单）。它们的判据本来就是"扫源码"，
/// 放进用例比放在需要人记得跑的脚本里更靠得住——**改了代码就红**。
///
/// 每个扫描器都配了正反控制（合成样本证明它会命中、也会放过），
/// 免得"扫描器坏了"表现成"仓库很干净"。
///
/// 仍然没有覆盖的部分（退役时记在同样那两条清单里，不在这里假装有）：
///   · 库存读宿主的**配置侧**（虚拟列表达式 / 模块过滤 / 校验与动作参数 / 主表声明）与
///     **写路径交叉断言**（主表是库存表的模块不得进统一表单写名单）——那些要连库；
///   · 策略求值口径的**配置侧**（策略表不得被声明进校验规则 / 动作 / 快照）；
///   · 库别级字段同键一致、档位取值合法、哨兵列尾随空格、半成品账三键齐全（连库档位）。
/// </summary>
public sealed class StaticArchitectureGuardTests
{
    /// <summary>读库存必须经的收口点。命中大写库存表名的文件只允许是这三个（只减不增）。</summary>
    private static readonly string[] InventoryReadHostAllowlist =
    {
        Path.Combine("Data", "Inventory", "InventoryQueryService.cs"),
        Path.Combine("Data", "Effects", "ServiceEffectHandlers", "InventoryMoveHandler.cs"),
        Path.Combine("Data", "DepotStockPolicyService.cs"),
    };

    private static readonly string[] InventoryTableNames =
    {
        "INV_PRO_DEPOT", "INV_DEPOT_LOG", "INV_BATCH_M", "INV_BATCH_D",
    };

    /// <summary>方案 B：策略表只允许服务本身读——求值口径必须唯一。</summary>
    private static readonly string[] DepotStockPolicyReaders = { "DepotStockPolicyService.cs" };

    /// <summary>效果引擎运行时的"按模块硬分支"特征（ADR-015.4：语义只来自配置）。</summary>
    private static readonly Regex[] ModuleBranchPatterns =
    {
        new(@"switch\s*\(\s*moduleId\s*\)"),
        new(@"\bmoduleId\s*==\s*\d+"),
        new(@"\bmoduleId\s*is\s*\d+"),
        new(@"\bModuleId\s*==\s*\d+"),
        new(@"\bModuleId\s*is\s*\d+"),
        new(@"\.ModuleId\s*==\s*\d+"),
        new(@"\.ModuleId\s*is\s*\d+"),
    };

    [Fact]
    public void 效果引擎运行时保持数据驱动_不得按模块硬分支()
    {
        var effectsDir = Path.Combine(FindRepoRoot(), "EOS.API", "Data", "Effects");
        var scanned = 0;
        var hits = new List<string>();

        foreach (var file in CsFiles(effectsDir))
        {
            scanned++;
            foreach (var (lineNumber, line) in CodeLines(file))
            {
                if (ModuleBranchPatterns.Any(pattern => pattern.IsMatch(line)))
                {
                    hits.Add($"{Path.GetFileName(file)}:{lineNumber}: {line}");
                }
            }
        }

        Assert.True(scanned > 0, $"没扫到任何源码（{effectsDir}）——扫描器坏了会表现成「很干净」");
        Assert.True(hits.Count == 0,
            $"效果引擎出现按模块硬分支 {hits.Count} 处（语义必须只来自配置）：{string.Join("；", hits.Take(10))}");
    }

    [Fact]
    public void 库存读宿主收口_代码侧命中库存表名的文件必须在白名单内()
    {
        var apiRoot = Path.Combine(FindRepoRoot(), "EOS.API");
        var hits = ScanTableReferences(apiRoot, InventoryTableNames);
        var actual = hits.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var allowed = InventoryReadHostAllowlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var unregistered = actual.Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();
        var stale = allowed.Except(actual, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(unregistered.Count == 0,
            $"这些文件直接引用了库存表（读库存必须经 InventoryQueryService）：{string.Join("、", unregistered)}");
        Assert.True(stale.Count == 0,
            $"白名单条目已不再引用库存表，应当移除：{string.Join("、", stale)}");
    }

    [Fact]
    public void 策略求值口径唯一_只有策略服务能读策略表()
    {
        var apiRoot = Path.Combine(FindRepoRoot(), "EOS.API");
        var hits = ScanTableReferences(apiRoot, new[] { "DEPOT_STOCK_POLICY" });
        var readers = hits.Keys
            .Where(path => !DepotStockPolicyReaders.Contains(Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(Directory.EnumerateFiles(apiRoot, "DepotStockPolicyService.cs", SearchOption.AllDirectories).Any(),
            "找不到 DepotStockPolicyService.cs——判据的「唯一允许者」不存在时，这条断言失去意义");
        Assert.True(readers.Count == 0,
            $"策略表在服务之外被引用（求值口径必须唯一）：{string.Join("、", readers)}");
    }

    [Fact]
    public void 库别级字段不得被聚合读取()
    {
        // INIT_QTY / COST_PRICE / COST_AMOUNT 是**库别级**列：每行冗余同一库别的同一个值，
        // 任何 SUM 都会按行数成倍虚增；库别级数值应取任一行或 MAX，合计只对行级字段
        // （QTY / USEABLE_QTY）。这条原先由 `check-stock-depot-level-consistency.ps1` 的 ④ 守着。
        var repoRoot = FindRepoRoot();
        var patterns = new[]
        {
            new Regex(@"SUM\s*\(\s*(ISNULL\s*\(\s*)?[A-Za-z_\.\[\]]*\.?(INIT_QTY|COST_PRICE|COST_AMOUNT)"),
            new Regex(@"SUM\s*\(\s*\[?(INIT_QTY|COST_PRICE|COST_AMOUNT)\]?\s*\)"),
        };
        var scanned = 0;
        var hits = new List<string>();

        foreach (var root in new[] { "EOS.API", "scripts", "EOS.API.Tests" })
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir)) { continue; }
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                     || file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                                     || file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                         .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                     && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                     && !file.Contains($"{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                scanned++;
                foreach (var (lineNumber, line) in CodeLines(file))
                {
                    if (patterns.Any(pattern => pattern.IsMatch(line)))
                    {
                        hits.Add($"{Path.GetRelativePath(repoRoot, file)}:{lineNumber}");
                    }
                }
            }
        }

        Assert.True(scanned > 0, "没扫到任何文件——扫描器坏了会表现成「很干净」");
        Assert.True(hits.Count == 0,
            $"存在对库别级字段（INIT_QTY / COST_PRICE / COST_AMOUNT）的聚合读取 {hits.Count} 处：{string.Join("、", hits.Take(10))}");
    }

    [Fact]
    public void 表名扫描器_能抓出直读且忽略注释行与迁移脚本()
    {
        // 正反控制：合成一棵小目录树，证明扫描器既不漏（直读命中）、也不滥（注释行 / 迁移脚本 / 小写表名）。
        var probe = Path.Combine(Path.GetTempPath(), "eos-static-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(probe, "Data", "Inventory")).FullName;
            var migrations = Directory.CreateDirectory(Path.Combine(probe, "Data", "Migrations")).FullName;
            var effects = Directory.CreateDirectory(Path.Combine(probe, "Data", "Effects")).FullName;

            File.WriteAllText(Path.Combine(sub, "DirectReader.cs"),
                "var sql = \"SELECT QTY FROM dbo.INV_PRO_DEPOT\";\n");
            File.WriteAllText(Path.Combine(sub, "CommentOnly.cs"),
                "// 这条注释提到 INV_PRO_DEPOT，但不是读取\nvar x = 1;\n");
            File.WriteAllText(Path.Combine(migrations, "Migration.cs"),
                "var sql = \"DELETE FROM dbo.INV_PRO_DEPOT\";\n");
            File.WriteAllText(Path.Combine(effects, "LowerCase.cs"),
                "var sql = \"select qty from dbo.inv_pro_depot\";\n");

            var hits = ScanTableReferences(probe, InventoryTableNames);
            var found = hits.Keys.Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "DirectReader.cs" }, found);

            var branchHits = 0;
            File.WriteAllText(Path.Combine(effects, "ModuleBranch.cs"), "if (moduleId == 1601) { return; }\n");
            foreach (var file in CsFiles(effects))
            {
                branchHits += CodeLines(file).Count(entry => ModuleBranchPatterns.Any(p => p.IsMatch(entry.Line)));
            }
            Assert.Equal(1, branchHits);
            File.WriteAllText(Path.Combine(effects, "ModuleBranch.cs"), "if (moduleId is null) { return; }\n");
            branchHits = 0;
            foreach (var file in CsFiles(effects))
            {
                branchHits += CodeLines(file).Count(entry => ModuleBranchPatterns.Any(p => p.IsMatch(entry.Line)));
            }
            Assert.Equal(0, branchHits);
        }
        finally
        {
            if (Directory.Exists(probe)) { Directory.Delete(probe, recursive: true); }
        }
    }

    /// <summary>
    /// 统一表单能力（`UNIFIED_FORM_ENABLED` / `UNIFIED_FORM_EDIT_ENABLED`）是**一处真源**：
    /// 模块行上的两列 + 一个读取点（`Data/Workbench/UnifiedFormCapability.cs`）。
    ///
    /// 迁移 355 之前这件事由 `appsettings.json` 的两个名单回答，读法散在六个消费点里，
    /// 改一次要重启、库里查不到、审计里看不见。现在**离线可断**的就是这两条：
    /// ① 列名只许出现在读取点、发布枚举与导入候选的两条 SQL、菜单管理的读写与模型契约里；
    /// ② 名单段不得回到 appsettings（留着就是一处没人读的配置——改它的人会以为放量了）。
    ///
    /// 模块号与主表的对应关系（"审计模块停在仅浏览"这类）在库里，由真库用例
    /// `AuditModuleGovernanceLiveTests` 与 `InventoryConfigFaceLiveTests` 守。
    /// </summary>
    [Fact]
    public void 统一表单能力只有一处真源()
    {
        var repoRoot = FindRepoRoot();
        var apiRoot = Path.Combine(repoRoot, "EOS.API");
        var capabilitySource = Path.Combine(apiRoot, "Data", "Workbench", "UnifiedFormCapability.cs");
        Assert.True(File.Exists(capabilitySource),
            "找不到统一表单能力的读取点——判据的「唯一允许者」不存在时，这条断言失去意义");

        var allowed = new[]
        {
            Path.Combine("Data", "Workbench", "UnifiedFormCapability.cs"),   // 读取点（单行 + 两个集合查询）
            Path.Combine("Data", "WorkbenchDefinitionSnapshotService.cs"),   // 发布管理/落后检测的状态列
            Path.Combine("Features", "Import", "ImportService.cs"),          // 导入候选集按可写位过滤
            Path.Combine("Data", "MenuAdminRepository.cs"),                  // 2301 的读与写
            Path.Combine("Models", "MenuAdminModels.cs"),                    // JSON 契约属性名（前端按它提交）
            // 助手能力目录里那句"真值来源：dbo.MODULES（…）"的说明文案：只是文本，不做判定
            Path.Combine("Features", "Assistant", "Catalog", "SystemCapabilityCatalog.cs"),
        };
        var hits = ScanTableReferences(apiRoot, ["UNIFIED_FORM_ENABLED", "UNIFIED_FORM_EDIT_ENABLED"]);
        var actual = hits.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var unregistered = actual.Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();
        var stale = allowed.Except(actual, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(unregistered.Count == 0,
            $"统一表单能力在以下文件里被直接引用（判定只该走 IUnifiedFormCapabilitySource）：{string.Join("、", unregistered)}");
        Assert.True(stale.Count == 0,
            $"白名单条目已不再引用这两位，应当移除：{string.Join("、", stale)}");

        var settingsPath = Path.Combine(apiRoot, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(document.RootElement.TryGetProperty("UnifiedFormEditor", out _),
            "appsettings 里不该再出现 UnifiedFormEditor 名单段：统一表单能力已经搬进 MODULES 的两列，"
            + "名单留在这里就是一处没人读的配置。");
    }

    /// <summary>
    /// 报表可见性判定只有一处实现。这段判据此前在三处各写一遍（报表权读取、打印面板的报表
    /// 清单、报表中心目录），"目录里看得见、打印面板里没有"这类不一致只能靠人眼发现。
    ///
    /// 判据是**去掉空白后的字面量** `REPORT_TAG,0)=1`——即"个人优先、否则组取或"这两条 SQL
    /// 分支本身；只把 `REPORT_TAG` 当列名登记的配置面（`RightsAdminRepository` 的权限列清单）
    /// 不算。缩进与空格随格式化漂移，故先归一化再判。
    /// </summary>
    [Fact]
    public void 报表可见性判定只有一处实现()
    {
        var apiRoot = Path.Combine(FindRepoRoot(), "EOS.API");
        var owner = Path.Combine("Data", "Reports", "ModuleReportVisibility.cs");
        var needle = "REPORT_TAG,0)=1";

        var ownerPath = Path.Combine(apiRoot, owner);
        Assert.True(File.Exists(ownerPath),
            $"找不到报表可见性判定的唯一实现 {owner}——「唯一允许者」不在时这条断言会假绿");
        Assert.Contains(File.ReadLines(ownerPath),
            line => Normalize(line).Contains(needle, StringComparison.Ordinal));

        var hits = new List<string>();
        foreach (var file in CsFiles(apiRoot))
        {
            var relative = Path.GetRelativePath(apiRoot, file);
            if (string.Equals(relative, owner, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (lineNumber, line) in CodeLines(file))
            {
                if (Normalize(line).Contains(needle, StringComparison.Ordinal))
                {
                    hits.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(hits.Count == 0,
            $"报表可见性判定（REPORT_TAG 个人优先 / 否则组取或）只允许写在 {owner}，其余调用点改引 "
            + $"ModuleReportVisibility.ForUser / IsVisibleAsync：" + Environment.NewLine
            + string.Join(Environment.NewLine, hits));
    }

    // ===== 扫描原语 =====

    private static string Normalize(string line) => line.Replace(" ", string.Empty);

    /// <summary>
    /// 按路径找"直接引用表名"的文件（大小写敏感：报表编号 `Inv_Pro_Depot_List` 与物理表不是一回事）。
    /// 与退役门禁同口径：跳过 `Data*/Migrations*`（一次性迁移不是运行时读取）、跳过以 `//` 开头的注释行。
    /// </summary>
    private static Dictionary<string, List<string>> ScanTableReferences(string root, string[] tableNames)
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in CsFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("Data" + Path.DirectorySeparatorChar + "Migrations", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (lineNumber, line) in CodeLines(file))
            {
                if (tableNames.Any(table => line.Contains(table, StringComparison.Ordinal)))
                {
                    if (!hits.TryGetValue(relative, out var lines))
                    {
                        lines = new List<string>();
                        hits[relative] = lines;
                    }
                    lines.Add($"{lineNumber}: {line}");
                }
            }
        }
        return hits;
    }

    private static IEnumerable<string> CsFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                            && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    /// <summary>逐行取出"算数的代码行"：整行注释（`//`）不算——注释里提到表名不是读取。</summary>
    private static IEnumerable<(int LineNumber, string Line)> CodeLines(string file)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(file))
        {
            lineNumber++;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) { continue; }
            if (trimmed.Length == 0) { continue; }
            yield return (lineNumber, trimmed.TrimEnd());
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found (EOS.API/EOS.API.csproj).");
    }
}
