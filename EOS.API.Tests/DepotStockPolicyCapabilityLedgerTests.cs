using System.Text.RegularExpressions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存策略**能力台帐**：凡是 `DepotStockPolicyService.Tiers` 里声明为「已实现」的档位，
/// 必须指出它的**运行期消费方**——否则界面给顾问看的能力是空的（"声称=实现"）。
///
/// 原先由脚本门禁 `check-capability-ledger.ps1` 守着，该门禁已退役（见 `scripts/README.md` 退役清单），
/// 本文件承接它的 **S1（能力档位）** 那一段：纯源码扫描，离线、进 CI。
/// 原门禁的 S2（库存域库对象两侧探针）与 S3（配置面库存写入旁路棘轮）**未迁**——它们的探针要连库，
/// 需要时按 `git show <祖先提交>:scripts/check-capability-ledger.ps1` 取回。
///
/// 三类分类（与退役时的台帐逐条同口径）：
///   · <c>Consumer</c>           声明已实现，运行期必须有消费方（探针命中 ≥ Min）；
///   · <c>NoConsumerByDesign</c> 本版未实现，"没有消费方"是正确状态（探针必须为 0）；
///   · <c>KnownGap</c>           已登记的缺口（必须有 Reason；命中不为 0 时说明缺口可能已补）。
///
/// 探针**排除声明文件与其控制器**：它们出现维度名只说明"这是被声明的维度"，不是消费方。
/// 因此命中即证明运行期真的读了这一维。词边界必须保留——不加 <c>\b</c> 时 `EFFECT_DATE`
/// 会把 `IN_EFFECT_DATE` 一起算上，足以把结论整个翻过来。
/// </summary>
public sealed class DepotStockPolicyCapabilityLedgerTests
{
    private static readonly Regex DimensionPattern = new(
        @"new\(\s*""(?<key>[A-Za-z]+)""\s*,\s*""(?<label>[^""]*)""\s*,\s*(?<desc>""[^""]*""(?:\s*\+\s*""[^""]*"")*)\s*,\s*\[(?<body>.*?)\]\s*\)",
        RegexOptions.Singleline);

    private static readonly Regex TierPattern = new(
        @"new\(\s*""(?<value>[^""]*)""\s*,\s*""(?<text>[^""]*)""\s*,\s*(?<impl>true|false)\s*\)");

    /// <summary>
    /// 台帐（人工分类）。新增维度一旦进 `Tiers`，覆盖性断言会立刻失败并要求分类——
    /// 那是要的行为（参数不得先于实现存在）。
    /// </summary>
    private static readonly Dictionary<string, Expectation> Ledger = new(StringComparer.Ordinal)
    {
        ["locationMode"] = new("Consumer", @"LOCATION_MODE|LocationMode", 1,
            "位置档位在移动引擎里决定「未指定位置是否拒绝」（PolicyTmp.LOCATION_MODE + 档 3 判据）；档 2「建议」另有保存期消费点 DepotLocationSuggestionService，档 1 不做"),
        ["storageMode"] = new("Consumer", @"STORAGE_MODE|StorageMode", 2,
            "存放方式照原文由解析器 DepotLocationResolver、取值侧 DepotLocationService 与消费点 InventoryMoveHandler 三处接上。Min=2：单处命中可能只是注释，两处才说明真的接了"),
        ["batchMode"] = new("Consumer", @"BATCH_MODE|BatchMode", 1,
            "批次档位决定非批管料件批号是否归零（档 0）、是否保留（档 1）、空批号是否拒绝（档 >=2）"),
        ["capacityMode"] = new("NoConsumerByDesign", @"CAPACITY_MODE|CapacityMode", 0,
            "容量维度本版未实现：档 0「不校验」是天然成立的默认值，其余档界面灰显不可选、服务端拒存；因此「没有消费方」是正确状态，不是缺口"),
        ["mixProduct"] = new("Consumer", @"MIX_PRODUCT|MixProduct", 1,
            "禁止混品号（档 0）时，同一库位放入不同料号被拒（CheckMixingAsync）"),
        ["mixBatch"] = new("Consumer", @"MIX_BATCH|MixBatch", 1,
            "禁止混批次（档 0）时，同一库位放入不同批次被拒（CheckMixingAsync）"),
        ["monthCloseByBatch"] = new("Consumer", @"MONTH_CLOSE_BY_BATCH|MonthCloseByBatch", 1,
            "月结按批次：消费方是快照生成器 MonthCloseSnapshotService（读部署级那一行的 MONTH_CLOSE_BY_BATCH 决定明细是否按批次细分）"),
        ["monthCloseByLocation"] = new("Consumer", @"MONTH_CLOSE_BY_LOCATION|MonthCloseByLocation", 1,
            "月结按库位：同 monthCloseByBatch，消费方同为快照生成器（按库位细分快照明细）"),
        ["expiryMode"] = new("Consumer", @"EXPIRY_MODE|ExpiryMode", 1,
            "过期批次档位：消费方是过账引擎（InventoryMoveHandler 的 PolicyTmp.EXPIRY_MODE → 出库前判已过期批次：档 2 拒绝、档 1 放行并把告警带回、档 0 不查）"),
        ["monthCloseScopeHalfStock"] = new("Consumer", @"MONTH_CLOSE_SCOPE_HALF_STOCK|MonthCloseScopeHalfStock", 1,
            "月结含半成品账：消费方是半成品过账引擎 HalfStockMoveHandler（按策略判这笔变动是否落在已关账期）与快照侧同源的月结范围判定"),
    };

    [Fact]
    public void 声明的能力档位必须指出运行期消费方()
    {
        var apiRoot = ApiSourceProbe.ApiRoot();
        var declared = ParseDeclaredTiers(Path.Combine(apiRoot, "Data", "DepotStockPolicyService.cs"));
        Assert.True(declared.Count > 0,
            "没解析出任何能力维度——要么 Tiers 的写法变了（解析器要跟上），要么它被清空了；两种情况都不能当成「通过」");

        // 未分类 = 新增能力没声明消费方（参数不得先于实现存在）。
        var unclassified = declared.Keys.Where(key => !Ledger.ContainsKey(key)).OrderBy(key => key).ToList();
        Assert.True(unclassified.Count == 0,
            $"这些能力维度未在台帐中分类：{string.Join("、", unclassified)}——新增能力必须说清它的运行期消费方（Consumer / NoConsumerByDesign / KnownGap）");

        // 反方向：台帐里登记了、Tiers 里已没有的维度（与 NolockAllowlist 的陈旧条目同口径，一并失败）。
        var stale = Ledger.Keys.Where(key => !declared.ContainsKey(key)).OrderBy(key => key).ToList();
        Assert.True(stale.Count == 0,
            $"台帐条目在 Tiers 中已不存在，应当移除（一行的事）：{string.Join("、", stale)}");

        var failures = new List<string>();
        foreach (var (key, tiers) in declared.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var expectation = Ledger[key];
            var hits = ApiSourceProbe.CountConsumerHits(apiRoot, expectation.Pattern);
            Assert.True(hits >= 0, $"能力维度「{key}」的探针未执行（源码目录缺失？）");

            var claimed = tiers.Where(tier => tier.Implemented).Select(tier => tier.Text).ToList();
            var claimedText = claimed.Count > 0 ? string.Join(" / ", claimed) : "（无）";

            switch (expectation.Kind)
            {
                case "Consumer" when hits < expectation.Min:
                    failures.Add($"「{key}」声明为已实现（{claimedText}），但运行期消费点探针仅命中 {hits} 次（要求 >= {expectation.Min}）——声称与实现不符");
                    break;
                case "NoConsumerByDesign" when hits > 0:
                    failures.Add($"「{key}」登记为「本版未实现、无消费方是正确的」，但探针命中 {hits} 次：要么实现已补上（请改为 Consumer），要么存在未经声明的读取点");
                    break;
            }
        }

        Assert.True(failures.Count == 0, string.Join("；", failures));
    }

    [Fact]
    public void 档位解析器_能读出多行拼接说明的维度()
    {
        // 正反控制：`monthCloseScopeHalfStock` 曾因「说明文字写成多行拼接」被解析器整条漏读，
        // 从而绕过了「未分类即失败」这条断言——漏掉的维度既不在覆盖性断言的视野里，也不会被拦下。
        const string source = """
            public static IReadOnlyList<DepotStockPolicyTier> Tiers { get; } =
            [
                new("alphaMode", "甲档位", "单行说明",
                [
                    new("0", "不管", true),
                    new("1", "管", false),
                ]),
                new("betaMode", "乙档位", "第一段说明" + "第二段说明",
                [
                    new("0", "不管", true),
                ]),
                new("gammaMode", "丙档位", "说明",
                [
                    new("0", "不管", true),
                ]),
            ];
            """;

        var declared = ParseDeclaredTiersText(source);
        Assert.Equal(new[] { "alphaMode", "betaMode", "gammaMode" }, declared.Keys.OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal(2, declared["alphaMode"].Count);
        Assert.Equal(1, declared["alphaMode"].Count(tier => tier.Implemented));
        Assert.Single(declared["betaMode"]);
        Assert.Equal("不管", declared["betaMode"][0].Text);
    }

    // ===== 解析与探针 =====

    private sealed record Expectation(string Kind, string Pattern, int Min, string Reason);

    private sealed record Tier(string Value, string Text, bool Implemented);

    private static Dictionary<string, List<Tier>> ParseDeclaredTiers(string path)
    {
        Assert.True(File.Exists(path), $"找不到声明文件：{path}");
        return ParseDeclaredTiersText(File.ReadAllText(path));
    }

    /// <summary>从 C# 源码解析 `Tiers` 的字面量：维度 → 档位（含"是否已实现"）。</summary>
    private static Dictionary<string, List<Tier>> ParseDeclaredTiersText(string source)
    {
        var result = new Dictionary<string, List<Tier>>(StringComparer.Ordinal);
        foreach (Match dimension in DimensionPattern.Matches(source))
        {
            var tiers = new List<Tier>();
            foreach (Match tier in TierPattern.Matches(dimension.Groups["body"].Value))
            {
                tiers.Add(new Tier(
                    tier.Groups["value"].Value,
                    tier.Groups["text"].Value,
                    tier.Groups["impl"].Value == "true"));
            }
            result[dimension.Groups["key"].Value] = tiers;
        }
        return result;
    }

}
