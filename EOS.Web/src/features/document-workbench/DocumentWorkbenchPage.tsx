import { IconAdjustmentsHorizontal, IconCheck, IconColumns, IconFileExport, IconLayoutRows, IconMinimize, IconPlus, IconPrinter, IconRefresh, IconRotateClockwise } from '@tabler/icons-react'
import { IconEdit } from '@tabler/icons-react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpQueryBuilder } from '../../components/common/ErpQueryBuilder'
import { emptyQueryCondition, type QueryCondition } from '../../components/common/queryCondition'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'
import { FieldEditorModal } from '../field-admin/FieldEditorModal'
import { alignClass, formatFieldValue } from './fieldFormat'
import { FieldBrowseLink } from './FieldBrowseLink'
import { readListState, writeListState } from './listStateUrl'

interface Field { key:string; label:string; dataType:string; width:number; align:string; isPrimaryKey:boolean; isQueryable:boolean; headerAlign:string; format:string|null; browseUrl:string|null; browseModuleId:number|null }
interface Definition { moduleId:number; title:string; masterTable:string; detailTable?:string; masterFields:Field[]; detailFields:Field[]; hasAdd:boolean; hasEdit:boolean; masterPkOrder:string[]; hasWorkflow:boolean }
interface DataResponse { rows:Record<string,unknown>[]; total:number; page:number; pageSize:number; totals?:Record<string,number|null> }
interface ColumnSetting { key:string; label:string; isVisible:boolean; order:number }
interface ColumnSettings { master:ColumnSetting[]; detail:ColumnSetting[] }
interface SetupLookup { value:string; label:string }
interface ChooserSource { active:boolean; table:string|null; description:string|null; moduleId:number|null; filter:string|null; returnMapping:string|null }
interface FieldMetadata { key:string; tableId:string; label:string; dataType:string; width:number; align:string; headerAlign:string; format:string|null; isVisible:boolean; isDefault:boolean; isQueryable:boolean; isReadonly:boolean; isRequired:boolean; isCost:boolean; isSecrecy:boolean; defaultValue:string|null; verifyIndex:number|null; regex:string|null; remark:string|null; browseUrl:string|null; browseModuleId:number|null; onlyChoose:boolean; chooseMultiple:boolean; choosePage:string|null; choosers:ChooserSource[]; isVirtual:boolean; virtualExpression:string|null; canCopy:boolean; isAutoIncrement:boolean; convertFunction:string|null; dataSourceSql:string|null; lastUpdatedBy:string|null; lastUpdatedAt:string|null }
const uniqueFields=(fields:Field[])=>fields.filter((field,index,all)=>all.findIndex(item=>item.key.toLowerCase()===field.key.toLowerCase())===index)
const renderText=(value:string)=>value.length>24?<span className="erp-cell-ellipsis" title={value}>{value}</span>:value
const sortQuery=(sort:SortingState)=>({sortFields:sort.length?sort.map(item=>item.id).join(','):undefined,sortDirections:sort.length?sort.map(item=>item.desc?'desc':'asc').join(','):undefined})
export function DocumentWorkbenchPage() {
  const { hasPermission } = useAuth()
  const navigate=useNavigate()
  const { moduleId='' }=useParams()
  const queryClient=useQueryClient()
  const [searchParams,setSearchParams]=useSearchParams()
  const [initialState]=useState(()=>readListState(searchParams))
  const [selected,setSelected]=useState<Record<string,Record<string,unknown>>>({})
  const [activeKey,setActiveKey]=useState<string|null>(null)
  const [page,setPage]=useState(initialState.page)
  const [sort,setSort]=useState<SortingState>(initialState.sort)
  const [detailSort,setDetailSort]=useState<{field:string;direction:'asc'|'desc'}|null>(null)
  const [queryOpen,setQueryOpen]=useState(false)
  const [columnsOpen,setColumnsOpen]=useState(false)
  const [pageSizePref,setPageSizePref]=useState<number|null>(initialState.pageSize)
  const [columnFilters,setColumnFilters]=useState<Record<string,QueryCondition>>(initialState.columnFilters)
  const [dense,setDense]=useState(false)
  const [fieldEditor,setFieldEditor]=useState<{detail:boolean;fieldKey:string}|null>(null)
  const [appliedConditions,setAppliedConditions]=useState<QueryCondition[]>(initialState.conditions)
  const [conditions,setConditions]=useState<QueryCondition[]>([emptyQueryCondition()])
  const [keyword,setKeyword]=useState(initialState.keyword)
  const [exporting,setExporting]=useState(false)
  const definition=useQuery({queryKey:['workbench',moduleId,'definition'],queryFn:()=>apiClient.get<Definition>(`/document-workbench/${moduleId}/definition`)})
  const pageSize=pageSizePref??(definition.data?.detailTable?10:16)
  const master=useMemo(()=>uniqueFields(definition.data?.masterFields??[]).slice(0,30),[definition.data])
  const detail=useMemo(()=>uniqueFields(definition.data?.detailFields??[]).slice(0,30),[definition.data])
  const allowedMasterKeys=useMemo(()=>new Set(master.map(field=>field.key.toLowerCase())),[master])
  const safeSort=useMemo(()=>sort.filter(item=>allowedMasterKeys.has(item.id.toLowerCase())),[sort,allowedMasterKeys])
  const safeConditions=useMemo(()=>appliedConditions.filter(item=>allowedMasterKeys.has(item.field.toLowerCase())),[appliedConditions,allowedMasterKeys])
  const safeColumnFilters=useMemo(()=>Object.fromEntries(Object.entries(columnFilters).filter(([key])=>allowedMasterKeys.has(key.toLowerCase()))),[columnFilters,allowedMasterKeys])
  const records=useQuery({queryKey:['workbench',moduleId,'records',page,pageSize,safeConditions,keyword,safeSort],queryFn:()=>{const sq=sortQuery(safeSort);return safeConditions.length?apiClient.post<DataResponse>(`/document-workbench/${moduleId}/query?page=${page}&pageSize=${pageSize}${keyword?`&keyword=${encodeURIComponent(keyword)}`:''}${sq.sortFields?`&sortFields=${encodeURIComponent(sq.sortFields)}&sortDirections=${encodeURIComponent(sq.sortDirections??'')}`:''}`,{conditions:safeConditions}):apiClient.get<DataResponse>(`/document-workbench/${moduleId}/records`,{query:{page,pageSize,keyword:keyword||undefined,...sq}})},enabled:definition.isSuccess,placeholderData:keepPreviousData})
  const totalPages=Math.max(1,Math.ceil((records.data?.total??0)/pageSize))
  const hydrated=useRef(false)
  useEffect(()=>{
    if(!hydrated.current){hydrated.current=true;return}
    setSearchParams(writeListState({page,pageSize:pageSizePref,keyword,sort:safeSort,conditions:safeConditions,columnFilters:safeColumnFilters}),{replace:true})
  },[page,pageSizePref,keyword,safeSort,safeConditions,safeColumnFilters,setSearchParams])
  useEffect(()=>{if(records.data&&page>totalPages)setPage(totalPages)},[page,totalPages,records.data])
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
      meta:{className:'erp-select-column',frozenLeft:true,resizable:false},
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
      enableSorting:true,
      meta:{
        className:alignClass(field.headerAlign),
        cellClassName:alignClass(field.align),
        minWidth:field.width,
        filterable:field.isQueryable,
        dataType:field.dataType,
        headerMenu:[{label:'字段设置',onClick:()=>setFieldEditor({detail:false,fieldKey:field.key})}],
      },
      cell:(info)=>{
        const value=info.getValue()
        if((field.dataType??'').toLowerCase()==='bit')return <input type="checkbox" className="form-check-input" checked={Boolean(value)} disabled aria-label={field.label}/>
        const text=formatFieldValue(value,field.dataType,field.format)
        if(!text)return '—'
        return field.browseUrl&&field.browseModuleId&&field.browseModuleId>0
          ?<FieldBrowseLink value={text} browseModuleId={field.browseModuleId} canBrowse={hasPermission(`legacy-module.${field.browseModuleId}.read`)}/>
          :renderText(text)
      },
    })),
  ],[master,hasPermission])
  const detailColumns=useMemo<ColumnDef<Record<string,unknown>,unknown>[]>(()=>detail.map((field):ColumnDef<Record<string,unknown>,unknown>=>({
    id:field.key,
    accessorKey:field.key,
    header:field.label,
    enableSorting:true,
    meta:{
      className:alignClass(field.headerAlign),
      cellClassName:alignClass(field.align),
      minWidth:field.width,
      dataType:field.dataType,
      headerMenu:[{label:'字段设置',onClick:()=>setFieldEditor({detail:true,fieldKey:field.key})}],
    },
    cell:(info)=>{
      const value=info.getValue()
      if((field.dataType??'').toLowerCase()==='bit')return <input type="checkbox" className="form-check-input" checked={Boolean(value)} disabled aria-label={field.label}/>
      const text=formatFieldValue(value,field.dataType,field.format)
      if(!text)return '—'
      return field.browseUrl&&field.browseModuleId&&field.browseModuleId>0
        ?<FieldBrowseLink value={text} browseModuleId={field.browseModuleId} canBrowse={hasPermission(`legacy-module.${field.browseModuleId}.read`)}/>
        :renderText(text)
    },
  })),[detail,hasPermission])
  const rowSelection=useMemo<RowSelectionState>(()=>Object.fromEntries(Object.keys(selected).map(key=>[key,true])),[selected])

  const rowKey=(row:Record<string,unknown>)=>{const keys=master.filter(field=>field.isPrimaryKey).map(field=>String(row[field.key]??''));return keys.length?keys.join('|'):JSON.stringify(row)}
  const active=activeKey?selected[activeKey]??records.data?.rows.find(row=>rowKey(row)===activeKey)??null:null
  const keys=useMemo(()=>master.filter(field=>field.isPrimaryKey).reduce<Record<string,string>>((result,field)=>{if(active?.[field.key]!=null)result[field.key]=String(active[field.key]);return result},{}),[active,master])
  const details=useQuery({queryKey:['workbench',moduleId,'details',keys,detailSort],queryFn:()=>apiClient.get<DataResponse>(`/document-workbench/${moduleId}/details`,{query:{...keys,sortField:detailSort?.field,sortDirection:detailSort?.direction}}),enabled:Boolean(active&&definition.data?.detailTable)})
  const widthSaveQueue=useRef<{detail:boolean;fieldKey:string;width:number}|null>(null)
  const widthSaveRunning=useRef(false)
  const writeFieldWidth=useCallback(async(detailTable:boolean,fieldKey:string,width:number)=>{
    const definitionData=definition.data;if(!definitionData)return
    const tableId=detailTable?definitionData.detailTable:definitionData.masterTable;if(!tableId)return
    const meta=await apiClient.get<FieldMetadata>(`/document-workbench/${moduleId}/field-settings/${encodeURIComponent(fieldKey)}`,{query:{detail:String(detailTable)}})
    const {key:_key,tableId:_tableId,isVirtual:_virtual,virtualExpression:_expression,isAutoIncrement:_auto,convertFunction:_convert,dataSourceSql:_sql,lastUpdatedBy:_by,lastUpdatedAt:_at,...input}=meta
    await apiClient.put<void>(`/document-workbench/${moduleId}/field-settings/${encodeURIComponent(fieldKey)}?detail=${detailTable}`,{...input,width,original:{...input,key:meta.key,width:meta.width}})
    await queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']})
  },[definition.data,moduleId,queryClient])
  const saveColumnWidth=useCallback(async(detailTable:boolean,fieldKey:string,width:number)=>{
    widthSaveQueue.current={detail:detailTable,fieldKey,width}
    if(widthSaveRunning.current)return
    widthSaveRunning.current=true
    try{
      while(widthSaveQueue.current){
        const target=widthSaveQueue.current
        widthSaveQueue.current=null
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
    }finally{
      widthSaveRunning.current=false
    }
  },[writeFieldWidth])
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
    setPage(1)
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

  const rows=records.data?.rows??[]
  const detailSorting:SortingState=detailSort?[{id:detailSort.field,desc:detailSort.direction==='desc'}]:[]
  const changeKeyword=(value:string)=>{setKeyword(value);setPage(1)}
  const changeMasterSort=(next:SortingState)=>{setSort(next);setPage(1)}
  const changeDetailSort=(next:SortingState)=>{const first=next[0];setDetailSort(first?{field:first.id,direction:first.desc?'desc':'asc'}:null)}
  const handleRowSelectionChange=(next:RowSelectionState)=>{const selectedKeys=Object.keys(next).filter(key=>next[key]);setSelected(current=>{const result:Record<string,Record<string,unknown>>={};for(const key of selectedKeys){result[key]=current[key]??records.data?.rows.find(row=>rowKey(row)===key)??{}}return result})}
  const handleRowClick=(row:Record<string,unknown>)=>{const key=rowKey(row);setSelected({[key]:row});setActiveKey(key)}
  const openEdit=()=>{if(!active||!definition.data)return;const key=definition.data.masterPkOrder.map(column=>String(active[column]??''));navigate(`/document-workbench/${moduleId}/edit?key=${encodeURIComponent(JSON.stringify(key))}`)}
  const openNew=()=>{if(!definition.data?.hasAdd)return;navigate(`/document-workbench/${moduleId}/new`)}
  const runWorkflow=async(approve:boolean)=>{if(!definition.data||!active)return;const key=definition.data.masterPkOrder.map(column=>String(active[column]??''));try{await apiClient.post(`/document-workbench/${moduleId}/${approve?'approve':'deapprove'}`,{key:JSON.stringify(key)});await queryClient.invalidateQueries({queryKey:['workbench',moduleId,'records']});if(activeKey)void details.refetch()}catch(error){window.alert(error instanceof Error?`${approve?'批核':'解批'}失败：${error.message}`:`${approve?'批核':'解批'}失败。`)}}
  const handleExport=async()=>{if(!definition.data)return;setExporting(true);try{const selectedIds=Object.keys(rowSelection).filter(id=>rowSelection[id]);const blob=selectedIds.length>0?await apiClient.postFile(`/document-workbench/${moduleId}/export-selected`,{keys:selectedIds.map(id=>{const row=selected[id];return definition.data!.masterPkOrder.map(column=>String(row?.[column]??''))})},{}):await apiClient.postFile(`/document-workbench/${moduleId}/export`,{conditions:safeConditions},{query:{keyword:keyword||undefined,...sortQuery(safeSort)}});const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=`${definition.data.title}.csv`;document.body.appendChild(anchor);anchor.click();anchor.remove();URL.revokeObjectURL(url)}catch(error){window.alert(error instanceof Error?`导出失败：${error.message}`:'导出失败。')}finally{setExporting(false)}}
  const recordsError=records.error instanceof ApiError?records.error.body.message:'发生未知错误，请稍后重试。'

  return <div className="erp-workbench-page">
    <ErpListCard
      ariaLabel="单据列表查询与操作"
      search={<ErpSearchBox value={keyword} onChange={changeKeyword} debounceMs={400} placeholder="搜索单据、供应商或商品" ariaLabel="搜索" />}
      actions={<>
        <Button size="sm" icon={<IconAdjustmentsHorizontal size={16}/>} onClick={()=>setQueryOpen(true)}>高级{appliedConditions.length?` (${appliedConditions.length})`:''}</Button>
        <Button size="sm" icon={<IconColumns size={16}/>} onClick={()=>{queryClient.removeQueries({queryKey:['workbench',moduleId,'column-editor']});setColumnsOpen(true)}}>选择列</Button>
        <Button size="sm" icon={<IconPlus size={16}/>} disabled={!definition.data?.hasAdd} onClick={openNew}>新增</Button>
        <Button size="sm" icon={<IconEdit size={16}/>} disabled={!definition.data?.hasEdit||!active} onClick={openEdit}>编辑</Button>
        {definition.data?.hasWorkflow&&<>
          <Button size="sm" icon={<IconCheck size={16}/>} disabled={!active} onClick={()=>void runWorkflow(true)}>批核</Button>
          <Button size="sm" icon={<IconRotateClockwise size={16}/>} disabled={!active} onClick={()=>void runWorkflow(false)}>解批</Button>
        </>}
        <Button size="sm" icon={<IconPrinter size={16}/>} onClick={()=>window.print()}>打印</Button>
        <Button size="sm" icon={<IconFileExport size={16}/>} loading={exporting} onClick={()=>void handleExport()}>{Object.keys(rowSelection).length?`导出所选 (${Object.keys(rowSelection).length})`:'导出'}</Button>
        <Button size="sm" icon={<IconRefresh size={16}/>} title="刷新" aria-label="刷新" onClick={()=>{void records.refetch();if(active)void details.refetch()}} />
        <Button size="sm" icon={dense?<IconMinimize size={16}/>:<IconLayoutRows size={16}/>} title={dense?'标准行高':'紧凑行高'} aria-label={dense?'标准行高':'紧凑行高'} onClick={()=>setDense(current=>!current)} />
      </>}
      footer={<ErpPagination total={records.data?.total??0} page={page} pageSize={pageSize} onPageChange={setPage} pageSizes={[10,16,25,50]} onPageSizeChange={(size)=>{setPageSizePref(size);setPage(1)}} />}
    >
      <div className={`erp-master-table-region ${records.isFetching?'is-loading':''}`}>
        {records.isPending?<LoadingState label="正在加载主表数据…"/>:records.isError?<ErrorState message={recordsError} onRetry={()=>void records.refetch()}/>:<ErpTable
          columns={masterColumns}
          data={rows}
          getRowId={rowKey}
          sorting={safeSort}
          onSortingChange={changeMasterSort}
          rowSelection={rowSelection}
          onRowSelectionChange={handleRowSelectionChange}
          onRowClick={handleRowClick}
          activeRowId={activeKey??undefined}
          resizable
          storageKey={`workbench-${moduleId}-master`}
          persistResize={false}
          onColumnResize={saveMasterWidth}
          columnFilterValue={columnFilters}
          onColumnFilterChange={handleColumnFilterChange}
          onColumnsReorder={handleColumnsReorder}
          dense={dense}
          totals={records.data?.totals}
          empty={null}
        />}
      </div>
    </ErpListCard>
    {definition.data.detailTable&&<section className="card erp-detail-card">{details.isError?<div className="alert alert-danger d-flex align-items-center justify-content-between m-2 mb-0"><span>明细数据加载失败。</span><button type="button" className="btn btn-danger btn-sm" onClick={()=>void details.refetch()}>重新加载</button></div>:<ErpTable
      columns={detailColumns}
      data={active?details.data?.rows??[]:[]}
      sorting={detailSorting}
      onSortingChange={changeDetailSort}
      resizable
      storageKey={`workbench-${moduleId}-detail`}
      persistResize={false}
      onColumnResize={saveDetailWidth}
      dense={dense}
      className="table-sm"
      empty={null}
    />}</section>}
    {fieldEditor&&<FieldEditorModal open mode="edit" tableId={(fieldEditor.detail?definition.data.detailTable:definition.data.masterTable)??''} fieldKey={fieldEditor.fieldKey} title={`${fieldEditor.detail?'子表':'主表'}字段设置`} endpoints={{load:async()=>{const meta=await apiClient.get<FieldMetadata>(`/document-workbench/${moduleId}/field-settings/${encodeURIComponent(fieldEditor.fieldKey)}`,{query:{detail:String(fieldEditor.detail)}});return{...meta,tableId:(fieldEditor.detail?definition.data.detailTable:definition.data.masterTable)??''}},save:async(input,_table,fieldId,original)=>apiClient.put<void>(`/document-workbench/${moduleId}/field-settings/${encodeURIComponent(fieldId)}?detail=${fieldEditor.detail}`,{...input,original:original?{...original,key:fieldId}:undefined}),tables:()=>apiClient.get<SetupLookup[]>(`/document-workbench/${moduleId}/field-settings/lookups/tables`),modules:()=>apiClient.get<SetupLookup[]>(`/document-workbench/${moduleId}/field-settings/lookups/modules`)}} onClose={()=>setFieldEditor(null)} onSaved={()=>{setFieldEditor(null);void Promise.all([queryClient.invalidateQueries({queryKey:['workbench',moduleId,'definition']}),queryClient.invalidateQueries({queryKey:['workbench',moduleId,'field-settings']})])}}/>}
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
      onApply={()=>{setAppliedConditions(conditions);setPage(1);setQueryOpen(false)}}
      onClear={()=>{setConditions([emptyQueryCondition()]);setAppliedConditions([]);setColumnFilters({});setPage(1);setQueryOpen(false)}}
      onClose={()=>setQueryOpen(false)}
    />}
  </div>
}
