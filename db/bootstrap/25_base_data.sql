/*
 * 基础资料种子（供其它模块引用的主档）
 *
 * 目的：空库初始化后即具备一套可用的基础资料，业务单据能直接引用，无需逐张主档先建。
 * 范围：与运行时引用最相关的主档——公司/部门、员工、币别、税别、银行、价格与付款条件、
 *       结算方式、帐款类型、产品类别/颜色/单位/材质、仓库/库位/库存策略，以及
 *       电子元器件行业上下游的示例客户与厂商。
 *
 * 口径：
 * - 全部按物理列写入，批核标记（CONFIRM_TAG）一律置 1，保证可直接被选择器引用；
 * - 幂等：按主键存在性判断，可重复执行；已存在的记录不覆盖、不删除；
 * - 示例客户/厂商为模拟数据，非真实企业，REMARK 中已注明，可整体替换为真实数据；
 * - **不得引用真实主体的资料**：人名一律用一眼可辨的占位名（张三/李四/王五…），
 *   员工姓名用虚拟姓名；不要把手边既有库里的姓名、企业名直接抄进本文件。
 *
 * 说明：本文件只放"供引用"的主档；登录账号与权限见 30_admin.sql。
 */

SET NOCOUNT ON;
GO

-- ================================================================ 公司与部门
IF NOT EXISTS (SELECT 1 FROM dbo.COMPANY WHERE LTRIM(RTRIM(COMPANY_ID)) = N'DEFAULT')
    INSERT dbo.COMPANY (COMPANY_ID, NAME_CN, NAME_EN, SHORT_NAME_CN, SHORT_NAME_EN, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
    VALUES (N'DEFAULT', N'默认有限公司', N'DEFAULT LLC', N'默认公司', N'DEFAULT', 1, GETDATE(), GETDATE());
GO

INSERT dbo.DEPT (DEPT_ID, DEPT_NAME, DEPT_ID_SUPERIOR, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.DEPT_ID, v.DEPT_NAME, v.DEPT_ID_SUPERIOR, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'ZJB', N'总经办',   CAST(NULL AS nchar(10))),
    (N'YW2', N'业务部',   N'ZJB'),
    (N'YW3', N'项目部',   N'ZJB'),
    (N'YZ',  N'业助',     N'ZJB'),
    (N'CG',  N'采购部',   N'ZJB'),
    (N'CK',  N'仓库',     N'ZJB'),
    (N'SCB', N'生产部',   N'ZJB'),
    (N'GC',  N'工程部',   N'ZJB'),
    (N'PB',  N'品保',     N'GC'),
    (N'CW',  N'财务部',   N'ZJB'),
    (N'HS',  N'示例公司', CAST(NULL AS nchar(10)))
) v(DEPT_ID, DEPT_NAME, DEPT_ID_SUPERIOR)
WHERE NOT EXISTS (SELECT 1 FROM dbo.DEPT d WHERE LTRIM(RTRIM(d.DEPT_ID)) = v.DEPT_ID);
GO

-- ================================================================ 员工（登录人）
INSERT dbo.SYSDN (EMP_ID, EMP_NAME, DEPT_ID, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.EMP_ID, v.EMP_NAME, v.DEPT_ID, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'admin', N'系统管理员', N'ZJB'),
    (N'larry', N'演示用户',     N'YW3')
) v(EMP_ID, EMP_NAME, DEPT_ID)
WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSDN e WHERE LTRIM(RTRIM(e.EMP_ID)) = v.EMP_ID);
GO

-- ================================================================ 员工基本资料（供单据引用）
-- 业务单据的"业务员/经办人/员工"引用的是 HR_EMPLOYEE；与登录账号同身份，不额外造人。
INSERT dbo.HR_EMPLOYEE (EMP_ID, EMP_NO, EMP_NAME, DEPT_ID, IN_DATE, KIND, STATE, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.EMP_ID, v.EMP_NO, v.EMP_NAME, v.DEPT_ID, v.IN_DATE, 1, 1, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'admin', N'admin', N'系统管理员', N'ZJB', CAST('2024-01-01' AS date)),
    (N'larry', N'larry', N'演示用户',     N'YW3', CAST('2024-01-01' AS date))
) v(EMP_ID, EMP_NO, EMP_NAME, DEPT_ID, IN_DATE)
WHERE NOT EXISTS (SELECT 1 FROM dbo.HR_EMPLOYEE e WHERE LTRIM(RTRIM(e.EMP_ID)) = v.EMP_ID);
GO

-- ================================================================ 币别
-- 汇率以人民币为本位币的近似值，仅作为可用的初值，按实际行情维护。
INSERT dbo.CURR (CURR_ID, CURR_NAME, CURR_RATE, IS_BASE, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.CURR_ID, v.CURR_NAME, v.CURR_RATE, v.IS_BASE, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'RMB', N'人民币',           1.0000, 1),
    (N'USD', N'美元',             7.2000, 0),
    (N'EUR', N'欧元',             7.8200, 0),
    (N'JPY', N'日元',             0.0475, 0),
    (N'GBP', N'英镑',             9.1500, 0),
    (N'HKD', N'港币',             0.9200, 0),
    (N'TWD', N'新台币',           0.2230, 0),
    (N'KRW', N'韩元',             0.0053, 0),
    (N'SGD', N'新加坡元',         5.3600, 0),
    (N'AUD', N'澳大利亚元',       4.7300, 0),
    (N'CAD', N'加拿大元',         5.2800, 0),
    (N'CHF', N'瑞士法郎',         8.1500, 0),
    (N'NZD', N'新西兰元',         4.3500, 0),
    (N'MYR', N'马来西亚林吉特',   1.5400, 0),
    (N'THB', N'泰铢',             0.2000, 0),
    (N'VND', N'越南盾',           0.00028, 0),
    (N'PHP', N'菲律宾比索',       0.1250, 0),
    (N'IDR', N'印尼卢比',         0.00045, 0),
    (N'INR', N'印度卢比',         0.0860, 0),
    (N'SEK', N'瑞典克朗',         0.6800, 0),
    (N'NOK', N'挪威克朗',         0.6600, 0),
    (N'DKK', N'丹麦克朗',         1.0500, 0),
    (N'RUB', N'俄罗斯卢布',       0.0800, 0),
    (N'ZAR', N'南非兰特',         0.3950, 0),
    (N'BRL', N'巴西雷亚尔',       1.3000, 0),
    (N'MXN', N'墨西哥比索',       0.3600, 0),
    (N'AED', N'阿联酋迪拉姆',     1.9600, 0),
    (N'SAR', N'沙特里亚尔',       1.9200, 0)
) v(CURR_ID, CURR_NAME, CURR_RATE, IS_BASE)
WHERE NOT EXISTS (SELECT 1 FROM dbo.CURR c WHERE LTRIM(RTRIM(c.CURR_ID)) = v.CURR_ID);
GO

-- ================================================================ 税别与税别类型
INSERT dbo.TAX (TAX_ID, TAX_NAME, TAX_RATE, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.TAX_ID, v.TAX_NAME, v.TAX_RATE, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'TAX01', N'转厂',   0.0),
    (N'TAX02', N'增值税', 0.0),
    (N'TAX05', N'不含税', 0.0),
    (N'TAX06', N'出口',   0.0)
) v(TAX_ID, TAX_NAME, TAX_RATE)
WHERE NOT EXISTS (SELECT 1 FROM dbo.TAX t WHERE LTRIM(RTRIM(t.TAX_ID)) = v.TAX_ID);
GO

-- TAX_TYPE 是单据上的"税别类型"字典，被大量字段引用。
INSERT dbo.TAX_TYPE (TAX_TYPE, TAX_TYPE_NAME)
SELECT v.TAX_TYPE, v.TAX_TYPE_NAME
FROM (VALUES
    (N'I', N'内含税'),
    (N'N', N'不含税'),
    (N'O', N'外含税')
) v(TAX_TYPE, TAX_TYPE_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.TAX_TYPE t WHERE LTRIM(RTRIM(t.TAX_TYPE)) = v.TAX_TYPE);
GO

-- ================================================================ 银行账号
INSERT dbo.BANK (BANK_ID, BANK_NAME_CN, CURR_ID, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.BANK_ID, v.BANK_NAME_CN, v.CURR_ID, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'8888888888888888', N'中国银行', N'RMB')
) v(BANK_ID, BANK_NAME_CN, CURR_ID)
WHERE NOT EXISTS (SELECT 1 FROM dbo.BANK b WHERE LTRIM(RTRIM(b.BANK_ID)) = v.BANK_ID);
GO

-- ================================================================ 价格条件 / 付款条件 / 结算方式 / 帐款类型
INSERT dbo.PRICE (PRICE_ID, PRICE_NAME, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.PRICE_ID, v.PRICE_NAME, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'P001', N'整批可优惠'),
    (N'P002', N'全格'),
    (N'P003', N'单价不含税'),
    (N'P004', N'单价含普税'),
    (N'P005', N'单价含税')
) v(PRICE_ID, PRICE_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.PRICE p WHERE LTRIM(RTRIM(p.PRICE_ID)) = v.PRICE_ID);
GO

INSERT dbo.PAYMENT (PAYMENT_ID, PAYMENT_NAME, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.PAYMENT_ID, v.PAYMENT_NAME, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'001', N'现金结算'),
    (N'002', N'月结30天'),
    (N'003', N'月结45天'),
    (N'004', N'月结60天'),
    (N'005', N'月结90天'),
    (N'006', N'月结105天'),
    (N'007', N'月结120天'),
    (N'008', N'当月结'),
    (N'009', N'货到15天'),
    (N'010', N'月结55天')
) v(PAYMENT_ID, PAYMENT_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.PAYMENT p WHERE LTRIM(RTRIM(p.PAYMENT_ID)) = v.PAYMENT_ID);
GO

INSERT dbo.RECEIVE (RECEIVE_ID, RECEIVE_NAME, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.RECEIVE_ID, v.RECEIVE_NAME, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'CASH', N'现金'),
    (N'CHK',  N'支票'),
    (N'TT',   N'电汇'),
    (N'M30',  N'月结30天')
) v(RECEIVE_ID, RECEIVE_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.RECEIVE r WHERE LTRIM(RTRIM(r.RECEIVE_ID)) = v.RECEIVE_ID);
GO

INSERT dbo.ACCOUNT_TYPE (ACCOUNT_TYPE_ID, ACCOUNT_TYPE_NAME, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.ACCOUNT_TYPE_ID, v.ACCOUNT_TYPE_NAME, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'AP',     N'应付帐款'),
    (N'AR',     N'应收帐款'),
    (N'PREPAY', N'预付帐款')
) v(ACCOUNT_TYPE_ID, ACCOUNT_TYPE_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.ACCOUNT_TYPE a WHERE LTRIM(RTRIM(a.ACCOUNT_TYPE_ID)) = v.ACCOUNT_TYPE_ID);
GO

-- ================================================================ 产品类别（电子元器件行业）
INSERT dbo.SORT (SORT_ID, SORT_NAME, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.SORT_ID, v.SORT_NAME, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'SMD', N'半导体电子元件类'),
    (N'PCB', N'线路板类'),
    (N'CON', N'连接器'),
    (N'SWT', N'开关'),
    (N'CBA', N'线组类'),
    (N'INS', N'其他绝缘材料类'),
    (N'PAS', N'塑料产品类'),
    (N'MAS', N'五金件类'),
    (N'MTS', N'模治具'),
    (N'EPS', N'其他电子元件类')
) v(SORT_ID, SORT_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.SORT s WHERE LTRIM(RTRIM(s.SORT_ID)) = v.SORT_ID);
GO

-- ================================================================ 颜色
INSERT dbo.COLOR (COLOR_ID, COLOR_NAME, COLOR_NAME_CN, COLOR_NAME_EN, COLOR_IDNO, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.COLOR_ID, v.COLOR_NAME_CN, v.COLOR_NAME_CN, v.COLOR_NAME_EN, v.COLOR_IDNO, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'C001', N'黑色',     N'Black',              N'BK001'),
    (N'C002', N'红色',     N'Red',                N'RD001'),
    (N'C003', N'黄色',     N'Yellow',             N'YL001'),
    (N'C004', N'白色',     N'White',              N'WH001'),
    (N'C005', N'绿色',     N'Green',              N'GN001'),
    (N'C006', N'蓝色',     N'Blue',               N'BL001'),
    (N'C007', N'灰色',     N'Gray',               N'GY001'),
    (N'C008', N'棕色',     N'Brown',              N'BN001'),
    (N'C009', N'橙色',     N'Orange',             N'OR001'),
    (N'C010', N'紫色',     N'Purple',             N'PL001'),
    (N'C011', N'粉红色',   N'Pink',               N'PK001'),
    (N'C012', N'青色',     N'Cyan',               N'CY001'),
    (N'C013', N'金色',     N'Gold',               N'GD001'),
    (N'C014', N'银色',     N'Silver',             N'SV001'),
    (N'C015', N'米白色',   N'Ivory',              N'IV001'),
    (N'C016', N'藏青色',   N'Navy Blue',          N'NB001'),
    (N'C017', N'天蓝色',   N'Sky Blue',           N'SB001'),
    (N'C018', N'浅蓝色',   N'Light Blue',         N'LB001'),
    (N'C019', N'深蓝色',   N'Dark Blue',          N'DB001'),
    (N'C020', N'墨绿色',   N'Dark Green',         N'DG001'),
    (N'C021', N'草绿色',   N'Grass Green',        N'GG001'),
    (N'C022', N'翠绿色',   N'Emerald Green',      N'EG001'),
    (N'C023', N'橄榄绿',   N'Olive Green',        N'OG001'),
    (N'C024', N'卡其色',   N'Khaki',              N'KH001'),
    (N'C025', N'驼色',     N'Camel',              N'CM001'),
    (N'C026', N'咖啡色',   N'Coffee',             N'CF001'),
    (N'C027', N'酒红色',   N'Wine Red',           N'WR001'),
    (N'C028', N'玫红色',   N'Rose Red',           N'RR001'),
    (N'C029', N'桃红色',   N'Peach',              N'PC001'),
    (N'C030', N'朱红色',   N'Vermilion',          N'VM001'),
    (N'C031', N'柠檬黄',   N'Lemon Yellow',       N'LY001'),
    (N'C032', N'金黄色',   N'Golden Yellow',      N'GY002'),
    (N'C033', N'米黄色',   N'Beige',              N'BG001'),
    (N'C034', N'象牙白',   N'Ivory White',        N'IW001'),
    (N'C035', N'透明',     N'Transparent',        N'TR001'),
    (N'C036', N'半透明',   N'Translucent',        N'TL001'),
    (N'C037', N'本色',     N'Natural',            N'NT001'),
    (N'C038', N'深灰色',   N'Dark Gray',          N'DGY01'),
    (N'C039', N'浅灰色',   N'Light Gray',         N'LGY01'),
    (N'C040', N'银灰色',   N'Silver Gray',        N'SGY01'),
    (N'C041', N'碳黑色',   N'Carbon Black',       N'CB001'),
    (N'C042', N'珍珠白',   N'Pearl White',        N'PW001'),
    (N'C043', N'香槟金',   N'Champagne Gold',     N'CG001'),
    (N'C044', N'巧克力色', N'Chocolate',          N'CH001'),
    (N'C045', N'靛蓝色',   N'Indigo',             N'IN001'),
    (N'C046', N'湖蓝色',   N'Lake Blue',          N'LKB01'),
    (N'C047', N'军绿色',   N'Army Green',         N'AG001'),
    (N'C048', N'荧光绿',   N'Fluorescent Green',  N'FG001'),
    (N'C049', N'荧光黄',   N'Fluorescent Yellow', N'FY001'),
    (N'C050', N'荧光橙',   N'Fluorescent Orange', N'FO001')
) v(COLOR_ID, COLOR_NAME_CN, COLOR_NAME_EN, COLOR_IDNO)
WHERE NOT EXISTS (SELECT 1 FROM dbo.COLOR c WHERE LTRIM(RTRIM(c.COLOR_ID)) = v.COLOR_ID);
GO

-- ================================================================ 单位
-- UNIT_TYPE：1=数量、2=重量、3=长度（与库内既有取值一致）。
INSERT dbo.UNIT (UNIT_ID, UNIT_NAME, UNIT_TYPE, BASE_RATE, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.UNIT_ID, v.UNIT_NAME, v.UNIT_TYPE, 1, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'PCS',  N'个',        N'1'),
    (N'KPCS', N'千个',      N'1'),
    (N'SET',  N'套',        N'1'),
    (N'PAIR', N'对',        N'1'),
    (N'BOX',  N'箱',        N'1'),
    (N'BAG',  N'包',        N'1'),
    (N'卷',   N'卷',        N'1'),
    (N'TRAY', N'盘',        N'1'),
    (N'SHT',  N'张',        N'1'),
    (N'BAR',  N'条',        N'1'),
    (N'PCS2', N'只',        N'1'),
    (N'GRP',  N'组',        N'1'),
    (N'KG',   N'公斤',      N'2'),
    (N'G',    N'克',        N'2'),
    (N'MG',   N'毫克',      N'2'),
    (N'T',    N'吨',        N'2'),
    (N'LB',   N'磅',        N'2'),
    (N'OZ',   N'盎司',      N'2'),
    (N'M',    N'米',        N'3'),
    (N'CM',   N'厘米',      N'3'),
    (N'MM',   N'毫米',      N'3'),
    (N'KM',   N'千米',      N'3'),
    (N'UM',   N'微米',      N'3'),
    (N'YD',   N'码',        N'3'),
    (N'FT',   N'英尺',      N'3'),
    (N'IN',   N'英寸',      N'3')
) v(UNIT_ID, UNIT_NAME, UNIT_TYPE)
WHERE NOT EXISTS (SELECT 1 FROM dbo.UNIT u WHERE LTRIM(RTRIM(u.UNIT_ID)) = v.UNIT_ID);
GO

-- ================================================================ 材质
INSERT dbo.STUFF (STUFF_ID, STUFF_NAME, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.STUFF_ID, v.STUFF_NAME, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'SJ',  N'塑胶'),
    (N'TIE', N'铁'),
    (N'Cu',  N'铜'),
    (N'BXG', N'不锈钢'),
    (N'Yin', N'银')
) v(STUFF_ID, STUFF_NAME)
WHERE NOT EXISTS (SELECT 1 FROM dbo.STUFF s WHERE LTRIM(RTRIM(s.STUFF_ID)) = v.STUFF_ID);
GO

-- ================================================================ 仓库 / 库位 / 库存策略
INSERT dbo.DEPOT (DEPOT_ID, DEPOT_NAME, MRP, PRINCIPAL, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT v.DEPOT_ID, v.DEPOT_NAME, v.MRP, v.PRINCIPAL, 1, GETDATE(), GETDATE()
FROM (VALUES
    (N'CP',    N'示例仓',         CAST(1 AS bit), CAST(NULL AS nvarchar(50))),
    (N'TW',    N'托外仓',         CAST(1 AS bit), CAST(NULL AS nvarchar(50))),
    (N'PJ',    N'品检仓',         CAST(0 AS bit), CAST(NULL AS nvarchar(50))),
    (N'BF',    N'报废仓',         CAST(0 AS bit), CAST(NULL AS nvarchar(50))),
    (N'DEMOC', N'示例东莞仓', CAST(1 AS bit), N'CK-03')
) v(DEPOT_ID, DEPOT_NAME, MRP, PRINCIPAL)
WHERE NOT EXISTS (SELECT 1 FROM dbo.DEPOT d WHERE LTRIM(RTRIM(d.DEPOT_ID)) = v.DEPOT_ID);
GO

-- 每个仓库预置一个"未指定位置（待归位）"哨兵库位：库存的默认落点。
INSERT dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STATUS, CONFIRM_TAG, CREATE_DATE, CONFIRM_DATE)
SELECT d.DEPOT_ID, N'-', N'/-', N'BIN', N'未指定位置（待归位）', N'A', 1, GETDATE(), GETDATE()
FROM dbo.DEPOT d
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.DEPOT_LOCATION l
    WHERE l.DEPOT_ID = d.DEPOT_ID AND LTRIM(RTRIM(l.LOCATION_NO)) = N'-'
);
GO

-- 全局默认库存策略：作用于所有未单独配置的仓库。
IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY WHERE LTRIM(RTRIM(DEPOT_ID)) = N'*')
    INSERT dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, EXPIRY_MODE)
    VALUES (N'*', 0, N'FIXED', 0, 0, 2);
GO

-- ================================================================ 客户（电子元器件行业下游，模拟数据）
INSERT dbo.CLIENT (CLIENT_ID, CLIENT_NAME, FULL_NAME_CN, LINKMAN, TEL, FAX,
                   REG_ADDR_CN, DELI_ADDR_CN, CURR_ID, TAX_ID, TAX_TYPE, PAYMENT_DAY, SALES_ID,
                   BUSINESS_TAG, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE, CREATE_DATE, REMARK)
SELECT v.CLIENT_ID, v.CLIENT_NAME, v.FULL_NAME_CN, v.LINKMAN, v.TEL, v.FAX,
       v.REG_ADDR_CN, v.REG_ADDR_CN, N'RMB', N'TAX02', N'O', v.PAYMENT_DAY, N'larry',
       1, 1, N'admin', GETDATE(), GETDATE(), N'示例基础数据（模拟电子元器件行业下游客户，非真实企业）'
FROM (VALUES
    (N'C0001', N'睿驰科技',   N'深圳市睿驰电子科技有限公司', N'张三',  N'0755-8612-3301', N'0755-8612-3302', N'广东省深圳市南山区科技园南区高新南七道18号', 30),
    (N'C0002', N'恒通通讯',   N'东莞市恒通通讯设备有限公司', N'李四',  N'0769-2288-1102', N'0769-2288-1103', N'广东省东莞市松山湖高新技术产业开发区工业西路6号', 45),
    (N'C0003', N'华芯智能',   N'苏州华芯智能科技有限公司',   N'王五',  N'0512-6789-2200', N'0512-6789-2201', N'江苏省苏州市工业园区星湖街328号', 60),
    (N'C0004', N'天翼智穿',   N'深圳市天翼智能穿戴有限公司', N'赵六',  N'0755-2398-7710', N'0755-2398-7711', N'广东省深圳市宝安区新安街道宝源路2001号', 30),
    (N'C0005', N'锐能电源',   N'惠州市锐能电源科技有限公司', N'钱七',  N'0752-2088-5566', N'0752-2088-5567', N'广东省惠州市仲恺高新区惠风东三路12号', 45),
    (N'C0006', N'联创安防',   N'杭州联创安防科技有限公司',   N'孙八',  N'0571-8899-3312', N'0571-8899-3313', N'浙江省杭州市滨江区江陵路88号', 60),
    (N'C0007', N'普瑞医疗',   N'厦门普瑞医疗器械有限公司',   N'周九',  N'0592-5566-7788', N'0592-5566-7789', N'福建省厦门市海沧区新阳工业区阳光西路8号', 30)
) v(CLIENT_ID, CLIENT_NAME, FULL_NAME_CN, LINKMAN, TEL, FAX, REG_ADDR_CN, PAYMENT_DAY)
WHERE NOT EXISTS (SELECT 1 FROM dbo.CLIENT c WHERE LTRIM(RTRIM(c.CLIENT_ID)) = v.CLIENT_ID);
GO

INSERT dbo.CLIENT_LINKMAN (CLIENT_ID, SERIAL_NO, LINKMAN, DEPT_ID, TEL, EMAIL, REMARK)
SELECT v.CLIENT_ID, 1, v.LINKMAN, v.DEPT_ID, v.TEL, v.EMAIL, N'示例基础数据'
FROM (VALUES
    (N'C0001', N'张三', N'采购部', N'0755-8612-3301', N'client1@example.com'),
    (N'C0002', N'李四', N'采购部', N'0769-2288-1102', N'client2@example.com'),
    (N'C0003', N'王五', N'供应链', N'0512-6789-2200', N'client3@example.com'),
    (N'C0004', N'赵六', N'采购部', N'0755-2398-7710', N'client4@example.com'),
    (N'C0005', N'钱七', N'采购部', N'0752-2088-5566', N'client5@example.com'),
    (N'C0006', N'孙八', N'采购部', N'0571-8899-3312', N'client6@example.com'),
    (N'C0007', N'周九', N'供应链', N'0592-5566-7788', N'client7@example.com')
) v(CLIENT_ID, LINKMAN, DEPT_ID, TEL, EMAIL)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.CLIENT_LINKMAN l
    WHERE LTRIM(RTRIM(l.CLIENT_ID)) = v.CLIENT_ID AND l.SERIAL_NO = 1
);
GO

-- ================================================================ 厂商（电子元器件行业上游，模拟数据）
INSERT dbo.SUPPLIER (SUPPLIER_ID, SUPPLIER_NAME, FULL_NAME_CN, LINKMAN, TEL, FAX,
                     REG_ADDR_CN, DELI_ADDR_CN, CURR_ID, TAX_ID, TAX_TYPE, PAYMENT_DAY, PURCHASE_ID,
                     OUTER_TAG, BUSINESS_TAG, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE, CREATE_DATE, REMARK)
SELECT v.SUPPLIER_ID, v.SUPPLIER_NAME, v.FULL_NAME_CN, v.LINKMAN, v.TEL, v.FAX,
       v.REG_ADDR_CN, v.REG_ADDR_CN, N'RMB', N'TAX02', N'O', v.PAYMENT_DAY, N'larry',
       0, 1, 1, N'admin', GETDATE(), GETDATE(), N'示例基础数据（模拟电子元器件行业上游供应商，非真实企业）'
FROM (VALUES
    (N'S0001', N'捷科半导体', N'深圳市捷科半导体有限公司', N'吴十',  N'0755-8321-6601', N'0755-8321-6602', N'广东省深圳市福田区深南大道1006号', 30),
    (N'S0002', N'盈丰元件',   N'东莞市盈丰电子元件有限公司', N'郑十一', N'0769-8821-2203', N'0769-8821-2204', N'广东省东莞市长安镇振安东路88号', 45),
    (N'S0003', N'昆山精密',   N'昆山精密电路板有限公司',     N'王十二', N'0512-5733-9901', N'0512-5733-9902', N'江苏省昆山市玉山镇城北路168号', 60),
    (N'S0004', N'华翔连接器', N'宁波华翔连接器有限公司',     N'冯十三', N'0574-8766-3320', N'0574-8766-3321', N'浙江省宁波市鄞州区首南街道天童南路99号', 45),
    (N'S0005', N'科锐材料',   N'苏州科锐电子材料有限公司',   N'陈十四', N'0512-6888-7701', N'0512-6888-7702', N'江苏省苏州市吴中区木渎镇金枫路7号', 60),
    (N'S0006', N'微芯微电子', N'深圳市微芯微电子有限公司',   N'褚十五', N'0755-2699-8801', N'0755-2699-8802', N'广东省深圳市南山区高新北区朗山路9号', 30),
    (N'S0007', N'鸿远电子',   N'上海鸿远电子科技有限公司',   N'卫十六', N'021-5488-6601', N'021-5488-6602', N'上海市闵行区申北路1122号', 45)
) v(SUPPLIER_ID, SUPPLIER_NAME, FULL_NAME_CN, LINKMAN, TEL, FAX, REG_ADDR_CN, PAYMENT_DAY)
WHERE NOT EXISTS (SELECT 1 FROM dbo.SUPPLIER s WHERE LTRIM(RTRIM(s.SUPPLIER_ID)) = v.SUPPLIER_ID);
GO

INSERT dbo.SUPPLIER_LINKMAN (SUPPLIER_ID, SERIAL_NO, LINKMAN, DEPT_ID, TEL, EMAIL, REMARK)
SELECT v.SUPPLIER_ID, 1, v.LINKMAN, v.DEPT_ID, v.TEL, v.EMAIL, N'示例基础数据'
FROM (VALUES
    (N'S0001', N'吴十',   N'销售部', N'0755-8321-6601', N'supplier1@example.com'),
    (N'S0002', N'郑十一', N'销售部', N'0769-8821-2203', N'supplier2@example.com'),
    (N'S0003', N'王十二', N'销售部', N'0512-5733-9901', N'supplier3@example.com'),
    (N'S0004', N'冯十三', N'销售部', N'0574-8766-3320', N'supplier4@example.com'),
    (N'S0005', N'陈十四', N'销售部', N'0512-6888-7701', N'supplier5@example.com'),
    (N'S0006', N'褚十五', N'销售部', N'0755-2699-8801', N'supplier6@example.com'),
    (N'S0007', N'卫十六', N'销售部', N'021-5488-6601', N'supplier7@example.com')
) v(SUPPLIER_ID, LINKMAN, DEPT_ID, TEL, EMAIL)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SUPPLIER_LINKMAN l
    WHERE LTRIM(RTRIM(l.SUPPLIER_ID)) = v.SUPPLIER_ID AND l.SERIAL_NO = 1
);
GO

PRINT '基础资料种子已就绪（公司/部门、员工、币别、税别、银行、条件、类别/颜色/单位/材质、仓库、示例客户与厂商）';
GO
