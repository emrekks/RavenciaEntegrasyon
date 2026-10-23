import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { UiIcon, type UiIconName } from '../../shared/components'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { formatCount, formatNumber, formatPercent, platformName, unknownLabel, type ReportGroup, type ReportSales } from './report-format'
import type { MapRegion } from './sales-map-types'
import { otherZone, turkeyZones, worldZones, type MapZone } from './sales-map-regions'

type Scope = 'turkey' | 'world'
type Metric = 'revenue' | 'orders' | 'basket'
type Mode = 'fill' | 'bubble' | 'channel'
type Tab = 'rank' | 'zones' | 'platforms'
type MapData = { viewBox: string; regions: MapRegion[] }
// The amounts a metric is read from: an area, one platform's part of it, or a region's areas added up.
type Amounts = Pick<ReportGroup, 'orders' | 'ordersWithAmount' | 'netRevenue' | 'grossSales'>

// The outlines are loaded only when the map is shown, each scope on its own.
const loaders: Record<Scope, () => Promise<MapData>> = {
  turkey: () => import('./turkey-map').then(module => ({ viewBox: module.turkeyViewBox, regions: module.turkeyRegions })),
  world: () => import('./world-map').then(module => ({ viewBox: module.worldViewBox, regions: module.worldRegions }))
}

// The overview's platform colours.
const platformColors: Record<string, string> = { TRENDYOL: '#f27a1a', HEPSIBURADA: '#f5b301', SHOPIFY: '#5e8e3e', N11: '#7c5cff', PAZARAMA: '#2f9bd8', PTTAVM: '#1f3a8a' }
const platformColor = (code: string) => platformColors[code.toUpperCase()] ?? '#8a8f98'

const metricLabels: Record<Metric, string> = { revenue: 'Ciro', orders: 'Sipariş', basket: 'Sepet Ort.' }
const metricOptions: Array<SwitchOption<Metric>> = [
  { key: 'revenue', text: 'Ciro', icon: 'trend' }, { key: 'orders', text: 'Sipariş', icon: 'orders' }, { key: 'basket', text: 'Sepet Ort.', icon: 'bag' }
]
const lira = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 })
// The map's labels: whole liras, thousands kept short ("₺913", "₺55 B") so they fit their area.
const liraWhole = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 })
const liraShort = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', notation: 'compact', maximumFractionDigits: 0 })
const labelAmount = (value: number) => Math.abs(value) < 1000 ? liraWhole.format(value) : liraShort.format(value)
const provinceCount = 81

// A province name as the map and the report both write it, without Turkish letters and marks ("Hakkâri" = "Hakkari").
export function foldPlaceName(value: string) {
  return value.toLocaleLowerCase('tr-TR').replace(/ı/g, 'i').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z]/g, '')
}

const zoneByProvince = new Map(turkeyZones.flatMap(zone => zone.members.map(name => [foldPlaceName(name), zone.key] as const)))
const zoneByCountry = new Map(worldZones.flatMap(zone => zone.members.map(code => [code, zone.key] as const)))
const zoneOf = (scope: Scope, name: string, code: string) => scope === 'turkey' ? zoneByProvince.get(foldPlaceName(name)) ?? null : zoneByCountry.get(code) ?? otherZone.key

function metricValue(amounts: Amounts, metric: Metric): number | null {
  if (metric === 'orders') return amounts.orders
  if (amounts.ordersWithAmount === 0) return null
  return metric === 'revenue' ? amounts.netRevenue : amounts.grossSales / amounts.ordersWithAmount
}

function sum(items: Amounts[]): Amounts {
  return items.reduce((total, item) => ({ orders: total.orders + item.orders, ordersWithAmount: total.ordersWithAmount + item.ordersWithAmount, netRevenue: total.netRevenue + item.netRevenue, grossSales: total.grossSales + item.grossSales }),
    { orders: 0, ordersWithAmount: 0, netRevenue: 0, grossSales: 0 })
}

const formatMetric = (value: number | null, metric: Metric) => value === null ? 'Tutar yok' : metric === 'orders' ? formatCount(value) : lira.format(value)

// The change against the same area in the previous period; "Yeni" when it sold nothing then.
function change(now: number | null, before: number | null) {
  if (now === null) return null
  if (before === null || before === 0) return now > 0 ? { text: 'Yeni', tone: 'up' as const } : null
  const percent = Math.round((now - before) / before * 100)
  return { text: `${percent > 0 ? '+' : percent < 0 ? '−' : ''}%${Math.abs(percent)}`, tone: percent > 0 ? 'up' as const : percent < 0 ? 'down' as const : 'flat' as const }
}

// A part's share in a split: its metric, or its orders when amounts are missing (or the metric is an average).
const partWeight = (part: ReportGroup, metric: Metric) => metric !== 'revenue' || part.ordersWithAmount === 0 ? part.orders : Math.max(0, part.netRevenue)

// The platform with the largest share of the area's metric.
function dominant(group: ReportGroup, metric: Metric) {
  const parts = group.split.map(part => ({ code: part.key, value: partWeight(part, metric) }))
  const total = parts.reduce((all, part) => all + part.value, 0)
  const top = parts.reduce<{ code: string; value: number } | null>((best, part) => !best || part.value > best.value ? part : best, null)
  return top && total > 0 ? { code: top.code, share: top.value / total } : null
}

// The viewBox around the countries that sold (and Turkey), so a few nearby countries are not lost in the whole world.
function fitViewBox(full: string, points: Array<{ x: number; y: number }>) {
  if (points.length < 2) return full
  const xs = points.map(point => point.x), ys = points.map(point => point.y)
  const pad = 22
  let minX = Math.min(...xs) - pad, maxX = Math.max(...xs) + pad, minY = Math.min(...ys) - pad, maxY = Math.max(...ys) + pad
  const width = Math.max(150, maxX - minX, (maxY - minY) * 2)
  const height = width / 2
  const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2
  minX = cx - width / 2; maxX = cx + width / 2; minY = cy - height / 2; maxY = cy + height / 2
  return `${minX.toFixed(1)} ${minY.toFixed(1)} ${width.toFixed(1)} ${height.toFixed(1)}`
}

// The best-selling areas named on the map (the rest show their order count only).
const namedAreas = 8

// Shorter names on the map for countries whose full name would not fit beside their neighbours'.
const shortNames: Record<string, string> = { AE: 'BAE', US: 'ABD', GB: 'İngiltere', MK: 'K. Makedonya', BA: 'Bosna-Hersek' }

type LabelSpot = { key: string; x: number; y: number; name: string | null; count: string; dark: boolean; zone: string | null }

// One rule for every measure: each area that sold shows its value at its centre; the best sellers also get their name
// above it, when the name clears every value and the names placed before it (biggest areas first).
function placeLabels(items: Array<Omit<LabelSpot, 'name'> & { name: string; named: boolean }>, size: number): LabelSpot[] {
  type Box = [number, number, number, number]
  const hits = (box: Box, other: Box) => box[0] < other[2] && box[2] > other[0] && box[1] < other[3] && box[3] > other[1]
  const gap = size * 0.2
  const values = items.map(item => { const width = item.count.length * size * 0.6; return [item.x - width / 2 - gap, item.y - size * 0.45, item.x + width / 2 + gap, item.y + size * 0.45] as Box })
  const names: Box[] = []
  return items.map((item, index) => {
    if (!item.named) return { ...item, name: null }
    const width = item.name.length * size * 0.6
    const box: Box = [item.x - width / 2 - gap, item.y - size * 1.75, item.x + width / 2 + gap, item.y - size * 0.6]
    const free = !values.some((other, at) => at !== index && hits(box, other)) && !names.some(other => hits(box, other))
    if (!free) return { ...item, name: null }
    names.push(box)
    return { ...item, name: item.name }
  })
}

// view: the area's amounts on the map (one platform's part when a platform is picked); group: the whole area.
type Area = { key: string; group: ReportGroup; view: ReportGroup; value: number | null; previous: number | null; zone: string | null }

// Satış Haritası: Turkey's provinces or the countries orders went to, painted by revenue, orders or basket size, as
// circles, or by their leading platform. Above it the period in four figures; beside it the areas ranked, by region
// and by platform (a platform picked there repaints the map with its orders only), and a picked area's detail.
export function SalesMap({ data }: { data: ReportSales }) {
  const [scope, setScope] = useState<Scope>('turkey')
  const [metric, setMetric] = useState<Metric>('revenue')
  const [mode, setMode] = useState<Mode>('fill')
  const [tab, setTab] = useState<Tab>('rank')
  const [platform, setPlatform] = useState<string | null>(null)
  const [zone, setZone] = useState<string | null>(null)
  const [hoverZone, setHoverZone] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [hover, setHover] = useState<{ key: string; x: number; y: number } | null>(null)
  const [maps, setMaps] = useState<Partial<Record<Scope, MapData>>>({})
  const [failed, setFailed] = useState(false)
  const canvas = useRef<HTMLDivElement>(null)
  const map = maps[scope]

  useEffect(() => {
    if (maps[scope]) return
    let live = true
    loaders[scope]().then(loaded => { if (live) setMaps(current => ({ ...current, [scope]: loaded })) }, () => { if (live) setFailed(true) })
    return () => { live = false }
  }, [scope, maps])

  const groups = scope === 'turkey' ? data.cities.filter(group => group.known) : data.geography.countries.filter(group => group.key !== 'TR')
  const previousGroups = scope === 'turkey' ? data.geography.previousCities : data.geography.previousCountries
  const viewOf = (group: ReportGroup) => platform ? group.split.find(part => part.key === platform) : group
  const before = new Map(previousGroups.flatMap(group => { const view = viewOf(group); return view ? [[group.key, view] as const] : [] }))
  const areas: Area[] = groups.flatMap(group => {
    const view = viewOf(group)
    if (!view) return []
    const previous = before.get(group.key)
    return [{ key: group.key, group, view, value: metricValue(view, metric), previous: previous ? metricValue(previous, metric) : null, zone: zoneOf(scope, group.label, group.key) }]
  }).sort((a, b) => (b.value ?? -1) - (a.value ?? -1) || b.view.orders - a.view.orders)
  const max = Math.max(0, ...areas.map(area => area.value ?? 0))
  const byRegion = new Map(areas.map(area => [scope === 'turkey' ? foldPlaceName(area.group.label) : area.key, area]))
  const areaOf = (region: MapRegion) => byRegion.get(scope === 'turkey' ? foldPlaceName(region.name) : region.code)
  const picked = areas.find(area => area.key === selected) ?? null
  const hovered = hover ? areas.find(area => area.key === hover.key) ?? null : null
  const unknownCity = scope === 'turkey' ? data.cities.find(group => !group.known) : undefined
  const platforms = [...new Set(groups.flatMap(group => group.split.map(part => part.key)))]
  const focusZone = hoverZone ?? zone
  const zones = scope === 'turkey' ? turkeyZones : [...worldZones, otherZone]

  const viewBox = !map ? '0 0 100 60' : scope === 'world'
    ? fitViewBox(map.viewBox, map.regions.filter(region => region.code === 'TR' || byRegion.has(region.code)).map(region => ({ x: region.x, y: region.y })))
    : map.viewBox
  const unit = Number(viewBox.split(' ')[2]) / 1000
  const labelSize = (scope === 'turkey' ? 11.5 : 10.5) * unit
  const choose = (next: Scope) => { setScope(next); setSelected(null); setHover(null); setPlatform(null); setZone(null); setHoverZone(null) }
  const pickPlatform = (code: string | null) => { setPlatform(current => current === code ? null : code); setSelected(null) }
  const showTab = (next: Tab) => { setTab(next); setSelected(null) }
  const track = (key: string) => (event: ReactPointerEvent) => {
    const box = canvas.current?.getBoundingClientRect()
    if (box) setHover({ key, x: event.clientX - box.left, y: event.clientY - box.top })
  }
  // The card closes as soon as the pointer leaves the area (a move into the next area opens that one's).
  const leave = (key: string) => () => setHover(current => current?.key === key ? null : current)
  const toggle = (key: string) => () => setSelected(current => current === key ? null : key)

  const fill = (area: Area | undefined) => {
    if (!area || (area.value ?? 0) <= 0) return undefined
    if (mode === 'bubble') return 'color-mix(in srgb, var(--rv-color-info) 14%, var(--rv-color-surface-soft))'
    if (mode === 'channel') {
      const lead = platform ? { code: platform, share: Math.sqrt((area.value ?? 0) / max) } : dominant(area.view, metric)
      return lead ? `color-mix(in srgb, ${platformColor(lead.code)} ${Math.round(45 + lead.share * 55)}%, var(--rv-color-surface-soft))` : undefined
    }
    return `color-mix(in srgb, var(--rv-color-info) ${Math.round(24 + Math.sqrt((area.value ?? 0) / max) * 76)}%, var(--rv-color-surface-soft))`
  }
  const dark = (area: Area) => mode === 'fill' && max > 0 && Math.sqrt((area.value ?? 0) / max) > 0.62

  const regionOfArea = new Map(map ? map.regions.flatMap(region => { const area = areaOf(region); return area ? [[area.key, region] as const] : [] }) : [])
  const labels = placeLabels(areas.flatMap((area, index) => {
    const region = regionOfArea.get(area.key)
    if (!region || area.view.orders === 0 || area.value === null) return []
    return [{ key: area.key, x: region.x, y: region.y, name: shortNames[area.key] ?? area.group.label, count: metric === 'orders' ? formatCount(area.value) : labelAmount(area.value), dark: dark(area), zone: area.zone, named: index < namedAreas }]
  }), labelSize)

  return <section className="reports-panel reports-map-panel" aria-labelledby="reports-map-title">
    <header className="reports-map-head">
      <h2 id="reports-map-title">Satış Haritası</h2>
      <div className="reports-map-scope" role="tablist" aria-label="Harita">
        {([['turkey', 'Türkiye', 'pin'], ['world', 'Dünya · İhracat', 'globe']] as const).map(([key, text, icon]) =>
          <button key={key} type="button" role="tab" aria-selected={scope === key} onClick={() => choose(key)}><UiIcon name={icon} size={15} />{text}</button>)}
      </div>
      <div className="reports-map-switches">
        <MapSwitch label="Ölçü" value={metric} onChange={setMetric} options={metricOptions} />
        <MapSwitch label="Gösterim" value={mode} onChange={setMode} options={[
          { key: 'fill', text: scope === 'turkey' ? 'İl İl Boyalı' : 'Ülke Ülke Boyalı', icon: 'layers' }, { key: 'bubble', text: 'Daire', icon: 'target' }, { key: 'channel', text: 'Baskın Kanal', icon: 'platforms' }
        ]} />
      </div>
    </header>
    <Highlights areas={areas} scope={scope} metric={metric} previous={[...before.values()]} zones={zones} />
    <div className="reports-map-body">
      <div className="reports-map-canvas" ref={canvas} onPointerLeave={() => setHover(null)}>
        {failed && !map ? <p className="reports-empty">Harita yüklenemedi; sayfayı yenileyin.</p>
          : !map ? <div className="reports-map-loading" role="status">Harita yükleniyor…</div>
          : <svg viewBox={viewBox} role="img" aria-label={`${scope === 'turkey' ? 'Türkiye illeri' : 'Ülkeler'}: ${metricLabels[metric]} haritası; ayrıntılı liste yanda.`}>
            <g className="reports-map-regions">
              {map.regions.map(region => {
                const area = areaOf(region)
                const active = !!area && (area.value ?? 0) > 0
                const dim = focusZone !== null && zoneOf(scope, region.name, region.code) !== focusZone
                return <path key={region.code} d={region.d}
                  className={`${active ? 'is-active' : ''}${region.code === '00' ? ' is-decor' : ''}${dim ? ' is-dim' : ''}${area && area.key === selected ? ' is-selected' : ''}${area && hover?.key === area.key ? ' is-hover' : ''}`}
                  style={{ fill: fill(area) }}
                  onPointerMove={area ? track(area.key) : undefined}
                  onPointerLeave={area ? leave(area.key) : undefined}
                  onClick={area ? toggle(area.key) : undefined} />
              })}
            </g>
            {mode === 'bubble' && <g className="reports-map-bubbles">
              {map.regions.map(region => {
                const area = areaOf(region)
                if (!area || !area.value || area.value <= 0 || max <= 0) return null
                return <circle key={region.code} cx={region.x} cy={region.y} r={unit * (4 + Math.sqrt(area.value / max) * 30)}
                  className={area.key === selected ? 'is-selected' : undefined} onPointerMove={track(area.key)} onPointerLeave={leave(area.key)} onClick={toggle(area.key)} />
              })}
            </g>}
            <g className="reports-map-labels" aria-hidden="true">
              {labels.map(label => <text key={label.key} x={label.x} y={label.y} className={`${label.dark ? 'is-on-dark' : ''}${focusZone !== null && label.zone !== focusZone ? ' is-dim' : ''}` || undefined} style={{ fontSize: labelSize, strokeWidth: 1.8 * unit }}>
                {label.name && <tspan x={label.x} y={label.y - labelSize * 0.9} className="is-name">{label.name}</tspan>}
                <tspan x={label.x} y={label.y + labelSize * 0.35}>{label.count}</tspan>
              </text>)}
            </g>
          </svg>}
        {hovered && hover && <MapTooltip area={hovered} x={hover.x} y={hover.y} width={canvas.current?.clientWidth ?? 0} height={canvas.current?.clientHeight ?? 0} />}
        {map && <div className="reports-map-canvas-foot">
          <Legend mode={mode} metric={metric} max={max} platforms={platform ? [platform] : platforms} />
          {platform && <button type="button" className="reports-map-chip" onClick={() => pickPlatform(null)} aria-label={`${platformName(platform)} süzgecini kaldır`}>
            <i style={{ background: platformColor(platform) }} aria-hidden="true" />Yalnız {platformName(platform)}<UiIcon name="close" size={13} />
          </button>}
        </div>}
      </div>
      <aside className="reports-map-side" aria-label={scope === 'turkey' ? 'İllerin kırılımı' : 'Ülkelerin kırılımı'}>
        <div className="reports-map-tabs" role="tablist" aria-label="Kırılım">
          {([['rank', 'Sıralama'], ['zones', 'Bölgeler'], ['platforms', 'Platformlar']] as const).map(([key, text]) =>
            <button key={key} type="button" role="tab" aria-selected={tab === key && !picked} onClick={() => showTab(key)}>{text}</button>)}
        </div>
        <div className="reports-map-tabpanel" role="tabpanel">
          {picked ? <AreaDetail area={picked} scope={scope} metric={metric} platform={platform} onBack={() => setSelected(null)} />
            : tab === 'zones' ? <ZoneList areas={areas} zones={zones} scope={scope} metric={metric} focus={zone} onFocus={setHoverZone} onPick={key => setZone(current => current === key ? null : key)} />
            : tab === 'platforms' ? <PlatformList groups={groups} scope={scope} metric={metric} picked={platform} onPick={pickPlatform} />
            : <AreaList areas={areas} scope={scope} metric={metric} max={max} selected={selected} platform={platform} onPick={setSelected} />}
        </div>
      </aside>
    </div>
    <footer className="reports-map-foot">
      {scope === 'turkey' && unknownCity && <span>{formatCount(unknownCity.orders)} siparişte il okunamadı; haritada yok.</span>}
      {scope === 'turkey' && data.geography.abroadOrders > 0 && <span>{formatCount(data.geography.abroadOrders)} sipariş yurt dışına gitti; Dünya · İhracat sekmesinde.</span>}
      {scope === 'world' && <span>Trendyol yurt dışı vitrin siparişleri, adresi İstanbul'daki ihracat deposu olsa da gittiği ülkede gösterilir.</span>}
      <span className="reports-map-credit">Harita: {scope === 'turkey' ? 'svg-turkiye-haritasi (MIT)' : 'svg-maps (CC BY 4.0)'}</span>
    </footer>
  </section>
}

type SwitchOption<T extends string> = { key: T; text: string; icon: UiIconName }

// One button in the theme toggle's manner: its icon and the current choice; a click moves to the next one.
function MapSwitch<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: Array<SwitchOption<T>>; onChange: (value: T) => void }) {
  const index = Math.max(0, options.findIndex(option => option.key === value))
  const current = options[index], next = options[(index + 1) % options.length]
  return <button type="button" className="reports-map-switch" onClick={() => onChange(next.key)}
    aria-label={`${label}: ${current.text}; ${next.text} için tıklayın`} title={`${label}: ${current.text} → ${next.text}`}>
    <UiIcon name={current.icon} size={16} /><small>{label}</small><span>{current.text}</span>
  </button>
}

// The period on the map in four figures: the leading area, the top three's share, how much of the map sold and the
// change. Shares are of revenue (orders when the metric is the basket average, which does not add up).
function Highlights({ areas, scope, metric, previous, zones }: { areas: Area[]; scope: Scope; metric: Metric; previous: ReportGroup[]; zones: MapZone[] }) {
  const shareMetric: Metric = metric === 'basket' ? 'revenue' : metric
  const weight = (amounts: Amounts) => Math.max(0, metricValue(amounts, shareMetric) ?? 0)
  const ranked = [...areas].sort((a, b) => weight(b.view) - weight(a.view))
  const total = ranked.reduce((all, area) => all + weight(area.view), 0)
  const before = previous.reduce((all, group) => all + weight(group), 0)
  const top = ranked.slice(0, 3).filter(area => weight(area.view) > 0)
  const topShare = total > 0 ? top.reduce((all, area) => all + weight(area.view), 0) / total : null
  const sold = areas.filter(area => area.view.orders > 0).length
  const soldZones = new Set(areas.filter(area => area.view.orders > 0 && area.zone).map(area => area.zone)).size
  const delta = change(total, before)
  const lead = top[0]
  const noun = scope === 'turkey' ? 'il' : 'ülke'
  const ring = 2 * Math.PI * 15
  return <dl className="reports-map-strip">
    <div>
      <dt>{scope === 'turkey' ? 'Lider İl' : 'Lider Ülke'}</dt>
      <dd>{lead ? lead.group.label : '—'}</dd>
      <small>{lead ? `${formatMetric(weight(lead.view), shareMetric)} · ${formatPercent(total > 0 ? weight(lead.view) / total : null)}` : `Bu dönemde ${noun} yok`}</small>
    </div>
    <div>
      <dt>İlk 3 Payı</dt>
      <dd>{formatPercent(topShare)}</dd>
      <span className="reports-map-strip-bar" aria-hidden="true">{top.map((area, index) => <i key={area.key} className={`is-${index + 1}`} style={{ width: `${weight(area.view) / total * 100}%` }} />)}</span>
      <small title={top.map(area => area.group.label).join(', ')}>{top.length ? top.map(area => area.group.label).join(', ') : '—'}</small>
    </div>
    <div className="has-ring">
      {scope === 'turkey'
        ? <svg viewBox="0 0 40 40" className="reports-map-ring" aria-hidden="true"><circle cx="20" cy="20" r="15" /><circle cx="20" cy="20" r="15" className="is-value" strokeDasharray={`${sold / provinceCount * ring} ${ring}`} /></svg>
        : <span className="reports-map-ring-icon" aria-hidden="true"><UiIcon name="globe" size={20} /></span>}
      <dt>Kapsama</dt>
      <dd>{scope === 'turkey' ? `${formatCount(sold)} / ${provinceCount} il` : `${formatCount(sold)} ülke`}</dd>
      <small>{formatCount(soldZones)} / {zones.length} bölgede satış</small>
    </div>
    <div>
      <dt>Önceki Döneme Göre</dt>
      <dd className={delta ? `is-${delta.tone}` : undefined}>{delta?.text ?? '—'}</dd>
      <small>{metricLabels[shareMetric]}: {formatMetric(before, shareMetric)} → {formatMetric(total, shareMetric)}</small>
    </div>
  </dl>
}

function Legend({ mode, metric, max, platforms }: { mode: Mode; metric: Metric; max: number; platforms: string[] }) {
  if (mode === 'channel') return <ul className="reports-map-legend is-channels" aria-label="Baskın kanal renkleri">
    {platforms.map(code => <li key={code}><i style={{ background: platformColor(code) }} aria-hidden="true" />{platformName(code)}</li>)}
  </ul>
  if (max <= 0) return null
  return <div className="reports-map-legend" aria-label={`${metricLabels[metric]} ölçeği`}>
    <span>{metric === 'orders' ? '0' : lira.format(0)}</span>
    <i className={mode === 'bubble' ? 'is-bubble' : undefined} aria-hidden="true" />
    <span>{formatMetric(max, metric)}</span>
  </div>
}

// Beside the pointer, turned to the other side near the map's right or bottom edge and kept inside the map, which
// clips what runs out of it.
const tooltipWidth = 200
const tooltipHeight = 132
function MapTooltip({ area, x, y, width, height }: { area: Area; x: number; y: number; width: number; height: number }) {
  const delta = change(area.value, area.previous)
  const gap = 14
  const clamp = (value: number, size: number, room: number) => room > size + 16 ? Math.min(Math.max(8, value), room - size - 8) : value
  const left = clamp(width > 0 && x + gap + tooltipWidth > width - 8 ? x - gap - tooltipWidth : x + gap, tooltipWidth, width)
  const top = clamp(height > 0 && y + gap + tooltipHeight > height - 8 ? y - gap - tooltipHeight : y + gap, tooltipHeight, height)
  return <div className="reports-map-tooltip" style={{ left, top }} role="presentation">
    <strong>{area.group.label}</strong>
    <dl>
      <div><dt>Ciro</dt><dd>{formatMetric(metricValue(area.view, 'revenue'), 'revenue')}</dd></div>
      <div><dt>Sipariş</dt><dd>{formatCount(area.view.orders)}</dd></div>
      <div><dt>Sepet Ort.</dt><dd>{formatMetric(metricValue(area.view, 'basket'), 'basket')}</dd></div>
      {delta && <div><dt>Önceki Döneme Göre</dt><dd className={`is-${delta.tone}`}>{delta.text}</dd></div>}
    </dl>
  </div>
}

// A bar split by platform (one colour when the map shows one platform).
function SplitBar({ area, metric, width, platform }: { area: Area; metric: Metric; width: number; platform: string | null }) {
  const parts = platform ? [{ key: platform, weight: 1 }] : area.view.split.map(part => ({ key: part.key, weight: partWeight(part, metric) })).filter(part => part.weight > 0)
  return <span className="reports-map-bar" aria-hidden="true" style={{ width: `${width}%` }}>
    {parts.map(part => <i key={part.key} style={{ flexGrow: part.weight, background: platformColor(part.key) }} />)}
  </span>
}

// The areas ranked by the metric; each bar is split by platform.
function AreaList({ areas, scope, metric, max, selected, platform, onPick }: { areas: Area[]; scope: Scope; metric: Metric; max: number; selected: string | null; platform: string | null; onPick: (key: string) => void }) {
  if (areas.length === 0) return <p className="reports-empty">{platform ? `${platformName(platform)} için bu haritada sipariş yok.` : scope === 'turkey' ? 'Bu dönemde il bilgisi olan sipariş yok.' : 'Bu dönemde yurt dışına giden sipariş yok.'}</p>
  return <ol className="reports-map-list">{areas.map((area, index) => {
    const delta = change(area.value, area.previous)
    return <li key={area.key}>
      <button type="button" aria-pressed={selected === area.key} onClick={() => onPick(area.key)}>
        <span className="reports-map-rank">{index + 1}</span>
        <span className="reports-map-name">{area.group.label}</span>
        <strong>{formatMetric(area.value, metric)}</strong>
        <SplitBar area={area} metric={metric} platform={platform} width={max > 0 && area.value && area.value > 0 ? Math.max(4, area.value / max * 100) : 4} />
        <small>{formatCount(area.view.orders)} sipariş{delta && <> · <em className={`is-${delta.tone}`}>{delta.text}</em></>}</small>
      </button>
    </li>
  })}</ol>
}

// The regions (Turkey's seven, or parts of the world) with their areas added up; pointing at one dims the rest of the
// map, a click keeps it so.
function ZoneList({ areas, zones, scope, metric, focus, onFocus, onPick }: { areas: Area[]; zones: MapZone[]; scope: Scope; metric: Metric; focus: string | null; onFocus: (key: string | null) => void; onPick: (key: string) => void }) {
  const rows = zones.map(zone => {
    const members = areas.filter(area => area.zone === zone.key)
    const sold = members.filter(area => area.view.orders > 0)
    return { zone, value: metricValue(sum(members.map(area => area.view)), metric), sold: sold.length, lead: sold[0]?.group.label ?? null }
  }).filter(row => scope === 'turkey' || row.sold > 0).sort((a, b) => (b.value ?? -1) - (a.value ?? -1))
  const top = Math.max(0, ...rows.map(row => row.value ?? 0))
  if (rows.length === 0) return <p className="reports-empty">Bu dönemde bölgeye ayrılacak sipariş yok.</p>
  return <ol className="reports-map-list is-zones" onPointerLeave={() => onFocus(null)}>{rows.map(row => <li key={row.zone.key}>
    <button type="button" aria-pressed={focus === row.zone.key} className={row.sold === 0 ? 'is-empty' : undefined}
      onPointerEnter={() => onFocus(row.zone.key)} onFocus={() => onFocus(row.zone.key)} onBlur={() => onFocus(null)} onClick={() => onPick(row.zone.key)}>
      <span className="reports-map-name">{row.zone.label}</span>
      <strong>{formatMetric(row.value, metric)}</strong>
      <span className="reports-map-bar is-zone" aria-hidden="true"><i style={{ width: `${top > 0 && row.value && row.value > 0 ? Math.max(3, row.value / top * 100) : 0}%` }} /></span>
      <small>{scope === 'turkey' ? `${formatCount(row.sold)}/${row.zone.members.length} il` : `${formatCount(row.sold)} ülke`}{row.lead && ` · ${row.lead}`}</small>
    </button>
  </li>)}</ol>
}

// The platforms across the map, each with its share and its leading area; a click shows only its orders on the map.
function PlatformList({ groups, scope, metric, picked, onPick }: { groups: ReportGroup[]; scope: Scope; metric: Metric; picked: string | null; onPick: (code: string) => void }) {
  const codes = [...new Set(groups.flatMap(group => group.split.map(part => part.key)))]
  const rows = codes.map(code => {
    const parts = groups.flatMap(group => { const part = group.split.find(item => item.key === code); return part ? [{ group, part }] : [] })
    const lead = parts.reduce<typeof parts[number] | null>((best, item) => !best || partWeight(item.part, metric) > partWeight(best.part, metric) ? item : best, null)
    return { code, value: metricValue(sum(parts.map(item => item.part)), metric), weight: parts.reduce((all, item) => all + partWeight(item.part, metric), 0), areas: parts.length, lead: lead?.group.label ?? null }
  }).sort((a, b) => b.weight - a.weight)
  const total = rows.reduce((all, row) => all + row.weight, 0)
  if (rows.length === 0) return <p className="reports-empty">Bu dönemde platforma ayrılacak sipariş yok.</p>
  return <ol className="reports-map-list is-platforms">{rows.map(row => <li key={row.code}>
    <button type="button" aria-pressed={picked === row.code} onClick={() => onPick(row.code)} aria-label={`${platformName(row.code)}: haritada yalnız bu platform`}>
      <PlatformSquareMark code={row.code} name={platformName(row.code)} />
      <span className="reports-map-name">{platformName(row.code)}</span>
      <strong>{formatMetric(row.value, metric)}</strong>
      <span className="reports-map-bar" aria-hidden="true"><i style={{ width: `${total > 0 ? row.weight / total * 100 : 0}%`, flex: 'none', background: platformColor(row.code) }} /></span>
      <small>{formatPercent(total > 0 ? row.weight / total : null)} · {formatCount(row.areas)} {scope === 'turkey' ? 'il' : 'ülke'}{row.lead && ` · ${row.lead}`}</small>
    </button>
  </li>)}</ol>
}

function AreaDetail({ area, scope, metric, platform, onBack }: { area: Area; scope: Scope; metric: Metric; platform: string | null; onBack: () => void }) {
  const group = area.group
  const view = area.view
  const delta = change(area.value, area.previous)
  const parts = [...group.split].sort((a, b) => b.orders - a.orders)
  const districts = [...group.children].sort((a, b) => (metricValue(b, metric) ?? -1) - (metricValue(a, metric) ?? -1)).slice(0, 8)
  const districtMax = Math.max(0, ...districts.map(item => metricValue(item, metric) ?? 0))
  return <div className="reports-map-detail">
    <button type="button" className="reports-map-back" onClick={onBack}><UiIcon name="arrowLeft" size={14} />{scope === 'turkey' ? 'Tüm İller' : 'Tüm Ülkeler'}</button>
    <h3>{group.label}{platform && <small> · {platformName(platform)}</small>}</h3>
    <dl className="reports-map-stats">
      <div><dt>Ciro</dt><dd>{formatMetric(metricValue(view, 'revenue'), 'revenue')}</dd></div>
      <div><dt>Sipariş</dt><dd>{formatCount(view.orders)}</dd></div>
      <div><dt>Sepet Ort.</dt><dd>{formatMetric(metricValue(view, 'basket'), 'basket')}</dd></div>
      <div><dt>Satılan Adet</dt><dd>{formatNumber(view.soldQuantity)}</dd></div>
      <div><dt>İade Oranı</dt><dd>{formatPercent(view.returnRate)}</dd></div>
      <div><dt>Önceki Döneme Göre</dt><dd className={delta ? `is-${delta.tone}` : undefined}>{delta?.text ?? '—'}</dd></div>
    </dl>
    <h4>Platformlar</h4>
    <ul className="reports-map-parts">{parts.map(part => <li key={part.key} className={platform && part.key !== platform ? 'is-muted' : undefined}>
      <i style={{ background: platformColor(part.key) }} aria-hidden="true" /><span>{platformName(part.key)}</span>
      <strong>{formatCount(part.orders)} sipariş</strong><small>{formatMetric(metricValue(part, 'revenue'), 'revenue')}</small>
    </li>)}</ul>
    {scope === 'turkey' && districts.length > 0 && <>
      <h4>İlçeler{platform && <small> · tüm platformlar</small>}</h4>
      <ul className="reports-map-districts">{districts.map(item => {
        const value = metricValue(item, metric)
        return <li key={item.key} className={item.known ? undefined : 'is-unknown'}>
          <span>{item.known ? item.label : unknownLabel}</span>
          <span className="reports-map-district-bar" aria-hidden="true"><span style={{ width: `${districtMax > 0 && value ? Math.max(3, value / districtMax * 100) : 3}%` }} /></span>
          <strong>{formatMetric(value, metric)}</strong>
        </li>
      })}</ul>
    </>}
  </div>
}
