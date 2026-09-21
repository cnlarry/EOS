/*------------------------------------------------------------------
  退役"备注"参数行：SYSSS.REMARK / HR_SETUP.REMARK / HRM_SETUP.REMARK 是旧单行宽表给那一行
  留的**行级自由文本备注**，不是参数——全系统没有任何读取方；纵向化后新表每行自带 REMARK 列
  承担"参数说明"，把旧备注当参数搬进来只会让页面多出一个改了也没有作用的项。

  这里删除这三行，"其它（MISC）"分组随之消失（该组下只有这一个候选）。

  删除前的三处取值留档：110111 = '0'、180213 = 'JJJ  YYYY MM DD HH NN KKKKK' + 换行 +
  '123 4567  89   01   23 45   67890'（当年手记的卡号位置草稿）、180662 = NULL。
------------------------------------------------------------------*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT N'退役备注参数行，原值如下：';
SELECT CONVERT(nvarchar(10), OWNER_MODULE) + N' | ' + ISNULL(PARAM_VALUE, N'<null>') AS RETIRED_REMARK
FROM dbo.SYSSS WHERE PARAM_KEY = N'REMARK';

DELETE FROM dbo.SYSSS WHERE PARAM_KEY = N'REMARK';

IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE PARAM_KEY = N'REMARK')
    THROW 51000, N'系统参数：备注参数行未退役。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE GROUP_CODE = N'MISC')
    THROW 51000, N'系统参数：备注退役后"其它"分组仍有参数。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSSS) <> 103
    THROW 51000, N'系统参数：退役备注后参数条数不是 103。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 110111 HAVING COUNT(*) <> 36)
    THROW 51000, N'系统参数：110111 参数条数不是 36。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 180213 HAVING COUNT(*) <> 34)
    THROW 51000, N'系统参数：180213 参数条数不是 34。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 180662 HAVING COUNT(*) <> 33)
    THROW 51000, N'系统参数：180662 参数条数不是 33。', 1;

PRINT N'备注参数行已退役：SYSSS 103 行（110111=36 / 180213=34 / 180662=33）。';
GO
