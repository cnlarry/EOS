import type { ReactNode } from 'react'
import {
  IconAdjustmentsHorizontal,
  IconArrowAutofitWidth,
  IconArrowDown,
  IconArrowLeft,
  IconArrowUp,
  IconCheck,
  IconColumns,
  IconCopy,
  IconDeviceFloppy,
  IconEdit,
  IconEye,
  IconFileExport,
  IconFileUpload,
  IconHelpCircle,
  IconHistory,
  IconLock,
  IconLockOpen,
  IconPaperclip,
  IconPlus,
  IconPrinter,
  IconRefresh,
  IconReportAnalytics,
  IconRotateClockwise,
  IconSearch,
  IconTrash,
  IconX,
} from '@tabler/icons-react'

/**
 * 标准动作 → 图标 + 悬停标题 的中央注册表（对应服务端 FORM_BUTTONS 白名单动作）。
 * 统一动作表意：同一动作在任何页面（工作台/统一表单/列表）显示同一图标与标题，
 * 避免不同实现带来的歧义。新增动作必须在此登记 + 服务端 ParseFormButtons 白名单同步。
 */
export const COMMAND_ACTIONS: Record<string, { icon: ReactNode; title: string }> = {
  new: { icon: <IconPlus size={16} />, title: '新增' },
  edit: { icon: <IconEdit size={16} />, title: '编辑' },
  view: { icon: <IconEye size={16} />, title: '查看' },
  copy: { icon: <IconCopy size={16} />, title: '复制' },
  delete: { icon: <IconTrash size={16} />, title: '删除' },
  save: { icon: <IconDeviceFloppy size={16} />, title: '保存' },
  cancel: { icon: <IconX size={16} />, title: '取消' },
  approve: { icon: <IconCheck size={16} />, title: '批核' },
  deapprove: { icon: <IconRotateClockwise size={16} />, title: '解批' },
  withdraw: { icon: <IconRotateClockwise size={16} />, title: '撤回' },
  history: { icon: <IconHistory size={16} />, title: '审批历史' },
  endcase: { icon: <IconLock size={16} />, title: '结案' },
  unendcase: { icon: <IconLockOpen size={16} />, title: '取消结案' },
  print: { icon: <IconPrinter size={16} />, title: '打印' },
  // 报表：**模块级**动作（打开本模块的报表清单），不是单据级动作，
  // 因此不进 FORM_BUTTONS 白名单（不进=管理员配置不了它，它按"有没有可见报表"自己决定出不出现）。
  report: { icon: <IconReportAnalytics size={16} />, title: '报表' },
  export: { icon: <IconFileExport size={16} />, title: '导出' },
  search: { icon: <IconSearch size={16} />, title: '通用查询' },
  attach: { icon: <IconPaperclip size={16} />, title: '附件' },
  refresh: { icon: <IconRefresh size={16} />, title: '刷新' },
  back: { icon: <IconArrowLeft size={16} />, title: '返回' },
  query: { icon: <IconAdjustmentsHorizontal size={16} />, title: '高级查询' },
  columns: { icon: <IconColumns size={16} />, title: '选择列' },
  fit: { icon: <IconArrowAutofitWidth size={16} />, title: '自适应列宽' },
  upload: { icon: <IconFileUpload size={16} />, title: '上传' },
  // Browse-mode navigation (previous/next by current list order) and help (HELP_URL)
  prior: { icon: <IconArrowUp size={16} />, title: '上一条' },
  next: { icon: <IconArrowDown size={16} />, title: '下一条' },
  help: { icon: <IconHelpCircle size={16} />, title: '帮助' },
}