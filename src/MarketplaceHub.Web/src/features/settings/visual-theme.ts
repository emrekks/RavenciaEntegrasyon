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

// The light/dark buttons switch with one calm colour transition (styles/motion.css): the class stays on <html> only
// for the switch itself, so page loads, route changes and hovers are never slowed. Reduced motion switches at once.
export function switchVisualThemeSmoothly(theme: VisualTheme) {
  const root = document.documentElement
  if (!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
    root.classList.add(themeTransitionClass)
    window.clearTimeout(themeTransitionTimer)
    themeTransitionTimer = window.setTimeout(() => root.classList.remove(themeTransitionClass), 800)
  }
  setVisualThemePreference(theme)
}
