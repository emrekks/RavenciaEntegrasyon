// TEMPORARY DIAGNOSTIC (remove after the standalone bottom-gap fix)
// Measures the phone shell's viewport, fixed layers and containing blocks on the real device. Read-only, no network, no personal data.

export type DiagnosticSnapshot = { menu: 'open' | 'closed'; values: Record<string, string> }

const fmt = (n: number | undefined | null, digits = 1) => typeof n === 'number' && Number.isFinite(n) ? String(Math.round(n * 10 ** digits) / 10 ** digits) : '?'

const rectText = (el: Element | null) => {
  if (!el) return 'yok'
  const r = el.getBoundingClientRect()
  return `${fmt(r.top)}/${fmt(r.bottom)}/${fmt(r.height)}`
}

// Ancestors whose style makes them the containing block of position: fixed descendants (or clip them).
const breakers = ['transform', 'translate', 'scale', 'rotate', 'filter', 'contain', 'willChange', 'backdropFilter', 'webkitBackdropFilter', 'perspective'] as const
const isActive = (value: string) => !(value === '' || value === 'none' || value === 'auto' || value === 'normal')
export function ancestorBreakers(el: Element | null, win: Window): string[] {
  const found: string[] = []
  for (let node = el?.parentElement ?? null; node; node = node.parentElement) {
    const style = win.getComputedStyle(node) as unknown as Record<string, string>
    for (const prop of breakers) {
      const value = style[prop] ?? ''
      if (isActive(value)) found.push(`${node.tagName.toLowerCase()}${node.id ? '#' + node.id : ''}${typeof node.className === 'string' && node.className ? '.' + node.className.trim().split(/\s+/)[0] : ''}:${prop}=${value.slice(0, 30)}`)
    }
  }
  return found
}

const layerText = (el: Element | null, win: Window, into: Record<string, string>, name: string) => {
  if (!el) { for (const part of ['css', 'inset', 'transform', 'ancestor']) into[`${name}.${part}`] = 'yok'; return }
  const s = win.getComputedStyle(el)
  into[`${name}.css`] = `pos=${s.position} top=${s.top} bottom=${s.bottom} h=${s.height}`
  into[`${name}.inset`] = s.inset || '-'
  into[`${name}.transform`] = `${s.transform} | translate=${s.translate}`
  const list = ancestorBreakers(el, win)
  into[`${name}.ancestor`] = list.length ? list.join(' ; ') : 'yok'
}

// CSS lengths resolved by the browser through a hidden probe element.
function probeLengths(doc: Document, win: Window) {
  const out: Record<string, string> = {}
  const probe = doc.createElement('div')
  probe.setAttribute('aria-hidden', 'true')
  probe.style.cssText = 'position:fixed;left:0;top:0;width:1px;visibility:hidden;pointer-events:none;'
  doc.body.appendChild(probe)
  try {
    for (const unit of ['100vh', '100svh', '100lvh', '100dvh']) {
      probe.style.height = unit
      out[unit] = fmt(probe.getBoundingClientRect().height)
    }
    probe.style.height = '0px'
    probe.style.padding = 'env(safe-area-inset-top, 0px) env(safe-area-inset-right, 0px) env(safe-area-inset-bottom, 0px) env(safe-area-inset-left, 0px)'
    const s = win.getComputedStyle(probe)
    out['safe T/R/B/L'] = [s.paddingTop, s.paddingRight, s.paddingBottom, s.paddingLeft].map(v => fmt(parseFloat(v))).join('/')
  } finally { probe.remove() }
  return out
}

export function collectSnapshot(doc: Document, win: Window): DiagnosticSnapshot {
  const menuEl = doc.querySelector('.rv-fullmenu')
  const v: Record<string, string> = {}
  const nav = win.navigator as Navigator & { standalone?: boolean }
  const ios = /OS (\d+[_.]\d+(?:[_.]\d+)?)/.exec(nav.userAgent)
  v.standalone = String(nav.standalone)
  v['display-mode'] = String(win.matchMedia?.('(display-mode: standalone)').matches)
  v.iOS = ios ? ios[1].replace(/_/g, '.') : '?'
  v.dpr = fmt(win.devicePixelRatio, 2)
  v['screen w/h/availH'] = `${win.screen.width}/${win.screen.height}/${win.screen.availHeight}`
  const html = doc.documentElement
  v['inner w/h'] = `${win.innerWidth}/${win.innerHeight}`
  v['html client w/h'] = `${html.clientWidth}/${html.clientHeight}`
  v['body clientH'] = String(doc.body.clientHeight)
  v['html scrollH'] = String(html.scrollHeight)
  v.scrollY = fmt(win.scrollY)
  const vv = win.visualViewport
  v['vv w/h'] = vv ? `${fmt(vv.width)}/${fmt(vv.height)}` : 'yok'
  v['vv offTop/pageTop/scale'] = vv ? `${fmt(vv.offsetTop)}/${fmt(vv.pageTop)}/${fmt(vv.scale, 2)}` : 'yok'
  Object.assign(v, probeLengths(doc, win))
  const dockLayer = doc.querySelector('.rv-dock-layer')
  const dock = doc.querySelector('.rv-dock')
  const rects: Array<[string, Element | null]> = [['html', html], ['body', doc.body], ['#root', doc.getElementById('root')], ['.app-shell', doc.querySelector('.app-shell')], ['menu', menuEl], ['menu body', doc.querySelector('.rv-fullmenu-body')], ['dock-layer', dockLayer], ['dock', dock]]
  for (const [name, el] of rects) v[`rect ${name} t/b/h`] = rectText(el)
  layerText(dockLayer, win, v, 'dock-layer')
  layerText(menuEl, win, v, 'menu')
  const hs = win.getComputedStyle(html)
  const bs = win.getComputedStyle(doc.body)
  v['html ovf/pos/h'] = `${hs.overflow}/${hs.position}/${hs.height}`
  v['body ovf/pos/h'] = `${bs.overflow}/${bs.position}/${bs.height}`
  v['html data'] = Object.entries(html.dataset).map(([k, val]) => `${k}=${val}`).join(' ') || '-'
  v['html class'] = html.className || '-'
  const gap = (el: Element | null, against: number | undefined) => el && typeof against === 'number' ? fmt(against - el.getBoundingClientRect().bottom) : '?'
  v['gapBelowDock vs inner/screen/vv'] = `${gap(dock, win.innerHeight)}/${gap(dock, win.screen.height)}/${gap(dock, vv?.height)}`
  v['gapBelowMenu vs inner/screen/vv'] = `${gap(menuEl, win.innerHeight)}/${gap(menuEl, win.screen.height)}/${gap(menuEl, vv?.height)}`
  return { menu: menuEl && !menuEl.classList.contains('is-closing') ? 'open' : 'closed', values: v }
}

// Open and closed side by side: "key: open | closed" (one screenshot or one copy carries both).
export function formatSideBySide(open: DiagnosticSnapshot | null, closed: DiagnosticSnapshot | null, now: 'open' | 'closed'): string {
  const keys = [...new Set([...Object.keys(open?.values ?? {}), ...Object.keys(closed?.values ?? {})])]
  const lines = [`menu=${now}   (acik | kapali)`]
  for (const key of keys) lines.push(`${key}: ${open?.values[key] ?? '-'} | ${closed?.values[key] ?? '-'}`)
  return lines.join('\n')
}

// Five taps within three seconds.
export function createTapCounter(count = 5, windowMs = 3000) {
  let taps: number[] = []
  return (now: number) => {
    taps = [...taps.filter(t => now - t <= windowMs), now]
    if (taps.length < count) return false
    taps = []
    return true
  }
}
