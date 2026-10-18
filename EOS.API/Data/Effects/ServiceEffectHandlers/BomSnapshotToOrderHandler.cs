using System.Data;
using System.Text.Json;
using EOS.API.Data.Bom;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// bom-snapshot-to-order（产品 BOM → 工单 BOM 快照）：制令单首次保存时，把该产品的
/// 产品 BOM 展开成"每个父件一行、其直接子件在明细"的形态，整棵写进工单 BOM。
///
/// 为什么是整棵树而不是只写顶层：工单 BOM 是「这一单实际用什么」的完整依据，
/// 展开子制令要靠它判断"哪些子件自己还有下阶"，缺了中间层就断链。
///
/// 三个必须守住的口径：
///   ① **只快照一次**：目标工单 BOM 已有任何行即跳过。否则每次保存制令单都会把
///      计划员手工调整过的工单 BOM 冲掉——快照是起点，不是每次覆盖的镜子。
///   ② **产品没有 BOM 不算失败**：返回 0、不抛异常，制令单照常保存。
///      产品还在设计阶段就先下制令单是常见流程，为此阻断保存会把人卡死。
///   ③ **版次显式记录**：目标主表的 EDITION 写"实际用了产品 BOM 的哪一版"
///      （制令单指定且存在则用它，否则回退到最大版次）。回退必须留痕，否则
///      半年后没人说得清这张单当初抄的是哪一版。
///
/// 展开的选版与环检测与 <see cref="BomRepository"/> 的三套查询同源（ADR-039 6.2/6.3）：
/// 锁版优先、缺失回退、路径判环。改动任一处时两边要一起改——用例
/// `BomSnapshotLiveTests` 拿同一棵树对拍"产品 BOM 的边集"与"工单 BOM 的边集"。
/// </summary>
public sealed class BomSnapshotToOrderHandler : IEffectServiceHandler
{
    private const int DefaultMaxDepth = 20;

    public string EffectKey => "bom-snapshot-to-order";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("bom-snapshot-to-order 缺少参数。");
        if (context.ExecutionEvent is not EffectEvent.Save) return 0;

        var plan = context.Plan;
        if (plan.MasterTable is null || plan.MasterPkOrder.Count < 2 || context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("bom-snapshot-to-order 需要两列主键（单别 / 单号）。");

        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, plan.MasterTable, columns);

        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        if (type.Length == 0 || no.Length == 0)
            throw new EffectConfigException("bom-snapshot-to-order 缺少单据主键，禁止无条件快照。");

        // ① 已有工单 BOM 即跳过：快照是起点，人工调整不该被下一次保存冲掉。
        if (await HasSnapshotAsync(context, config, type, no, token))
            return 0;

        var (productNo, orderEdition) = await ReadOrderAsync(context, config, type, no, token);
        if (productNo.Length == 0)
            throw new EffectConfigException($"制令单 {type}/{no} 没有主产品，无法快照 BOM。");

        var edition = await ResolveEditionAsync(context, config, productNo, orderEdition, token);
        if (edition is null) return 0;   // ② 产品没有 BOM：不阻断保存

        return await SnapshotAsync(context, config, type, no, productNo, edition, token);
    }

    private static async Task<bool> HasSnapshotAsync(
        ServiceEffectContext context, BomSnapshotConfig config, string type, string no, CancellationToken token)
    {
        var sql = "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo." + ServiceEffectSql.Q(config.TargetMasterTable)
            + " WHERE " + ServiceEffectSql.Q(config.TypeField) + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no)"
            + " THEN 1 ELSE 0 END;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static async Task<(string ProductNo, string Edition)> ReadOrderAsync(
        ServiceEffectContext context, BomSnapshotConfig config, string type, string no, CancellationToken token)
    {
        var sql = "SELECT ISNULL(" + ServiceEffectSql.Q(config.OrderProductField) + ", N''), "
            + "ISNULL(" + ServiceEffectSql.Q(config.OrderEditionField) + ", N'') "
            + "FROM dbo." + ServiceEffectSql.Q(config.OrderTable)
            + " WHERE " + ServiceEffectSql.Q(config.TypeField) + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new EffectConfigException($"找不到制令单 {type}/{no}，无法快照 BOM。");
        return (reader.GetString(0).Trim(), reader.GetString(1).Trim());
    }

    /// <summary>解析要抄的版次：制令单指定且存在则用它，否则回退该料号最大版次；都没有返回 null。</summary>
    private static async Task<string?> ResolveEditionAsync(
        ServiceEffectContext context, BomSnapshotConfig config, string productNo, string orderEdition, CancellationToken token)
    {
        var sql = "SELECT TOP 1 LTRIM(RTRIM(" + ServiceEffectSql.Q(config.SourceEditionField) + "))"
            + " FROM dbo." + ServiceEffectSql.Q(config.SourceMasterTable)
            + " WHERE " + ServiceEffectSql.Q(config.SourceProductField) + "=@pro"
            + "   AND (@edition IS NULL OR " + ServiceEffectSql.Q(config.SourceEditionField) + "=@edition)"
            + " ORDER BY " + ServiceEffectSql.Q(config.SourceEditionField) + " DESC;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value =
            orderEdition.Length == 0 ? DBNull.Value : orderEdition;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// 展开 + 落库。先把边集物化到临时表，再用两条 INSERT 分别写主表与明细——
    /// 递归 CTE 不能跨语句复用，临时表也让"主表取 distinct 父件"这一步不必把递归再算一遍。
    /// </summary>
    private static async Task<int> SnapshotAsync(
        ServiceEffectContext context, BomSnapshotConfig config,
        string type, string no, string productNo, string edition, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var depth = Math.Clamp(config.MaxDepth, 1, 100);

        // 选版必须先落成独立 CTE：递归成员禁止子查询、TOP、OUTER JOIN，
        // 所以"下一层用哪一版"不能写成派生表 join（ADR-039 6.3 记的是同一个约束）。
        // 边集也不能直接 SELECT ... INTO #EDGE 再在同一个 UNION 里引用它——
        // "源表与目的表不能相同"。故递归先落在 CTE 里，最后一步才 INTO。
        var buildEdges =
            $"""
            WITH EDITION_PICK AS (
                SELECT m.{q(config.SourceProductField)} AS PRO_NO,
                       m.{q(config.SourceEditionField)} AS EDITION,
                       ROW_NUMBER() OVER (
                           PARTITION BY m.{q(config.SourceProductField)}
                           ORDER BY CASE WHEN m.{q(config.SourceEditionField)} = @rootEdition THEN 0 ELSE 1 END,
                                    m.{q(config.SourceEditionField)} DESC) AS RN
                FROM dbo.{q(config.SourceMasterTable)} m
            ),
            EDGE AS (
                SELECT LTRIM(RTRIM(d.{q(config.DetailParentField)})) AS PARENT_NO,
                       d.{q(config.SourceEditionField)} AS PARENT_EDITION,
                       LTRIM(RTRIM(d.{q(config.DetailChildField)})) AS CHILD_NO,
                       -- 子件自己的 BOM 版次（明细指定，迁移 416）：下一层用它继续展开，
                       -- 与产品 BOM 展开页同一口径——不再运行时选版
                       LTRIM(RTRIM(ISNULL(d.{q("ELEMENT_EDITION")}, N''))) AS CHILD_EDITION,
                       d.{q("ELEMENT_QTY")} AS ELEMENT_QTY,
                       d.{q("BASE_QTY")} AS BASE_QTY,
                       d.{q("LOST_RATE")} AS LOST_RATE,
                       CAST(N'/' + LTRIM(RTRIM(d.{q(config.DetailParentField)})) + N'/'
                            + LTRIM(RTRIM(d.{q(config.DetailChildField)})) + N'/' AS nvarchar(4000)) AS PATH,
                       1 AS LVL
                FROM dbo.{q(config.SourceDetailTable)} d
                WHERE d.{q(config.DetailParentField)} = @rootProNo
                  AND d.{q(config.SourceEditionField)} = @rootEdition

                UNION ALL

                SELECT LTRIM(RTRIM(d.{q(config.DetailParentField)})),
                       d.{q(config.SourceEditionField)},
                       LTRIM(RTRIM(d.{q(config.DetailChildField)})),
                       LTRIM(RTRIM(ISNULL(d.{q("ELEMENT_EDITION")}, N''))),
                       d.{q("ELEMENT_QTY")}, d.{q("BASE_QTY")}, d.{q("LOST_RATE")},
                       CAST(e.PATH + LTRIM(RTRIM(d.{q(config.DetailChildField)})) + N'/' AS nvarchar(4000)),
                       e.LVL + 1
                FROM dbo.{q(config.SourceDetailTable)} d
                -- 下一层用父行指定的子件版本：d.EDITION = e.CHILD_EDITION。
                -- 材料（CHILD_EDITION 为空）不会命中，展开到此为止
                JOIN EDGE e ON d.{q(config.DetailParentField)} = e.CHILD_NO
                           AND LTRIM(RTRIM(d.{q(config.SourceEditionField)})) = e.CHILD_EDITION
                WHERE e.LVL < @maxDepth
                  AND CHARINDEX(N'/' + LTRIM(RTRIM(d.{q(config.DetailChildField)})) + N'/', e.PATH) = 0
            )
            SELECT * INTO #EDGE FROM EDGE OPTION (MAXRECURSION 100);
            """;

        var insertMaster =
            "INSERT INTO dbo." + q(config.TargetMasterTable)
            + " (" + q(config.TypeField) + ", " + q(config.NoField) + ", " + q(config.TargetProductField)
            + ", " + q(config.TargetEditionField) + ", " + q("BATCH_QTY") + ", "
            + q("CREATE_PERSON") + ", " + q("CREATE_DATE") + ", " + q("CONFIRM_TAG") + ") "
            + "SELECT DISTINCT @type, @no, e.PARENT_NO, e.PARENT_EDITION, sm." + q("BATCH_QTY") + ", "
            + "@executor, SYSDATETIME(), 0 "
            + "FROM #EDGE e "
            + "LEFT JOIN dbo." + q(config.SourceMasterTable) + " sm "
            + "  ON sm." + q(config.SourceProductField) + "=e.PARENT_NO "
            + " AND sm." + q(config.SourceEditionField) + "=e.PARENT_EDITION;";

        var insertDetail =
            "INSERT INTO dbo." + q(config.TargetDetailTable)
            + " (" + q(config.TypeField) + ", " + q(config.NoField) + ", " + q(config.TargetProductField)
            + ", " + q("SERIAL_NO") + ", " + q(config.TargetChildField) + ", " + q("ELEMENT_EDITION") + ", "
            + q("ELEMENT_QTY") + ", " + q("BASE_QTY") + ", " + q("LOST_RATE") + ") "
            + "SELECT @type, @no, e.PARENT_NO, "
            + "ROW_NUMBER() OVER (PARTITION BY e.PARENT_NO ORDER BY e.CHILD_NO, e.LVL, e.ELEMENT_QTY), "
            // 空串存 NULL：工单 BOM 用 NULL 表示"材料，无下阶"
            + "e.CHILD_NO, NULLIF(e.CHILD_EDITION, N''), e.ELEMENT_QTY, e.BASE_QTY, e.LOST_RATE "
            + "FROM #EDGE e;";

        // 三条语句必须同批执行：#EDGE 是本地临时表，而带参数的语句走 sp_executesql，
        // 批结束时临时表随之销毁——分成三次调用会得到"对象名 '#EDGE' 无效"。
        // 行数也不靠 ExecuteNonQuery 的累计（SELECT INTO 也会计入），而是用变量显式捕获。
        var sql = "SET NOCOUNT ON;\nDECLARE @rowsMaster INT = 0, @rowsDetail INT = 0;\n"
            + buildEdges.TrimEnd(';', '\n', ' ') + ";\n"
            + insertMaster.TrimEnd(';') + ";\nSET @rowsMaster = @@ROWCOUNT;\n"
            + insertDetail.TrimEnd(';') + ";\nSET @rowsDetail = @@ROWCOUNT;\n"
            + "SELECT @rowsMaster + @rowsDetail;";

        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.Add("@rootProNo", SqlDbType.NVarChar, 60).Value = productNo;
        command.Parameters.Add("@rootEdition", SqlDbType.NVarChar, 20).Value = edition;
        command.Parameters.Add("@maxDepth", SqlDbType.Int).Value = depth;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        command.Parameters.Add("@executor", SqlDbType.NVarChar, 100).Value = context.Executor;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>参数解析（fail-closed：四张表的每个列名都必须物理存在）。发布校验器复用同一份判据。</summary>
    internal static BomSnapshotConfig Parse(
        JsonElement root, string orderTable, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("bom-snapshot-to-order 参数必须是 JSON 对象。");

        var orderProduct = Required(root, "orderProductField");
        var orderEdition = Required(root, "orderEditionField");
        var targetMaster = Required(root, "targetMasterTable");
        var targetDetail = Required(root, "targetDetailTable");
        var sourceMaster = Required(root, "sourceMasterTable");
        var sourceDetail = Required(root, "sourceDetailTable");
        var maxDepth = root.TryGetProperty("maxDepth", out var depthElement) && depthElement.ValueKind == JsonValueKind.Number
            ? depthElement.GetInt32()
            : DefaultMaxDepth;

        var config = new BomSnapshotConfig(
            orderTable, orderProduct, orderEdition,
            targetMaster, targetDetail, sourceMaster, sourceDetail, maxDepth);

        foreach (var (table, column) in new[]
                 {
                     (orderTable, orderProduct),
                     (orderTable, orderEdition),
                     (targetMaster, config.TargetProductField),
                     (targetMaster, config.TargetEditionField),
                     (targetDetail, config.TargetProductField),
                     (targetDetail, config.TargetChildField),
                     // 子件版本列（迁移 416）：源明细要有、目标明细也要有，缺任一侧快照都会丢信息
                     (sourceDetail, "ELEMENT_EDITION"),
                     (targetDetail, "ELEMENT_EDITION"),
                     (sourceMaster, config.SourceProductField),
                     (sourceMaster, config.SourceEditionField),
                     (sourceDetail, config.DetailParentField),
                     (sourceDetail, config.SourceEditionField),
                     (sourceDetail, config.DetailChildField),
                     (sourceDetail, "ELEMENT_QTY"),
                     (sourceDetail, "BASE_QTY"),
                     (sourceDetail, "LOST_RATE"),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"bom-snapshot-to-order 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"bom-snapshot-to-order 缺少字符串字段 {name}。");
}

/// <summary>
/// 快照参数。单别/单号列名不在这里——它们与主表主键同名，由 <see cref="ModuleEffectPlan.MasterPkOrder"/> 给出。
/// </summary>
internal sealed record BomSnapshotConfig(
    string OrderTable,
    string OrderProductField,
    string OrderEditionField,
    string TargetMasterTable,
    string TargetDetailTable,
    string SourceMasterTable,
    string SourceDetailTable,
    int MaxDepth)
{
    /// <summary>单别 / 单号列：与主表主键同名的约定（工单 BOM 的主键前两列）。</summary>
    public string TypeField => "PRODUCE_TYPE";
    public string NoField => "PRODUCE_NO";

    /// <summary>目标主表与明细共有的"父件产品"列。</summary>
    public string TargetProductField => "PRO_NO";

    /// <summary>目标主表的版次列（记录抄的是产品 BOM 哪一版）。</summary>
    public string TargetEditionField => "EDITION";

    /// <summary>目标明细的"子件"列。</summary>
    public string TargetChildField => "ELEMENT_PRO_NO";

    /// <summary>来源主表的料号列。</summary>
    public string SourceProductField => "PRO_NO";

    /// <summary>来源主表的版次列。</summary>
    public string SourceEditionField => "EDITION";

    /// <summary>来源明细的"父件"列。</summary>
    public string DetailParentField => "PRO_NO";

    /// <summary>来源明细的"子件"列。</summary>
    public string DetailChildField => "ELEMENT_PRO_NO";
}
