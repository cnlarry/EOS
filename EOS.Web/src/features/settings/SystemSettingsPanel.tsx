import type { CSSProperties } from 'react'
import { useMemo } from 'react'
import { IconDeviceFloppy } from '@tabler/icons-react'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSelectionList } from '../../components/common/ErpSelectionList'
import { useSectionScrollSpy } from '../../components/common/useSectionScrollSpy'
import { Button } from '../../components/ui/Button'
import {
  formatValueForInput,
  isBitValueType,
  isNumericValueType,
  isTruthy,
  parseOptions,
  type SettingsForm,
  type SystemParameterGroup,
  type SystemParameterItem,
} from './settingsTypes'

/**
 * 每行参数个数。
 *
 * **两列**而不是三列：左栏占掉 256px 之后，三列下每格只剩约 300px，而设置项的标签是
 * "说明 + 键 + 徽标"三段、说明常常十几个汉字——三列时几乎每项都要换成三行，输入框还被挤到
 * 132px，读起来是"标签墙"。两列把标签还给一行、控件还给可读宽度，一屏仍能看十几项。
 */
const COLUMNS = 2

interface SystemSettingsPanelProps {
  groups: SystemParameterGroup[]
  form: SettingsForm
  onChange: (key: string, value: string) => void
  onSave: () => void
  saving: boolean
}

/**
 * 系统参数的两栏表单体（110111 系统参数设置 / 180213 考勤数据设置 / 180662 薪资考勤设置，
 * 路由段决定取哪一批参数）。
 *
 * <p>
 * **左导航 + 右配置**，与助手设置（3105）同一套骨架：左栏按参数分组列（分组数量随归属模块而定，
 * 系统参数 9 组、考勤 4 组），右栏把**所有分组一次铺开**并按序滚动；两栏各自内部滚动，
 * 页面级不出滚动条。原先每个分组一个页签，一次只看得见一组——配置者核对"这几个相关开关
 * 到底怎么配的"要来回切页签，切完还回不到原处。
 * </p>
 *
 * <p>
 * 滚动联动（左栏高亮到哪一组、顶部「当前」写到哪一项）由 `useSectionScrollSpy` 提供，
 * 与 3105 共用一份实现：判定线与容差是调出来的经验值，抄两份只会有一处被修好。
 * </p>
 *
 * <p>
 * 控件按参数类型渲染（有枚举选项→下拉、开关→复选框、整数/数值→数字输入、其余→文本框）；
 * 生效范围为"需重启"的参数在标签旁标注，避免误以为改完立刻生效。
 * </p>
 */
export function SystemSettingsPanel({
  groups,
  form,
  onChange,
  onSave,
  saving,
}: SystemSettingsPanelProps) {
  const total = useMemo(
    () => groups.reduce((sum, group) => sum + group.parameters.length, 0),
    [groups])

  /**
   * 每个分组的两类标记：**待保存**（界面值与服务端有效值不同）与**已是非默认值**
   * （服务端就说这条没在用默认值）。前者才是"活没干完"，后者只是"这条被谁调过"。
   */
  const marks = useMemo(() => {
    const result = new Map<string, { pending: number; changed: number }>()
    for (const group of groups) {
      let pending = 0
      let changed = 0
      for (const parameter of group.parameters) {
        if (!parameter.usesDefault) changed += 1
        if ((form[parameter.key] ?? '') !== formatValueForInput(parameter, parameter.effectiveValue)) {
          pending += 1
        }
      }
      result.set(group.groupCode, { pending, changed })
    }
    return result
  }, [groups, form])

  const pendingTotal = useMemo(
    () => Array.from(marks.values()).reduce((sum, mark) => sum + mark.pending, 0),
    [marks])

  const sections = useMemo(
    () => groups.map(group => ({ code: group.groupCode, label: group.groupLabel })),
    [groups])

  const { paneRef, activeGroupCode, activeItemLabel, onScroll, jumpTo } = useSectionScrollSpy(sections)
  const activeLabel = groups.find(group => group.groupCode === activeGroupCode)?.groupLabel ?? '—'

  return (
    <ErpListCard
      ariaLabel="系统参数"
      search={<div className="erp-settings-split-title">
        <span className="text-secondary small">
          共 {total} 项参数，分 {groups.length} 组；生效范围以各参数自己的标注为准
          {pendingTotal > 0 ? `（${pendingTotal} 项待保存）` : ''}
        </span>
      </div>}
      actions={
        <Button size="sm" variant="primary" className="erp-command-btn"
          icon={<IconDeviceFloppy size={16} />} accessKey="o" loading={saving}
          title="保存全部参数" onClick={onSave}>确定(O)</Button>
      }
    >
      <div className="card-body erp-settings-split">
        {/* 左：分组导航；选中态由 ErpSelectionList 提供（与字段设置页的左栏同一套外观） */}
        <ErpSelectionList
          ariaLabel="参数分组"
          className="erp-settings-rail"
          items={groups.map(group => {
            const mark = marks.get(group.groupCode)
            const pendingCount = mark?.pending ?? 0
            const changedCount = mark?.changed ?? 0
            return {
              id: group.groupCode,
              label: group.groupLabel,
              tooltip: pendingCount > 0
                ? `${pendingCount} 项改动还没保存`
                : (changedCount > 0 ? `${changedCount} 项已不是默认值` : undefined),
              meta: (
                <>
                  {pendingCount > 0
                    ? <span className="erp-settings-rail-dot" />
                    : (changedCount > 0 ? <span className="erp-settings-rail-dot is-saved" /> : null)}
                  <span>{group.parameters.length}</span>
                </>
              ),
            }
          })}
          activeId={activeGroupCode}
          onSelect={jumpTo}
        />

        {/* 右：全部参数，按分组一次铺开 */}
        <div className="erp-settings-split-pane" ref={paneRef} onScroll={onScroll}>
          <div className="erp-settings-split-now">
            <span className="text-secondary">当前</span>
            <span className="fw-semibold">{activeLabel}</span>
            {activeItemLabel && (
              <>
                <span className="text-secondary">·</span>
                <span>{activeItemLabel}</span>
              </>
            )}
          </div>

          {groups.map(group => {
            const rows: SystemParameterItem[][] = []
            for (let index = 0; index < group.parameters.length; index += COLUMNS) {
              rows.push(group.parameters.slice(index, index + COLUMNS))
            }
            return (
              <section key={group.groupCode} className="erp-settings-split-section"
                data-settings-anchor="group" data-group-code={group.groupCode}
                data-group-label={group.groupLabel}>
                <div className="erp-settings-split-section-head">
                  <h3 className="h5 mb-0">{group.groupLabel}</h3>
                  <span className="text-secondary small font-monospace">{group.groupCode}</span>
                  <span className="text-secondary small">{group.parameters.length} 项</span>
                </div>
                <div className="erp-settings-form">
                  {rows.map((row, rowIndex) => (
                    <div
                      // 行按位置呈现，无稳定业务键；序号即其身份
                      key={`row-${rowIndex}`}
                      className="erp-form-row"
                      style={{ '--erp-form-cols': COLUMNS } as CSSProperties}
                    >
                      {row.map(parameter => (
                        <ParameterField
                          key={parameter.key}
                          parameter={parameter}
                          groupCode={group.groupCode}
                          value={form[parameter.key] ?? ''}
                          onChange={onChange}
                        />
                      ))}
                      {Array.from({ length: COLUMNS - row.length }).map((_, fillerIndex) => (
                        <div key={`empty-${fillerIndex}`} className="erp-form-field" />
                      ))}
                    </div>
                  ))}
                </div>
              </section>
            )
          })}
        </div>
      </div>
    </ErpListCard>
  )
}

interface ParameterFieldProps {
  parameter: SystemParameterItem
  groupCode: string
  value: string
  onChange: (key: string, value: string) => void
}

/** 一项参数：标签（说明 + 键 + 生效范围徽标）+ 控件。字段容器同时是滚动联动的**单项锚点**。 */
function ParameterField({ parameter, groupCode, value, onChange }: ParameterFieldProps) {
  return (
    <div className="erp-form-field"
      data-settings-anchor="item" data-group-code={groupCode}
      data-item-label={parameter.description}>
      <label className={`erp-form-label${parameter.isReferenced ? '' : ' text-secondary'}`}>
        {parameter.description}
        <span className="text-secondary ms-1" style={{ fontSize: 11 }}>{parameter.key}</span>
        {parameter.effectScope === 'restart' && <span className="badge bg-warning-lt ms-1">需重启</span>}
        {!parameter.isReferenced && (
          <span
            className="badge bg-secondary-lt ms-1"
            title="当前没有任何读取方引用这个参数（已发布配置与代码都没有），修改它暂时不产生效果"
          >
            当前无引用方
          </span>
        )}
      </label>
      <div className="erp-form-control">
        <ParameterControl parameter={parameter} value={value} onChange={onChange} />
      </div>
    </div>
  )
}

interface ParameterControlProps {
  parameter: SystemParameterItem
  value: string
  onChange: (key: string, value: string) => void
}

function ParameterControl({ parameter, value, onChange }: ParameterControlProps) {
  const label = parameter.description
  const options = parseOptions(parameter.options)
  if (options.length > 0) {
    return (
      <select
        aria-label={label}
        className="form-select"
        value={value}
        onChange={(event) => onChange(parameter.key, event.target.value)}
      >
        <option value="">（默认）</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
    )
  }
  if (isBitValueType(parameter.valueType)) {
    return (
      <input
        aria-label={label}
        type="checkbox"
        className="form-check-input"
        checked={isTruthy(value)}
        onChange={(event) => onChange(parameter.key, event.target.checked ? 'true' : 'false')}
      />
    )
  }
  if (isNumericValueType(parameter.valueType)) {
    return (
      <input
        aria-label={label}
        type="number"
        step={parameter.valueType.toLowerCase() === 'int' ? '1' : 'any'}
        className="form-control"
        value={value}
        onChange={(event) => onChange(parameter.key, event.target.value)}
      />
    )
  }
  return (
    <input
      aria-label={label}
      className="form-control"
      value={value}
      onChange={(event) => onChange(parameter.key, event.target.value)}
    />
  )
}
