-- ============================================================================
-- EOS.ERP migration 247: 按钮授权表的生命周期列收紧（清掉 check-lifecycle-columns 的历史红）
-- ----------------------------------------------------------------------------
-- 现象：`check-lifecycle-columns` 断言"`CREATE_PERSON` / `CREATE_DATE` 必须 NOT NULL 且有 DEFAULT"，
-- 而 `SYSDD_BUTTON` / `SYSDH_BUTTON`（建表迁移 `218`，的按钮级授权面）这两列是
-- `NULL` 且无默认 ⇒ 这条门禁自那以后一直是红的（SSDT 快照 `EOS.Database/dbo/Tables/*.sql`
-- 声明的也是 `NULL`，与真库一致 ⇒ 不是漂移，是当初就这么建的）。
--
-- 为什么值得修：授权行的"谁在什么时候发的"是审计线索；允许 NULL 就允许"发了但查不出谁发的"。
-- 与其它表的生命周期列口径一致（NOT NULL + DEFAULT）之后，写入侧不必再各自判空。
--
-- 回填取值（**不编造事实**）：
--   · `CREATE_PERSON` 为 NULL ⇒ 落空串（"未知"，而不是随便填一个人名）；
--   · `CREATE_DATE` 为 NULL ⇒ 落哨兵 `1900-01-01`（"未知时刻"，而不是拿收紧当天的 GETDATE() 冒充历史）。
-- 收紧之后新写入的行由 DEFAULT 兜底（`''` / `GETDATE()`）。
--
-- 幂等：列已是 NOT NULL 且已有默认约束即跳过；回填只碰 NULL 行。
-- 出口断言：4 列（2 表 × 2 列）都必须 NOT NULL 且有默认约束，否则迁移失败。
-- ============================================================================

DECLARE @filledPerson INT = 0;
DECLARE @filledDate INT = 0;

UPDATE dbo.SYSDD_BUTTON SET CREATE_PERSON = N'' WHERE CREATE_PERSON IS NULL;
SET @filledPerson = @filledPerson + @@ROWCOUNT;
UPDATE dbo.SYSDD_BUTTON SET CREATE_DATE = '1900-01-01' WHERE CREATE_DATE IS NULL;
SET @filledDate = @filledDate + @@ROWCOUNT;
UPDATE dbo.SYSDH_BUTTON SET CREATE_PERSON = N'' WHERE CREATE_PERSON IS NULL;
SET @filledPerson = @filledPerson + @@ROWCOUNT;
UPDATE dbo.SYSDH_BUTTON SET CREATE_DATE = '1900-01-01' WHERE CREATE_DATE IS NULL;
SET @filledDate = @filledDate + @@ROWCOUNT;
PRINT CONCAT(N'== 回填：CREATE_PERSON ', @filledPerson, N' 行 / CREATE_DATE ', @filledDate, N' 行（未知 → 空串 / 哨兵 1900-01-01）==');

/* ---------- SYSDD_BUTTON ---------- */
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.SYSDD_BUTTON')
            AND name = N'CREATE_PERSON' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.SYSDD_BUTTON ALTER COLUMN CREATE_PERSON NVARCHAR(50) NOT NULL;
    PRINT N'== SYSDD_BUTTON.CREATE_PERSON → NOT NULL ==';
END
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.SYSDD_BUTTON')
                  AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.SYSDD_BUTTON'), N'CREATE_PERSON', 'ColumnId'))
    ALTER TABLE dbo.SYSDD_BUTTON ADD CONSTRAINT DF_SYSDD_BUTTON_CREATE_PERSON DEFAULT (N'') FOR CREATE_PERSON;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.SYSDD_BUTTON')
            AND name = N'CREATE_DATE' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.SYSDD_BUTTON ALTER COLUMN CREATE_DATE DATETIME NOT NULL;
    PRINT N'== SYSDD_BUTTON.CREATE_DATE → NOT NULL ==';
END
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.SYSDD_BUTTON')
                  AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.SYSDD_BUTTON'), N'CREATE_DATE', 'ColumnId'))
    ALTER TABLE dbo.SYSDD_BUTTON ADD CONSTRAINT DF_SYSDD_BUTTON_CREATE_DATE DEFAULT (GETDATE()) FOR CREATE_DATE;

/* ---------- SYSDH_BUTTON ---------- */
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.SYSDH_BUTTON')
            AND name = N'CREATE_PERSON' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.SYSDH_BUTTON ALTER COLUMN CREATE_PERSON NVARCHAR(50) NOT NULL;
    PRINT N'== SYSDH_BUTTON.CREATE_PERSON → NOT NULL ==';
END
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.SYSDH_BUTTON')
                  AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.SYSDH_BUTTON'), N'CREATE_PERSON', 'ColumnId'))
    ALTER TABLE dbo.SYSDH_BUTTON ADD CONSTRAINT DF_SYSDH_BUTTON_CREATE_PERSON DEFAULT (N'') FOR CREATE_PERSON;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.SYSDH_BUTTON')
            AND name = N'CREATE_DATE' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.SYSDH_BUTTON ALTER COLUMN CREATE_DATE DATETIME NOT NULL;
    PRINT N'== SYSDH_BUTTON.CREATE_DATE → NOT NULL ==';
END
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.SYSDH_BUTTON')
                  AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.SYSDH_BUTTON'), N'CREATE_DATE', 'ColumnId'))
    ALTER TABLE dbo.SYSDH_BUTTON ADD CONSTRAINT DF_SYSDH_BUTTON_CREATE_DATE DEFAULT (GETDATE()) FOR CREATE_DATE;

/* ---------- 出口断言 ---------- */
DECLARE @bad INT = (
    SELECT COUNT(*) FROM sys.columns c
     LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
     WHERE c.object_id IN (OBJECT_ID(N'dbo.SYSDD_BUTTON'), OBJECT_ID(N'dbo.SYSDH_BUTTON'))
       AND c.name IN (N'CREATE_PERSON', N'CREATE_DATE')
       AND (c.is_nullable = 1 OR dc.object_id IS NULL));
IF @bad > 0 THROW 52270, N'按钮授权表的生命周期列仍未收紧到 NOT NULL + DEFAULT，迁移中止。', 1;
PRINT N'== 就位：SYSDD_BUTTON / SYSDH_BUTTON 的 CREATE_PERSON / CREATE_DATE 均已 NOT NULL 且有默认 ==';
