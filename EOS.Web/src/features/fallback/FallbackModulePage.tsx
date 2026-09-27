import { useParams } from 'react-router-dom'

export function FallbackModulePage() {
  const { moduleId } = useParams()
  return <section className="card"><div className="card-body py-5 text-center"><h2 className="card-title">模块正在迁移</h2><p className="text-secondary mb-0">已从真实权限菜单载入模块 {moduleId}，业务页面将在后续重构中接入。</p></div></section>
}
