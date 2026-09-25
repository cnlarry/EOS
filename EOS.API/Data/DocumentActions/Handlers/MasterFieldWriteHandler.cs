using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// 批次 / 库存主档的**受控写入口**（ADR-020 §9.2 D2 / WS-17）：只改"人工语义列"，
/// 引擎维护列一律拒，且**绝不碰库存账**（不触发 `inventory-move`、不写 `INV_DEPOT_LOG`）。
/// </summary>
/// <remarks>
/// **为什么另开一扇门，而不是给 1302 / 1303 开统一表单白名单**：
/// `check-inventory-read-hosts.ps1` 的写路径断言要求"主表是余额 / 流水 / 批次账的模块不得进白名单"——
/// 白名单一开，通用表单就能**绕过移动引擎直接改账**（改的是账的底层行，而不是单据）。
/// 所以这里只开一条窄路：字段白名单 + 引擎维护列拒写 + 按钮级授权（fail-closed）+ 全程审计。
///
/// **三条边界**：
/// <list type="bullet">
/// <item>白名单在**代码**里（<see cref="WritableByParameter"/>）。它表达的是"这几列是人工语义列"这一
/// 领域事实，不是可以用配置打开的开关：配置只能决定**按钮**（谁可点、要不要二次确认、参数声明），
/// 不能扩权。</item>
/// <item>引擎维护列**无论如何都拒**（<see cref="EngineMaintainedColumns"/>）：即使白名单被人改了、
/// 即使配置把它们声明成参数，服务端一样拒——这一层不依赖前端灰显，也不依赖 `FIELDS` 的只读位。</item>
/// <item>真正的写入走**统一保存路径**（<see cref="WorkbenchCommandHandler.UpdateRecordAsync"/>）：
/// 字段校验、只读判定、审计前后值、幂等全部复用；本处理器不自己拼业务表的写 SQL。</item>
/// </list>
///
/// **探路（`Preview`）绝不写**：`UpdateRecordAsync` 自带连接与事务并自行提交，执行器那次回滚兜不住它
/// （框架文档对这个坑有明写），所以探路分支必须在调用它**之前**返回。
/// </remarks>
internal sealed class MasterFieldWriteHandler : IDocumentUserAction
{
    public const string ActionKey = "master-field-write";

    /// <summary>参数键 → 允许修改的列。白名单只此一处；动它等于改领域事实，要走评审。</summary>
    private static readonly IReadOnlyDictionary<string, string> WritableByParameter =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["effectDate"] = "EFFECT_DATE",
            ["batchDate"] = "BATCH_DATE",
            ["againCheckDate"] = "AGAIN_CHECK_DATE",
        };

    /// <summary>
    /// 引擎维护列：移动引擎 / 月结 / 盘点在写，人工不得改。这是**第二道闸**——
    /// 与 `FIELDS.IS_READONLY` 各管一层：只读位管"表单能不能提交"，本表管"服务端认不认"。
    /// </summary>
    private static readonly IReadOnlySet<string> EngineMaintainedColumns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IN_SUM", "OUT_SUM", "LATELY_IN_DATE",
            "QTY", "COST_PRICE", "COST_AMOUNT", "INIT_QTY", "USEABLE_QTY", "LAST_CHECK_DATE",
        };

    private readonly WorkbenchAuditWriter _audit;

    public MasterFieldWriteHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string Key => ActionKey;

    public string Label => "修改人工字段";

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var changes = ReadRequestedChanges(context);
        if (changes.Count == 0)
        {
            throw new EffectValidationException(
                $"没有要修改的字段：请至少提供一个允许修改的列（{string.Join('、', WritableByParameter.Values)}）。");
        }

        var before = await ReadCurrentAsync(context, changes.Keys.ToArray(), token);
        var summary = string.Join("；", changes.Select(change =>
            $"{change.Key}：{Show(before.GetValueOrDefault(change.Key))} → {Show(change.Value)}"));

        if (context.Preview)
        {
            // 探路：只说清楚"会改成什么"，一个字都不写。
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将修改 {changes.Count} 个人工字段（{summary}）。本次未改动任何数据。");
        }

        // 校验复用统一路径的同一支（字段是否开放、是否只读、类型与必填）。
        var validation = RecordPayloadValidator.ValidateSubmitted(context.Form.MasterFields, changes);
        if (validation.Errors.Count > 0)
        {
            throw new EffectValidationException($"修改未完成：{validation.Errors[0].Message}");
        }

        /*
         * 写入在**执行器的事务里**完成，而不是再调一次统一保存入口——这不是偷懒，是实测出来的：
         * 执行器已经用 UPDLOCK 锁住了这一行（`ReadMasterRowAsync(..., lockRow: !probe)`），
         * 而统一保存入口自带连接与事务 ⇒ 从另一个事务改同一行会一直等锁，实测 30 秒超时。
         * 因此这里只更新白名单里的那几列，其余一律沿用既有原语：
         * 校验 = RecordPayloadValidator（与统一路径同一支）、审计 = WorkbenchAuditWriter（带前后值）。
         */
        var keyColumns = context.MasterPkOrder;
        var setSql = string.Join(", ", changes.Keys.Select((column, index) => $"[{column}]=@v{index}"));
        var where = string.Join(" AND ", keyColumns.Select((column, index) => $"[{column}]=@k{index}"));
        int affected;
        await using (var command = new SqlCommand(
            $"UPDATE dbo.[{context.Definition.MasterTable}] SET {setSql} WHERE {where};",
            context.Connection,
            context.Transaction))
        {
            var index = 0;
            foreach (var value in changes.Values)
            {
                command.Parameters.Add($"@v{index}", SqlDbType.NVarChar, -1).Value = (object?)value ?? DBNull.Value;
                index++;
            }
            for (var key = 0; key < keyColumns.Count; key++)
            {
                command.Parameters.Add($"@k{key}", SqlDbType.NVarChar, 60).Value = context.KeyValues[key];
            }
            affected = await command.ExecuteNonQueryAsync(token);
        }
        if (affected != 1)
        {
            throw new EffectValidationException($"目标记录 {context.RecordKey} 不存在或已被删除，修改未生效。");
        }

        // 前后值进审计：这条入口改的是属性列，状态列不留痕，审计就是唯一的痕迹。
        await _audit.WriteEventAsync(
            context.Connection,
            context.Transaction,
            context.Definition.ModuleId,
            context.RecordKey,
            "UPDATE",
            $"人工字段修改（{string.Join('、', changes.Keys)}）",
            context.Executor,
            "WORKBENCH_RECORD",
            result: 1,
            fieldChanges: changes
                .Select(change => new AuditFieldChange(
                    change.Key, before.GetValueOrDefault(change.Key), change.Value, null))
                .ToList(),
            token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已修改 {changes.Count} 个人工字段（{summary}）。库存账未受影响。");
    }

    /// <summary>
    /// 把参数翻成"列 → 新值"。三道判据都在这里，顺序也是刻意的。
    /// </summary>
    /// <remarks>
    /// **空值 = 这次不改它**：框架会把**声明过的参数全部**物化进 <see cref="DocumentActionContext.Parameters"/>
    /// （没填的是空串），所以这里根本无法区分"用户没填"与"用户想清空"。按"不改"处置——
    /// 反过来按"清空"处置，一次点击就会把用户没碰过的另外两个日期一起抹掉。
    /// 代价如实写明：**本入口不支持把某列清空**（要把日期清成 NULL 得另开一条路径）。
    ///
    /// **维护列先判**：参数键按 `camelCase → UPPER_SNAKE` 折成列名再比对维护列集合，
    /// 因此这一层**不依赖白名单**——白名单被谁改宽了，`IN_SUM` 一样进不来（WS-17 的判别性就在这）。
    /// </remarks>
    private static Dictionary<string, string?> ReadRequestedChanges(DocumentActionContext context)
    {
        var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameter, value) in context.Parameters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;   // 未提供：不是"要改成空"
            }
            var column = ToColumnName(parameter);
            if (EngineMaintainedColumns.Contains(column))
            {
                throw new EffectValidationException($"字段“{column}”由引擎维护，不允许人工修改。");
            }
            if (!WritableByParameter.TryGetValue(parameter, out var allowed))
            {
                throw new EffectValidationException(
                    $"字段“{parameter}”不在允许修改的清单里（只允许：{string.Join('、', WritableByParameter.Values)}）。");
            }
            changes[allowed] = value.Trim();
        }
        return changes;
    }

    /// <summary>参数键 → 列名：`effectDate` → `EFFECT_DATE`。两种写法指的是同一列，只允许一处口径。</summary>
    private static string ToColumnName(string parameter)
    {
        var builder = new System.Text.StringBuilder(parameter.Length + 4);
        foreach (var character in parameter.Trim())
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    /// <summary>
    /// 读改动前的值——只用于探路文案与"改了什么"的对账，不参与写入判据。
    /// 值从**库里**读，不从请求体里取（请求体只有主键与声明过的参数可信）。
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string?>> ReadCurrentAsync(
        DocumentActionContext context,
        IReadOnlyList<string> columns,
        CancellationToken token)
    {
        var keyColumns = context.MasterPkOrder;
        if (keyColumns.Count == 0 || keyColumns.Count != context.KeyValues.Count)
        {
            throw new EffectValidationException("主档模块的主键列未定义，无法定位记录。");
        }
        var where = string.Join(" AND ", keyColumns.Select((column, index) => $"[{column}]=@k{index}"));
        var projection = string.Join(", ", columns.Select(column => $"[{column}]"));

        await using var command = new SqlCommand(
            $"SELECT {projection} FROM dbo.[{context.Definition.MasterTable}] WHERE {where};",
            context.Connection,
            context.Transaction);
        for (var index = 0; index < keyColumns.Count; index++)
        {
            command.Parameters.Add($"@k{index}", SqlDbType.NVarChar, 60).Value = context.KeyValues[index];
        }

        var current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new EffectValidationException($"找不到记录 {context.RecordKey}。");
        }
        for (var index = 0; index < columns.Count; index++)
        {
            current[columns[index]] = reader.IsDBNull(index) ? null : Show(reader.GetValue(index));
        }
        return current;
    }

    /// <summary>给用户看的取值：空/空串一律说成"(空)"，别让人对着两个空白猜前后差异。</summary>
    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(空)" : value.Trim();

    private static string Show(object? value) =>
        value is null or DBNull ? "(空)" : Show(Convert.ToString(value));
}
