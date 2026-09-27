-- ============================================================================
-- EOS.ERP migration 244: 月结快照按钮的授权（WS-8 的可用性缺口）
-- ----------------------------------------------------------------------------
-- **这条迁移是端到端复核逼出来的**：WS-18b 的 HTTP 验收要经生产路径点一次
-- 模块 1304 的「生成快照」，结果拿到的不是快照而是 **403 `ACTION_FORBIDDEN`**——
-- 按钮级授权是 **fail-closed** 的（配了、发布了，不等于有人能按），而这张按钮
-- **在 `SYSDD_BUTTON` 与 `SYSDH_BUTTON` 里一行都没有** ⇒ 生产上**没人能按**，
-- 那条"人工生成快照"的路等于不存在（快照只能靠别的手段产生）。
--
-- 授权规则与迁移 241（1302 的受控写入口）同一条：**能浏览该模块的组**即可按；
-- 库内若没有该模块的组记录，退回管理员组 `1`（并在日志里说清是兜底）。
--
-- 幂等：按 (G_IDX, M_IDX, BUTTON_KEY) 判重；已存在则不动。
-- 出口断言：迁移后必须**至少有一个组**被授权（ALLOW_TAG=1）——按钮的全部意义就是
-- 让人能点，配了却没人能按属于缺陷，宁可让迁移失败。
-- ============================================================================

DECLARE @granted INT = 0;
DECLARE @module INT = 1304;
DECLARE @button NVARCHAR(200) = N'month-close-snapshot';

INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
SELECT DISTINCT h.G_IDX, @module, @button, 1,
       N'WS-8：能浏览料件每月统计单的组可生成月结快照', N'ADR020', GETDATE()
  FROM dbo.SYSDH h
 WHERE h.M_IDX = @module
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON b
                    WHERE b.M_IDX = @module AND RTRIM(b.BUTTON_KEY) = @button AND b.G_IDX = h.G_IDX);
SET @granted = @@ROWCOUNT;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON
                WHERE M_IDX = @module AND RTRIM(BUTTON_KEY) = @button AND ALLOW_TAG = 1)
BEGIN
    INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
        VALUES (N'1', @module, @button, 1,
                N'WS-8：兜底授权（库内没有能浏览 1304 的组记录）', N'ADR020', GETDATE());
    PRINT N'== 按钮授权：库内无能浏览 1304 的组记录，已兜底授权给组 1 ==';
END
ELSE
    PRINT CONCAT(N'== 按钮授权：本次新增 ', @granted, N' 个组 ==');

DECLARE @auth INT = (SELECT COUNT(*) FROM dbo.SYSDH_BUTTON
                      WHERE M_IDX = @module AND RTRIM(BUTTON_KEY) = @button AND ALLOW_TAG = 1);
IF @auth = 0 THROW 52240, N'生成快照按钮仍无人可点击，迁移中止（按钮的全部意义就是让人能点）。', 1;
PRINT CONCAT(N'== 就位：生成快照按钮的授权组数 = ', @auth, N' ==');
