import { IconFileExport, IconZoomScan } from '@tabler/icons-react'
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpQueryBuilder } from '../../components/common/ErpQueryBuilder'
import { emptyQueryCondition, type QueryCondition } from '../../components/common/queryCondition'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { moduleReadPermission } from '../auth/modulePermissions'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'
import { alignClass, formatFieldValue } from './fieldFormat'
import { FieldBrowseLink } from './FieldBrowseLink'
import { workbenchNew, workbenchView } from './workbenchPath'
import { readListState, writeListState } from './listStateUrl'

interface Field { key:string; label:string; dataType:string; width:number; align:string|null; isPrimaryKey:boolean; isQueryable:boolean; headerAlign:string; format:string|null; browseUrl:string|null; browseModuleId:number|null; browseKeyFields:string[]|null; isVirtual?:boolean }
interface Definition { moduleId:number; title:string; masterTable:string; detailTable?:string; masterFields:Field[]; detailFields:Field[]; hasAdd:boolean; hasEdit:boolean; masterPkOrder:string[]; hasWorkflow:boolean; ifCopy:boolean; searchMaster:boolean; searchDetail:boolean; buttons:{action:string}[]|null; newUrl?:string|null; modiUrl?:string|null; canDelete?:boolean }
interface DataResponse { rows:Record<string,unknown>[]; total:number; page:number; pageSize:number }
interface NavigationGroupDef { index:number; description:string; available:boolean }
interface ColumnSetting { key:string; label:string; isVisible:boolean; order:number }
interface ColumnSettings { master:ColumnSetting[]; detail:ColumnSetting[] }
interface ChooserSource { active:boolean; table:string|null; description:string|null; moduleId:number|null; filter:string|null; returnMapping:string|null; serialNo:number|null }
interface FieldMetadata { key:string; tableId:string; label:string; dataType:string; width:number; align:string|null; headerAlign:string; format:string|null; isVisible:boolean; isDefault:boolean; isQueryable:boolean; isReadonly:boolean; isRequired:boolean; isCost:boolean; isSecrecy:boolean; defaultValue:string|null; verifyIndex:number|null; regex:string|null; remark:string|null; browseUrl:string|null; browseModuleId:number|null; onlyChoose:boolean; chooseMultiple:boolean; choosePage:string|null; choosers:ChooserSource[]; isVirtual:boolean; virtualExpression:string|null; canCopy:boolean; isAutoIncrement:boolean; convertFunction:string|null; dataSourceSql:string|null; lastUpdatedBy:string|null; lastUpdatedAt:string|null; options:string|null }
const uniqueFields=(fields:Field[])=>fields.filter((field,index,all)=>all.findIndex(item=>item.key.toLowerCase()===field.key.toLowerCase())===index)
const sortQuery=(sort:SortingState)=>({sortFields:sort.length?sort.map(item=>item.id).join(','):undefined,sortDirections:sort.length?sort.map(item=>item.desc?'desc':'asc').join(','):undefined})
export function DocumentWorkbenchPage() {
  const { hasPermission } = useAuth()
  const navigate=useNavigate()
  const { moduleId='' }=useParams()
  const queryClient=useQueryClient()
  const [searchParams,setSearchParams]=useSearchParams()
  const [initialState]=useState(()=>readListState(searchParams))
  const masterFitRef=useRef<(() => Record<string, number>)|null>(null)
  const detailFitRef=useRef<(() => Record<string, number>)|null>(null)
  const [fitting,setFitting]=useState(false)
  const [selected,setSelected]=useState<Record<string,Record<string,unknown>>>({})
  const [activeKey,setActiveKey]=useState<string|null>(null)
  const [sort,setSort]=useState<SortingState>(initialState.sort)
  const [detailSort,setDetailSort]=useState<{field:string;direction:'asc'|'desc'}|null>(null)
  const [queryOpen,setQueryOpen]=useState(false)
  const [columnsOpen,setColumnsOpen]=useState(false)
  const [columnFilters,setColumnFilters]=useState<Record<string,QueryCondition>>(initialState.columnFilters)
  const [appliedConditions,setAppliedConditions]=useState<QueryCondition[]>(initialState.conditions)
  const [conditions,setConditions]=useState<QueryCondition[]>([emptyQueryCondition()])
  const [keyword,setKeyword]=useState(initialState.keyword)
  const [exporting,setExporting]=useState(false)
  const [helpOpen,setHelpOpen]=useState(false)
  const [groupDefs,setGroupDefs]=useState<NavigationGroupDef[]|null>(null)
  const [groupValues,setGroupValues]=useState<string[]|null>(null)
  const [activeGroup,setActiveGroup]=useState<NavigationGroupDef|null>(null)
  const [groupMenuOpen,setGroupMenuOpen]=useState(false)
  const rawGroupIndex=searchParams.get('groupIndex')
  const rawGroupValue=searchParams.get('groupValue')
  const groupIndex=rawGroupIndex!=null&&/^[1-5]$/.test(rawGroupIndex)?Number(rawGroupIndex):null
  const groupValue=groupIndex!=null&&rawGroupValue!=null?rawGroupValue:null
  const definition=useQuery({queryKey:['workbench',moduleId,'definition'],queryFn:()=>apiClient.get<Definition>(`/document-workbench/${moduleId}/definition`)})
  // 滚动加载模式下 pageSize 即每次抓取的块大小：50 ≈ 两屏缓冲，减少请求与“加载更多”闪烁
  const pageSize=50
  // 网格渲染全部显示列：服务端下发的集合即用户选择列，截断会让选择列与网格不一致
  const master=useMemo(()=>uniqueFields(definition.data?.masterFields??[]),[definition.data])
  const detail=useMemo(()=>uniqueFields(definition.data?.detailFields??[]),[definition.data])
  const allowedMasterKeys=useMemo(()=>new Set(master.map(field=>field.key.toLowerCase())),[master])
  const safeSort=useMemo(()=>sort.filter(item=>allowedMasterKeys.has(item.id.toLowerCase())),[sort,allowedMasterKeys])
  const safeConditions=useMemo(()=>appliedConditions.filter(item=>allowedMasterKeys.has(item.field.toLowerCase())),[appliedConditions,allowedMasterKeys])
  const safeColumnFilters=useMemo(()=>Object.fromEntries(Object.entries(columnFilters).filter(([key])=>allowedMasterKeys.has(key.toLowerCase()))),[columnFilters,allowedMasterKeys])
  const records=useInfiniteQuery({
    queryKey:['workbench',moduleId,'records',pageSize,safeConditions,keyword,safeSort,groupIndex,groupValue],
    queryFn:({pageParam})=>{const sq=sortQuery(safeSort);const group=groupIndex!=null&&groupValue!=null?`&groupIndex=${groupIndex}&groupValue=${encodeURIComponent(groupValue)}`:'';return safeConditions.length?apiClient.post<DataResponse>(`/document-workbench/${moduleId}/query?page=${pageParam}&pageSize=${pageSize}${keyword?`&keyword=${encodeURIComponent(keyword)}`:''}${sq.sortFields?`&sortFields=${encodeURIComponent(sq.sortFields)}&sortDirections=${encodeURIComponent(sq.sortDirections??'')}`:''}${group}`,{conditions:safeConditions}):apiClient.get<DataResponse>(`/document-workbench/${moduleId}/records`,{query:{page:pageParam,pageSize,keyword:keyword||undefined,...sq,...(groupIndex!=null&&groupValue!=null?{groupIndex,groupValue}:{})}})},
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
    if(!hydrated.current){hydrated.current=true;return}
    setSearchParams((current)=>{
      const state=writeListState({keyword,sort:safeSort,conditions:safeConditions,columnFilters:safeColumnFilters})
      const groupIndexParam=current.get('groupIndex')
      const groupValueParam=current.get('groupValue')
      if(groupIndexParam)state.set('groupIndex',groupIndexParam)
      if(groupValueParam)state.set('groupValue',groupValueParam)
      return state
    },{replace:true})
  },[keyword,safeSort,safeConditions,safeColumnFilters,setSearchParams])
  const columnSettings=useQuery({queryKey:['workbench',moduleId,'column-editor'],queryFn:()=>apiClient.get<{current:ColumnSettings;defaults:ColumnSettings}>(`/document-workbench/${moduleId}/column-editor`),enabled:columnsOpen})
  const saveColumns=useMutation({mutationFn:(settings:{master:string[];detail:string[]})=>apiClient.put<void>(`/document-workbench/${moduleId}/columns`,{master:settings.master,detail:settings.detail}),onSuccess:async()=>{await Promise.all([queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']}),queryClient.invalidateQueries({queryKey:['workbench',moduleId,'column-editor']})])}})
  const columnGroups=useMemo<ColumnSelectorGroup[]>(()=>{
    const current=columnSettings.data?.current
    if(!current)return []
    const toGroup=(id:'master'|'detail',label:string):ColumnSelectorGroup=>({
      id,
      label,
      fields:current[id].map(item=>({key:item.key,label:item.label})),
      visibleKeys:current[id].filter(item=>item.isVisible).map(item=>item.key),
      defaultKeys:(columnSettings.data?.defaults[id]??[]).filter(item=>item.isVisible).map(item=>item.key),
    })
    const groups=[toGroup('master','主表字段')]
    if(definition.data?.detailTable)groups.push(toGroup('detail','子表字段'))
    return groups
  },[columnSettings.data,definition.data?.detailTable])

  const masterColumns=useMemo<ColumnDef<Record<string,unknown>,unknown>[]>(()=>[
    {
      id:'select',
      enableSorting:false,
      enableHiding:false,
      meta:{className:'erp-select-column',frozenLeft:true,resizable:false,truncate:false},
      header:({table})=>(
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={(input)=>{if(input)input.indeterminate=table.getIsSomePageRowsSelected()}}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell:({row})=>(
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event=>event.stopPropagation()}
        />
      ),
    },
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
        title:({value})=>formatFieldValue(value,field.dataType,field.format)||undefined,
        copyText:({value})=>formatFieldValue(value,field.dataType,field.format)||'—',
        headerMenu:[{label:'字段设置',onClick:()=>navigate(`/admin/fields/${encodeURIComponent(definition.data?.masterTable ?? '')}/${encodeURIComponent(field.key)}?moduleId=${moduleId}`)}],
      },
      cell:(info)=>{
        const value=info.getValue()
        if((field.dataType??'').toLowerCase()==='bit')return <input type="checkbox" className="form-check-input" checked={Boolean(value)} disabled aria-label={field.label}/>
        const text=formatFieldValue(value,field.dataType,field.format)
        if(!text)return '—'
        return field.browseModuleId&&field.browseModuleId>0
          ?<FieldBrowseLink value={text} browseModuleId={field.browseModuleId} browseKeyFields={field.browseKeyFields} row={info.row.original} fromModuleId={moduleId} canBrowse={hasPermission(moduleReadPermission(field.browseModuleId))}/>
          :text
      },
    })),
  ],[master,hasPermission,moduleId,navigate,definition.data?.masterTable])
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
      title:({value})=>formatFieldValue(value,field.dataType,field.format)||undefined,
      copyText:({value})=>formatFieldValue(value,field.dataType,field.format)||'—',
      headerMenu:[{label:'字段设置',onClick:()=>navigate(`/admin/fields/${encodeURIComponent(definition.data?.detailTable ?? '')}/${encodeURIComponent(field.key)}?moduleId=${moduleId}`)}],
    },
    cell:(info)=>{
      const value=info.getValue()
      if((field.dataType??'').toLowerCase()==='bit')return <input type="checkbox" className="form-check-input" checked={Boolean(value)} disabled aria-label={field.label}/>
      const text=formatFieldValue(value,field.dataType,field.format)
      if(!text)return '—'
      return field.browseModuleId&&field.browseModuleId>0
        ?<FieldBrowseLink value={text} browseModuleId={field.browseModuleId} browseKeyFields={field.browseKeyFields} row={info.row.original} fromModuleId={moduleId} canBrowse={hasPermission(moduleReadPermission(field.browseModuleId))}/>
        :text
    },
  })),[detail,hasPermission,moduleId,navigate,definition.data?.detailTable])
  const rowSelection=useMemo<RowSelectionState>(()=>Object.fromEntries(Object.keys(selected).map(key=>[key,true])),[selected])

  const rowKey=(row:Record<string,unknown>)=>{const keys=(definition.data?.masterPkOrder??[]).map(column=>String(row[column]??''));return keys.some(key=>key!=='')?keys.join('|'):JSON.stringify(row)}
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
  // 多槽队列：自适应列宽一次入队多列，逐列串行写回；单槽会被同步循环覆盖导致只保存最后一列
  const widthSaveQueue=useRef<{detail:boolean;fieldKey:string;width:number}[]>([])
  const widthSaveRunning=useRef(false)
  const writeFieldWidth=useCallback(async(detailTable:boolean,fieldKey:string,width:number)=>{
    const definitionData=definition.data;if(!definitionData)return
    const tableId=detailTable?definitionData.detailTable:definitionData.masterTable;if(!tableId)return
    const meta=await apiClient.get<FieldMetadata>(`/document-workbench/${moduleId}/field-settings/${encodeURIComponent(fieldKey)}`,{query:{detail:String(detailTable)}})
    const {key:_key,tableId:_tableId,isVirtual:_virtual,virtualExpression:_expression,isAutoIncrement:_auto,convertFunction:_convert,dataSourceSql:_sql,lastUpdatedBy:_by,lastUpdatedAt:_at,...input}=meta
    await apiClient.put<void>(`/document-workbench/${moduleId}/field-settings/${encodeURIComponent(fieldKey)}?detail=${detailTable}`,{...input,width,original:{...input,key:meta.key,width:meta.width}})
  },[definition.data,moduleId])
  const saveColumnWidth=useCallback(async(detailTable:boolean,fieldKey:string,width:number)=>{
    widthSaveQueue.current.push({detail:detailTable,fieldKey,width})
    if(widthSaveRunning.current)return
    widthSaveRunning.current=true
    try{
      while(widthSaveQueue.current.length>0){
        const target=widthSaveQueue.current.shift()!
        // 列宽钳制 [40,300]：与后端 WorkbenchFieldMetaMapper.FilterColumnWidths 同源（双端各自收紧，改需同步）
        const normalized=Math.min(300,Math.max(40,Math.round(target.width)))
        try{
          await writeFieldWidth(target.detail,target.fieldKey,normalized)
        }catch{
          // 并发/乐观锁冲突：重新拉取元数据重试一次
          try{
            await writeFieldWidth(target.detail,target.fieldKey,normalized)
          }catch(error2){
            window.alert(error2 instanceof Error?`保存列宽失败：${error2.message}`:'保存列宽失败。')
          }
        }
      }
      // 全部写回完成后再刷新一次定义，避免逐列刷新把未保存完的列重置回旧宽度
      try{await queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']})}catch{/* 忽略刷新失败 */}
    }finally{
      widthSaveRunning.current=false
    }
  },[writeFieldWidth,queryClient,moduleId])
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
  const handleColumnsReorder=useCallback(async(columnIds:string[])=>{
    if(!definition.data)return
    const master=columnIds.filter(id=>id!=='select')
    const detail=(definition.data.detailFields??[]).map(field=>field.key)
    try{
      await saveColumns.mutateAsync({master,detail})
    }catch(error){
      window.alert(error instanceof Error?`保存列顺序失败：${error.message}`:'保存列顺序失败。')
    }
  },[definition.data,saveColumns])
  if(definition.isPending)return <LoadingState label="正在加载单据定义…"/>
  if(definition.isError)return <section className="card"><div className="card-body text-center py-5">无法加载模块定义。</div></section>

  const detailSorting:SortingState=detailSort?[{id:detailSort.field,desc:detailSort.direction==='desc'}]:[]
  const changeKeyword=(value:string)=>{setKeyword(value)}
  const changeMasterSort=(next:SortingState)=>{setSort(next)}
  const changeDetailSort=(next:SortingState)=>{const first=next[0];setDetailSort(first?{field:first.id,direction:first.desc?'desc':'asc'}:null)}
  const handleRowSelectionChange=(next:RowSelectionState)=>{const selectedKeys=Object.keys(next).filter(key=>next[key]);setSelected(current=>{const result:Record<string,Record<string,unknown>>={};for(const key of selectedKeys){result[key]=current[key]??rows.find(row=>rowKey(row)===key)??{}}return result});if(selectedKeys.length===1){const only=selectedKeys[0];setActiveKey(only)}else if(selectedKeys.length===0){setActiveKey(null)}}
  const handleRowClick=(row:Record<string,unknown>)=>{const key=rowKey(row);setSelected({[key]:row});setActiveKey(key)}
  // Routing contract: NEW_URL/MODI_URL metadata wins when present; fall back to the unified form
  const openNew=()=>{if(!definition.data?.hasAdd)return;navigate(definition.data.newUrl??workbenchNew(moduleId))}
  const canOpenView=Boolean(definition.data?.hasEdit)
  // Double-click a master row to open browse mode (detail rows do not); query-only modules have no action.
  // Carries the current list display order (after sort/filter) as previous/next navigation context.
  const openViewFromRow=(row:Record<string,unknown>)=>{
    if(!definition.data||!canOpenView)return
    const key=definition.data.masterPkOrder.map(column=>String(row[column]??''))
    const navKeys=rows.map(item=>definition.data!.masterPkOrder.map(column=>String(item[column]??'')))
    const navIndex=navKeys.findIndex(candidate=>candidate.every((value,i)=>value===key[i]))
    navigate(workbenchView(moduleId,key),{state:{navKeys,navIndex}})
  }
  const openSearchCenter=()=>{navigate(`/search-center/${moduleId}`)}
  // FORM_BUTTONS business actions: action whitelist mirrors the server.
  // The list keeps only "new" plus list tools (export / query); document-level actions
  // (edit/copy/delete/approve/close/print...) live in the unified form browse toolbar,
  // and destructive actions require opening the browse view first.
  const businessItems:ErpCommandItem[]=(definition.data?.buttons&&definition.data.buttons.length>0
    ?definition.data.buttons
    :[{action:'new'},{action:'export'}]).map((button)=>({
    action:button.action,
    visible:(()=>{
      switch(button.action){
        case 'new':return definition.data?.hasAdd
        case 'export':return true
        case 'search':return Boolean(definition.data?.searchMaster||definition.data?.searchDetail)
        default:return false
      }
    })(),
    onClick:(()=>{
      switch(button.action){
        case 'new':return openNew
        case 'search':return openSearchCenter
        default:return undefined
      }
    })(),
    render:button.action==='export'?()=>exportButton:undefined,
    variant:button.action==='new'?'primary':undefined,
  }))
  const openGroupValues=async(group:NavigationGroupDef)=>{setActiveGroup(group);setGroupValues(null);try{const data=await apiClient.get<{values:string[]}>(`/navigation/${moduleId}/groups/${group.index}/values`);setGroupValues(data.values)}catch{setGroupValues([])}}
  const applyGroupValue=(value:string)=>{if(!activeGroup)return;setGroupMenuOpen(false);setSearchParams(current=>{current.set('groupIndex',String(activeGroup.index));current.set('groupValue',value);return current},{replace:true})}
  const groupQuery=groupIndex!=null&&groupValue!=null?{groupIndex,groupValue}:{}
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
  const handleExport=async(format:'csv'|'xls')=>{if(!definition.data)return;setExporting(true);try{const selectedIds=Object.keys(rowSelection).filter(id=>rowSelection[id]);const exportColumns=definition.data.masterFields.map(field=>field.key).join(',');const commonQuery={format,columns:exportColumns};const blob=selectedIds.length>0?await apiClient.postFile(`/document-workbench/${moduleId}/export-selected`,{keys:selectedIds.map(id=>{const row=selected[id];return definition.data!.masterPkOrder.map(column=>String(row?.[column]??''))})},{query:{...groupQuery,...commonQuery}}):await apiClient.postFile(`/document-workbench/${moduleId}/export`,{conditions:safeConditions},{query:{keyword:keyword||undefined,...sortQuery(safeSort),...groupQuery,...commonQuery}});const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=`${definition.data.title}.${format==='xls'?'xls':'csv'}`;document.body.appendChild(anchor);anchor.click();anchor.remove();URL.revokeObjectURL(url)}catch(error){window.alert(error instanceof Error?`导出失败：${error.message}`:'导出失败。')}finally{setExporting(false)}}
  const exportLabel=Object.keys(rowSelection).filter(id=>rowSelection[id]).length
  const exportDisabled=exportLabel===0
  const exportButton=<Button size="sm" className="erp-command-btn" icon={<IconFileExport size={16}/>} loading={exporting} disabled={exportDisabled} title={exportLabel?`导出所选 (${exportLabel})`:'请先选择要导出的行'} onClick={()=>void handleExport('csv')}>
    {exportLabel?`导出所选 (${exportLabel})`:'导出'}
  </Button>
  const recordsError=records.error instanceof ApiError?records.error.body.message:'发生未知错误，请稍后重试。'

  return <div className={`erp-workbench-page${definition.data.detailTable?'':' erp-workbench-single'}`}>
    <ErpListCard
      ariaLabel="单据列表查询与操作"
      search={<ErpSearchBox value={keyword} onChange={changeKeyword} debounceMs={400} placeholder="搜索单据、供应商或商品" ariaLabel="搜索" />}
      actions={<>
        <ErpCommandBar items={[
          {action:'query',onClick:()=>setQueryOpen(true),title:appliedConditions.length?`高级查询 (${appliedConditions.length})`:'高级查询'},
          {action:'columns',onClick:()=>{queryClient.removeQueries({queryKey:['workbench',moduleId,'column-editor']});setColumnsOpen(true)}},
          ...(groupDefs&&groupDefs.length>0?[{action:'group',render:()=>(
            <div className="dropdown erp-group-dropdown">
              <Button size="sm" className={`erp-command-icon-btn${groupMenuOpen?' show':''}`} icon={<IconZoomScan size={16}/>} aria-expanded={groupMenuOpen} title="分组" aria-label="分组" onClick={()=>setGroupMenuOpen(open=>!open)} />
              {groupMenuOpen&&(
                <div className="dropdown-menu dropdown-menu-end show" role="menu">
                  {activeGroup===null?groupDefs.map(group=>(
                    <button key={group.index} type="button" role="menuitem" className={`dropdown-item ${group.available?'':'disabled'}`} disabled={!group.available} onClick={()=>void openGroupValues(group)}>{group.description}</button>
                  )):(
                    <>
                      <button type="button" role="menuitem" className="dropdown-item" onClick={()=>setActiveGroup(null)}>← {activeGroup.description}</button>
                      <div className="dropdown-divider"/>
                      {groupValues===null
                        ?<div className="dropdown-item-text text-secondary">加载中…</div>
                        :groupValues.length===0
                          ?<div className="dropdown-item-text text-secondary">无分组数据</div>
                          :groupValues.map(value=>(
                            <button key={value} type="button" role="menuitem" className={`dropdown-item ${groupIndex===activeGroup.index&&groupValue===value?'active':''}`} onClick={()=>applyGroupValue(value)}>{value}</button>
                          ))}
                    </>
                  )}
                </div>
              )}
            </div>
          )}] satisfies ErpCommandItem[]:[]),
          {action:'fit',loading:fitting,onClick:()=>void fitAllColumns()},
          ...businessItems.filter(item=>item.action!=='new'),
          ...((definition.data?.searchMaster||definition.data?.searchDetail)&&!businessItems.some(item=>item.action==='search')
            ?[{action:'search',onClick:openSearchCenter}] satisfies ErpCommandItem[]
            :[]),
          {action:'refresh',onClick:()=>{void records.refetch();if(active)void details.refetch()}},
          {action:'help',onClick:()=>setHelpOpen(true)},
          ...businessItems.filter(item=>item.action==='new'),
        ]} />
      </>}
      footer={
        <div className="d-flex align-items-center gap-2 w-100">
          <span className="text-secondary small text-nowrap">共 {total} 条{rows.length < total ? `，已加载 ${rows.length} 条` : ''}</span>
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
    >
      {groupValue!=null&&(
        <div className="erp-active-group-filter">
          <IconZoomScan size={14} aria-hidden="true" />
          <span>分组筛选：{groupValue}</span>
          <button type="button" onClick={()=>{setSearchParams((current)=>{current.delete('groupIndex');current.delete('groupValue');return current},{replace:true})}}>清除分组</button>
        </div>
      )}
      <div className={`erp-master-table-region ${records.isFetching && !records.isFetchingNextPage ? 'is-loading' : ''}`}>
        {records.isPending?<LoadingState label="正在加载主表数据…"/>:records.isError?<ErrorState message={recordsError} onRetry={()=>void records.refetch()}/>:<ErpTable
          columns={masterColumns}
          data={rows}
          getRowId={rowKey}
          sorting={safeSort}
          onSortingChange={changeMasterSort}
          rowSelection={rowSelection}
          onRowSelectionChange={handleRowSelectionChange}
          onRowClick={handleRowClick}
          onRowDoubleClick={openViewFromRow}
          activeRowId={activeKey??undefined}
          virtualize
          resizable
          fitRef={masterFitRef}
          storageKey={`workbench-${moduleId}-master`}
          persistResize={false}
          onColumnResize={saveMasterWidth}
          columnFilterValue={columnFilters}
          onColumnFilterChange={handleColumnFilterChange}
          onColumnsReorder={handleColumnsReorder}
          empty={keyword?<EmptyState title="未找到匹配记录" description={`没有找到与“${keyword}”匹配的${definition.data.title}记录，可尝试其它关键词或清除搜索。`}/>:null}
          onEndReached={()=>void records.fetchNextPage()}
          hasMore={Boolean(records.hasNextPage)}
          loadingMore={records.isFetchingNextPage}
        />}
      </div>
    </ErpListCard>
    {definition.data.detailTable&&<section className="card erp-detail-card">
      <div className="card-header d-flex align-items-center gap-2 py-1">
        <span className="small fw-semibold text-secondary text-nowrap">明细</span>
        {active&&masterDescribe?(<div className="small text-truncate" title={masterDescribe}>{masterDescribe}</div>):(<span className="small text-secondary text-nowrap">选择主表行查看明细</span>)}
        {details.isFetching&&<span className="small text-secondary ms-auto text-nowrap">正在加载明细…</span>}
      </div>
      {details.isError?<div className="alert alert-danger d-flex align-items-center justify-content-between m-2 mb-0"><span>明细数据加载失败。</span><button type="button" className="btn btn-danger btn-sm" onClick={()=>void details.refetch()}>重新加载</button></div>:<ErpTable
      columns={detailColumns}
      data={active?details.data?.rows??[]:[]}
      sorting={detailSorting}
      onSortingChange={changeDetailSort}
      resizable
      fitRef={detailFitRef}
      storageKey={`workbench-${moduleId}-detail`}
      persistResize={false}
      onColumnResize={saveDetailWidth}
      className="table-sm"
      empty={null}
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
      onSave={async(selection)=>{await saveColumns.mutateAsync({master:selection.master??[],detail:selection.detail??[]});setColumnsOpen(false)}}
    />}
    {queryOpen&&<ErpQueryBuilder
      open
      fields={master.filter(field=>field.isQueryable).map(field=>({key:field.key,label:field.label}))}
      conditions={conditions}
      onChange={setConditions}
      onApply={()=>{setAppliedConditions(conditions);setQueryOpen(false)}}
      onClear={()=>{setConditions([emptyQueryCondition()]);setAppliedConditions([]);setColumnFilters({});setQueryOpen(false)}}
      onClose={()=>setQueryOpen(false)}
    />}
    {helpOpen&&<Modal title="列表操作帮助" onClose={()=>setHelpOpen(false)}>
      <ul className="mb-0 erp-help-list">
        <li><strong>选择 / 浏览：</strong>单击行选中并加载该行明细（首列复选框可多选）；双击行进入浏览页。</li>
        <li><strong>表头：</strong>点击右侧箭头排序或筛选；拖动表头文字调整列顺序；拖动列右边线调整宽度；双击列右边线自动适配列宽。</li>
        <li><strong>复制：</strong>选中行后按 Ctrl+C，或右键单元格选择“复制单元格 / 复制本行 / 复制选中行”（TSV，可直接粘贴进 Excel）。</li>
        <li><strong>键盘导航：</strong>↑ ↓ / Home / End / PageUp / PageDown 移动活动行，Enter 打开浏览。</li>
        <li><strong>加载更多：</strong>滚动到底部自动加载下一页；底部进度条显示已加载占比。</li>
      </ul>
    </Modal>}
  </div>
}

