import { flushSync } from 'react-dom'

export type VisualTheme = 'light' | 'dark'

export const visualThemeChangeEvent = 'ravencia:visual-theme-change'

export function readVisualThemePreference(): VisualTheme {
  return localStorage.getItem('ravencia.visualTheme') === 'light' ? 'light' : 'dark'
}

export function setVisualThemePreference(theme: VisualTheme) {
  localStorage.setItem('ravencia.visualTheme', theme)
  window.dispatchEvent(new CustomEvent<VisualTheme>(visualThemeChangeEvent, { detail: theme }))
}

const themeTransitionClass = 'theme-transitioning'
let themeTransitionTimer: number | undefined

// The light/dark buttons switch with one calm crossfade. Where the browser has View Transitions it takes a picture of
// the page before and after and fades one into the other on the GPU (styles/motion.css), so nothing is restyled frame
// by frame: easing the colour tokens on <html> made every element restyle each frame and the switch stuttered on long
// pages. Without View Transitions the tokens still ease (html.theme-transitioning). Reduced motion switches at once.
export function switchVisualThemeSmoothly(theme: VisualTheme) {
  if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) { setVisualThemePreference(theme); return }
  if (typeof document.startViewTransition === 'function') {
    // React applies the theme in an effect after the event: the new picture is taken once that has run.
    document.startViewTransition(async () => {
      flushSync(() => setVisualThemePreference(theme))
      await new Promise(resolve => window.setTimeout(resolve, 0))
    })
    return
  }
  const root = document.documentElement
  root.classList.add(themeTransitionClass)
  window.clearTimeout(themeTransitionTimer)
  themeTransitionTimer = window.setTimeout(() => root.classList.remove(themeTransitionClass), 800)
  setVisualThemePreference(theme)
}
