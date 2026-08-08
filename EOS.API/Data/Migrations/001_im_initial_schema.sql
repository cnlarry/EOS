-- ============================================================================
-- EOS.IM 初始 Schema v1（001_im_initial_schema.sql）
-- ----------------------------------------------------------------------------
-- 库：EOS.IM（企业即时通讯消息库，独立于 Hiswitek 旧库）
--
-- 存储模型（方案 B：服务器仅"送达暂存"，客户端本地为权威历史）：
--   1. im_messages 只保存"送达暂存"副本，不是权威历史；
--      客户端确认收到后写入本地权威库，服务端按保留期（默认 30 天）清理。
--   2. 消息序号 Seq 由服务端原子分配（im_conversations.LastMessageSeq 充当计数器，
--      用 UPDATE ... OUTPUT 递增），客户端排序 / 断线补拉 / 未读计算一律以
--      Seq 为准，不信任客户端时间。
--   3. 每个成员的已收 / 已读序号（LastReceivedMessageSeq / LastReadMessageSeq）
--      驱动送达确认与未读数计算。
--   4. 审计只记元数据，不落消息正文。
--
-- 权限边界：成员关系（im_conversation_members）即访问权限，
--           所有消息读写必须先校验成员关系，由 EOS.API 统一授权。
--
-- 命名约定：表 / 列全部使用 im_ 前缀；备注使用中文，说明用途与取值语义。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @imGuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.IM 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.IM'
    THROW 50000, @imGuardMessage, 1;

PRINT N'== 开始创建 EOS.IM schema ==';

-- ============================================================================
-- 1. 会话表
-- ============================================================================
CREATE TABLE dbo.im_conversations
(
    Id                 BIGINT          IDENTITY(1,1) NOT NULL,
    ConversationType   NVARCHAR(16)    NOT NULL,
    Name               NVARCHAR(200)   NULL,
    OwnerUserId        NVARCHAR(64)    NULL,
    CreatedByUserId    NVARCHAR(64)    NOT NULL,
    CreatedAt          DATETIME2(3)    NOT NULL CONSTRAINT DF_im_conversations_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt          DATETIME2(3)    NOT NULL CONSTRAINT DF_im_conversations_UpdatedAt DEFAULT SYSUTCDATETIME(),
    LastMessageSeq     BIGINT          NOT NULL CONSTRAINT DF_im_conversations_LastMessageSeq DEFAULT 0,
    LastMessageAt      DATETIME2(3)    NULL,
    LastMessagePreview NVARCHAR(200)   NULL
);

ALTER TABLE dbo.im_conversations ADD CONSTRAINT PK_im_conversations PRIMARY KEY CLUSTERED (Id);
ALTER TABLE dbo.im_conversations ADD CONSTRAINT CK_im_conversations_Type
    CHECK (ConversationType IN (N'Direct', N'Group'));
GO

-- ============================================================================
-- 2. 会话成员表（含每个成员的同步状态）
-- ============================================================================
CREATE TABLE dbo.im_conversation_members
(
    ConversationId          BIGINT         NOT NULL,
    UserId                  NVARCHAR(64)   NOT NULL,
    Role                    NVARCHAR(16)   NOT NULL CONSTRAINT DF_im_members_Role DEFAULT N'Member',
    JoinedAt                DATETIME2(3)   NOT NULL CONSTRAINT DF_im_members_JoinedAt DEFAULT SYSUTCDATETIME(),
    LeftAt                  DATETIME2(3)   NULL,
    LastReceivedMessageSeq  BIGINT         NOT NULL CONSTRAINT DF_im_members_LastReceived DEFAULT 0,
    LastReadMessageSeq      BIGINT         NOT NULL CONSTRAINT DF_im_members_LastRead DEFAULT 0,
    IsMuted                 BIT            NOT NULL CONSTRAINT DF_im_members_IsMuted DEFAULT 0,
    IsArchived              BIT            NOT NULL CONSTRAINT DF_im_members_IsArchived DEFAULT 0,
    CreatedAt               DATETIME2(3)   NOT NULL CONSTRAINT DF_im_members_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt               DATETIME2(3)   NOT NULL CONSTRAINT DF_im_members_UpdatedAt DEFAULT SYSUTCDATETIME()
);

ALTER TABLE dbo.im_conversation_members ADD CONSTRAINT PK_im_conversation_members PRIMARY KEY CLUSTERED (ConversationId, UserId);
ALTER TABLE dbo.im_conversation_members ADD CONSTRAINT CK_im_members_Role
    CHECK (Role IN (N'Owner', N'Admin', N'Member'));
GO

-- ============================================================================
-- 3. 消息表（送达暂存副本，非权威历史）
-- ============================================================================
CREATE TABLE dbo.im_messages
(
    Id                BIGINT           IDENTITY(1,1) NOT NULL,
    ConversationId    BIGINT           NOT NULL,
    Seq               BIGINT           NOT NULL,
    ClientMessageId   UNIQUEIDENTIFIER NOT NULL,
    SenderUserId      NVARCHAR(64)     NOT NULL,
    MessageType       NVARCHAR(16)     NOT NULL,
    Content           NVARCHAR(MAX)    NOT NULL,
    SentAt            DATETIME2(3)     NOT NULL CONSTRAINT DF_im_messages_SentAt DEFAULT SYSUTCDATETIME(),
    DeliveredAt       DATETIME2(3)     NULL,
    ExpiresAt         DATETIME2(3)     NOT NULL CONSTRAINT DF_im_messages_ExpiresAt DEFAULT DATEADD(DAY, 30, SYSUTCDATETIME()),
    IsRecalled        BIT              NOT NULL CONSTRAINT DF_im_messages_IsRecalled DEFAULT 0,
    RecalledAt        DATETIME2(3)     NULL,
    RecalledByUserId  NVARCHAR(64)     NULL
);

ALTER TABLE dbo.im_messages ADD CONSTRAINT PK_im_messages PRIMARY KEY CLUSTERED (Id);
ALTER TABLE dbo.im_messages ADD CONSTRAINT CK_im_messages_Type
    CHECK (MessageType IN (N'Text', N'Card', N'System'));
GO

-- ============================================================================
-- 4. 元数据审计日志表
-- ============================================================================
CREATE TABLE dbo.im_audit_log
(
    Id               BIGINT         IDENTITY(1,1) NOT NULL,
    ConversationId   BIGINT         NULL,
    MessageId        BIGINT         NULL,
    ActorUserId      NVARCHAR(64)   NOT NULL,
    ActionType       NVARCHAR(32)   NOT NULL,
    Detail           NVARCHAR(MAX)  NULL,
    CreatedAt        DATETIME2(3)   NOT NULL CONSTRAINT DF_im_audit_CreatedAt DEFAULT SYSUTCDATETIME()
);

ALTER TABLE dbo.im_audit_log ADD CONSTRAINT PK_im_audit_log PRIMARY KEY CLUSTERED (Id);
GO

-- ============================================================================
-- 5. 索引
-- ============================================================================
CREATE INDEX IX_im_conversation_members_User
    ON dbo.im_conversation_members (UserId, LeftAt);

CREATE UNIQUE INDEX UX_im_messages_ConversationSeq
    ON dbo.im_messages (ConversationId, Seq);

CREATE UNIQUE INDEX UX_im_messages_ConversationClient
    ON dbo.im_messages (ConversationId, ClientMessageId);

CREATE INDEX IX_im_messages_Expiry
    ON dbo.im_messages (ExpiresAt, DeliveredAt);

CREATE INDEX IX_im_audit_log_Conversation
    ON dbo.im_audit_log (ConversationId, CreatedAt);

CREATE INDEX IX_im_audit_log_Actor
    ON dbo.im_audit_log (ActorUserId, CreatedAt);
GO

-- ============================================================================
-- 6. 外键
-- ============================================================================
ALTER TABLE dbo.im_conversation_members ADD CONSTRAINT FK_im_conversation_members_conversation
    FOREIGN KEY (ConversationId) REFERENCES dbo.im_conversations (Id) ON DELETE CASCADE;

ALTER TABLE dbo.im_messages ADD CONSTRAINT FK_im_messages_conversation
    FOREIGN KEY (ConversationId) REFERENCES dbo.im_conversations (Id) ON DELETE CASCADE;

ALTER TABLE dbo.im_audit_log ADD CONSTRAINT FK_im_audit_log_conversation
    FOREIGN KEY (ConversationId) REFERENCES dbo.im_conversations (Id);

ALTER TABLE dbo.im_audit_log ADD CONSTRAINT FK_im_audit_log_message
    FOREIGN KEY (MessageId) REFERENCES dbo.im_messages (Id);
GO

PRINT N'== 表结构创建完成，开始添加中文备注 ==';

-- ============================================================================
-- 7. 表与列的中文备注（MS_Description）
-- ============================================================================

-- ---- 表：im_conversations ----
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话表：记录单聊 / 群聊会话本身。只存会话级元数据（类型、名称、创建人、消息序号计数），不存消息正文；成员关系与个人状态见 im_conversation_members。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话ID，内部主键，由数据库自增生成；对外一律按字符串处理，不参与业务计算。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'Id';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话类型：Direct=单聊（两人，无群名）；Group=群聊（多人，可有群名）。取值受 CHECK 约束限制。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'ConversationType';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话名称：群聊为群名称，可为空（界面默认按成员展示）；单聊恒为 NULL（界面显示对方姓名）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'Name';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'群主用户ID（对应 EOS 现有用户体系，非本库新建用户）；单聊为 NULL。群主可转让。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'OwnerUserId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话创建人用户ID，用于审计与默认权限。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'CreatedByUserId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话创建时间（服务器 UTC），客户端展示时转本地时区。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'CreatedAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话最近变更时间（改名、成员变更等），用于列表排序与缓存失效。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'UpdatedAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话内最后一条已分配消息序号，同时充当会话内 Seq 分配计数器：插入新消息时用 UPDATE … OUTPUT 原子递增，保证并发安全；禁止客户端直接修改。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'LastMessageSeq';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'最后一条消息的服务端时间（UTC），会话列表排序字段；尚无消息时为 NULL。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'LastMessageAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话列表展示的最后一条消息预览：仅存脱敏摘要（文本截断；卡片为“[卡片]：<类型>”；系统消息为“[系统消息]”），严禁存完整消息正文。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversations', @level2type=N'COLUMN', @level2name=N'LastMessagePreview';

-- ---- 表：im_conversation_members ----
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话成员表：成员即访问权限边界。同时承载每个成员在会话内的同步状态（已收到 / 已读序号、免打扰、归档），是送达确认与未读计算的依据。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'所属会话ID，外键关联 im_conversations。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'ConversationId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'成员用户ID，对应 EOS 认证用户。服务端所有消息读写必须先校验本表成员关系，未入群即无权限。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'UserId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'成员角色：Owner=群主；Admin=管理员；Member=普通成员；单聊固定为 Member。取值受 CHECK 约束限制。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'Role';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'加入会话时间（最近一次加入时间；退群后重新加入时更新）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'JoinedAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'退群时间；NULL 表示当前在群。退群成员不可再收发消息，历史消息按客户端本地副本保留。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'LeftAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'该成员已确认收到（客户端 ACK）的消息序号；服务端据此判断消息是否已全部送达，作为暂存清理的依据。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'LastReceivedMessageSeq';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'该成员已读消息序号（客户端上报）；未读数 = 会话 LastMessageSeq - LastReadMessageSeq。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'LastReadMessageSeq';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'免打扰标记：1=本会话不弹通知，仅更新未读角标；0=正常通知。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'IsMuted';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'成员级归档标记：1=从会话列表隐藏（仍可搜索与恢复）；0=正常显示。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'IsArchived';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'成员记录创建时间（UTC）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'CreatedAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'成员记录最近更新时间（角色、免打扰、归档、已读位置变更时更新）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'COLUMN', @level2name=N'UpdatedAt';

-- ---- 表：im_messages ----
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'消息表（送达暂存副本）：方案 B 下服务器只负责可靠投递，不承担权威历史。消息按保留期（默认 30 天）自动清理；客户端收到后写入本地权威库。序号由服务端原子分配。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'消息记录ID，内部主键（数据库自增）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'Id';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'所属会话ID，外键关联 im_conversations。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'ConversationId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话内单调递增消息序号（从 1 开始），由服务端原子分配；客户端排序、断线补拉、未读计算的唯一依据，禁止用客户端时间排序。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'Seq';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'客户端生成的消息标识（GUID），用于发送重试幂等：同一会话内唯一，重复提交同一 ClientMessageId 时服务端返回原消息而非重复落库。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'ClientMessageId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'发送者用户ID。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'SenderUserId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'消息类型：Text=文本；Card=业务卡片；System=系统消息（入群 / 退群 / 改名等）。取值受 CHECK 约束限制。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'MessageType';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'消息内容（结构化 JSON）：Text 为 {"text":"…"}；Card 为 {"cardType":"purchase-order","entityId":"…","title":"…","fields":[…],"actions":[…]}（快照由服务端根据业务数据生成并校验，客户端只提交 cardType+entityId，不提交渲染内容）；System 为 {"action":"member-joined","operator":"…","target":"…"}。本表仅是送达暂存副本，客户端收到后写入本地权威历史。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'Content';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'服务端接收消息的时间（UTC），客户端展示时转本地时区；禁止使用客户端时间。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'SentAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'全部在群成员均已确认收到的时间；为 NULL 表示仍有成员未送达。清理任务对已送达且超过保留期的记录优先清理。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'DeliveredAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'暂存到期时间（= SentAt + 保留期，默认 30 天，可配置）；到期后无论是否送达一律删除。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'ExpiresAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'撤回标记：1=已撤回。撤回为软删除，客户端已收到的本地副本按撤回事件处理，审计记录保留。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'IsRecalled';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'撤回时间（UTC）；未撤回为 NULL。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'RecalledAt';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'撤回操作人用户ID；未撤回为 NULL。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'COLUMN', @level2name=N'RecalledByUserId';

-- ---- 表：im_audit_log ----
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'元数据审计日志：记录会话 / 成员 / 消息的关键动作，只存元数据与动作信息，不存消息正文；用于可追责性与纠纷取证。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'审计记录ID，自增主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'Id';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'关联会话ID；与消息无关的系统动作可为 NULL。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'ConversationId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'关联消息记录ID（发送 / 撤回动作）；其他动作为 NULL。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'MessageId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'操作人用户ID。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'ActorUserId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'动作类型：Send、Recall、CreateConversation、RenameConversation、AddMember、RemoveMember、ChangeRole、LeaveConversation 等，按需扩展。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'ActionType';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'动作补充信息（JSON）：如改名前后名称、成员变更名单；只记元数据，严禁写入消息正文。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'Detail';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'动作发生时间（UTC）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'COLUMN', @level2name=N'CreatedAt';

-- ============================================================================
-- 8. 索引的中文备注
-- ============================================================================
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'按用户查询"我的会话"（含已归档 / 已退群过滤）的索引。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_conversation_members', @level2type=N'INDEX', @level2name=N'IX_im_conversation_members_User';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'会话内消息序号唯一索引：保证同一会话 Seq 单调不重复，支撑断线补拉与排序。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'INDEX', @level2name=N'UX_im_messages_ConversationSeq';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'客户端消息标识唯一索引：保证重试幂等（同一会话内 ClientMessageId 不重复）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'INDEX', @level2name=N'UX_im_messages_ConversationClient';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'暂存清理任务索引：按到期时间 + 是否已送达批量删除过期消息。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_messages', @level2type=N'INDEX', @level2name=N'IX_im_messages_Expiry';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'按会话查询审计记录的索引。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'INDEX', @level2name=N'IX_im_audit_log_Conversation';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'按操作人查询审计记录的索引。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_audit_log', @level2type=N'INDEX', @level2name=N'IX_im_audit_log_Actor';

PRINT N'== 中文备注添加完成 ==';

-- 数据库级说明（失败不影响建表结果，仅提示）
BEGIN TRY
    EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'EOS 企业即时通讯消息库（独立于 Hiswitek 旧库）：方案 B 存储模型——服务器仅保存"送达暂存"副本并按保留期清理，权威聊天历史存放在各客户端本地；成员关系即访问权限边界，所有访问经 EOS.API 授权。';
    PRINT N'== 数据库级备注添加成功 ==';
END TRY
BEGIN CATCH
    PRINT N'数据库级备注已存在或添加失败（不影响表结构）：' + ERROR_MESSAGE();
END CATCH

PRINT N'== EOS.IM schema 初始化完成 ==';
