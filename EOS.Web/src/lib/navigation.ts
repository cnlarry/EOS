/**
 * 跨模块浏览的"精确回跳"地址。
 *
 * 问题：从 A 页面点开一个关联料号 → 跳到 B 模块的记录浏览 → 点返回，过去只能回到
 * **B 的来源模块列表**（`?from=1204` 只带模块号），而不是"A 页面上我刚才在看的那个节点"。
 *
 * 解法：链接带上 `back=<来源页完整地址含 query>`。工作区页签是按 URL 存取的，
 * 所以切回来源引址时，只要来源页把自己的视图状态也写进了 URL（筛选、展开、选中节点），
 * 位置就会原样恢复。
 *
 * **这里是开放重定向的唯一入口**，校验必须严：
 *  - 只接受以单个 `/` 开头的应用内相对路径；
 *  - 拒绝 `//host`（协议相对）、`/\host`（部分浏览器把反斜杠当斜杠）、带协议的形式；
 *  - 解码失败按"没有来源"处理，退回旧的 `from` 模块列表口径。
 */
export function safeBackPath(raw: string | null | undefined): string | null {
  if (!raw) return null
  let decoded: string
  try {
    decoded = decodeURIComponent(raw)
  } catch {
    return null
  }
  if (!decoded.startsWith('/')) return null
  if (decoded.startsWith('//') || decoded.startsWith('/\\')) return null
  if (decoded.includes('\\') || decoded.includes('://')) return null
  return decoded
}

/**
 * 把当前地址编码成 `back` 参数值。
 *
 * 用 `pathname + search` 而不是 `location.href`：只回跳应用内位置，不带 origin；
 * `hash` 不参与（页签按 path+search 存取）。
 */
export function encodeBackPath(pathname: string, search: string): string | null {
  // 首页 / 空路径不当作"来源页"：从那里点出去的链接带 `back=/` 没有意义，
  // 返回时只是回到一个空壳，还会让 URL 多一个看不懂的参数。
  // 返回 null 让链接退化成不带 `back` 的形态（目标页自然会用 `from` 兜底）。
  if (pathname === '' || pathname === '/') return null
  return encodeURIComponent(pathname + search)
}

/** 给目标地址追加 `back` 参数（目标本身可能已带 query，如 `?from=1204`）。 */
export function withBackPath(target: string, back: string | null): string {
  if (!back) return target
  return `${target}${target.includes('?') ? '&' : '?'}back=${back}`
}
