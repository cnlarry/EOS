import { describe, expect, it } from 'vitest'
import { encodeBackPath, safeBackPath, withBackPath } from './navigation'

describe('safeBackPath', () => {
  it('接受应用内相对路径（含 query 与 hash）', () => {
    expect(safeBackPath('/bom/product/X/01/tree?node=/A1/')).toBe('/bom/product/X/01/tree?node=/A1/')
    expect(safeBackPath(encodeURIComponent('/workbench/1204?keyword=abc'))).toBe('/workbench/1204?keyword=abc')
  })

  it('缺省或解码失败返回 null', () => {
    expect(safeBackPath(null)).toBeNull()
    expect(safeBackPath('')).toBeNull()
    expect(safeBackPath('%E0%A4%A')).toBeNull()
  })

  it('拒绝协议相对与跨站地址（开放重定向）', () => {
    expect(safeBackPath(encodeURIComponent('//evil.example/x'))).toBeNull()
    expect(safeBackPath(encodeURIComponent('http://evil.example/x'))).toBeNull()
    expect(safeBackPath(encodeURIComponent('https://evil.example'))).toBeNull()
    expect(safeBackPath(encodeURIComponent('/\\evil.example'))).toBeNull()
  })

  it('拒绝非绝对路径（相对当前路径）', () => {
    expect(safeBackPath(encodeURIComponent('../../admin'))).toBeNull()
    expect(safeBackPath(encodeURIComponent('admin/users'))).toBeNull()
  })

  it('拒绝含反斜杠或内嵌协议的路径', () => {
    expect(safeBackPath(encodeURIComponent('/x\\y'))).toBeNull()
    expect(safeBackPath(encodeURIComponent('/x?a=http://evil'))).toBeNull()
  })
})

describe('encodeBackPath / withBackPath', () => {
  it('编码只含 path 与 search', () => {
    expect(encodeBackPath('/bom/product', '?a=1')).toBe(encodeURIComponent('/bom/product?a=1'))
  })

  it('目标无 query 时用 ? 拼接，有 query 时用 & 拼接', () => {
    expect(withBackPath('/workbench/1201/view/P001', 'b')).toBe('/workbench/1201/view/P001?back=b')
    expect(withBackPath('/workbench/1201/view/P001?from=1204', 'b'))
      .toBe('/workbench/1201/view/P001?from=1204&back=b')
  })

  it('没有 back 时原样返回', () => {
    expect(withBackPath('/workbench/1201/view/P001', null)).toBe('/workbench/1201/view/P001')
  })
})
