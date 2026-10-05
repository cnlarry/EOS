namespace EOS.API.Security;

/// <summary>
/// 业务模块号集中定义：消除 Controller 与仓储中的魔法模块 ID。
/// 模块号对应 MODULES.M_IDX；系统管理 2306 见 PermissionModules.SystemManagement。
/// 新增模块权限门一律引用本类常量，禁止在调用点散落裸数字。
/// </summary>
public static class ModuleIds
{
    /// <summary>BOM 结构（1204，Bom/AdminFields/FieldConfiguration 三链共用）。</summary>
    public const int BomStructure = 1204;

    /// <summary>流程设计器（2101）。</summary>
    public const int FlowDesigner = 2101;

    /// <summary>待办工作台/我的任务（2102）。</summary>
    public const int MyTasks = 2102;

    /// <summary>流程监控（2103）。</summary>
    public const int WorkflowMonitor = 2103;

    /// <summary>产品可用库存重计（230901）。</summary>
    public const int MrpRecalc = 230901;

    /// <summary>员工批量发卡（180218）。</summary>
    public const int CardBatch = 180218;

    /// <summary>考勤模拟生成（180654）。</summary>
    public const int AttendanceSimulate = 180654;

    /// <summary>考勤真实抽取生成（180659）。</summary>
    public const int AttendanceExtract = 180659;

    /// <summary>依薪资调整考勤（180505）。</summary>
    public const int AttendanceAdjustWage = 180505;

    /// <summary>车辆汇总分析表（199901）。</summary>
    public const int CarSummary = 199901;

    /// <summary>单行参数表设置 SYSSS（110111）。</summary>
    public const int SystemSettings = 110111;

    /// <summary>单行参数表设置 HR_SETUP（180213）。</summary>
    public const int HrSetup = 180213;

    /// <summary>单行参数表设置 HRM_SETUP（180662）。</summary>
    public const int HrmSetup = 180662;
}
