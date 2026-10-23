-- 清理历史脏标记：15 条 2026-10-22 由 EOS-MIG / IT 标下的行，其快照已与当前配置一致。
--
-- 判据不是"标记时间久"，而是**已核对快照新鲜**：scripts/check-snapshot-staleness.ps1
-- 实测 262/262 fresh、stale=0，即这 15 个模块的已发布快照与按当前配置重建的定义一致，
-- 脏标记已无对象可指向，留着只会让"待发布"清单长期失真。
--
-- 只删这两类来源的行：LAST_MODIFIED_BY 为 EOS-MIG / IT 且落在下面这组模块上。
-- 之后新产生的脏标记（其它来源或其它模块）不受影响。

SET NOCOUNT ON;

DELETE FROM dbo.WORKBENCH_MODULE_DIRTY
WHERE LAST_MODIFIED_BY IN (N'EOS-MIG', N'IT')
  AND M_IDX IN (1509, 1515, 1603, 1604, 1609, 1903, 2815,
                130102, 180102, 180105, 180110, 180111,
                300301, 300304, 110101);

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY
            WHERE LAST_MODIFIED_BY IN (N'EOS-MIG', N'IT')
              AND M_IDX IN (1509, 1515, 1603, 1604, 1609, 1903, 2815,
                            130102, 180102, 180105, 180110, 180111,
                            300301, 300304, 110101))
    THROW 51002, '历史脏标记未被清空。', 1;
