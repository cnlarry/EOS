using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

// `produce-gen-sub`（展开子制令）：用户在制令单上点一下，按工单 BOM 把有下阶料的子件逐个建成子制令。
//
// 与旧系统同一件事（`MOC/Produce.aspx.cs` 的 `btnGenSubProduce`，文案"展开制令"），五处有意不同：
//   ① 子单走统一记录创建路径（`WorkbenchCommandHandler.CreateRecordAsync`）：单号由发号器原子取号
//      （旧 `GetNewID` 是 MAX+1，并发连点会重号），归属/校验/审计照常生效；
//   ② 已有子单的行跳过而不是重复创建：旧实现点一次生一批，再点一次再生一批；现代实现按
//      （父单别, 父单号, 子件料号）判重，已有即跳过并计数——重复点击只补漏，不复制；
//   ③ 成环直接拒绝：旧实现遇到 BOM 成环会无限递归直到爆栈，现代实现按路径判环（最多 20 层），
//      把"挂死"换成可解释的拒绝（与 `no-cycle` 校验同一口径）；
//   ④ 不重写本单明细：旧实现点按钮时会把本单明细也按订单 BOM 追加一遍（无去重）；
//      本单明细的权威口径已由"计算用料"（产品 BOM）承担，两边同时写明细只会打架，
//      故本操作只建子单（含子单自己的明细），不动本单；
//   ⑤ 子单不自动批核：旧实现只建单不批核，保持一致（何时开工由计划决定，不由按钮决定）。
// 只配在 1502（默认制令模块）：子单的单别恒取默认制令单别，家就在 1502；
// 配到其它制令模块会让人误以为子单会落在那里。
// 不要求本单已批核：展开的正常时机在批核之前（先展开再逐单送审）。
// 已完工结案（`FINISHED_TAG = 1`）的制令单拒绝。
internal sealed class ProduceGenSubHandler(
    IPermissionService permissions,
    WorkbenchDefinitionBuilder definitionBuilder,
    WorkbenchCommandHandler commandHandler,
    DbConnectionFactory connections,
    ILogger<ProduceGenSubHandler> logger) : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "produce-gen-sub";

    private const int TargetModuleId = 1502;
    private const int MaxDepth = 20;
    private const string MasterTable = "MOC_PRODUCE_M";
    private const string DetailTable = "MOC_PRODUCE_D";
    private const string OrderBomMasterTable = "MOC_BOM_STRU_M";
    private const string OrderBomTable = "MOC_BOM_STRU_D";
    private const string ProductTable = "PRODUCT";
    private const string TypeField = "PRODUCE_TYPE";
    private const string NoField = "PRODUCE_NO";
    private const string OrderTypeField = "ORDER_TYPE";
    private const string OrderNoField = "ORDER_NO";
    private const string ParentTypeField = "PARENT_TYPE";
    private const string ParentNoField = "PARENT_NO";
    private const string ProField = "PRO_NO";
    private const string QtyField = "QTY";
    private const string SpareQtyField = "SPARE_QTY";
    private const string DepotField = "DEPOT_ID";
    private const string EditionField = "EDITION";
    private const string LineField = "LINE_ID";
    private const string UnitField = "UNIT_ID";
    private const string PlanStartField = "PLAN_START";
    private const string PlanEndField = "PLAN_END";
    private const string PreSendField = "PRE_SEND_DATE";
    private const string ProduceDateField = "PRODUCE_DATE";
    private const string FinishedField = "FINISHED_TAG";
    private const string SerialField = "SERIAL_NO";
    private const string OrderSerialField = "ORDER_SERIAL_NO";
    private const string ApplyQtyField = "APPLY_QTY";
    private const string PurchaseQtyField = "PURCHASE_QTY";
    private const string ElementProField = "ELEMENT_PRO_NO";
    private const string ElementQtyField = "ELEMENT_QTY";
    private const string BaseQtyField = "BASE_QTY";
    private const string LostRateField = "LOST_RATE";
    private const string NeedQtyField = "NEED_QTY";
    private const string ProQtyField = "PRO_QTY";
    private const string ComponentQtyField = "COMPONENT_QTY";

    public string Key => ActionKey;

    public string Label => "展开子制令";

    /// <summary>生成的是下游单据，按钮落在子表标题栏（与"生成调整单"同落点）。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var (table, column) in new[]
                 {
                     (MasterTable, TypeField), (MasterTable, NoField),
                     (MasterTable, ParentTypeField), (MasterTable, ParentNoField),
                     (MasterTable, ProField), (MasterTable, QtyField), (MasterTable, SpareQtyField),
                     (MasterTable, OrderTypeField), (MasterTable, OrderNoField), (MasterTable, OrderSerialField),
                     (MasterTable, DepotField), (MasterTable, EditionField), (MasterTable, LineField),
                     (MasterTable, UnitField), (MasterTable, ProduceDateField), (MasterTable, PreSendField),
                     (MasterTable, FinishedField),                     (DetailTable, SerialField), (DetailTable, ProField),
                     (DetailTable, NeedQtyField), (DetailTable, ProQtyField),
                     (DetailTable, ComponentQtyField), (DetailTable, LostRateField),
                     (OrderBomMasterTable, TypeField), (OrderBomMasterTable, NoField), (OrderBomMasterTable, ProField),
                     (OrderBomTable, TypeField), (OrderBomTable, NoField), (OrderBomTable, ProField),
                     (OrderBomTable, ElementProField), (OrderBomTable, ElementQtyField),
                     (OrderBomTable, BaseQtyField), (OrderBomTable, LostRateField), (OrderBomTable, SerialField),
                     (ProductTable, ProField), (ProductTable, DepotField),
                     (ProductTable, LineField), (ProductTable, UnitField),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"展开子制令需要 {table}.{column}，相关表没有这一列，请联系管理员调整。");
            }
        }

        var type = context.KeyValues.Count > 0 ? context.KeyValues[0].Trim() : string.Empty;
        var no = context.KeyValues.Count > 1 ? context.KeyValues[1].Trim() : string.Empty;
        if (type.Length == 0 || no.Length == 0)
        {
            throw new InvalidOperationException("缺少单据主键，禁止无条件展开。");
        }

        var master = await ReadMasterAsync(context, type, no, token);
        if (master.Finished)
        {
            throw new InvalidOperationException("该制令单已完工结案，不能再展开子制令。");
        }

        // 探路：子单由统一创建路径在**它自己的事务**里落库，外层"执行后回滚"兜不住它，
        // 所以这里必须显式只走规划——把"将建几张、跳过几行"说清楚，一行都不写。
        var plan = await PlanAsync(context, type, no, master, token);
        if (context.Preview)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message,
                plan.Total == 0
                    ? "该制令单的用料没有可展开的下阶料，不需要生成子制令。"
                    : $"将生成子制令 {plan.Total} 张（已存在 {plan.Skipped} 行跳过，只补漏）。");
        }
        if (plan.Total == 0)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message, "该制令单的用料没有可展开的下阶料，不需要生成子制令。");
        }

        // 下游单据的权限门：由 API 判断，不因为"是系统生成的"而跳过——生成与手工新建同一把尺子。
        var targetRights = (await permissions.GetAsync(context.ExecutorUserId, TargetModuleId, token)).Rights;
        if (!targetRights.CanAddNew)
        {
            throw new InvalidOperationException("没有制令单的新增权限，请联系管理员开通后再展开。");
        }
        var targetDefinition = await definitionBuilder.GetDefinitionAsync(
            TargetModuleId, context.ExecutorUserId, targetRights.ExecuteTag, targetRights.CanViewCost,
            targetRights.CanViewSecrecy, targetRights.DeniedMasterFields, targetRights.DeniedDetailFields, token)
            ?? throw new InvalidOperationException($"找不到模块 {TargetModuleId} 的定义，无法生成子制令。");
        var targetForm = await definitionBuilder.GetFormDefinitionAsync(
            targetDefinition, context.ExecutorUserId, "new", targetRights.CanViewCost, targetRights.CanViewSecrecy,
            targetRights.DeniedMasterFields, targetRights.DeniedDetailFields,
            targetRights.DenyNewMasterFields, targetRights.DenyNewDetailFields,
            targetRights.DenyModiMasterFields, targetRights.DenyModiDetailFields, token,
            targetRights.CanAddNew, targetRights.CanEdit, targetRights.CanDelete, targetRights.CanApprove,
            targetRights.CanDeapprove, targetRights.CanEndCase, targetRights.CanUnEndCase,
            targetRights.CanFileView, targetRights.CanFileUpda, targetRights.CanFileEdit, targetRights.CanFileDele);
        if (targetForm is null)
        {
            throw new InvalidOperationException("制令单的表单定义不可用，无法生成子制令。");
        }
        var allowedMasterKeys = targetForm.MasterFields
            .Where(field => !field.IsVirtual)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedDetailKeys = targetForm.DetailFields
            .Where(field => !field.IsVirtual)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = new List<string>();
        var counter = new Counter();
        var filled = await FillSelfDetailsAsync(context, targetDefinition, targetForm,
            allowedMasterKeys, allowedDetailKeys, type, no, master, token);
        foreach (var item in plan.Items)
        {
            if (await ChildExistsAsync(context, type, no, item.ElementProNo, token))
            {
                counter.Value++;
                continue;
            }
            var childKey = await CreateChildAsync(context, targetDefinition, targetForm,
                allowedMasterKeys, allowedDetailKeys, type, no, type, no, master, item, token);
            created.Add($"{item.ElementProNo}→{childKey[0]}/{childKey[1]}");
            await ExpandRecursiveAsync(context, targetDefinition, targetForm,
                allowedMasterKeys, allowedDetailKeys, type, no, childKey, item, master,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { master.ProductNo, item.ElementProNo },
                1, created, counter, token);
        }

        logger.LogInformation("制令单展开子制令 module={ModuleId} source={Type}/{No} created={Created} skipped={Skipped}",
            TargetModuleId, type, no, created.Count, counter.Value);
        return new DocumentActionResult(DocumentActionOutcome.Message,
            (filled > 0 ? $"本单按订单 BOM 补 {filled} 行明细；" : string.Empty)
            + $"已生成子制令 {created.Count} 张"
            + (created.Count > 0 ? $"（{string.Join("、", created.Take(5))}{(created.Count > 5 ? "……" : string.Empty)}）" : string.Empty)
            + (counter.Value > 0 ? $"，{counter.Value} 行已有子单跳过" : string.Empty)
            + "。子单未批核，何时开工由计划决定。");
    }

    private sealed record ProduceMaster(
        string ProductNo, double Qty, double SpareQty, string OrderType, string OrderNo, int OrderSerial,
        string DepotId, string Edition, string LineId, string UnitId,
        string ProduceDate, string PreSendDate, bool Finished);

    private sealed record Counter(int Skipped = 0)
    {
        public int Value { get; set; } = Skipped;
    }

    /// <summary>库内同名列类型不一（字符/数字/日期并存），一律按值转文本，不按 string 直读。</summary>
    private static string Text(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal))!.Trim();

    private sealed record ExpandItem(
        string ElementProNo, double ElementQty, double BaseQty, double LostRate,
        string DepotId, string LineId, string UnitId, double Amount, double Spare);

    private sealed record ExpandPlan(IReadOnlyList<ExpandItem> Items, int Skipped, int Total);

    private static async Task<ProduceMaster> ReadMasterAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(ProField)}, N''), ISNULL({q(QtyField)}, 0), ISNULL({q(SpareQtyField)}, 0), "
            + $"ISNULL({q(OrderTypeField)}, N''), ISNULL({q(OrderNoField)}, N''), ISNULL({q(OrderSerialField)}, 0), "
            + $"ISNULL({q(DepotField)}, N''), ISNULL({q(EditionField)}, N''), ISNULL({q(LineField)}, N''), "
            + $"ISNULL({q(UnitField)}, N''), ISNULL({q(ProduceDateField)}, N''), ISNULL({q(PreSendField)}, N''), "
            + $"ISNULL({q(FinishedField)}, 0) "
            + $"FROM dbo.{q(MasterTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new InvalidOperationException($"找不到制令单 {type}/{no}。");
        }
        return new ProduceMaster(
            Text(reader, 0), Convert.ToDouble(reader.GetValue(1)),
            Convert.ToDouble(reader.GetValue(2)), Text(reader, 3), Text(reader, 4),
            Convert.ToInt32(reader.GetValue(5)), Text(reader, 6), Text(reader, 7),
            Text(reader, 8), Text(reader, 9), Text(reader, 10), Text(reader, 11),
            !reader.IsDBNull(12) && Convert.ToBoolean(reader.GetValue(12)));
    }

    /// <summary>
    /// 规划：本单用料里有下阶料的行（`MOC_BOM_STRU_M` 里该料号还当过父项）。
    /// 只读不写，探路与执行共用——执行时按同一名单建，名单变了以执行时为准。
    /// </summary>
    private static async Task<ExpandPlan> PlanAsync(
        DocumentActionContext context, string type, string no, ProduceMaster master, CancellationToken token)
    {
        var items = await ReadExpandableElementsAsync(context, type, no, master.ProductNo, master.Qty, master.SpareQty, token);
        var skipped = 0;
        foreach (var item in items)
        {
            if (await ChildExistsAsync(context, type, no, item.ElementProNo, token))
            {
                skipped++;
            }
        }
        return new ExpandPlan(items, skipped, items.Count - skipped);
    }

    /// <summary>
    /// 订单 BOM 里本料号的有下阶料的行：用量按 `ELEMENT_QTY/BASE_QTY×(1+损耗)` 摊到本单数量上。
    /// `BASE_QTY = 0` 的行直接拒绝（除零与"计算用料"同一口径）。
    /// </summary>
    private static async Task<IReadOnlyList<ExpandItem>> ReadExpandableElementsAsync(
        DocumentActionContext context, string rootType, string rootNo, string productNo,
        double amount, double spare, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM(d.{q(ElementProField)})), ISNULL(d.{q(ElementQtyField)}, 0), "
            + $"ISNULL(d.{q(BaseQtyField)}, 0), ISNULL(d.{q(LostRateField)}, 0), "
            + $"ISNULL(p.{q(DepotField)}, N''), ISNULL(p.{q(LineField)}, N''), ISNULL(p.{q(UnitField)}, N'') "
            + $"FROM dbo.{q(OrderBomTable)} d "
            + $"LEFT JOIN dbo.{q(ProductTable)} p ON LTRIM(RTRIM(p.{q(ProField)}))=LTRIM(RTRIM(d.{q(ElementProField)})) "
            + $"WHERE LTRIM(RTRIM(d.{q(TypeField)}))=@rootType AND LTRIM(RTRIM(d.{q(NoField)}))=@rootNo "
            + $"AND LTRIM(RTRIM(d.{q(ProField)}))=@pro "
            + $"AND EXISTS (SELECT 1 FROM dbo.{q(OrderBomMasterTable)} m "
            + $"WHERE LTRIM(RTRIM(m.{q(TypeField)}))=@rootType AND LTRIM(RTRIM(m.{q(NoField)}))=@rootNo "
            + $"AND LTRIM(RTRIM(m.{q(ProField)}))=LTRIM(RTRIM(d.{q(ElementProField)}))) "
            + $"ORDER BY d.{q(SerialField)};",
            context.Connection, context.Transaction);
        command.Parameters.Add("@rootType", SqlDbType.NVarChar, 20).Value = rootType;
        command.Parameters.Add("@rootNo", SqlDbType.NVarChar, 40).Value = rootNo;
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        var rows = new List<ExpandItem>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var baseQty = Convert.ToDouble(reader.GetValue(2));
            if (Math.Abs(baseQty) < 0.0000001)
            {
                throw new InvalidOperationException(
                    $"订单 BOM 有底数为 0 的行（料号 {reader.GetString(0).Trim()}），无法展开子制令，请先修正 BOM。");
            }
            var factor = Convert.ToDouble(reader.GetValue(1)) / baseQty
                * (1 + Convert.ToDouble(reader.GetValue(3)) / 100);
            var unitId = Text(reader, 6);
            if (unitId.Length == 0)
            {
                throw new InvalidOperationException(
                    $"料号 {Text(reader, 0)} 在产品主档里没有单位，子单明细单位必填，无法展开。请先维护产品资料。");
            }
            rows.Add(new ExpandItem(Text(reader, 0),
                Convert.ToDouble(reader.GetValue(1)), baseQty, Convert.ToDouble(reader.GetValue(3)),
                Text(reader, 4), Text(reader, 5), unitId,
                factor * amount, factor * spare));
        }
        return rows;
    }

    /// <summary>
    /// 本单明细补齐（旧 `FillElementFromBom` 的去重版）：只补本单还没有的料号，
    /// 已有行一分不动——与统一更新整单重写不同，這裡是纯增量写入，不碰既有行的项次与数量。
    /// 用直接 INSERT 而不用统一更新路径是故意的：后者会整表删后重插并重排项次，
    /// 把用户已有的明细身份推平（F1 口径），而这里只要求"缺的补上"。
    /// </summary>
    private static async Task<int> FillSelfDetailsAsync(
        DocumentActionContext context, WorkbenchDefinition targetDefinition, FormDefinition targetForm,
        IReadOnlySet<string> allowedMasterKeys, IReadOnlySet<string> allowedDetailKeys,
        string type, string no, ProduceMaster master, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"INSERT INTO dbo.{q(DetailTable)} ({q(TypeField)}, {q(NoField)}, {q(SerialField)}, "
            + $"{q(ProField)}, {q(DepotField)}, {q(NeedQtyField)}, {q(ProQtyField)}, {q(ComponentQtyField)}, "
            + $"{q(LostRateField)}, {q(OrderTypeField)}, {q(OrderNoField)}, {q(OrderSerialField)}, "
            + $"{q(ApplyQtyField)}, {q(PurchaseQtyField)}) "
            + $"SELECT @type, @no, "
            + $"(SELECT ISNULL(MAX({q(SerialField)}), 0) FROM dbo.{q(DetailTable)} "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no) "
            + $"+ ROW_NUMBER() OVER (ORDER BY LTRIM(RTRIM(d.{q(ElementProField)}))), "
            + $"LTRIM(RTRIM(d.{q(ElementProField)})), ISNULL(p.{q(DepotField)}, N''), "
            + $"ISNULL(d.{q(ElementQtyField)}, 0)/ISNULL(NULLIF(d.{q(BaseQtyField)}, 0), 1)"
            + $"*(1+ISNULL(d.{q(LostRateField)}, 0)/100)*@qty, @qty, "
            + $"ISNULL(d.{q(ElementQtyField)}, 0), ISNULL(d.{q(LostRateField)}, 0), "
            + $"@orderType, @orderNo, @orderSerial, 0, 0 "
            + $"FROM dbo.{q(OrderBomTable)} d "
            + $"LEFT JOIN dbo.{q(ProductTable)} p ON LTRIM(RTRIM(p.{q(ProField)}))=LTRIM(RTRIM(d.{q(ElementProField)})) "
            + $"WHERE LTRIM(RTRIM(d.{q(TypeField)}))=@rootType AND LTRIM(RTRIM(d.{q(NoField)}))=@rootNo "
            + $"AND LTRIM(RTRIM(d.{q(ProField)}))=@pro "
            + $"AND NOT EXISTS (SELECT 1 FROM dbo.{q(DetailTable)} x "
            + $"WHERE x.{q(TypeField)}=@type AND x.{q(NoField)}=@no "
            + $"AND LTRIM(RTRIM(x.{q(ProField)}))=LTRIM(RTRIM(d.{q(ElementProField)})));",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        command.Parameters.Add("@rootType", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@rootNo", SqlDbType.NVarChar, 40).Value = no;
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = master.ProductNo;
        command.Parameters.Add("@qty", SqlDbType.Float).Value = master.Qty;
        command.Parameters.Add("@orderType", SqlDbType.NVarChar, 20).Value = master.OrderType;
        command.Parameters.Add("@orderNo", SqlDbType.NVarChar, 40).Value = master.OrderNo;
        command.Parameters.Add("@orderSerial", SqlDbType.Int).Value = master.OrderSerial;
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> ChildExistsAsync(
        DocumentActionContext context, string parentType, string parentNo, string elementProNo, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT TOP 1 1 FROM dbo.{q(MasterTable)} "
            + $"WHERE LTRIM(RTRIM({q(ParentTypeField)}))=@parentType AND LTRIM(RTRIM({q(ParentNoField)}))=@parentNo "
            + $"AND LTRIM(RTRIM({q(ProField)}))=@pro;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@parentType", SqlDbType.NVarChar, 20).Value = parentType;
        command.Parameters.Add("@parentNo", SqlDbType.NVarChar, 40).Value = parentNo;
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = elementProNo;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private async Task ExpandRecursiveAsync(
        DocumentActionContext context, WorkbenchDefinition targetDefinition, FormDefinition targetForm,
        IReadOnlySet<string> allowedMasterKeys, IReadOnlySet<string> allowedDetailKeys,
        string rootType, string rootNo, IReadOnlyList<string> childKey, ExpandItem item, ProduceMaster master,
        ISet<string> path, int depth, List<string> created, Counter counter, CancellationToken token)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"展开超过 {MaxDepth} 层（可能 BOM 成环），已拒绝，请先检查工单 BOM。");
        }
        var children = await ReadExpandableElementsAsync(
            context, rootType, rootNo, item.ElementProNo, item.Amount, item.Spare, token);
        foreach (var child in children)
        {
            if (!path.Add(child.ElementProNo))
            {
                throw new InvalidOperationException($"工单 BOM 成环（料号 {child.ElementProNo} 在路径上重复出现），已拒绝展开。");
            }
            try
            {
                if (await ChildExistsAsync(context, childKey[0], childKey[1], child.ElementProNo, token))
                {
                    counter.Value++;
                    continue;
                }
                // 注意两套键各干各的：PARENT_ 指向直接父单，订单 BOM 永远按根单查。
                var grandKey = await CreateChildAsync(context, targetDefinition, targetForm,
                    allowedMasterKeys, allowedDetailKeys, rootType, rootNo,
                    childKey[0], childKey[1], master, child, token);
                created.Add($"{child.ElementProNo}→{grandKey[0]}/{grandKey[1]}");
                await ExpandRecursiveAsync(context, targetDefinition, targetForm,
                    allowedMasterKeys, allowedDetailKeys, rootType, rootNo, grandKey, child, master,
                    path, depth + 1, created, counter, token);
            }
            finally
            {
                path.Remove(child.ElementProNo);
            }
        }
    }

    /// <summary>
    /// 建一张子单（含它自己的明细，一次提交）：主表只交目标表单认得的列，
    /// 明细按订单 BOM 逐行带用量；单号由统一路径发号，不自定。
    ///
    /// 父项两列（`PARENT_TYPE/PARENT_NO`）不在统一表单里（UNKNOWN_FIELD，交了会被拒），
    /// 建完后定向 UPDATE 回填——与"生成调整单"回写来源单同一手法：统一路径管"建得合法"，
    /// 表单之外的追溯列由处理器自己写，两步的归属见下面的补偿说明。
    /// </summary>
    private async Task<IReadOnlyList<string>> CreateChildAsync(
        DocumentActionContext context, WorkbenchDefinition targetDefinition, FormDefinition targetForm,
        IReadOnlySet<string> allowedMasterKeys, IReadOnlySet<string> allowedDetailKeys,
        string rootType, string rootNo,
        string parentType, string parentNo, ProduceMaster master, ExpandItem item, CancellationToken token)
    {
        var masterValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Put(string key, string? value)
        {
            if (allowedMasterKeys.Contains(key))
            {
                masterValues[key] = value;
            }
        }
        Put(ProField, item.ElementProNo);
        // 判别标志（REWORK_TAG/OUTSIDE_TAG）不交：库里有 DEFAULT (0) 兜底，
        // 交了反而被"只读不可提交"拦下——与手工建单同一口径。
        Put(QtyField, item.Amount.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        Put(SpareQtyField, item.Spare.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        Put(DepotField, item.DepotId);
        Put(EditionField, master.Edition);
        Put(LineField, item.LineId);
        Put(UnitField, item.UnitId);
        // 子单自己的单据日期取父单日期（旧实现同：建子单时沿用触发单的日期）。
        Put(ProduceDateField, NormalizeDate(master.ProduceDate));
        Put(PlanStartField, NormalizeDate(master.ProduceDate));
        Put(PlanEndField, NormalizeDate(master.PreSendDate));
        Put(PreSendField, NormalizeDate(master.PreSendDate));

        var details = await ReadChildDetailsAsync(context, rootType, rootNo, item, token);
        var request = new SaveRecordRequest(
            masterValues,
            details.Select(row => (IReadOnlyDictionary<string, string?>)row
                .Where(pair => allowedDetailKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
                .ToList());
        var created = await commandHandler.CreateRecordAsync(
            targetDefinition, targetForm, request, context.Executor,
            context.ExecutorUserId, dataFilter: null, token);
        if (created.Status != RecordAccessStatus.Ok || created.Key is null || created.Key.Count < 2)
        {
            var errors = created.FieldErrors is { Count: > 0 }
                ? "（" + string.Join("；", created.FieldErrors.Take(5).Select(error =>
                    error.RowIndex is int row ? $"第{row + 1}行 {error.Field}：{error.Message}" : $"{error.Field}：{error.Message}")) + "）"
                : string.Empty;
            throw new InvalidOperationException(
                $"生成子制令（料号 {item.ElementProNo}）失败：{created.ErrorMessage ?? created.ErrorCode ?? "未知原因"}{errors}");
        }
        // 父项回填与上面的创建不在同一事务（统一创建路径自带事务，回滚兜不住）：
        // 若这一步失败，子单已存在但没挂上父项，报错里点名子单号，由人工按号核对
        // （与"生成调整单"的两阶段补偿同一口径）。
        await StampParentAsync(parentType, parentNo, created.Key[0], created.Key[1], token);
        return created.Key;
    }

    /// <summary>
    /// 子单的父项回填：只写追溯两列，其它一分不动。独立提交（子单创建已独立提交，
    /// 回填若搭调用方事务的车、中途失败回滚会导致"单在、父项没挂上"，重试即复制子单）。
    /// </summary>
    private async Task StampParentAsync(
        string parentType, string parentNo, string childType, string childNo, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(MasterTable)} SET {q(ParentTypeField)}=@parentType, {q(ParentNoField)}=@parentNo "
            + $"WHERE {q(TypeField)}=@childType AND {q(NoField)}=@childNo "
            + $"AND ISNULL({q(ParentTypeField)}, N'')=N'' AND ISNULL({q(ParentNoField)}, N'')=N'';",
            connection);
        command.Parameters.Add("@parentType", SqlDbType.NVarChar, 20).Value = parentType;
        command.Parameters.Add("@parentNo", SqlDbType.NVarChar, 40).Value = parentNo;
        command.Parameters.Add("@childType", SqlDbType.NVarChar, 20).Value = childType;
        command.Parameters.Add("@childNo", SqlDbType.NVarChar, 40).Value = childNo;
        if (await command.ExecuteNonQueryAsync(token) == 0)
        {
            throw new InvalidOperationException(
                $"子制令 {childType}/{childNo} 已生成，但父项回填未生效（可能已被并发修改）；请核对该子单后处理。");
        }
    }

    /// <summary>日期归一：父单日期能解析即用，否则取今天（子单日期必填，不能空着建）。</summary>
    private static string NormalizeDate(string value) =>
        DateTime.TryParse(value, out var date)
            ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>子单明细：订单 BOM 里该子件料号的行，用量按子单数量摊（与旧 `FillElementFromBom` 同形）。</summary>
    private async Task<IReadOnlyList<Dictionary<string, string?>>> ReadChildDetailsAsync(
        DocumentActionContext context, string rootType, string rootNo, ExpandItem item, CancellationToken token)
    {
        // 注意 MOC_BOM_STRU_D 的 SERIAL_NO 在子单明细里不沿用：子单按自己的行序从 1 起排。
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM(d.{q(ElementProField)})), ISNULL(d.{q(ElementQtyField)}, 0), "
            + $"ISNULL(d.{q(BaseQtyField)}, 0), ISNULL(d.{q(LostRateField)}, 0), "
            + $"ISNULL(p.{q(DepotField)}, N'') "
            + $"FROM dbo.{q(OrderBomTable)} d "
            + $"LEFT JOIN dbo.{q(ProductTable)} p ON LTRIM(RTRIM(p.{q(ProField)}))=LTRIM(RTRIM(d.{q(ElementProField)})) "
            + $"WHERE LTRIM(RTRIM(d.{q(TypeField)}))=@rootType AND LTRIM(RTRIM(d.{q(NoField)}))=@rootNo "
            + $"AND LTRIM(RTRIM(d.{q(ProField)}))=@pro ORDER BY d.{q(SerialField)};",
            context.Connection, context.Transaction);
        command.Parameters.Add("@rootType", SqlDbType.NVarChar, 20).Value = rootType;
        command.Parameters.Add("@rootNo", SqlDbType.NVarChar, 40).Value = rootNo;
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = item.ElementProNo;
        var rows = new List<Dictionary<string, string?>>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var baseQty = Convert.ToDouble(reader.GetValue(2));
            if (Math.Abs(baseQty) < 0.0000001)
            {
                throw new InvalidOperationException(
                    $"订单 BOM 有底数为 0 的行（料号 {Text(reader, 0)}），无法展开子制令。");
            }
            var need = Convert.ToDouble(reader.GetValue(1)) / baseQty
                * (1 + Convert.ToDouble(reader.GetValue(3)) / 100) * item.Amount;
            // 项次不交：明细序号由服务端按提交顺序分配（ServerFilled），与手工建单同一口径。
            rows.Add(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ProField] = Text(reader, 0),
                [DepotField] = Text(reader, 4),
                [NeedQtyField] = need.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                [ProQtyField] = item.Amount.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                [ComponentQtyField] = Convert.ToDouble(reader.GetValue(1)).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                [LostRateField] = Convert.ToDouble(reader.GetValue(3)).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                [OrderTypeField] = rootType,
                [OrderNoField] = rootNo,
            });
        }
        return rows;
    }
}
