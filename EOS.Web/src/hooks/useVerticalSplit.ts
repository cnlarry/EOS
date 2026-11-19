import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
  type RefObject,
} from 'react'

/** 上方区域（主表）最小高度：工具栏 + 表头 + 一行 */
const MIN_TOP = 140
/**
 * 下方区域（明细）最小高度：卡头 32 + 表头 29 + 一行 30。
 * 与主表的最小高度同一条判据（"一眼能看见一行"），不按"留白好看"取值——
 * 收得再高，用户想专心看主表时就只能拖到一半，跟没做这件事一样。
 */
const MIN_BOTTOM = 92
/** 上方区域最大占比：再大就把主表压成一条缝 */
const MAX_RATIO = 0.85
/** 方向键步长（px）；按住 Shift 加速 */
const KEY_STEP = 16
const KEY_STEP_FAST = 64
/** 量不出布局时（首帧、隐藏标签）的兜底比例，与 app.css 里明细卡的默认档位同量级 */
const FALLBACK_RATIO = 0.3

export interface VerticalSplitOptions {
  /** 上方区域最小高度（px），默认 140 */
  minTop?: number
  /** 下方区域最小高度（px），默认 92（卡头 + 表头 + 一行） */
  minBottom?: number
  /** 下方区域最大占比，默认 0.85 */
  maxRatio?: number
}

export interface VerticalSplit {
  /** 下方区域占分割区的比例；null = 未自定义，沿用样式里的默认分配 */
  ratio: number | null
  /**
   * 下方区域的内联样式：自定义过才给，未自定义时为 undefined（现有样式原样生效）。
   * 自定义时同时解除样式里的固定高度与封顶——只改 flex-basis 的话，
   * 明细卡原有的 `max-height` 会让"往上拖"到上限就拖不动。
   */
  bottomStyle: CSSProperties | undefined
  /** 拖动中：分割条高亮（全局光标由 `erp-row-resizing` 给） */
  dragging: boolean
  /** 当前比例（百分数，供 aria-valuenow）；还没量过布局时为 null */
  percent: number | null
  /** 分割条元素（同时是量布局的锚点：父元素 = 分割区，下一个兄弟 = 下方区域） */
  handleRef: RefObject<HTMLDivElement | null>
  onPointerDown: (event: ReactPointerEvent<HTMLDivElement>) => void
  onPointerMove: (event: ReactPointerEvent<HTMLDivElement>) => void
  onPointerUp: (event: ReactPointerEvent<HTMLDivElement>) => void
  /** 聚焦时补量一次当前比例（未自定义过时 aria 读数只能从布局得来） */
  onFocus: () => void
  onKeyDown: (event: ReactKeyboardEvent<HTMLDivElement>) => void
  /** 双击分割条：清掉本机记录，回到样式里的默认分配 */
  onReset: () => void
}

function readStoredRatio(storageKey: string): number | null {
  try {
    const raw = localStorage.getItem(storageKey)
    if (!raw) return null
    const value = Number(raw)
    return Number.isFinite(value) && value > 0 && value < 1 ? value : null
  } catch {
    return null
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max)
}

/**
 * 「上下两块」的高度分配（统一工作台的列表 + 明细）。
 *
 * 约定：分割条是上下两块之间的兄弟元素，**父元素就是分割区**（页面根），
 * 分割条的**下一个兄弟是下方区域**（明细卡）。这样量高度不需要调用方再传 ref。
 *
 * - 没拖过（storageKey 无记录）时 `ratio` 为 null，调用方不写内联样式——现有布局原样生效，
 *   鼠标一按下就按**当时实际的高度**起算，不会跳一下；
 * - 拖动过程只改状态，松手才落 localStorage（跟侧栏宽度同一手法）；
 * - 方向键与双击：`↑/↓` 每次 16px（Shift 64px），双击回到默认；
 * - 比例存的是"占分割区高度的百分比"，窗口大小/分辨率变化后两块按同一比例缩放。
 */
export function useVerticalSplit(storageKey: string, options: VerticalSplitOptions = {}): VerticalSplit {
  const minTop = options.minTop ?? MIN_TOP
  const minBottom = options.minBottom ?? MIN_BOTTOM
  const maxRatio = options.maxRatio ?? MAX_RATIO

  const handleRef = useRef<HTMLDivElement | null>(null)
  const [ratio, setRatio] = useState<number | null>(() => readStoredRatio(storageKey))
  const [dragging, setDragging] = useState(false)
  /** 布局实测的比例：未自定义时它就是当前比例（供 aria 与键盘起点） */
  const [measured, setMeasured] = useState<number | null>(null)
  /** 拖动起点：起始比例 + 当次拖拽量到的分割区几何 */
  const dragRef = useRef<{ startY: number; startRatio: number; box: { height: number; gap: number } } | null>(null)
  // 事件回调里读最新值：pointerup 与 pointermove 在同一个元素上先后触发，不能靠闭包里的旧状态
  const ratioRef = useRef<number | null>(ratio)

  /**
   * 分割区（父元素内容盒）的上下边界、高度与区块间距。
   * 间距要单独取：两块卡之间隔着 gap，按比例算主表高度时得把它减掉，
   * 否则"主表最小 140px"实际会落到 130px（差的正是那 10px 间隙）。
   */
  const metrics = useCallback(() => {
    const parent = handleRef.current?.parentElement
    if (!parent) return null
    const rect = parent.getBoundingClientRect()
    const style = window.getComputedStyle(parent)
    const top = rect.top + (Number.parseFloat(style.paddingTop) || 0)
    const bottom = rect.bottom - (Number.parseFloat(style.paddingBottom) || 0)
    const height = bottom - top
    const gap = Number.parseFloat(style.rowGap) || 0
    return height > 0 ? { top, bottom, height, gap } : null
  }, [])

  /** 下方区域当前实际占的比例（没自定义过时靠它给出"现在的比例"） */
  const measuredRatio = useCallback(() => {
    const box = metrics()
    const bottomElement = handleRef.current?.nextElementSibling as HTMLElement | null
    if (!box || !bottomElement) return null
    const height = bottomElement.getBoundingClientRect().height
    return height > 0 ? height / box.height : null
  }, [metrics])

  /** 夹在「主表最小高度 / 明细最小高度 / 上限」之间（上方区域的高度 = 容器高 - 间距 - 明细高） */
  const clampRatio = useCallback((value: number, box: { height: number; gap: number }) => {
    const min = minBottom / box.height
    const max = Math.min(maxRatio, 1 - (minTop + box.gap) / box.height)
    // 容器矮到两条下限互相打架时，保住主表的下限，多出来的差额让明细吃完（明细有内部滚动）
    return min > max ? max : clamp(value, min, max)
  }, [minBottom, minTop, maxRatio])

  const persist = useCallback((value: number | null) => {
    try {
      if (value === null) localStorage.removeItem(storageKey)
      else localStorage.setItem(storageKey, String(value))
    } catch {
      // 存不了只影响下次打开，不影响本次拖动
    }
  }, [storageKey])

  const preview = useCallback((next: number) => {
    ratioRef.current = next
    setRatio(next)
    setMeasured(next)
  }, [])

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return
    const box = metrics()
    if (!box) return
    const start = clampRatio(ratioRef.current ?? measuredRatio() ?? FALLBACK_RATIO, box)
    dragRef.current = { startY: event.clientY, startRatio: start, box }
    event.preventDefault()
    setDragging(true)
    document.body.classList.add('erp-row-resizing')
    try {
      event.currentTarget.setPointerCapture?.(event.pointerId)
    } catch {
      // jsdom/无活动指针时忽略，拖拽仍可继续
    }
  }

  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current
    if (!drag) return
    // 向上拖（clientY 变小）=> 明细变高、主表变矮
    preview(clampRatio(drag.startRatio + (drag.startY - event.clientY) / drag.box.height, drag.box))
  }

  const onPointerUp = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (!dragRef.current) return
    dragRef.current = null
    setDragging(false)
    document.body.classList.remove('erp-row-resizing')
    if (ratioRef.current !== null) persist(ratioRef.current)
    try {
      event.currentTarget.releasePointerCapture?.(event.pointerId)
    } catch {
      // 指针捕获已经丢失时释放会抛，忽略即可
    }
  }

  const onKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return
    const box = metrics()
    if (!box) return
    const current = ratioRef.current ?? measuredRatio() ?? FALLBACK_RATIO
    const step = (event.shiftKey ? KEY_STEP_FAST : KEY_STEP) / box.height
    event.preventDefault()
    const next = clampRatio(current + (event.key === 'ArrowUp' ? step : -step), box)
    preview(next)
    persist(next)
  }

  const onReset = useCallback(() => {
    ratioRef.current = null
    setRatio(null)
    persist(null)
    // 复位后布局要等这一帧渲染完才回到默认分配，此刻量到的还是旧高度——先清空，
    // 真正的读数留给下一次聚焦/拖动（宁可暂时不报，也别报一个已经不存在的高度）
    setMeasured(null)
  }, [persist])

  /** 聚焦时补量一次：没自定义过比例时，aria 读数只能从当前布局得来 */
  const onFocus = useCallback(() => {
    setMeasured(measuredRatio())
  }, [measuredRatio])

  // 拖动中卸载（切走标签/换模块）：全局光标与禁选状态必须收回，否则整页都点不了字
  useEffect(() => () => {
    document.body.classList.remove('erp-row-resizing')
  }, [])

  const shown = ratio ?? measured
  return {
    ratio,
    bottomStyle: ratio === null
      ? undefined
      : {
        flexBasis: `${(ratio * 100).toFixed(2)}%`,
        // 自定义时同时接管三条与高度有关的既有约束：样式里的固定封顶（会挡住"往上拖"）、
        // 明细卡默认的 150px 下限（会挡住"往下拖"，看起来就是"明细缩不下去"）
        maxHeight: 'none',
        height: 'auto',
        minHeight: `${minBottom}px`,
      },
    dragging,
    percent: shown === null ? null : Math.round(clamp(shown, 0, 1) * 100),
    handleRef,
    onPointerDown,
    onPointerMove,
    onPointerUp,
    onFocus,
    onKeyDown,
    onReset,
  }
}
