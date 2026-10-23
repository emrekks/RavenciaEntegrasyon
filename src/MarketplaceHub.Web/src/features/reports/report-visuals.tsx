import { useEffect, useRef, useState } from 'react'

// The small animated pieces of the overview. Every animation is decoration over a value that is already in the page:
// the counter's final figure is in the DOM for screen readers from the first render, and with reduced motion the
// figures and shapes appear at once (the CSS drops the keyframes).

const reducedMotion = () => typeof window === 'undefined' || typeof requestAnimationFrame !== 'function' || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true

// A figure that counts up to its value (and from the old value to a new one when the filters change).
export function CountUp({ value, format }: { value: number; format: (value: number) => string }) {
  const [animate] = useState(() => !reducedMotion())
  const [shown, setShown] = useState(() => animate ? 0 : value)
  const current = useRef(shown)
  useEffect(() => {
    if (!animate) return
    const origin = current.current
    const start = performance.now()
    let frame = requestAnimationFrame(function step(now) {
      const t = Math.min(1, (now - start) / 900)
      current.current = origin + (value - origin) * (1 - Math.pow(1 - t, 3))
      setShown(current.current)
      if (t < 1) frame = requestAnimationFrame(step)
    })
    return () => cancelAnimationFrame(frame)
  }, [animate, value])
  return <><span aria-hidden="true">{format(animate ? shown : value)}</span><span className="reports-sr">{format(value)}</span></>
}

// A share from 0 to 1 as a ring that sweeps to its length; no ring for an unknown share.
export function Ring({ value, tone, label }: { value: number | null; tone?: 'good' | 'bad'; label: string }) {
  const percent = value === null ? 0 : Math.max(0, Math.min(100, Math.abs(value) * 100))
  return <svg className={`reports-ring${tone ? ` is-${tone}` : ''}`} viewBox="0 0 48 48" role="img" aria-label={label}>
    <circle className="reports-ring-base" cx="24" cy="24" r="19" />
    {value !== null && percent > 0 && <circle className="reports-ring-value" cx="24" cy="24" r="19" pathLength={100} strokeDasharray={`${percent} 100`} />}
  </svg>
}

// The period's buckets as faint bars behind a tile's figure, growing in one after another.
export function BarsBackdrop({ values }: { values: number[] }) {
  const max = Math.max(0, ...values)
  if (values.length < 2 || max <= 0) return null
  const gap = values.length > 40 ? 0.3 : 0.8
  const width = (100 - gap * (values.length - 1)) / values.length
  return <svg className="reports-backdrop" viewBox="0 0 100 40" preserveAspectRatio="none" aria-hidden="true">
    {values.map((value, index) => {
      const height = Math.max(1.5, Math.max(0, value) / max * 40)
      return <rect key={index} x={index * (width + gap)} y={40 - height} width={width} height={height} rx={Math.min(1.2, width / 2)}
        style={{ animationDelay: `${Math.round(index / values.length * 450)}ms` }} />
    })}
  </svg>
}

// A line through the points as a monotone cubic curve (Fritsch–Carlson): smooth, but never above the highest or below
// the lowest neighbouring point, so a day with no sales does not dip under zero.
export function smoothPath(points: Array<[number, number]>) {
  const n = points.length
  if (n < 3) return points.map(([x, y], index) => `${index ? 'L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ')
  const slopes: number[] = []
  for (let i = 0; i < n - 1; i++) slopes.push((points[i + 1][1] - points[i][1]) / (points[i + 1][0] - points[i][0] || 1))
  const tangents = points.map((_, i) => i === 0 ? slopes[0] : i === n - 1 ? slopes[n - 2] : slopes[i - 1] * slopes[i] <= 0 ? 0 : (slopes[i - 1] + slopes[i]) / 2)
  for (let i = 0; i < n - 1; i++) {
    if (slopes[i] === 0) { tangents[i] = 0; tangents[i + 1] = 0; continue }
    const a = tangents[i] / slopes[i], b = tangents[i + 1] / slopes[i], sum = a * a + b * b
    if (sum > 9) { const tau = 3 / Math.sqrt(sum); tangents[i] = tau * a * slopes[i]; tangents[i + 1] = tau * b * slopes[i] }
  }
  let path = `M${points[0][0].toFixed(1)},${points[0][1].toFixed(1)}`
  for (let i = 0; i < n - 1; i++) {
    const [x0, y0] = points[i], [x1, y1] = points[i + 1], third = (x1 - x0) / 3
    path += ` C${(x0 + third).toFixed(1)},${(y0 + tangents[i] * third).toFixed(1)} ${(x1 - third).toFixed(1)},${(y1 - tangents[i + 1] * third).toFixed(1)} ${x1.toFixed(1)},${y1.toFixed(1)}`
  }
  return path
}
