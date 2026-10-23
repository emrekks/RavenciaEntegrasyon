import { useEffect, useRef, useState } from 'react'

// A formatted figure ("984", "₺400.479,78", "%38,7") split around its number, read the Turkish way (dot thousands,
// comma decimals). Null for text that is not one figure ("—", "Tutar yok", "…").
export function parseFigure(text: string): { prefix: string; value: number; decimals: number; suffix: string } | null {
  const match = /^(\D*?)(\d{1,3}(?:\.\d{3})*|\d+)(?:,(\d+))?(\D*)$/.exec(text)
  if (!match) return null
  const [, prefix, whole, fraction = '', suffix] = match
  return { prefix, value: Number(`${whole.replace(/\./g, '')}.${fraction || '0'}`), decimals: fraction.length, suffix }
}

const canAnimate = () => typeof window !== 'undefined' && typeof window.matchMedia === 'function'
  && typeof window.requestAnimationFrame === 'function' && !window.matchMedia('(prefers-reduced-motion: reduce)').matches

// A summary figure that counts up to its value when it first shows (and from the old value when it changes), keeping
// the value's own format. Text that is not a figure, reduced motion and environments without animation frames
// (tests) show the value as it is.
export function CountUp({ value, duration = 600 }: { value: string; duration?: number }) {
  const from = useRef<number | null>(null)
  const [shown, setShown] = useState(() => {
    const figure = parseFigure(value)
    return figure && canAnimate() ? `${figure.prefix}${format(0, figure.decimals)}${figure.suffix}` : value
  })

  useEffect(() => {
    const figure = parseFigure(value)
    if (!figure || !canAnimate()) { setShown(value); from.current = figure?.value ?? null; return }
    const start = from.current ?? 0
    from.current = figure.value
    if (start === figure.value) { setShown(value); return }
    let frame = 0
    const began = performance.now()
    const step = (now: number) => {
      // A frame's time can be a little before the effect ran; the figure never starts below its origin.
      const progress = Math.max(0, Math.min(1, (now - began) / duration))
      const eased = 1 - Math.pow(1 - progress, 3)
      if (progress < 1) {
        setShown(`${figure.prefix}${format(start + (figure.value - start) * eased, figure.decimals)}${figure.suffix}`)
        frame = requestAnimationFrame(step)
      } else setShown(value)
    }
    frame = requestAnimationFrame(step)
    return () => cancelAnimationFrame(frame)
  }, [value, duration])

  return <>{shown}</>
}

function format(value: number, decimals: number) {
  return value.toLocaleString('tr-TR', { minimumFractionDigits: decimals, maximumFractionDigits: decimals })
}
