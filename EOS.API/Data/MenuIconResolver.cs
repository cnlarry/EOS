namespace EOS.API.Data;

/// <summary>
/// 导航/菜单管理图标解析：
/// 配置覆盖（NavigationIcons，按根菜单 M_IDX）→ 根菜单名关键字 → 内置根映射 → folder。
/// </summary>
internal static class MenuIconResolver
{
    private static readonly Dictionary<int, string> RootIconOverrides = new() { [13] = "inventory", [14] = "sales", [15] = "procurement" };

    private static readonly (string Keyword, string Icon)[] IconKeywordRules =
    {
        ("采购", "procurement"),
        ("销售", "sales"),
        ("生产", "production"),
        ("BOM", "production"),
        ("制造", "production"),
        ("工艺", "production"),
        ("制程", "production"),
        ("工序", "production"),
        ("半成品", "product"),
        ("产品", "product"),
        ("人事", "hr"),
        ("人力资源", "hr"),
        ("考勤", "hr"),
        ("工资", "hr"),
        ("薪资", "hr"),
        ("招聘", "hr"),
        ("财务", "finance"),
        ("应收", "finance"),
        ("应付", "finance"),
        ("会计", "finance"),
        ("海关", "customs"),
        ("报关", "customs"),
        ("质量", "quality"),
        ("质检", "quality"),
        ("品管", "quality"),
        ("品质", "quality"),
        ("品检", "quality"),
        ("报表", "report"),
        ("查询", "query"),
        ("基础资料", "base"),
        ("基本资料", "base"),
        ("资料", "base"),
        ("系统", "settings"),
        ("设置", "settings"),
        ("权限", "settings"),
        ("设备", "equipment"),
        ("机器", "equipment"),
        ("模具", "equipment"),
        ("车辆", "vehicle"),
        ("车队", "vehicle"),
        ("汽车", "vehicle"),
        ("条码", "barcode"),
        ("条形码", "barcode"),
        ("工作流", "workflow"),
        ("流程", "workflow"),
        ("打样", "sample"),
        ("样品", "sample"),
        ("托外", "outsource"),
        ("外发", "outsource"),
        ("客户", "customer"),
        ("供应商", "supplier"),
        ("库存", "inventory"),
        ("仓存", "inventory"),
        ("盘点", "inventory"),
        ("单据", "document"),
        ("订单", "document"),
    };

    public static string Resolve(int rootId, string rootLabel, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(rootId.ToString(), out var overrideIcon) && !string.IsNullOrWhiteSpace(overrideIcon))
            return overrideIcon;
        foreach (var (keyword, icon) in IconKeywordRules)
            if (rootLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return icon;
        if (RootIconOverrides.TryGetValue(rootId, out var overrideByRoot)) return overrideByRoot;
        return "folder";
    }
}
