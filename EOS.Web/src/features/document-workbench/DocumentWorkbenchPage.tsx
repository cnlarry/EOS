import { IconFileExport, IconZoomScan } from '@tabler/icons-react'
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '../../lib/tanstackTable'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useOpenTab } from '../../components/layout/WorkspaceNavContext'
import { useParams, useSearchParams } from 'react-router-dom'
import { EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { checkboxSelectColumn } from '../../components/common/erpCheckboxSelectColumn'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpQueryBuilder } from '../../components/common/ErpQueryBuilder'
import { emptyQueryCondition, type QueryCondition } from '../../components/common/queryCondition'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import type { RangeSummary } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { useColumnWidthSaver } from '../../hooks/useColumnWidthSaver'
import { apiClient } from '../../services/api'
import { moduleReadPermission } from '../auth/modulePermissions'
import { ApiError } from '../../types/api'

/** 选区摘要的数值格式：最多 4 位小数（数量列常见 4 位），千分位跟随中文习惯 */
const summaryNumber=new Intl.NumberFormat('zh-CN',{maximumFractionDigits:4})
import { useAuth } from '../auth/authContext'
import { alignClass, formatFieldValue } from './fieldFormat'
import { FieldBrowseLink } from './FieldBrowseLink'
import { useOpenWorkbenchForm } from './formOpenMode'
import { workbenchNew, workbenchView } from './workbenchPath'
import { readListState, writeListState } from './listStateUrl'
import { reportListFilters, reportSelection } from '../assistant/situationSource'

interface Field { key:string; label:string; dataType:string; width:number; align:string|null; isPrimaryKey:boolean; isQueryable:boolean; headerAlign:string; format:string|null; browseUrl:string|null; browseModuleId:number|null; browseKeyFields:string[]|null; isVirtual?:boolean }
interface Definition { moduleId:number; title:string; masterTable:string; detailTable?:string; masterFields:Field[]; detailFields:Field[]; hasAdd:boolean; hasEdit:boolean; masterPkOrder:string[]; hasWorkflow:boolean; ifCopy:boolean; searchMaster:boolean; searchDetail:boolean; buttons:{action:string}[]|null; canDelete?:boolean; /** 统一表单打开方式（本页签/新页签/弹窗）+ 弹窗尺寸，见 formOpenMode.ts */ formOpenMode?:string|null; formDialogWidth?:number|null; formDialogHeight?:number|null }
interface DataResponse { rows:Record<string,unknown>[]; total:number; page:number; pageSize:number }
interface NavigationGroupDef { groupId:number; description:string; available:boolean }
interface ColumnSetting { key:string; label:string; isVisible:boolean; order:number }
interface ColumnSettings { master:ColumnSetting[]; detail:ColumnSetting[] }
const uniqueFields=(fields:Field[])=>fields.filter((field,index,all)=>all.findIndex(item=>item.key.toLowerCase()===field.key.toLowerCase())===index)
/**
 * 拖拽列头后的落库顺序：新顺序在前，回调里没有的列按原顺序垫后。
 * 保存列配置是替换语义（没传的列会被删掉），所以两个入口都按"整份列集"提交，不能只交被拖过的那部分。
 */
const applyColumnOrder=(current:string[],next:string[])=>{
  // 只把 current 里已有的键按 next 重排：next 可能来自上一次乐观草稿，里面还留着
  // 后来被隐藏的列；直接拼进"可见列"会让选择列把隐藏字段也当成已选。
  const allowed=new Set(current.map(key=>key.toLowerCase()))
  const moved=next.filter(id=>id!=='select'&&allowed.has(id.toLowerCase()))
  const seen=new Set(moved.map(id=>id.toLowerCase()))
  return [...moved,...current.filter(key=>!seen.has(key.toLowerCase()))]
}
/**
 * 把拖出来的列序套到字段数组上（乐观渲染用）。
 * 顺序表里没有的字段（刚勾选的新列）按原顺序垫后；稳定排序，避免同 rank 的字段每次渲染互换。
 */
const applyFieldOrder=(fields:Field[],order:string[]|null)=>{
  if(!order||order.length===0)return fields
  const rank=new Map(order.map((key,index)=>[key.toLowerCase(),index]))
  return [...fields].sort((a,b)=>(rank.get(a.key.toLowerCase())??Number.MAX_SAFE_INTEGER)-(rank.get(b.key.toLowerCase())??Number.MAX_SAFE_INTEGER))
}
/** 服务端定义是否已经回到草稿顺序：是的话乐观副本可以撤掉，别永远盖住服务端 */
const sameColumnOrder=(order:string[]|null,keys:string[])=>{
  if(!order||order.length!==keys.length)return false
  return order.every((key,index)=>key.toLowerCase()===keys[index]!.toLowerCase())
}
interface ColumnOrderSnapshot { master:string[]; detail:string[] }
interface ColumnOrderSaveOptions {
  /** 拖前整份顺序：保存成功后用它提供「撤销」 */
  undo?:ColumnOrderSnapshot
  /** 成功后的提示文案（撤销自身用它，不再挂第二个「撤销」） */
  successMessage?:string
}
/** 列顺序的连续状态共用一条轻提示：已调整 → 未保存 → 已撤销，不叠屏 */
const COLUMN_ORDER_TOAST_KEY='workbench-column-order'
const COLUMN_ORDER_ADJUSTED='列顺序已调整'
const COLUMN_ORDER_UNDONE='已撤销列顺序调整'
const sortQuery=(sort:SortingState)=>({sortFields:sort.length?sort.map(item=>item.id).join(','):undefined,sortDirections:sort.length?sort.map(item=>item.desc?'desc':'asc').join(','):undefined})
/**
 * 统一工作台列表页。
 *
 * `underlay`：作为"弹窗打开方式的背景底图"渲染（统一表单弹窗浮在它上面）。
 * 底图只负责看着像工作台，**不参与任何写路径**：不把列表状态写回地址（否则会改掉
 * 表单页的地址）、不上报助手处境（用户此刻在表单里，不是在看列表），并被 `inert` 冻结交互。
 */
export function DocumentWorkbenchPage({ underlay = false }: { underlay?: boolean } = {}) {
  const { hasPermission } = useAuth()
  const toast=useToast()
  // 跨页面/跨模块一律走标签入口：原地 navigate 会把当前现场顶掉（ADR-040）
  const openTab=useOpenTab()
  /** 按模块声明的打开方式打开统一表单（新页签 / 弹窗 / 本页签） */
  const openForm=useOpenWorkbenchForm()
  const { moduleId='' }=useParams()
  const queryClient=useQueryClient()
  const [searchParams,setSearchParams]=useSearchParams()
  const [initialState]=useState(()=>readListState(searchParams))
  const masterFitRef=useRef<(() => Record<string, number>)|null>(null)
  const detailFitRef=useRef<(() => Record<string, number>)|null>(null)
  const [fitting,setFitting]=useState(false)
  /** 选区数值摘要（Excel 状态栏的等价物）：选一块单元格后显示合计/平均 */
  const [rangeSummary,setRangeSummary]=useState<RangeSummary|null>(null)
  const [selected,setSelected]=useState<Record<string,Record<string,unknown>>>({})
  const [activeKey,setActiveKey]=useState<string|null>(null)
  const [sort,setSort]=useState<SortingState>(initialState.sort)
  const [detailSort,setDetailSort]=useState<{field:string;direction:'asc'|'desc'}|null>(null)
  const [queryOpen,setQueryOpen]=useState(false)
  const [columnsOpen,setColumnsOpen]=useState(false)
  /**
   * 列顺序的乐观副本：拖完立即按新顺序渲染，不等 PUT + 重新拉定义（那是整整两跳）。
   * 服务端定义回到同一顺序后由下方 effect 撤掉；保存失败则回滚到拖前顺序。
   */
  const [columnOrderDraft,setColumnOrderDraft]=useState<{master:string[]|null;detail:string[]|null}>({master:null,detail:null})
  const columnSaveSeqRef=useRef(0)
  const columnSaveChainRef=useRef<Promise<void>>(Promise.resolve())
  /** 页面是否还挂着：切模块/关标签后，迟到的保存失败不能往新页面上弹提示 */
  const mountedRef=useRef(true)
  const [columnFilters,setColumnFilters]=useState<Record<string,QueryCondition>>(initialState.columnFilters)
  const [appliedConditions,setAppliedConditions]=useState<QueryCondition[]>(initialState.conditions)
  const [conditions,setConditions]=useState<QueryCondition[]>(initialState.conditions.length?initialState.conditions:[emptyQueryCondition()])
  const [keyword,setKeyword]=useState(initialState.keyword)
  const [exporting,setExporting]=useState(false)
  const [groupDefs,setGroupDefs]=useState<NavigationGroupDef[]|null>(null)
  const [groupValues,setGroupValues]=useState<string[]|null>(null)
  const [activeGroup,setActiveGroup]=useState<NavigationGroupDef|null>(null)
  const [groupMenuOpen,setGroupMenuOpen]=useState(false)
  // 分组筛选的深链参数是分组编号（MODULE_GROUPS.GROUP_ID，见 230102 模块分组），不是"第几组"：
  // 序号只决定下拉顺序，拿它当身份会让调序/删除后的旧链接指向另一个表达式。
  const rawGroupId=searchParams.get('groupId')
  const rawGroupValue=searchParams.get('groupValue')
  const groupId=rawGroupId!=null&&/^\d+$/.test(rawGroupId)?Number(rawGroupId):null
  const groupValue=groupId!=null&&rawGroupValue!=null?rawGroupValue:null
  const definition=useQuery({queryKey:['workbench',moduleId,'definition'],queryFn:()=>apiClient.get<Definition>(`/document-workbench/${moduleId}/definition`)})
  // 数值语义族（ADR-037）：显示默认要用它（金额固定两位、整数不分组）。
  // 定义快照不含这一项，故单独取一次；与表单页共用同一 queryKey，切页不重复请求。
  const numericContext=useQuery({queryKey:['workbench',moduleId,'numeric-context'],queryFn:()=>apiClient.get<{fieldRoles:Record<string,string>}>(`/document-workbench/${moduleId}/numeric-context`),enabled:Boolean(moduleId),staleTime:5*60*1000})
  const fieldRoles=numericContext.data?.fieldRoles
  const roleOf=useCallback((key:string)=>fieldRoles?.[key.toUpperCase()],[fieldRoles])
  // 滚动加载模式下 pageSize 即每次抓取的块大小：50 ≈ 两屏缓冲，减少请求与“加载更多”闪烁
  const pageSize=50
  // 网格渲染全部显示列：服务端下发的集合即用户选择列，截断会让选择列与网格不一致
  const master=useMemo(()=>applyFieldOrder(uniqueFields(definition.data?.masterFields??[]),columnOrderDraft.master),[definition.data,columnOrderDraft.master])
  const detail=useMemo(()=>applyFieldOrder(uniqueFields(definition.data?.detailFields??[]),columnOrderDraft.detail),[definition.data,columnOrderDraft.detail])
  const allowedMasterKeys=useMemo(()=>new Set(master.map(field=>field.key.toLowerCase())),[master])
  const safeSort=useMemo(()=>sort.filter(item=>allowedMasterKeys.has(item.id.toLowerCase())),[sort,allowedMasterKeys])
  const safeConditions=useMemo(()=>appliedConditions.filter(item=>allowedMasterKeys.has(item.field.toLowerCase())),[appliedConditions,allowedMasterKeys])
  const safeColumnFilters=useMemo(()=>Object.fromEntries(Object.entries(columnFilters).filter(([key])=>allowedMasterKeys.has(key.toLowerCase()))),[columnFilters,allowedMasterKeys])
  // 服务端定义已经回到草稿顺序：乐观副本完成使命，撤掉它，避免永远盖住服务端（如另一端改了列配置）。
  useEffect(()=>{
    if(!definition.data)return
    setColumnOrderDraft(current=>{
      if(!current.master&&!current.detail)return current
      const masterKeys=definition.data!.masterFields.map(field=>field.key)
      const detailKeys=definition.data!.detailFields.map(field=>field.key)
      const master=sameColumnOrder(current.master,masterKeys)?null:current.master
      const detail=sameColumnOrder(current.detail,detailKeys)?null:current.detail
      return master===current.master&&detail===current.detail?current:{master,detail}
    })
  },[definition.data])
  // 换模块：上一张表的列序不能套到下一张表；旧的「撤销列顺序」也不再有效。
  // 用 ref 记住上一次的模块号：toast 上下文一旦换了身份（Provider 重渲染）不能让这个
  // effect 误跑，否则一有提示就被当成"换了模块"清掉。
  const lastModuleIdRef=useRef<string|null>(null)
  useEffect(()=>{
    if(lastModuleIdRef.current===moduleId)return
    lastModuleIdRef.current=moduleId
    setColumnOrderDraft({master:null,detail:null})
    toast.dismissKey(COLUMN_ORDER_TOAST_KEY)
  },[moduleId,toast])
  // 离开工作台：撤掉页面级提示，别让"撤销列顺序"跟着用户跑到别的页面
  useEffect(()=>{
    mountedRef.current=true
    return ()=>{mountedRef.current=false}
  },[])
  useEffect(()=>()=>{toast.dismissKey(COLUMN_ORDER_TOAST_KEY)},[toast])
  const records=useInfiniteQuery({
    queryKey:['workbench',moduleId,'records',pageSize,safeConditions,keyword,safeSort,groupId,groupValue],
    queryFn:({pageParam})=>{const sq=sortQuery(safeSort);const group=groupId!=null&&groupValue!=null?`&groupId=${groupId}&groupValue=${encodeURIComponent(groupValue)}`:'';return safeConditions.length?apiClient.post<DataResponse>(`/document-workbench/${moduleId}/query?page=${pageParam}&pageSize=${pageSize}${keyword?`&keyword=${encodeURIComponent(keyword)}`:''}${sq.sortFields?`&sortFields=${encodeURIComponent(sq.sortFields)}&sortDirections=${encodeURIComponent(sq.sortDirections??'')}`:''}${group}`,{conditions:safeConditions}):apiClient.get<DataResponse>(`/document-workbench/${moduleId}/records`,{query:{page:pageParam,pageSize,keyword:keyword||undefined,...sq,...(groupId!=null&&groupValue!=null?{groupId,groupValue}:{})}})},
    initialPageParam:1,
    getNextPageParam:(last)=>last.page<Math.ceil(last.total/pageSize)?last.page+1:undefined,
    enabled:definition.isSuccess,
    placeholderData:keepPreviousData,
  })
  const rows=useMemo(()=>records.data?.pages.flatMap(item=>item.rows??[])??[],[records.data])
  const total=records.data?.pages[records.data.pages.length-1]?.total??0
  const hydrated=useRef(false)
  useEffect(()=>{
    if(!definition.data||groupDefs!==null)return
    apiClient.get<{groups:NavigationGroupDef[]}>(`/navigation/${moduleId}/groups`)
      .then(data=>setGroupDefs(data.groups??[]))
      .catch(()=>setGroupDefs([]))
  },[definition.data,moduleId,groupDefs])
  useEffect(()=>{
    if(!groupMenuOpen)return
    const close=(event:MouseEvent)=>{if(!(event.target as HTMLElement).closest('.erp-group-dropdown'))setGroupMenuOpen(false)}
    const esc=(event:KeyboardEvent)=>{if(event.key==='Escape')setGroupMenuOpen(false)}
    // pointerdown 先于 click：此时菜单项仍挂在 DOM 上，不会被"重渲染导致目标脱离"误判为外部点击
    document.addEventListener('pointerdown',close)
    document.addEventListener('keydown',esc)
    return ()=>{document.removeEventListener('pointerdown',close);document.removeEventListener('keydown',esc)}
  },[groupMenuOpen])
  useEffect(()=>{
    if(underlay)return
    if(!hydrated.current){hydrated.current=true;return}
    setSearchParams((current)=>{
      const state=writeListState({keyword,sort:safeSort,conditions:safeConditions,columnFilters:safeColumnFilters})
      const groupIdParam=current.get('groupId')
      const groupValueParam=current.get('groupValue')
      if(groupIdParam)state.set('groupId',groupIdParam)
      if(groupValueParam)state.set('groupValue',groupValueParam)
      return state
    },{replace:true})
  },[underlay,keyword,safeSort,safeConditions,safeColumnFilters,setSearchParams])
  const columnSettings=useQuery({queryKey:['workbench',moduleId,'column-editor'],queryFn:()=>apiClient.get<{current:ColumnSettings;defaults:ColumnSettings}>(`/document-workbench/${moduleId}/column-editor`),enabled:columnsOpen})
  const saveColumns=useMutation({mutationFn:(settings:{master:string[];detail:string[]})=>apiClient.put<void>(`/document-workbench/${moduleId}/columns`,{master:settings.master,detail:settings.detail}),onSuccess:async()=>{await Promise.all([queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']}),queryClient.invalidateQueries({queryKey:['workbench',moduleId,'column-editor']})])}})
  const columnGroups=useMemo<ColumnSelectorGroup[]>(()=>{
    const current=columnSettings.data?.current
    if(!current)return []
    const toGroup=(id:'master'|'detail',label:string):ColumnSelectorGroup=>{
      const visibleKeys=current[id].filter(item=>item.isVisible).map(item=>item.key)
      const draftOrder=columnOrderDraft[id]
      return {
        id,
        label,
        fields:current[id].map(item=>({key:item.key,label:item.label})),
        // 拖拽是乐观落位的：保存还没回来时打开「选择列」，也要看到刚拖出来的顺序
        visibleKeys:draftOrder?applyColumnOrder(visibleKeys,draftOrder):visibleKeys,
        defaultKeys:(columnSettings.data?.defaults[id]??[]).filter(item=>item.isVisible).map(item=>item.key),
      }
    }
    const groups=[toGroup('master','主表字段')]
    if(definition.data?.detailTable)groups.push(toGroup('detail','子表字段'))
    return groups
  },[columnSettings.data,definition.data?.detailTable,columnOrderDraft])

  const masterColumns=useMemo<ColumnDef<Record<string,unknown>,unknown>[]>(()=>[
    checkboxSelectColumn<Record<string,unknown>>(),
    ...master.map((field):ColumnDef<Record<string,unknown>,unknown>=>({
      id:field.key,
      accessorKey:field.key,
      header:field.label,
      enableSorting:!field.isVirtual,
      meta:{
        className:alignClass(field.headerAlign),
        cellClassName:alignClass(field.align, field.dataType),
        minWidth:field.width,
        filterable:field.isQueryable,
        dataType:field.dataType,
        truncate:(field.dataType??'').toLowerCase()!=='bit',
        title:({value})=>formatFieldValue(value,field.dataType,field.format,{numericRole:roleOf(field.key)})||undefined,
        copyText:({value})=>formatFieldValue(value,field.dataType,field.format,{numericRole:roleOf(field.key)})||'—',
        headerMenu:[{label:'字段设置',onClick:()=>openTab(`/admin/fields/${encodeURIComponent(definition.data?.masterTable ?? '')}/${encodeURIComponent(field.key)}?moduleId=${moduleId}`,{locate:true})}],
      },
      cell:(info)=>{
        const value=info.getValue()
        if((field.dataType??'').toLowerCase()==='bit')return <input type="checkbox" className="form-check-input" checked={Boolean(value)} disabled aria-label={field.label}/>
        const text=formatFieldValue(value,field.dataType,field.format,{numericRole:roleOf(field.key)})
        if(!text)return '—'
        return field.browseModuleId&&field.browseModuleId>0
          ?<FieldBrowseLink value={text} browseModuleId={field.browseModuleId} browseKeyFields={field.browseKeyFields} row={info.row.original} fromModuleId={moduleId} canBrowse={hasPermission(moduleReadPermission(field.browseModuleId))}/>
          :text
      },
    })),
  ],[master,hasPermission,moduleId,openTab,definition.data?.masterTable,roleOf])
  const detailColumns=useMemo<ColumnDef<Record<string,unknown>,unknown>[]>(()=>detail.map((field):ColumnDef<Record<string,unknown>,unknown>=>({
    id:field.key,
    accessorKey:field.key,
    header:field.label,
    enableSorting:!field.isVirtual,
    meta:{
      className:alignClass(field.headerAlign),
      cellClassName:alignClass(field.align, field.dataType),
      minWidth:field.width,
      dataType:field.dataType,
      truncate:(field.dataType??'').toLowerCase()!=='bit',
      title:({value})=>formatFieldValue(value,field.dataType,field.format,{numericRole:roleOf(field.key)})||undefined,
      copyText:({value})=>formatFieldValue(value,field.dataType,field.format,{numericRole:roleOf(field.key)})||'—',
      headerMenu:[{label:'字段设置',onClick:()=>openTab(`/admin/fields/${encodeURIComponent(definition.data?.detailTable ?? '')}/${encodeURIComponent(field.key)}?moduleId=${moduleId}`,{locate:true})}],
    },
    cell:(info)=>{
      const value=info.getValue()
      if((field.dataType??'').toLowerCase()==='bit')return <input type="checkbox" className="form-check-input" checked={Boolean(value)} disabled aria-label={field.label}/>
      const text=formatFieldValue(value,field.dataType,field.format,{numericRole:roleOf(field.key)})
      if(!text)return '—'
      return field.browseModuleId&&field.browseModuleId>0
        ?<FieldBrowseLink value={text} browseModuleId={field.browseModuleId} browseKeyFields={field.browseKeyFields} row={info.row.original} fromModuleId={moduleId} canBrowse={hasPermission(moduleReadPermission(field.browseModuleId))}/>
        :text
    },
  })),[detail,hasPermission,moduleId,openTab,definition.data?.detailTable,roleOf])
  const rowSelection=useMemo<RowSelectionState>(()=>Object.fromEntries(Object.keys(selected).map(key=>[key,true])),[selected])

  // 助手处境上报：列表筛选条件与选中行主键（服务端会再截断并按字段白名单校验）。
  // 底图模式不上报：那会儿用户在看表单，把"列表筛选/选中"报上去只会误导助手。
  useEffect(()=>{
    if(underlay)return
    reportListFilters([...safeConditions,...Object.values(safeColumnFilters)].map(condition=>({
      field:condition.field,
      operator:condition.operator,
      value:condition.value??'',
    })))
  },[underlay,safeConditions,safeColumnFilters])
  useEffect(()=>{
    if(underlay)return
    reportSelection(Object.keys(selected))
  },[underlay,selected])
  // 离开列表页即撤回上报，避免把上一页的筛选/选中带到别处
  useEffect(()=>()=>{
    reportListFilters([])
    reportSelection([])
  },[])

  // 行键身份要稳：ErpTable 拿它当 getRowId（行键去重、区域选择、行组件 memo 都依赖它），
  // 内联函数每次渲染都是新身份，会让"这份数据要不要重算行键"永远判成要。
  const masterPkOrder=definition.data?.masterPkOrder
  const rowKey=useCallback((row:Record<string,unknown>)=>{
    const keys=(masterPkOrder??[]).map(column=>String(row[column]??''))
    return keys.some(key=>key!=='')?keys.join('|'):JSON.stringify(row)
  },[masterPkOrder])
  const active=activeKey?selected[activeKey]??rows.find(row=>rowKey(row)===activeKey)??null:null
  // 明细卡标题摘要：优先取主键列值（用户据此识别“这是哪张单”），无主键值回退首个可查询字段
  const masterDescribe=useMemo(()=>{
    if(!active||!definition.data)return ''
    const cols=definition.data.masterPkOrder??[]
    const items:string[]=[]
    for(const column of cols){
      const field=definition.data.masterFields.find(item=>item.key.toLowerCase()===column.toLowerCase())
      const raw=active[column]
      if(raw==null)continue
      const text=field?formatFieldValue(raw,field.dataType,field.format):String(raw)
      if(text&&text!=='')items.push(text)
    }
    if(items.length>0)return items.join(' / ')
    const fallback=definition.data.masterFields.find(field=>field.isQueryable&&active[field.key]!=null&&String(active[field.key]??'')!=='')
    return fallback?formatFieldValue(active[fallback.key],fallback.dataType,fallback.format):''
  },[active,definition.data])
  const keys=useMemo(()=>(definition.data?.masterPkOrder??[]).reduce<Record<string,string>>((result,column)=>{if(active?.[column]!=null)result[column]=String(active[column]);return result},{}),[active,definition.data?.masterPkOrder])
  const details=useQuery({queryKey:['workbench',moduleId,'details',keys,detailSort],queryFn:()=>apiClient.get<DataResponse>(`/document-workbench/${moduleId}/details`,{query:{...keys,sortField:detailSort?.field,sortDirection:detailSort?.direction}}),enabled:Boolean(active&&definition.data?.detailTable),placeholderData:keepPreviousData})
  // 列宽是操作习惯：写个人层（SYS_USER_COLUMN.F_WIDTH，与"选择列"同一批行），有模块浏览权即可；字段元数据里的宽度是所有人的
  // 默认值，只在字段设置面改，不随拖拽变动。
  // 列宽写回走统一队列（与查询中心共用 `useColumnWidthSaver`；多槽串行，自适应列宽一次入队多列也不会丢）
  const { saveColumnWidth } = useColumnWidthSaver(moduleId, () => {
    void queryClient.invalidateQueries({ queryKey: ['workbench', moduleId, 'definition'] })
  })
  const saveMasterWidth=useCallback((columnKey:string,width:number)=>{void saveColumnWidth(false,columnKey,width)},[saveColumnWidth])
  const saveDetailWidth=useCallback((columnKey:string,width:number)=>{void saveColumnWidth(true,columnKey,width)},[saveColumnWidth])
  const handleColumnFilterChange=useCallback((columnId:string,condition:QueryCondition|null)=>{
    setColumnFilters(current=>{
      const next={...current}
      if(condition)next[columnId]=condition
      else delete next[columnId]
      return next
    })
    setAppliedConditions(current=>{
      const next=current.filter(item=>item.field.toLowerCase()!==columnId.toLowerCase())
      if(condition)next.push({...condition,logic:'and'})
      return next
    })
  },[])
  /**
   * 打开高级查询：草稿从"已生效条件"起步。列头/单元格右键筛选与高级查询是同一批条件的两个视图
   * （`handleColumnFilterChange` 双向同步），草稿若从空行起步，刚设的筛选在弹窗里就看不到。
   */
  const openQueryBuilder=useCallback(()=>{
    setConditions(appliedConditions.length?appliedConditions.map(item=>({...item})):[emptyQueryCondition()])
    setQueryOpen(true)
  },[appliedConditions])
  /** 应用高级查询：列头筛选标记按新条件重建，否则在弹窗里删掉的条件仍会显示筛选态 */
  const applyQueryConditions=useCallback((next:QueryCondition[])=>{
    setAppliedConditions(next)
    setColumnFilters(current=>{
      const result:Record<string,QueryCondition>={}
      for(const key of Object.keys(current)){
        const hit=next.find(item=>item.field.toLowerCase()===key.toLowerCase())
        if(hit)result[key]=hit
      }
      return result
    })
    setQueryOpen(false)
  },[])
  /** 滚动到底加载下一页：ErpTable 的哨兵观察器只在"有没有这个回调"上重建，回调本身必须稳定 */
  const fetchNextRowsRef=useRef(records.fetchNextPage)
  fetchNextRowsRef.current=records.fetchNextPage
  const loadMoreRows=useCallback(()=>{void fetchNextRowsRef.current()},[])
  // 列顺序与「选择列」的上下移动写同一份个人列配置（SYS_USER_COLUMN.F_IDX），拖完再打开选择列即同一顺序。
  // 主表改序时明细顺序原样带上，反之亦然——保存是替换语义，两份都得交全。
  const persistColumnOrderRef=useRef<(masterOrder:string[],detailOrder:string[],options?:ColumnOrderSaveOptions)=>void>(()=>{})
  const persistColumnOrder=useCallback((masterOrder:string[],detailOrder:string[],options:ColumnOrderSaveOptions={})=>{
    // 先按新顺序渲染（乐观），再把整份列集交给服务端
    setColumnOrderDraft({master:masterOrder,detail:detailOrder})
    const seq=++columnSaveSeqRef.current
    const run=async()=>{
      try{
        await saveColumns.mutateAsync({master:masterOrder,detail:detailOrder})
        if(!mountedRef.current)return
        const undo=options.undo
        if(undo){
          // 保存成功后才给撤销：失败时顺序已经回滚，不能再让「撤销」去覆盖服务端
          toast.notify({
            key:COLUMN_ORDER_TOAST_KEY,
            message:COLUMN_ORDER_ADJUSTED,
            action:{
              label:'撤销',
              onClick:()=>persistColumnOrderRef.current(undo.master,undo.detail,{successMessage:COLUMN_ORDER_UNDONE}),
            },
          })
        }else if(options.successMessage){
          toast.notify({key:COLUMN_ORDER_TOAST_KEY,message:options.successMessage,variant:'success',duration:2500})
        }
      }catch(error){
        if(!mountedRef.current)return
        // 只回滚最后一次拖拽：后面的拖拽已经覆盖了它，回滚反而会抹掉用户刚做的动作
        if(seq!==columnSaveSeqRef.current)return
        // 前面的保存是串行且等过定义回读的，此刻服务端/缓存的顺序就是可回滚的落点；
        // 清掉乐观副本再拉一次定义，避免把"上一次也没存成功"的草稿当成已保存状态。
        setColumnOrderDraft({master:null,detail:null})
        void queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']})
        const message=error instanceof Error?error.message:'保存列顺序失败。'
        // 非阻塞：同一 key 会把上面的「已调整」原地换成失败态，同时给出重试
        toast.notify({
          key:COLUMN_ORDER_TOAST_KEY,
          message:`列顺序未保存，已恢复原顺序（${message}）`,
          variant:'danger',
          action:{label:'重试',onClick:()=>persistColumnOrderRef.current(masterOrder,detailOrder,options)},
        })
      }
    }
    // 串行保存：两次快速拖拽若并发 PUT，服务端最后落库的顺序由响应先后决定，可能不是用户最后看到的
    const task=columnSaveChainRef.current.then(run,run)
    columnSaveChainRef.current=task.catch(()=>{})
  },[moduleId,queryClient,saveColumns,toast])
  useLayoutEffect(()=>{persistColumnOrderRef.current=persistColumnOrder},[persistColumnOrder])
  const handleColumnsReorder=useCallback((columnIds:string[])=>{
    if(!definition.data)return
    const currentMaster=master.map(field=>field.key)
    const currentDetail=detail.map(field=>field.key)
    const nextMaster=applyColumnOrder(currentMaster,columnIds)
    persistColumnOrder(nextMaster,currentDetail,{undo:{master:currentMaster,detail:currentDetail}})
  },[definition.data,master,detail,persistColumnOrder])
  const handleDetailColumnsReorder=useCallback((columnIds:string[])=>{
    if(!definition.data)return
    const currentMaster=master.map(field=>field.key)
    const currentDetail=detail.map(field=>field.key)
    const nextDetail=applyColumnOrder(currentDetail,columnIds)
    persistColumnOrder(currentMaster,nextDetail,{undo:{master:currentMaster,detail:currentDetail}})
  },[definition.data,master,detail,persistColumnOrder])
  if(definition.isPending)return <LoadingState label="正在加载单据定义…"/>
  if(definition.isError)return <section className="card"><div className="card-body text-center py-5">无法加载模块定义。</div></section>

  const detailSorting:SortingState=detailSort?[{id:detailSort.field,desc:detailSort.direction==='desc'}]:[]
  // 排序/筛选/搜索/换模块 = 整批换数据：列表要回到顶部，否则停在旧偏移上看到的是"另一批数据的中段"
  const listResetKey=`${moduleId}|${keyword}|${JSON.stringify(safeSort)}|${JSON.stringify(safeConditions)}|${groupValue??''}`
  const changeKeyword=(value:string)=>{setKeyword(value)}
  const changeMasterSort=(next:SortingState)=>{setSort(next)}
  const changeDetailSort=(next:SortingState)=>{const first=next[0];setDetailSort(first?{field:first.id,direction:first.desc?'desc':'asc'}:null)}
  const handleRowSelectionChange=(next:RowSelectionState)=>{const selectedKeys=Object.keys(next).filter(key=>next[key]);setSelected(current=>{const result:Record<string,Record<string,unknown>>={};for(const key of selectedKeys){result[key]=current[key]??rows.find(row=>rowKey(row)===key)??{}}return result});if(selectedKeys.length===1){const only=selectedKeys[0];setActiveKey(only)}else if(selectedKeys.length===0){setActiveKey(null)}}
  const handleRowClick=(row:Record<string,unknown>)=>{const key=rowKey(row);setSelected({[key]:row});setActiveKey(key)}
  // 新增入口只有一处：统一表单（NEW_URL/MODI_URL 已随迁移 321 物理删除，不再有自定义新增页路由）。
  // 按模块声明的打开方式开（本页签/新页签/弹窗）；弹窗方式把窗体标题与尺寸一并递过去，
  // 表单页首帧就把加载态放进窗体，不闪整页加载态。
  const formShellHint=()=>({dialogTitle:definition.data?.title,dialogWidth:definition.data?.formDialogWidth,dialogHeight:definition.data?.formDialogHeight})
  const openNew=()=>{if(!definition.data?.hasAdd)return;openForm(workbenchNew(moduleId),{openMode:definition.data.formOpenMode,...formShellHint()})}
  const canOpenView=Boolean(definition.data?.hasEdit)
  // Double-click a master row to open browse mode (detail rows do not); query-only modules have no action.
  // Carries the current list display order (after sort/filter) as previous/next navigation context.
  const openViewFromRow=(row:Record<string,unknown>)=>{
    if(!definition.data||!canOpenView)return
    const key=definition.data.masterPkOrder.map(column=>String(row[column]??''))
    const navKeys=rows.map(item=>definition.data!.masterPkOrder.map(column=>String(item[column]??'')))
    const navIndex=navKeys.findIndex(candidate=>candidate.every((value,i)=>value===key[i]))
    openForm(workbenchView(moduleId,key),{openMode:definition.data.formOpenMode,...formShellHint(),state:{navKeys,navIndex}})
  }
  // 列表工具条只留「新增」+ 列表自身工具（导出 / 查询）：单据级动作
  // （编辑/复制/删除/批核/结案/打印…）全在统一表单的浏览态工具条里，破坏性动作必须先打开单据看细节。
  // （曾经这套内容由 `MODULES.FORM_BUTTONS` 白名单裁剪，该列已随迁移 320 退役且全库为空；
  //   「查询」入口仍按模块的 SEARCH 位出现，见下方 businessItems 之后的补位。）
  const businessItems:ErpCommandItem[]=[
    {action:'new',visible:definition.data?.hasAdd,variant:'primary',onClick:openNew},
    {action:'export',render:()=>exportButton},
  ]
  const openGroupValues=async(group:NavigationGroupDef)=>{setActiveGroup(group);setGroupValues(null);try{const data=await apiClient.get<{values:string[]}>(`/navigation/${moduleId}/groups/${group.groupId}/values`);setGroupValues(data.values)}catch{setGroupValues([])}}
  const applyGroupValue=(value:string)=>{if(!activeGroup)return;setGroupMenuOpen(false);setSearchParams(current=>{current.set('groupId',String(activeGroup.groupId));current.set('groupValue',value);return current},{replace:true})}
  const groupQuery=groupId!=null&&groupValue!=null?{groupId,groupValue}:{}
  const fitAllColumns=async()=>{
    if(fitting)return
    const masterWidths=masterFitRef.current?.()??{}
    const detailWidths=detailFitRef.current?.()??{}
    if(Object.keys(masterWidths).length===0&&Object.keys(detailWidths).length===0)return
    setFitting(true)
    try{
      await apiClient.put<void>(`/document-workbench/${moduleId}/column-widths`,{master:masterWidths,detail:detailWidths})
      await queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']})
    }catch(error){
      window.alert(error instanceof Error?`保存列宽失败：${error.message}`:'保存列宽失败。')
    }finally{
      setFitting(false)
    }
  }
  const handleExport=async(format:'csv'|'xls')=>{if(!definition.data)return;setExporting(true);try{const selectedIds=Object.keys(rowSelection).filter(id=>rowSelection[id]);const exportColumns=master.map(field=>field.key).join(',');const commonQuery={format,columns:exportColumns};const blob=selectedIds.length>0?await apiClient.postFile(`/document-workbench/${moduleId}/export-selected`,{keys:selectedIds.map(id=>{const row=selected[id];return definition.data!.masterPkOrder.map(column=>String(row?.[column]??''))})},{query:{...groupQuery,...commonQuery}}):await apiClient.postFile(`/document-workbench/${moduleId}/export`,{conditions:safeConditions},{query:{keyword:keyword||undefined,...sortQuery(safeSort),...groupQuery,...commonQuery}});const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=`${definition.data.title}.${format==='xls'?'xls':'csv'}`;document.body.appendChild(anchor);anchor.click();anchor.remove();URL.revokeObjectURL(url)}catch(error){window.alert(error instanceof Error?`导出失败：${error.message}`:'导出失败。')}finally{setExporting(false)}}
  const exportLabel=Object.keys(rowSelection).filter(id=>rowSelection[id]).length
  const exportDisabled=exportLabel===0
  const exportButton=<Button size="sm" className="erp-command-icon-btn" icon={<IconFileExport size={16}/>} loading={exporting} disabled={exportDisabled} title={exportLabel?`导出所选 (${exportLabel})`:'导出（请先选择要导出的行）'} aria-label={exportLabel?`导出所选 (${exportLabel})`:'导出'} onClick={()=>void handleExport('csv')}/>
  const recordsError=records.error instanceof ApiError?records.error.body.message:'发生未知错误，请稍后重试。'

  return <div className={`erp-base-page erp-page-fill erp-workbench-page${definition.data.detailTable?'':' erp-workbench-single'}`}>
    <ErpListGrid
      ariaLabel="单据列表查询与操作"
      listId={`workbench-${moduleId}-master`}
      columns={masterColumns}
      rows={rows}
      rowKey={rowKey}
      loading={records.isPending}
      loadingLabel="正在加载主表数据…"
      fetching={records.isFetching&&!records.isFetchingNextPage}
      error={records.isError?recordsError:null}
      onRetry={()=>void records.refetch()}
      regionClassName="erp-master-table-region"
      sorting={{mode:'server',value:safeSort,onChange:changeMasterSort}}
      selection={{value:rowSelection,onChange:handleRowSelectionChange}}
      activeRowId={activeKey??undefined}
      onRowClick={handleRowClick}
      onRowDoubleClick={openViewFromRow}
      empty={keyword?<EmptyState title="未找到匹配记录" description={`没有找到与“${keyword}”匹配的${definition.data.title}记录，可尝试其它关键词或清除搜索。`}/>:null}
      search={<ErpSearchBox value={keyword} onChange={changeKeyword} debounceMs={400} placeholder="搜索单据、供应商或商品" ariaLabel="搜索" />}
      actions={<>
        <ErpCommandBar iconOnly items={[
          {action:'query',onClick:openQueryBuilder,title:safeConditions.length?`高级查询 (${safeConditions.length})`:'高级查询',badge:safeConditions.length},
          {action:'columns',onClick:()=>{queryClient.removeQueries({queryKey:['workbench',moduleId,'column-editor']});setColumnsOpen(true)}},
          ...(groupDefs&&groupDefs.length>0?[{action:'group',render:()=>(
            <div className="dropdown erp-group-dropdown">
              <Button size="sm" className={`erp-command-icon-btn${groupMenuOpen?' show':''}`} icon={<IconZoomScan size={16}/>} aria-expanded={groupMenuOpen} title="分组" aria-label="分组" onClick={()=>setGroupMenuOpen(open=>!open)} />
              {groupMenuOpen&&(
                <div className="dropdown-menu dropdown-menu-end show" role="menu">
                  {activeGroup===null?groupDefs.map(group=>(
                    <button key={group.groupId} type="button" role="menuitem" className={`dropdown-item ${group.available?'':'disabled'}`} disabled={!group.available} onClick={()=>void openGroupValues(group)}>{group.description}</button>
                  )):(
                    <>
                      <button type="button" role="menuitem" className="dropdown-item" onClick={()=>setActiveGroup(null)}>← {activeGroup.description}</button>
                      <div className="dropdown-divider"/>
                      {groupValues===null
                        ?<div className="dropdown-item-text text-secondary">加载中…</div>
                        :groupValues.length===0
                          ?<div className="dropdown-item-text text-secondary">无分组数据</div>
                          :groupValues.map(value=>(
                            <button key={value} type="button" role="menuitem" className={`dropdown-item ${groupId===activeGroup.groupId&&groupValue===value?'active':''}`} onClick={()=>applyGroupValue(value)}>{value}</button>
                          ))}
                    </>
                  )}
                </div>
              )}
            </div>
          )}] satisfies ErpCommandItem[]:[]),
          {action:'fit',loading:fitting,onClick:()=>void fitAllColumns()},
          ...businessItems.filter(item=>item.action!=='new'),
          {action:'refresh',onClick:()=>{void records.refetch();if(active)void details.refetch()}},
          ...businessItems.filter(item=>item.action==='new'),
        ]} />
      </>}
      footer={
        <div className="d-flex align-items-center gap-2 w-100">
          <span className="text-secondary small text-nowrap">共 {total} 条{rows.length < total ? `，已加载 ${rows.length} 条` : ''}</span>
          {rangeSummary&&rangeSummary.numbers>0&&(
            <span className="text-secondary small text-nowrap" aria-live="polite">
              已选 {rangeSummary.cells} 格 · 合计 {summaryNumber.format(rangeSummary.sum)}
              {rangeSummary.numbers>1?` · 平均 ${summaryNumber.format(rangeSummary.average)}`:''}
            </span>
          )}
          {rows.length>0&&rows.length<total&&(
            <>
              <div className="erp-load-progress flex-grow-1" role="progressbar" aria-label="已加载进度" aria-valuemin={0} aria-valuemax={total} aria-valuenow={rows.length}>
                <div className="erp-load-progress-bar" style={{width:`${Math.max(2,Math.round(rows.length/total*100))}%`}} />
              </div>
              {records.isFetchingNextPage
                ? <span className="text-secondary small text-nowrap">正在加载更多…</span>
                : <span className="text-secondary small text-nowrap">滚动到底自动加载{total-rows.length<=50?`（剩余 ${total-rows.length} 条）`:''}</span>}
            </>
          )}
        </div>
      }
      before={groupValue!=null?(
        <div className="erp-active-group-filter">
          <IconZoomScan size={14} aria-hidden="true" />
          <span>分组筛选：{groupValue}</span>
          <button type="button" onClick={()=>{setSearchParams((current)=>{current.delete('groupId');current.delete('groupValue');return current},{replace:true})}}>清除分组</button>
        </div>
      ):null}
      tableProps={{
        virtualize:true,
        fitRef:masterFitRef,
        persistResize:false,
        onColumnResize:saveMasterWidth,
        columnFilterValue:columnFilters,
        onColumnFilterChange:handleColumnFilterChange,
        // 右键「在查询中心查找此值」：把"看到某个料号/单号"直接接到跨单据倒查，
        // 这比从菜单进查询中心再选模块快得多。只在模块登记了检索位时出现。
        cellMenuExtra:({columnId,value}:{columnId:string;value:unknown})=>{
          if(!definition.data||!(definition.data.searchMaster||definition.data.searchDetail))return []
          const text=value==null?'':String(value).trim()
          if(!text)return []
          const shown=text.length>14?`${text.slice(0,14)}…`:text
          return [{
            key:'search-center',
            label:`在查询中心查找“${shown}”`,
            onClick:()=>openTab(`/search-center?module=${moduleId}&find=${encodeURIComponent(`${columnId}:${text}`)}`,{locate:true}),
          }]
        },
        onColumnsReorder:handleColumnsReorder,
        // 电子表格手感：拖选一块单元格 → Ctrl+C 复制成 TSV（不带表头），直接粘进 Excel
        cellSelection:true,
        scrollResetKey:listResetKey,
        onSelectionSummaryChange:setRangeSummary,
        onEndReached:loadMoreRows,
        hasMore:Boolean(records.hasNextPage),
        loadingMore:records.isFetchingNextPage,
      }}
    />
    {definition.data.detailTable&&<section className="card erp-detail-card">
      <div className="card-header erp-card-toolbar">
        <span className="small fw-semibold text-secondary text-nowrap">明细</span>
        {active&&masterDescribe?(<div className="small text-truncate" title={masterDescribe}>{masterDescribe}</div>):(<span className="small text-secondary text-nowrap">选择主表行查看明细</span>)}
        {details.isFetching&&<span className="small text-secondary ms-auto text-nowrap">正在加载明细…</span>}
      </div>
      {details.isError?<div className="alert alert-danger d-flex align-items-center justify-content-between m-2 mb-0"><span>明细数据加载失败。</span><button type="button" className="btn btn-danger btn-sm" onClick={()=>void details.refetch()}>重新加载</button></div>:<ErpListGrid
        plain
        listId={`workbench-${moduleId}-detail`}
        columns={detailColumns}
        rows={active?details.data?.rows:[]}
        sorting={{mode:'server',value:detailSorting,onChange:changeDetailSort}}
        empty={null}
        tableProps={{
          className:'table-sm',
          fitRef:detailFitRef,
          persistResize:false,
          onColumnResize:saveDetailWidth,
          // 子表列顺序同样是个人列配置：拖列头即改「选择列」里子表字段的上下顺序
          onColumnsReorder:handleDetailColumnsReorder,
        }}
      />}</section>}
    {columnsOpen&&<ErpColumnSelector
      open
      groups={columnGroups}
      loading={columnSettings.isPending||columnSettings.isFetching}
      loadError={columnSettings.isError?'字段配置加载失败，请重新加载。':null}
      onRetry={()=>void columnSettings.refetch()}
      saving={saveColumns.isPending}
      onClose={()=>setColumnsOpen(false)}
      canSave={(selection)=>Boolean(selection.master?.length)}
      onSave={async(selection)=>{
        // 拖拽保存可能还在队列里：先等它落库，否则"选择列"的整份列集可能被后到的拖拽 PUT 覆盖
        await columnSaveChainRef.current
        const masterOrder=selection.master??[]
        const detailOrder=selection.detail??[]
        await saveColumns.mutateAsync({master:masterOrder,detail:detailOrder})
        setColumnOrderDraft({master:null,detail:null})
        // 选择列可能增删了字段，拖拽前的整份列集已不能作为撤销目标
        toast.dismissKey(COLUMN_ORDER_TOAST_KEY)
        setColumnsOpen(false)
      }}
    />}
    {queryOpen&&<ErpQueryBuilder
      open
      fields={master.filter(field=>field.isQueryable).map(field=>({key:field.key,label:field.label}))}
      conditions={conditions}
      onChange={setConditions}
      onApply={()=>applyQueryConditions(conditions)}
      onClear={()=>{setConditions([emptyQueryCondition()]);setAppliedConditions([]);setColumnFilters({});setQueryOpen(false)}}
      onClose={()=>setQueryOpen(false)}
    />}
  </div>
}

