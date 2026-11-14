// Arrow-key movement for a dropdown made of a search box and a list of option buttons: Down from the search box lands on the
// first option, Up/Down walk the options, Home/End jump; the focused option is scrolled into view by the browser.
export function moveListboxFocus(event: { key: string; currentTarget: HTMLElement; preventDefault: () => void }) {
  const keys = ['ArrowDown', 'ArrowUp', 'Home', 'End']
  if (!keys.includes(event.key)) return
  const options = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('[role="option"]'))
  if (!options.length) return
  const active = document.activeElement as HTMLElement | null
  const index = active ? options.indexOf(active) : -1
  let next: number
  if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = options.length - 1
  else if (event.key === 'ArrowDown') next = index < 0 ? 0 : Math.min(options.length - 1, index + 1)
  else if (index < 0) return
  else if (index === 0) { event.currentTarget.querySelector<HTMLElement>('input')?.focus(); event.preventDefault(); return }
  else next = index - 1
  if ((event.key === 'Home' || event.key === 'End') && index < 0) return
  event.preventDefault()
  options[next].focus()
  options[next].scrollIntoView({ block: 'nearest' })
}
