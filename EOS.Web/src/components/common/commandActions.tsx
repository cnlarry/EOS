import type { ReactNode } from 'react'
import {
  IconAdjustmentsHorizontal,
  IconArrowAutofitWidth,
  IconArrowLeft,
  IconCheck,
  IconColumns,
  IconCopy,
  IconEdit,
  IconEye,
  IconFileExport,
  IconFileUpload,
  IconLock,
  IconLockOpen,
  IconPaperclip,
  IconPlus,
  IconPrinter,
  IconRefresh,
  IconRotateClockwise,
  IconSearch,
  IconTrash,
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
  approve: { icon: <IconCheck size={16} />, title: '批核' },
  deapprove: { icon: <IconRotateClockwise size={16} />, title: '解批' },
  endcase: { icon: <IconLock size={16} />, title: '结案' },
  unendcase: { icon: <IconLockOpen size={16} />, title: '未结案' },
  print: { icon: <IconPrinter size={16} />, title: '打印' },
  export: { icon: <IconFileExport size={16} />, title: '导出' },
  search: { icon: <IconSearch size={16} />, title: '通用查询' },
  attach: { icon: <IconPaperclip size={16} />, title: '附件' },
  refresh: { icon: <IconRefresh size={16} />, title: '刷新' },
  back: { icon: <IconArrowLeft size={16} />, title: '返回' },
  query: { icon: <IconAdjustmentsHorizontal size={16} />, title: '高级查询' },
  columns: { icon: <IconColumns size={16} />, title: '选择列' },
  fit: { icon: <IconArrowAutofitWidth size={16} />, title: '自适应列宽' },
  upload: { icon: <IconFileUpload size={16} />, title: '上传' },
}