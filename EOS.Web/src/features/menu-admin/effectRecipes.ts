/**
 * 效果配方（配置面的默认视图）与事件可用性的**纯逻辑**。
 *
 * 配方不是新的事实源：它由服务端 `/admin/module-business-config/schemas` 的 `recipes` 下发，
 * 只做三件事——把实现键收敛成业务概念、预填一条动作行、说明参数要点与边界。
 * 界面侧唯一的职责是"**照抄**"：本文件的产出必须与逐字段手工配置一致（等价性），
 * 不得在这里补任何服务端没说过的东西。
 */

/** 一个配方（与后端 EffectRecipeDto 同形）。 */
export interface EffectRecipe {
  key: string
  name: string
  summary: string
  eventCodes: string[]
  effectKeys: string[]
  formulaMode: boolean
  requiresRelation: boolean
  reversePreset: string
  paramsHint: string
  note?: string | null
}

/** 配方预填出来的动作字段（其余字段由新建动作的默认值负责）。 */
export interface RecipeActionShape {
  seq: number
  eventCode: string
  effectKey: string
  reverse: string | null
}

/** 反向预设的落库形态：与手工配置写的 JSON 逐字相同。 */
export function reversePresetJson(recipe: EffectRecipe): string {
  return JSON.stringify({ kind: recipe.reversePreset })
}

/**
 * 配方路径 → 动作字段。事件取配方建议的第一个事件，效果键取第一个（配方逐个键都成立），
 * 反向取预设。**不填参数、不造公式行**：这两件事配方只说要点（`paramsHint`），由人补。
 */
export function recipeToActionShape(recipe: EffectRecipe, seq: number): RecipeActionShape {
  return {
    seq,
    eventCode: recipe.eventCodes[0] ?? '',
    effectKey: recipe.effectKeys[0] ?? '',
    reverse: reversePresetJson(recipe),
  }
}

/**
 * 配方覆盖的实现键（判断某个键有没有配方可走）。
 * 集合里的键**一律小写**（效果键本身即小写闭集）：调用方用小写的键去查。
 */
export function recipeCoveredKeys(recipes: readonly EffectRecipe[] | null | undefined): Set<string> {
  const keys = new Set<string>()
  for (const recipe of recipes ?? []) {
    for (const key of recipe.effectKeys ?? []) keys.add(key.trim().toLowerCase())
  }
  return keys
}

/** 接不到效果链的事件：可配置但配了不会跑。 */
export const INERT_EVENT_NOTE = '该事件当前不会触发效果链'

/** 库内 0 行：可以不可选，并说明为什么。 */
export const UNUSED_EVENT_NOTE = '暂未启用（库内尚无使用）'

export interface EventAvailability {
  /** 是否可选。库内 0 行**且**接不到效果链 ⇒ 不可选。 */
  selectable: boolean
  /** 必须让配置者看见的说明（没有则 null）。 */
  note: string | null
}

/**
 * 事件可用性（入门路径口径）。
 *
 * 两个事实缺一不可：事件本身接不接得到效果链（`inertEvents`，代码侧事实）与库内有没有行
 * （`eventUsage`，库内事实）。只看前者会把"已有配置、只是暂时跑不到"的事件也禁掉
 * （配置者再也改不动那条既有配置）；只看后者则会把"配了不跑"的坑继续留着。
 *
 * `eventUsage` 缺失（旧版服务端）时**不做不可选判定**——没有事实就不下结论，
 * 只保留"接不到效果链"的标注。
 */
export function eventAvailability(
  eventCode: string,
  inertEvents: readonly string[] | null | undefined,
  eventUsage: Record<string, number> | null | undefined,
): EventAvailability {
  const code = (eventCode ?? '').trim()
  const inert = (inertEvents ?? []).some((item) => item.toLowerCase() === code.toLowerCase())
  if (!inert) return { selectable: true, note: null }
  if (!eventUsage) return { selectable: true, note: INERT_EVENT_NOTE }
  const used = (eventUsage[code] ?? 0) > 0
  return used ? { selectable: true, note: INERT_EVENT_NOTE } : { selectable: false, note: UNUSED_EVENT_NOTE }
}
