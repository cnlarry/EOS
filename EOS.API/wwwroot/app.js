const elements = Object.fromEntries([
  'identity','field','keyword','search','reset','error','masterCount','selectedProNo','selectedName',
  'detailCount','masterHead','masterBody','masterEmpty','detailHead','detailBody','detailEmpty','detailHint','loadingMaster','loadingDetail',
  'columns','columnBackdrop','columnDrawer','closeColumns','fieldList','restoreColumns','saveColumns',
  'adminFields','adminBackdrop','adminDrawer','closeAdmin','adminFieldList'
].map(id => [id, document.getElementById(id)]));

let masters = [];
let selected = '';
let fieldScope = 'master';
let fieldConfiguration = null;
let adminTable = 'BOM_STRU_M';
const formatter = new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 4 });

const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, char => ({
  '&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'
}[char]));
const number = value => value == null ? '—' : formatter.format(value);
const date = value => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—';

async function request(url) {
  const response = await fetch(url, { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail || problem?.title || `请求失败 (${response.status})`);
  }
  return response.json();
}

function showError(error) {
  elements.error.textContent = error.message || String(error);
  elements.error.classList.remove('hidden');
}
function clearError() { elements.error.classList.add('hidden'); }
function setLoading(element, active) { element.classList.toggle('hidden', !active); }

async function loadIdentity() {
  try {
    const identity = await request('/api/modules/1204/identity');
    elements.identity.textContent = `${identity.userId} · 模块 ${identity.moduleId}`;
    elements.adminFields.classList.toggle('hidden', !identity.canSetup);
  } catch (error) { elements.identity.textContent = '开发身份不可用'; showError(error); }
}

async function search() {
  clearError();
  setLoading(elements.loadingMaster, true);
  const query = new URLSearchParams({ field: elements.field.value, keyword: elements.keyword.value.trim(), limit: '200' });
  try {
    const result = await request(`/api/modules/1204/grid/boms?${query}`);
    masters = result.rows;
    selected = '';
    renderDynamicHead(elements.masterHead, result.columns);
    renderMasters(result.columns);
    clearDetails();
    elements.masterCount.textContent = result.count;
  } catch (error) { showError(error); }
  finally { setLoading(elements.loadingMaster, false); }
}

let masterColumns = [];

function renderDynamicHead(target, columns) {
  target.innerHTML = columns.map(column => `<th class="${isNumeric(column) ? 'number' : ''}">${escapeHtml(column.caption || column.sourceField)}</th>`).join('');
}

function renderValue(value, column) {
  if (value == null) return '—';
  if (column.dataType === 'bit') return value ? '是' : '否';
  if (column.dataType === 'datetime') return date(value);
  if (['int', 'float', 'decimal', 'numeric'].includes(column.dataType)) return number(value);
  return escapeHtml(value);
}

function isNumeric(column) { return ['int', 'float', 'decimal', 'numeric'].includes(column.dataType); }

function renderMasters(columns = masterColumns) {
  masterColumns = columns;
  elements.masterBody.innerHTML = masters.map(item => {
    const proNo = item['BOM_STRU_M^PRO_NO'];
    return `<tr data-pro-no="${escapeHtml(proNo)}" class="${proNo === selected ? 'selected' : ''}">${columns.map(column => `<td class="${isNumeric(column) ? 'number' : ''}" title="${escapeHtml(item[column.id])}">${renderValue(item[column.id], column)}</td>`).join('')}</tr>`;
  }).join('');
  elements.masterEmpty.classList.toggle('hidden', masters.length > 0);
  elements.masterBody.querySelectorAll('tr').forEach(row => row.addEventListener('click', () => selectBom(row.dataset.proNo)));
}

async function selectBom(proNo) {
  selected = proNo;
  renderMasters();
  const master = masters.find(item => item['BOM_STRU_M^PRO_NO'] === proNo);
  const proName = master?.['BOM_STRU_M^PRO_NAME'] || '';
  elements.selectedProNo.textContent = proNo;
  elements.selectedName.textContent = proName || '—';
  elements.detailHint.textContent = `${proNo} · ${proName}`;
  elements.detailBody.innerHTML = '';
  elements.detailEmpty.classList.add('hidden');
  setLoading(elements.loadingDetail, true);
  clearError();
  try {
    const result = await request(`/api/modules/1204/grid/boms/${encodeURIComponent(proNo)}/details`);
    renderDynamicHead(elements.detailHead, result.columns);
    renderDetails(result.rows, result.columns);
  } catch (error) { showError(error); elements.detailEmpty.classList.remove('hidden'); }
  finally { setLoading(elements.loadingDetail, false); }
}

function renderDetails(rows, columns) {
  elements.detailCount.textContent = rows.length;
  elements.detailBody.innerHTML = rows.map(item => `<tr>${columns.map(column => `<td class="${isNumeric(column) ? 'number' : ''}" title="${escapeHtml(item[column.id])}">${renderValue(item[column.id], column)}</td>`).join('')}</tr>`).join('');
  elements.detailEmpty.textContent = rows.length ? '' : '该 BOM 暂无组成明细';
  elements.detailEmpty.classList.toggle('hidden', rows.length > 0);
}

function clearDetails() {
  elements.selectedProNo.textContent = '未选择';
  elements.selectedName.textContent = '点击主表行查看明细';
  elements.detailCount.textContent = '—';
  elements.detailHint.textContent = '请先从主表选择一个料号';
  elements.detailBody.innerHTML = '';
  elements.detailEmpty.textContent = '选择主表记录后显示组成物料';
  elements.detailEmpty.classList.remove('hidden');
}

async function openColumns() {
  elements.columnBackdrop.classList.remove('hidden');
  elements.columnDrawer.classList.add('open');
  elements.columnDrawer.setAttribute('aria-hidden', 'false');
  await loadFieldConfiguration();
}

function closeColumns() {
  elements.columnBackdrop.classList.add('hidden');
  elements.columnDrawer.classList.remove('open');
  elements.columnDrawer.setAttribute('aria-hidden', 'true');
}

async function loadFieldConfiguration() {
  clearError();
  elements.fieldList.innerHTML = '<div class="empty">正在读取字段设置…</div>';
  try {
    fieldConfiguration = await request(`/api/modules/1204/fields?scope=${fieldScope}`);
    renderFieldConfiguration();
  } catch (error) { showError(error); elements.fieldList.innerHTML = '<div class="empty">字段设置读取失败</div>'; }
}

function renderFieldConfiguration() {
  const selectedFields = fieldConfiguration.selectedFields.map(field => ({ ...field, checked: true }));
  const availableFields = fieldConfiguration.availableFields.map(field => ({ ...field, checked: false }));
  const fields = [...selectedFields, ...availableFields];
  elements.fieldList.innerHTML = fields.map((field, index) => `
    <div class="field-item" data-id="${escapeHtml(field.id)}">
      <input type="checkbox" ${field.checked ? 'checked' : ''} aria-label="显示 ${escapeHtml(field.caption)}">
      <div class="field-meta"><strong>${escapeHtml(field.caption || field.sourceField)}</strong><small>${escapeHtml(field.sourceField)}</small>
        <div class="field-badges">${field.isVirtual ? '<span class="field-badge">虚拟字段</span>' : ''}${field.isCost ? '<span class="field-badge">成本</span>' : ''}${field.isSecrecy ? '<span class="field-badge">保密</span>' : ''}</div>
      </div>
      <div class="move-buttons"><button type="button" data-move="up" title="上移" ${index === 0 ? 'disabled' : ''}>↑</button><button type="button" data-move="down" title="下移" ${index === fields.length - 1 ? 'disabled' : ''}>↓</button></div>
    </div>`).join('');
  elements.fieldList.querySelectorAll('[data-move]').forEach(button => button.addEventListener('click', () => moveField(button.closest('.field-item'), button.dataset.move)));
}

function moveField(item, direction) {
  const sibling = direction === 'up' ? item.previousElementSibling : item.nextElementSibling;
  if (!sibling) return;
  if (direction === 'up') elements.fieldList.insertBefore(item, sibling);
  else elements.fieldList.insertBefore(sibling, item);
}

async function saveColumns() {
  const fieldIds = [...elements.fieldList.querySelectorAll('.field-item')]
    .filter(item => item.querySelector('input').checked)
    .map(item => item.dataset.id);
  try {
    const response = await fetch('/api/modules/1204/fields', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scope: fieldScope, fieldIds })
    });
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail || '字段设置保存失败');
    closeColumns();
    if (fieldScope === 'master') await search();
    else if (selected) await selectBom(selected);
  } catch (error) { showError(error); }
}

async function openAdmin() {
  elements.adminBackdrop.classList.remove('hidden'); elements.adminDrawer.classList.add('open');
  elements.adminDrawer.setAttribute('aria-hidden', 'false'); await loadAdminFields();
}
function closeAdmin() {
  elements.adminBackdrop.classList.add('hidden'); elements.adminDrawer.classList.remove('open');
  elements.adminDrawer.setAttribute('aria-hidden', 'true');
}
async function loadAdminFields() {
  elements.adminFieldList.innerHTML = '<div class="empty">正在读取全局字段…</div>';
  try {
    const fields = await request(`/api/modules/1204/admin/fields?table=${adminTable}`);
    elements.adminFieldList.innerHTML = fields.map(field => `
      <article class="admin-field" data-table="${escapeHtml(field.tableId)}" data-field="${escapeHtml(field.fieldId)}">
        <div class="admin-field-title"><strong>${escapeHtml(field.fieldId)}</strong><span>${field.virtual ? '虚拟字段' : '物理字段'}</span></div>
        <label>统一名称<input name="description" maxlength="300" value="${escapeHtml(field.description)}"></label>
        <label>虚拟表达式<textarea name="virtualExpression" maxlength="500" ${field.virtual ? '' : 'disabled'}>${escapeHtml(field.virtualExpression)}</textarea></label>
        <div class="admin-checks"><label><input name="visible" type="checkbox" ${field.visible ? 'checked' : ''}> 可见</label><label><input name="virtual" type="checkbox" ${field.virtual ? 'checked' : ''}> 虚拟</label><label><input name="cost" type="checkbox" ${field.cost ? 'checked' : ''}> 成本</label><label><input name="secrecy" type="checkbox" ${field.secrecy ? 'checked' : ''}> 保密</label></div>
        <button type="button" class="save-admin-field">保存此字段</button>
      </article>`).join('');
    elements.adminFieldList.querySelectorAll('[name=virtual]').forEach(box => box.addEventListener('change', () => { box.closest('.admin-field').querySelector('[name=virtualExpression]').disabled = !box.checked; }));
    elements.adminFieldList.querySelectorAll('.save-admin-field').forEach(button => button.addEventListener('click', () => saveAdminField(button.closest('.admin-field'))));
  } catch (error) { showError(error); elements.adminFieldList.innerHTML = '<div class="empty">无权访问或读取失败</div>'; }
}
async function saveAdminField(card) {
  const body = { tableId: card.dataset.table, fieldId: card.dataset.field,
    description: card.querySelector('[name=description]').value,
    visible: card.querySelector('[name=visible]').checked, virtual: card.querySelector('[name=virtual]').checked,
    virtualExpression: card.querySelector('[name=virtualExpression]').value || null,
    cost: card.querySelector('[name=cost]').checked, secrecy: card.querySelector('[name=secrecy]').checked,
    dataType: null, displayLength: null, displayFormat: null, headerAlign: null, itemAlign: null };
  try {
    const response = await fetch('/api/modules/1204/admin/fields', { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body) });
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail || '字段保存失败');
    await loadAdminFields(); await search();
  } catch (error) { showError(error); }
}

elements.search.addEventListener('click', search);
elements.keyword.addEventListener('keydown', event => { if (event.key === 'Enter') search(); });
elements.reset.addEventListener('click', () => { elements.field.value = 'proNo'; elements.keyword.value = ''; search(); });
elements.columns.addEventListener('click', openColumns);
elements.adminFields.addEventListener('click', openAdmin);
elements.closeAdmin.addEventListener('click', closeAdmin);
elements.adminBackdrop.addEventListener('click', closeAdmin);
document.querySelectorAll('.admin-tab').forEach(tab => tab.addEventListener('click', async () => {
  document.querySelectorAll('.admin-tab').forEach(item => item.classList.toggle('active', item === tab));
  adminTable = tab.dataset.table; await loadAdminFields();
}));
elements.closeColumns.addEventListener('click', closeColumns);
elements.columnBackdrop.addEventListener('click', closeColumns);
elements.saveColumns.addEventListener('click', saveColumns);
elements.restoreColumns.addEventListener('click', async () => {
  try {
    const response = await fetch('/api/modules/1204/fields', {
      method: 'DELETE', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scope: fieldScope })
    });
    if (!response.ok) throw new Error('恢复系统默认失败');
    await loadFieldConfiguration();
  } catch (error) { showError(error); }
});
document.querySelectorAll('.scope-tab').forEach(tab => tab.addEventListener('click', async () => {
  document.querySelectorAll('.scope-tab').forEach(item => item.classList.toggle('active', item === tab));
  fieldScope = tab.dataset.scope;
  await loadFieldConfiguration();
}));
loadIdentity();
search();
