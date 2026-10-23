import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { UiIcon } from '../../shared/components'
import { formatCount, formatNumber, formatPercent, platformName, unknownLabel, type ReportGroup, type ReportSales } from './report-format'
import type { MapRegion } from './sales-map-types'

type Scope = 'turkey' | 'world'
type Metric = 'revenue' | 'orders' | 'basket'
type Mode = 'fill' | 'bubble' | 'channel'
type MapData = { viewBox: string; regions: MapRegion[] }

// The outlines are loaded only when the map is shown, each scope on its own.
const loaders: Record<Scope, () => Promise<MapData>> = {
  turkey: () => import('./turkey-map').then(module => ({ viewBox: module.turkeyViewBox, regions: module.turkeyRegions })),
  world: () => import('./world-map').then(module => ({ viewBox: module.worldViewBox, regions: module.worldRegions }))
}

// The overview's platform colours.
const platformColors: Record<string, string> = { TRENDYOL: '#f27a1a', HEPSIBURADA: '#f5b301', SHOPIFY: '#5e8e3e', N11: '#7c5cff', PAZARAMA: '#2f9bd8', PTTAVM: '#1f3a8a' }
const platformColor = (code: string) => platformColors[code.toUpperCase()] ?? '#8a8f98'

const metricLabels: Record<Metric, string> = { revenue: 'Ciro', orders: 'Sipariş', basket: 'Sepet Ort.' }
const lira = new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 })

// A province name as the map and the report both write it, without Turkish letters and marks ("Hakkâri" = "Hakkari").
export function foldPlaceName(value: string) {
  return value.toLocaleLowerCase('tr-TR').replace(/ı/g, 'i').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z]/g, '')
}

function metricValue(group: ReportGroup, metric: Metric): number | null {
  if (metric === 'orders') return group.orders
  if (group.ordersWithAmount === 0) return null
  return metric === 'revenue' ? group.netRevenue : group.grossSales / group.ordersWithAmount
}

const formatMetric = (value: number | null, metric: Metric) => value === null ? 'Tutar yok' : metric === 'orders' ? formatCount(value) : lira.format(value)

// The change against the same area in the previous period; "Yeni" when it sold nothing then.
function change(now: number | null, before: number | null) {
  if (now === null) return null
  if (before === null || before === 0) return now > 0 ? { text: 'Yeni', tone: 'up' as const } : null
  const rate = (now - before) / before
  return { text: `${rate >= 0 ? '+' : '−'}%${Math.abs(Math.round(rate * 100))}`, tone: rate > 0.005 ? 'up' as const : rate < -0.005 ? 'down' as const : 'flat' as const }
}

// The platform with the largest share of the area's metric (orders when amounts are missing).
function dominant(group: ReportGroup, metric: Metric) {
  const parts = group.split.map(part => ({ code: part.key, value: metric === 'orders' || part.ordersWithAmount === 0 ? part.orders : part.netRevenue }))
  const total = parts.reduce((sum, part) => sum + part.value, 0)
  const top = parts.reduce<{ code: string; value: number } | null>((best, part) => !best || part.value > best.value ? part : best, null)
  return top && total > 0 ? { code: top.code, share: top.value / total } : null
}

// The viewBox around the countries that sold (and Turkey), so a few nearby countries are not lost in the whole world.
function fitViewBox(full: string, points: Array<{ x: number; y: number }>) {
  if (points.length < 2) return full
  const xs = points.map(point => point.x), ys = points.map(point => point.y)
  const pad = 40
  let minX = Math.min(...xs) - pad, maxX = Math.max(...xs) + pad, minY = Math.min(...ys) - pad, maxY = Math.max(...ys) + pad
  const width = Math.max(220, maxX - minX, (maxY - minY) * 2)
  const height = width / 2
  const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2
  minX = cx - width / 2; maxX = cx + width / 2; minY = cy - height / 2; maxY = cy + height / 2
  return `${minX.toFixed(1)} ${minY.toFixed(1)} ${width.toFixed(1)} ${height.toFixed(1)}`
}

type Area = { key: string; group: ReportGroup; value: number | null; previous: number | null }

// Satış Haritası: Turkey's provinces or the countries orders went to, painted by revenue, orders or basket size, as
// circles, or by their leading platform; beside it the areas ranked, and an area's detail when one is picked.
export function SalesMap({ data }: { data: ReportSales }) {
  const [scope, setScope] = useState<Scope>('turkey')
  const [metric, setMetric] = useState<Metric>('revenue')
  const [mode, setMode] = useState<Mode>('fill')
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
  const before = new Map(previousGroups.map(group => [group.key, group]))
  const areas: Area[] = groups.map(group => {
    const previous = before.get(group.key)
    return { key: group.key, group, value: metricValue(group, metric), previous: previous ? metricValue(previous, metric) : null }
  }).sort((a, b) => (b.value ?? -1) - (a.value ?? -1) || b.group.orders - a.group.orders)
  const max = Math.max(0, ...areas.map(area => area.value ?? 0))
  const byRegion = new Map(areas.map(area => [scope === 'turkey' ? foldPlaceName(area.group.label) : area.key, area]))
  const areaOf = (region: MapRegion) => byRegion.get(scope === 'turkey' ? foldPlaceName(region.name) : region.code)
  const labelled = new Set(areas.filter(area => (area.value ?? 0) > 0).slice(0, scope === 'turkey' ? 6 : 3).map(area => area.key))
  const picked = areas.find(area => area.key === selected) ?? null
  const hovered = hover ? areas.find(area => area.key === hover.key) ?? null : null
  const unknownCity = scope === 'turkey' ? data.cities.find(group => !group.known) : undefined
  const platforms = [...new Set(areas.flatMap(area => area.group.split.map(part => part.key)))]

  const viewBox = !map ? '0 0 100 60' : scope === 'world'
    ? fitViewBox(map.viewBox, map.regions.filter(region => region.code === 'TR' || byRegion.has(region.code)).map(region => ({ x: region.x, y: region.y })))
    : map.viewBox
  const unit = Number(viewBox.split(' ')[2]) / 1000
  const choose = (next: Scope) => { setScope(next); setSelected(null); setHover(null) }
  const track = (key: string) => (event: ReactPointerEvent) => {
    const box = canvas.current?.getBoundingClientRect()
    if (box) setHover({ key, x: event.clientX - box.left, y: event.clientY - box.top })
  }

  const fill = (area: Area | undefined) => {
    if (!area || (area.value ?? 0) <= 0) return undefined
    if (mode === 'bubble') return 'color-mix(in srgb, var(--rv-color-info) 14%, var(--rv-color-surface-soft))'
    if (mode === 'channel') {
      const lead = dominant(area.group, metric)
      return lead ? `color-mix(in srgb, ${platformColor(lead.code)} ${Math.round(45 + lead.share * 55)}%, var(--rv-color-surface-soft))` : undefined
    }
    return `color-mix(in srgb, var(--rv-color-info) ${Math.round(24 + Math.sqrt((area.value ?? 0) / max) * 76)}%, var(--rv-color-surface-soft))`
  }
  const dark = (area: Area | undefined) => mode === 'fill' && !!area && max > 0 && Math.sqrt((area.value ?? 0) / max) > 0.62

  return <section className="reports-panel reports-map-panel" aria-labelledby="reports-map-title">
    <header className="reports-map-head">
      <h2 id="reports-map-title">Satış Haritası</h2>
      <div className="reports-map-controls">
        <Segmented label="Harita" value={scope} onChange={choose} options={[['turkey', 'Türkiye'], ['world', 'Dünya · İhracat']]} />
        <Segmented label="Ölçü" value={metric} onChange={setMetric} options={(Object.keys(metricLabels) as Metric[]).map(key => [key, metricLabels[key]])} />
        <Segmented label="Gösterim" value={mode} onChange={setMode} options={[['fill', scope === 'turkey' ? 'İl İl Boyalı' : 'Ülke Ülke Boyalı'], ['bubble', 'Daire'], ['channel', 'Baskın Kanal']]} />
      </div>
      <p className="reports-map-hint">{scope === 'turkey' ? 'Bir ile tıklayın, ayrıntısı yanda açılır' : 'Bir ülkeye tıklayın, ayrıntısı yanda açılır'}</p>
    </header>
    <div className="reports-map-body">
      <div className="reports-map-canvas" ref={canvas} onPointerLeave={() => setHover(null)}>
        {failed && !map ? <p className="reports-empty">Harita yüklenemedi; sayfayı yenileyin.</p>
          : !map ? <div className="reports-map-loading" role="status">Harita yükleniyor…</div>
          : <svg viewBox={viewBox} role="img" aria-label={`${scope === 'turkey' ? 'Türkiye illeri' : 'Ülkeler'}: ${metricLabels[metric]} haritası; ayrıntılı liste yanda.`}>
            <g className="reports-map-regions">
              {map.regions.map(region => {
                const area = areaOf(region)
                const active = !!area && (area.value ?? 0) > 0
                return <path key={region.code} d={region.d}
                  className={`${active ? 'is-active' : ''}${region.code === '00' ? ' is-decor' : ''}${area && area.key === selected ? ' is-selected' : ''}${area && hover?.key === area.key ? ' is-hover' : ''}`}
                  style={{ fill: fill(area) }}
                  onPointerMove={area ? track(area.key) : undefined}
                  onClick={area ? () => setSelected(current => current === area.key ? null : area.key) : undefined} />
              })}
            </g>
            {mode === 'bubble' && <g className="reports-map-bubbles">
              {map.regions.map(region => {
                const area = areaOf(region)
                if (!area || !area.value || max <= 0) return null
                return <circle key={region.code} cx={region.x} cy={region.y} r={unit * (4 + Math.sqrt(area.value / max) * 30)}
                  className={area.key === selected ? 'is-selected' : undefined} onPointerMove={track(area.key)} onClick={() => setSelected(current => current === area.key ? null : area.key)} />
              })}
            </g>}
            <g className="reports-map-labels" aria-hidden="true">
              {map.regions.map(region => {
                const area = areaOf(region)
                if (!area || area.group.orders === 0) return null
                const named = labelled.has(area.key)
                const size = 12 * unit
                return <text key={region.code} x={region.x} y={region.y} className={dark(area) ? 'is-on-dark' : undefined} style={{ fontSize: size }}>
                  {named && <tspan x={region.x} dy={-size * 0.35} className="is-name">{area.group.label}</tspan>}
                  <tspan x={region.x} dy={named ? size * 1.15 : size * 0.35}>{formatCount(area.group.orders)}</tspan>
                </text>
              })}
            </g>
          </svg>}
        {hovered && hover && <MapTooltip area={hovered} x={hover.x} y={hover.y} width={canvas.current?.clientWidth ?? 0} />}
        {map && <Legend mode={mode} metric={metric} max={max} platforms={platforms} />}
      </div>
      <aside className="reports-map-side" aria-label={scope === 'turkey' ? 'En çok satan iller' : 'En çok satan ülkeler'}>
        {picked ? <AreaDetail area={picked} scope={scope} metric={metric} onBack={() => setSelected(null)} />
          : <AreaList areas={areas} scope={scope} metric={metric} max={max} selected={selected} onPick={setSelected} />}
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

function Segmented<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: Array<[T, string]>; onChange: (value: T) => void }) {
  return <div className="reports-map-segmented" role="group" aria-label={label}>
    {options.map(([key, text]) => <button key={key} type="button" aria-pressed={value === key} onClick={() => onChange(key)}>{text}</button>)}
  </div>
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

function MapTooltip({ area, x, y, width }: { area: Area; x: number; y: number; width: number }) {
  const delta = change(area.value, area.previous)
  const left = width > 0 && x > width - 220 ? x - 212 : x + 14
  return <div className="reports-map-tooltip" style={{ left, top: y + 14 }} role="presentation">
    <strong>{area.group.label}</strong>
    <dl>
      <div><dt>Ciro</dt><dd>{formatMetric(metricValue(area.group, 'revenue'), 'revenue')}</dd></div>
      <div><dt>Sipariş</dt><dd>{formatCount(area.group.orders)}</dd></div>
      <div><dt>Sepet Ort.</dt><dd>{formatMetric(metricValue(area.group, 'basket'), 'basket')}</dd></div>
      {delta && <div><dt>Önceki Döneme Göre</dt><dd className={`is-${delta.tone}`}>{delta.text}</dd></div>}
    </dl>
  </div>
}

// The areas ranked by the metric; each bar is split by platform.
function AreaList({ areas, scope, metric, max, selected, onPick }: { areas: Area[]; scope: Scope; metric: Metric; max: number; selected: string | null; onPick: (key: string) => void }) {
  const title = scope === 'turkey' ? 'En Çok Satan İller' : 'En Çok Satan Ülkeler'
  return <>
    <div className="reports-map-side-head"><h3>{title}</h3><span>{formatCount(areas.length)} {scope === 'turkey' ? 'il' : 'ülke'}</span></div>
    {areas.length === 0 ? <p className="reports-empty">{scope === 'turkey' ? 'Bu dönemde il bilgisi olan sipariş yok.' : 'Bu dönemde yurt dışına giden sipariş yok.'}</p>
      : <ol className="reports-map-list">{areas.map((area, index) => {
        const delta = change(area.value, area.previous)
        const total = area.group.split.reduce((sum, part) => sum + (metric === 'orders' || part.ordersWithAmount === 0 ? part.orders : part.netRevenue), 0)
        return <li key={area.key}>
          <button type="button" aria-pressed={selected === area.key} onClick={() => onPick(area.key)}>
            <span className="reports-map-rank">{index + 1}</span>
            <span className="reports-map-name">{area.group.label}</span>
            <strong>{formatMetric(area.value, metric)}</strong>
            <span className="reports-map-bar" aria-hidden="true" style={{ width: `${max > 0 && area.value ? Math.max(4, area.value / max * 100) : 4}%` }}>
              {area.group.split.map(part => {
                const value = metric === 'orders' || part.ordersWithAmount === 0 ? part.orders : part.netRevenue
                return total > 0 && value > 0 ? <i key={part.key} style={{ flexGrow: value, background: platformColor(part.key) }} /> : null
              })}
            </span>
            <small>{formatCount(area.group.orders)} sipariş{delta && <> · <em className={`is-${delta.tone}`}>{delta.text}</em></>}</small>
          </button>
        </li>
      })}</ol>}
  </>
}

function AreaDetail({ area, scope, metric, onBack }: { area: Area; scope: Scope; metric: Metric; onBack: () => void }) {
  const group = area.group
  const delta = change(area.value, area.previous)
  const parts = [...group.split].sort((a, b) => b.orders - a.orders)
  const districts = [...group.children].sort((a, b) => (metricValue(b, metric) ?? -1) - (metricValue(a, metric) ?? -1)).slice(0, 8)
  const districtMax = Math.max(0, ...districts.map(item => metricValue(item, metric) ?? 0))
  return <div className="reports-map-detail">
    <button type="button" className="reports-map-back" onClick={onBack}><UiIcon name="arrowLeft" size={14} />{scope === 'turkey' ? 'Tüm İller' : 'Tüm Ülkeler'}</button>
    <h3>{group.label}</h3>
    <dl className="reports-map-stats">
      <div><dt>Ciro</dt><dd>{formatMetric(metricValue(group, 'revenue'), 'revenue')}</dd></div>
      <div><dt>Sipariş</dt><dd>{formatCount(group.orders)}</dd></div>
      <div><dt>Sepet Ort.</dt><dd>{formatMetric(metricValue(group, 'basket'), 'basket')}</dd></div>
      <div><dt>Satılan Adet</dt><dd>{formatNumber(group.soldQuantity)}</dd></div>
      <div><dt>İade Oranı</dt><dd>{formatPercent(group.returnRate)}</dd></div>
      <div><dt>Önceki Döneme Göre</dt><dd className={delta ? `is-${delta.tone}` : undefined}>{delta?.text ?? '—'}</dd></div>
    </dl>
    <h4>Platformlar</h4>
    <ul className="reports-map-parts">{parts.map(part => <li key={part.key}>
      <i style={{ background: platformColor(part.key) }} aria-hidden="true" /><span>{platformName(part.key)}</span>
      <strong>{formatCount(part.orders)} sipariş</strong><small>{formatMetric(metricValue(part, 'revenue'), 'revenue')}</small>
    </li>)}</ul>
    {scope === 'turkey' && districts.length > 0 && <>
      <h4>İlçeler</h4>
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
