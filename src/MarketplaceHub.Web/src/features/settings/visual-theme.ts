export type VisualTheme = 'light' | 'dark'

export const visualThemeChangeEvent = 'ravencia:visual-theme-change'

export function readVisualThemePreference(): VisualTheme {
  return localStorage.getItem('ravencia.visualTheme') === 'light' ? 'light' : 'dark'
}

export function setVisualThemePreference(theme: VisualTheme) {
  localStorage.setItem('ravencia.visualTheme', theme)
  window.dispatchEvent(new CustomEvent<VisualTheme>(visualThemeChangeEvent, { detail: theme }))
}
