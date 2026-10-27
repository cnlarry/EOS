import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { describeApiError } from '../../lib/errors'
import { getMyUsage } from './api'
import { usageModelColumns, usageTrendColumns } from './usageColumns'

/** 统计区间。上限 180 天由服务端夹紧，界面只给这三档（再多也读不出有用信息）。 */
const WINDOWS = [7, 30, 90]

/**
 * 我的用量：当前用户**自己**的助手调用量——近 N 天按模型与按天，加上当日总览与每日上限。
 *
 * <p>
 * 它是普通工作区标签页（路由 `/assistant/usage`），与助手下拉、「我的会话」同构：
 * 登录即可看、可深链、可并存。**没有菜单模块**，因为这里的数据只有一个归属——本人：
 * 再造一个"个人用量"菜单，就要回答"菜单权限给谁看别人的用量"，而那个问题不该存在。
 * </p>
 *
 * <p>
 * 与「工作助手管理 → 模型与用量」（3102，跨用户视角）**同一套接口形状与同一份列定义**，
 * 差别只在取数范围：那边是全体，这边服务端按当前用户强制过滤。
 * </p>
 */
export function MyUsagePage() {
  const [days, setDays] = useState(30)
  const usage = useQuery({
    queryKey: ['assistant-my-usage', days],
    placeholderData: keepPreviousData,
    queryFn: () => getMyUsage(days),
  })

  const today = usage.data?.today
  const caps = usage.data?.caps
  const models = usage.data?.models ?? []
  const trend = usage.data?.trend ?? []
  const errorMessage = describeApiError(usage.error, '加载用量失败，请稍后重试。')

  return (
    <div className="erp-base-page erp-page-fill erp-my-usage-page">
      <ErpListCard
        ariaLabel="我的助手用量"
        actions={<>
          <select className="form-select form-select-sm" style={{ width: 120 }} value={days}
            aria-label="统计区间" onChange={(event) => setDays(Number(event.target.value))}>
            {WINDOWS.map(value => <option key={value} value={value}>近 {value} 天</option>)}
          </select>
          <ErpCommandBar iconOnly ariaLabel="用量工具"
            items={[{ action: 'refresh', onClick: () => void usage.refetch() }]} />
        </>}
        header={<div className="card-header erp-card-toolbar">
          <h2 className="card-title mb-0">我的用量</h2>
          <span className="text-secondary small">
            今日 {today?.requests ?? 0} 次回复、
            {((today?.promptTokens ?? 0) + (today?.completionTokens ?? 0)).toLocaleString('zh-CN')} tokens、
            约 ¥{(today?.estimatedCostYuan ?? 0).toFixed(4)}
            {caps ? `（每个账号每天上限 ¥${caps.userDailyCapYuan}）` : ''}
          </span>
          <span className="ms-auto text-secondary small">按 UTC 自然日统计；成本按各模型单价估算</span>
        </div>}
      >
        <div className="erp-master-table-region">
          {usage.isPending ? <LoadingState label="正在汇总用量…" /> : usage.isError ? (
            <ErrorState message={errorMessage} onRetry={() => void usage.refetch()} />
          ) : models.length === 0 ? (
            <EmptyState title="这段时间还没有提问记录" description="向工作助手提问之后，这里会出现按模型的用量。" />
          ) : (
            <ErpListGrid
              plain
              listId="assistant-my-usage-models"
              columns={usageModelColumns}
              rows={models}
              rowKey={(row) => row.modelName}
              fetching={usage.isFetching}
              sorting={{ mode: 'client' }}
            />
          )}
        </div>
      </ErpListCard>

      <section className="card erp-detail-card">
        <div className="card-header erp-card-toolbar">
          <span className="small fw-semibold">按天（近 {usage.data?.days ?? days} 天）</span>
        </div>
        {usage.isPending ? <LoadingState label="正在汇总用量…" /> : trend.length === 0 ? (
          <EmptyState title="这段时间还没有调用记录" description="有提问之后这里会出现每日趋势。" />
        ) : (
          // 倒序：最近的一天在上面（接口按天升序给，读的人第一眼想看今天）
          <ErpListGrid
            plain
            listId="assistant-my-usage-trend"
            columns={usageTrendColumns}
            rows={[...trend].reverse()}
            rowKey={(row) => row.day}
            fetching={usage.isFetching}
            sorting={{ mode: 'client' }}
          />
        )}
      </section>
    </div>
  )
}
