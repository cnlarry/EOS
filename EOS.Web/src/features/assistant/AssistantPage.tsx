import { useEffect } from 'react'
import { AssistantPanel } from './AssistantPanel'
import { useAssistant } from './assistantContext'
import { useCloseTab } from '../../components/layout/WorkspaceNavContext'

/**
 * 助手的**全屏形态**：一个普通的工作区标签页（路由 `/assistant`），
 * 像统一工作台那样可以并存多个标签、跟着工作区一起持久化。
 *
 * <para>
 * 它与半屏抽屉**共用同一份会话状态**（都在 <see cref="useAssistant"/> 里），
 * 所以"半屏 ⇄ 全屏"只是换壳：对话内容、以及正在流式生成的回答都不受影响。
 * </para>
 */
export function AssistantPage() {
  const { setOpen } = useAssistant()
  const closeTab = useCloseTab()

  /** 回到半屏：展开抽屉并关掉本标签（同一个对话，只是换个落脚处）。 */
  const collapse = () => {
    setOpen(true)
    closeTab()
  }

  // 全屏下 Ctrl+/ 的语义仍是"开合抽屉"：展开抽屉即回到半屏。Provider 里的同名快捷键
  // 在全屏页会主动让位（它按路径判断），避免两边同时响应把开关翻回去。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey && event.key === '/') {
        event.preventDefault()
        collapse()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  return (
    <div className="erp-assistant-page">
      <AssistantPanel variant="page" onCollapse={collapse} />
    </div>
  )
}
