import { useLayoutEffect, useState, type HTMLAttributes, type ReactNode, type RefObject } from 'react'

// Tab content slides in like iOS (owner, 5 Nov 2026): a tab to the right of the current one brings its content in from
// the right, a tab to the left from the left; the old content simply goes. Only a tab the user picks animates: the first
// render and a data refetch keep the content as it is, and so does picking the tab already shown. Under reduced motion
// the content switches at once. The animation (styles/motion.css, .rv-tab-in-next / .rv-tab-in-prev) moves only
// `translate` and `opacity` with `backwards` fill, so nothing stays applied on the wrapper afterwards: sticky and fixed
// children behave as before once it has played.
//
// A tab change also starts the page from its top (owner, 5 Nov 2026), at once and before the new tab is painted, so the
// slide plays where it is seen and no tab opens halfway down because the previous one was scrolled.
export type TabSlideClass = 'rv-tab-in-next' | 'rv-tab-in-prev' | ''
export type TabSlide = { className: TabSlideClass; key: string; sliding: boolean }

const reducedMotion = () => typeof window !== 'undefined' && Boolean(window.matchMedia?.('(prefers-reduced-motion: reduce)').matches)

export function tabSlideDirection(from: string, to: string, orderedKeys: readonly string[]): TabSlideClass {
  if (from === to) return ''
  const fromIndex = orderedKeys.indexOf(from)
  const toIndex = orderedKeys.indexOf(to)
  if (fromIndex < 0 || toIndex < 0) return 'rv-tab-in-next'
  return toIndex > fromIndex ? 'rv-tab-in-next' : 'rv-tab-in-prev'
}

// The page's scroller: the content area on a laptop (styles/design-system.css, min-width 701px), the window on a phone.
export function scrollPageToTop() {
  if (typeof window === 'undefined') return
  document.querySelectorAll<HTMLElement>('.app-shell > main > :is(.content, .rv-page)').forEach(scroller => { if (scroller.scrollTop > 0) scroller.scrollTop = 0 })
  if (window.scrollY > 0) window.scrollTo({ top: 0, left: window.scrollX, behavior: 'instant' })
}

// className goes on the content wrapper and key on the same element: the key changes only when the tab does (and the
// change animates), so the wrapper remounts and its animation plays once.
export function useTabSlide(activeKey: string, orderedKeys: readonly string[], { scrollToTop = true } = {}): TabSlide {
  const [state, setState] = useState<{ active: string; className: TabSlideClass; seq: number; changes: number }>({ active: activeKey, className: '', seq: 0, changes: 0 })
  let current = state
  if (state.active !== activeKey) {
    const className = reducedMotion() ? '' : tabSlideDirection(state.active, activeKey, orderedKeys)
    current = { active: activeKey, className, seq: className ? state.seq + 1 : state.seq, changes: state.changes + 1 }
    // Adjusting state while rendering (React's "storing information from previous renders"): the new tab's first paint
    // already carries its slide.
    setState(current)
  }
  const { changes } = current
  useLayoutEffect(() => { if (changes > 0 && scrollToTop) scrollPageToTop() }, [changes, scrollToTop])
  return { className: current.className, key: `tab-slide-${current.seq}`, sliding: Boolean(current.className) }
}

// The same slide on an element that must stay mounted (it holds state the other tabs keep, as Sistem Ayarları' panes
// do): it is played with the Web Animations API instead of a remount, with the same keyframes as the CSS classes.
export function useTabSlideInPlace(ref: RefObject<HTMLElement | null>, activeKey: string, orderedKeys: readonly string[], options: { scrollToTop?: boolean } = {}) {
  const slide = useTabSlide(activeKey, orderedKeys, options)
  const { className, key } = slide
  useLayoutEffect(() => {
    const element = ref.current
    if (!className || !element || typeof element.animate !== 'function') return undefined
    const style = getComputedStyle(element)
    const shift = style.getPropertyValue('--rv-motion-tab-shift').trim() || '28px'
    const duration = Number.parseFloat(style.getPropertyValue('--rv-motion-tab-slide')) || 320
    const from = className === 'rv-tab-in-next' ? shift : `calc(${shift} * -1)`
    const animation = element.animate([{ opacity: 0, translate: `${from} 0` }, { opacity: 1, translate: '0 0' }], { duration, easing: 'cubic-bezier(.32, .72, 0, 1)', fill: 'backwards' })
    return () => animation.cancel()
  }, [ref, className, key])
}

type TabSlidePanelProps = Omit<HTMLAttributes<HTMLDivElement>, 'children'> & { active: string; order: readonly string[]; scrollToTop?: boolean; children?: ReactNode }

// A content wrapper that slides on a tab change. While it slides it is one block for the page entry
// (app/hooks/usePageEntryReveal, data-motion="block"), so its panels do not rise on top of the slide.
export function TabSlidePanel({ active, order, scrollToTop = true, className, children, ...rest }: TabSlidePanelProps) {
  const slide = useTabSlide(active, order, { scrollToTop })
  return <div key={slide.key} {...rest} data-motion={slide.sliding ? 'block' : undefined} className={[className, slide.className].filter(Boolean).join(' ') || undefined}>{children}</div>
}
