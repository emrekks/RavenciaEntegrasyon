import { useEffect, useId, useRef, useState, type CSSProperties } from 'react'
import { UiIcon } from '../../shared/components'

// The overview's charts. Each plays once, when its card first comes into view: bars and gauges grow from zero to their
// value and figures count up, then everything stays still. Without IntersectionObserver (tests, old browsers) or with
// reduced motion the final state shows at once. Every figure is also in the DOM as text from the first render.

const reducedMotion = () => typeof window === 'undefined' || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true

// True once the element has been at least a third in view; it never turns back.
export function useReveal<T extends Element>() {
  const ref = useRef<T>(null)
  const [shown, setShown] = useState(() => typeof IntersectionObserver === 'undefined' || reducedMotion())
  useEffect(() => {
    const element = ref.current
    if (shown || !element) return
    const observer = new IntersectionObserver(entries => {
      if (!entries.some(entry => entry.isIntersecting)) return
      setShown(true)
      observer.disconnect()
    }, { threshold: 0.3 })
    observer.observe(element)
    return () => observer.disconnect()
  }, [shown])
  return [ref, shown] as const
}

// A figure that counts up from zero once `active` turns true (and from the old value to a new one after a filter change).
export function CountUp({ value, format, active = true }: { value: number; format: (value: number) => string; active?: boolean }) {
  const [animate] = useState(() => typeof requestAnimationFrame === 'function' && !reducedMotion())
  const [shown, setShown] = useState(() => animate ? 0 : value)
  const current = useRef(shown)
  useEffect(() => {
    if (!animate || !active) return
    const origin = current.current
    const start = performance.now()
    let frame = requestAnimationFrame(function step(now) {
      const t = Math.max(0, Math.min(1, (now - start) / 1100))
      current.current = origin + (value - origin) * (1 - Math.pow(1 - t, 3))
      setShown(current.current)
      if (t < 1) frame = requestAnimationFrame(step)
    })
    return () => cancelAnimationFrame(frame)
  }, [animate, active, value])
  return <><span aria-hidden="true">{format(animate ? shown : value)}</span><span className="reports-sr">{format(value)}</span></>
}

// The "?" button: the explanation shows on hover and on keyboard focus.
export function InfoTip({ label, text }: { label: string; text: string }) {
  const id = useId()
  return <span className="reports-info">
    <button type="button" aria-label={`${label} nasıl hesaplanır`} aria-describedby={id}><UiIcon name="help" size={14} /></button>
    <span role="tooltip" id={id}>{text}</span>
  </span>
}

// A round top for an axis: 1, 2, 2.5 or 5 times a power of ten, at least the largest value.
function niceMax(value: number) {
  if (value <= 0) return 1
  const power = 10 ** Math.floor(Math.log10(value))
  return ([1, 2, 2.5, 5, 10].find(step => step * power >= value) ?? 10) * power
}

export type ColumnPoint = { key: string; short: string; title: string; value: number; previous: number | null }

// The period's buckets as columns: soft grey, the best one in the accent colour, the one under the pointer darker
// with a card of its value beside the previous period's. The columns rise one after another when first seen.
export function Columns({ points, format, axisFormat, shown, label }: { points: ColumnPoint[]; format: (value: number) => string; axisFormat: (value: number) => string; shown: boolean; label: string }) {
  const [hover, setHover] = useState<number | null>(null)
  if (!points.length) return <p className="reports-empty">Bu dönemde veri yok.</p>
  const top = niceMax(Math.max(0, ...points.map(point => point.value)))
  const peak = points.reduce((best, point, index) => point.value > points[best].value ? index : best, 0)
  const every = Math.max(1, Math.ceil(points.length / 7))
  const point = hover === null ? null : points[hover]
  return <div className="reports-columns" onMouseLeave={() => setHover(null)}>
    <div className="reports-columns-axis" aria-hidden="true">{[1, 0.5, 0].map(step => <span key={step} style={{ bottom: `${step * 100}%` }}>{axisFormat(top * step)}</span>)}</div>
    <div className="reports-columns-plot">
      {[1, 0.5].map(step => <i key={step} className="reports-columns-grid" style={{ bottom: `calc(24px + (100% - 24px) * ${step})` }} aria-hidden="true" />)}
      <ol role="img" aria-label={label}>{points.map((item, index) => <li key={item.key} onMouseEnter={() => setHover(index)}
        className={`${index === peak && item.value > 0 ? 'is-peak' : ''}${hover === index ? ' is-hover' : ''}`}>
        <span className="reports-column" style={{ height: shown ? `${Math.max(item.value > 0 ? 1.5 : 0, item.value / top * 100)}%` : '0%', transitionDelay: `${index * 22}ms` }} />
        {index % every === 0 && <small>{item.short}</small>}
      </li>)}</ol>
      {point && hover !== null && <div className="reports-columns-tip" role="presentation"
        style={{ left: `${(hover + 0.5) / points.length * 100}%`, bottom: `calc(24px + (100% - 24px) * ${Math.min(0.82, point.value / top)})` } as CSSProperties}>
        <b>{point.title}</b>
        <span><i className="is-now" />Bu dönem<strong>{format(point.value)}</strong></span>
        {point.previous !== null && <span><i />Önceki<strong>{format(point.previous)}</strong></span>}
      </div>}
    </div>
  </div>
}

export type Part = { key: string; label: string; value: number; tone: string }

// Parts of a whole as one rounded bar that fills from the left when first seen, each part in its tone.
export function StackedBar({ parts, total, shown, label }: { parts: Part[]; total: number; shown: boolean; label: string }) {
  const drawn = parts.filter(part => part.value > 0)
  const sum = Math.max(total, drawn.reduce((value, part) => value + part.value, 0), 1)
  return <div className="reports-stacked" role="img" aria-label={label}>
    {drawn.map((part, index) => <span key={part.key} className={`is-${part.tone}`} title={part.label}
      style={{ width: shown ? `${part.value / sum * 100}%` : '0%', transitionDelay: `${index * 120}ms` }} />)}
  </div>
}

// A thin progress bar that fills to its share when first seen.
export function Progress({ value, shown, tone, delay = 0 }: { value: number | null; shown: boolean; tone?: string; delay?: number }) {
  const width = value === null ? 0 : Math.max(0, Math.min(1, value)) * 100
  return <span className={`reports-progress${tone ? ` is-${tone}` : ''}`} aria-hidden="true">
    <i style={{ width: shown ? `${width}%` : '0%', transitionDelay: `${delay}ms`, ...(tone?.startsWith('#') ? { background: tone } : {}) }} />
  </span>
}

// A half-circle gauge that sweeps from zero to its share when first seen; nothing is drawn for an unknown share.
export function HalfGauge({ value, shown, tone = 'good', label }: { value: number | null; shown: boolean; tone?: 'good' | 'bad'; label: string }) {
  const percent = value === null ? 0 : Math.max(0, Math.min(100, Math.abs(value) * 100))
  const arc = 'M20,100 A80,80 0 0 1 180,100'
  return <svg className={`reports-gauge is-${tone}`} viewBox="0 0 200 108" role="img" aria-label={label}>
    <path className="reports-gauge-base" d={arc} pathLength={100} />
    {value !== null && <path className="reports-gauge-value" d={arc} pathLength={100} strokeDasharray={`${shown ? percent : 0} 100`} />}
  </svg>
}
