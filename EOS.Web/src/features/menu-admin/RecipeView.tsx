import { IconBook, IconCopy, IconPlus } from '@tabler/icons-react'

import { Button } from '../../components/ui/Button'
import type { EffectRecipe } from './effectRecipes'
import { labelWithCode } from './businessActionText'
import type { BusinessAction, LabelLookups } from './BusinessActionsPanel'

/**
 * 配方视图：配置面「行为动作」页签的**默认视图**。
 *
 * 它只回答"这个模块要发生什么"，不暴露实现键——键级的完整编辑能力在「专家（键级）」里，
 * 两态共用同一份草稿，切换不丢改动。卡片上的每个键都指回服务端下发的 `effectKeys`，
 * 禁止凭空发明；参数要点与边界说明同样来自服务端（`paramsHint` / `note`）。
 */
export function RecipeView({
  recipes,
  actions,
  labels,
  onCreateFromRecipe,
  onClone,
  onExpert,
}: {
  recipes: EffectRecipe[]
  /** 当前草稿里的效果链行：空态三选一靠它判断。 */
  actions: BusinessAction[]
  labels: LabelLookups
  onCreateFromRecipe: (recipe: EffectRecipe) => void
  onClone: () => void
  onExpert: () => void
}) {
  if (recipes.length === 0) {
    return (
      <div className="border rounded p-3 d-flex flex-column gap-2">
        <div className="text-secondary">
          服务端没有下发配方目录（可能是旧版本 API）。可以直接用「专家（键级）」逐字段配置。
        </div>
        <div>
          <Button size="sm" onClick={onExpert}>去专家（键级）</Button>
        </div>
      </div>
    )
  }

  return (
    <div className="d-flex flex-column gap-3">
      {actions.length === 0 ? (
        <div className="border rounded p-3 d-flex flex-column gap-2">
          <strong>这个模块还没有配置任何行为</strong>
          <div className="text-secondary small">三种起步方式：</div>
          <div className="d-flex flex-wrap align-items-center gap-2">
            <span className="badge text-bg-primary">① 从配方开始（下面选一个）</span>
            <Button size="sm" icon={<IconCopy size={16} />} onClick={onClone}>② 从其它模块克隆</Button>
            <Button
              size="sm"
              disabled
              icon={<IconBook size={16} />}
              title="模板库尚未建设：当前由配方目录承担模板职责，模板库落地后再开放这一项"
            >
              ③ 从模板库选（未建设）
            </Button>
          </div>
          <div className="text-secondary small">
            ② 克隆只带效果链与校验规则；源模块的**自定义按钮不随配置迁移**——按钮级授权是
            fail-closed 名单，必须在目标模块单独发放。
          </div>
        </div>
      ) : null}

      <div className="row g-2">
        {recipes.map((recipe) => (
          <div className="col-12 col-lg-6" key={recipe.key}>
            <div className="border rounded p-3 h-100 d-flex flex-column gap-2">
              <div className="d-flex align-items-start justify-content-between gap-2">
                <div>
                  <strong>{recipe.name}</strong>
                  <span className="text-secondary small ms-2">{recipe.key}</span>
                </div>
                <Button size="sm" icon={<IconPlus size={16} />} onClick={() => onCreateFromRecipe(recipe)}>
                  用它新增
                </Button>
              </div>
              <div className="small">{recipe.summary}</div>
              <div className="small text-secondary">
                落到：{recipe.effectKeys.map((key) => labelWithCode(labels.effectKeys, key)).join('、')}
                {recipe.formulaMode ? '（以公式行为主体）' : ''}
                {recipe.requiresRelation ? ' · 需要定位键' : ''}
              </div>
              <div className="small text-secondary">参数要点：{recipe.paramsHint}</div>
              <div className="small text-secondary">
                建议反向：{recipe.reversePreset}
                {recipe.note ? ` · ${recipe.note}` : ''}
              </div>
            </div>
          </div>
        ))}
      </div>

      <div className="text-secondary small">
        配方只做预填：点「用它新增」会按配方的默认事件/效果键/反向起草一行，参数与公式行在同一个编辑器里补齐；
        要逐字段改一切，切到「专家（键级）」——两侧共用同一份草稿，切换不丢改动。
      </div>
    </div>
  )
}
