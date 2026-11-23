// jsdom has no matchMedia. Pages ask for the phone breakpoint at runtime, so
// tests get a stub that reports a desktop viewport (no width query matches). It asks for reduced motion, so
// figures that count up (CountUp) show their value at once and a test reads the number, not a frame of it.
if (typeof window !== 'undefined' && typeof window.matchMedia !== 'function') {
  window.matchMedia = (query: string): MediaQueryList => ({
    matches: query.includes('prefers-reduced-motion: reduce'),
    media: query,
    onchange: null,
    addListener: () => undefined,
    removeListener: () => undefined,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    dispatchEvent: () => false
  })
}

// A list page remembers the last Sıralama and Sayfa Başına in localStorage; a test must not inherit the previous test's pick.
import { beforeEach } from 'vitest'
beforeEach(() => { try { for (const key of Object.keys(localStorage)) if (key.startsWith('rv-sort:') || key.startsWith('rv-page-size:')) localStorage.removeItem(key) } catch { /* no storage here */ } })
