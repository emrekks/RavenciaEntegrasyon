// Copies text to the clipboard. The Clipboard API is refused where the page has no write permission (embedded
// browsers such as the desktop app's pane: "Write permission denied"), so a hidden text area and the copy command
// are tried next. Resolves true when either copied.
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // fall through to the copy command
  }
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.top = '-1000px'
  area.style.opacity = '0'
  document.body.appendChild(area)
  const active = document.activeElement instanceof HTMLElement ? document.activeElement : null
  area.select()
  let copied = false
  try {
    copied = document.execCommand('copy')
  } catch {
    copied = false
  } finally {
    area.remove()
    active?.focus({ preventScroll: true })
  }
  return copied
}
