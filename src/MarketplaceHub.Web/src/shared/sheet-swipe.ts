// Phones: bottom sheets (the filter sheet and the menus that open from the bottom) follow a finger pulled down from
// their handle strip and close when it is let go far enough; a shorter pull springs back. Closing goes through the
// sheet's own scrim, so each sheet closes the way a tap outside would.
const sheetSelector = '.rr-fopen > .rr-filters, .rr-anchor > .rr-menu:not(.rr-static), .rr-anchor > .rr-pop:not(.rr-static)'
const handleHeight = 44
const closeDistance = 70

export function installSheetSwipeClose() {
  let drag: { sheet: HTMLElement; startY: number; distance: number } | null = null

  document.addEventListener('touchstart', event => {
    if (event.touches.length !== 1 || !window.matchMedia('(max-width: 760px)').matches) return
    const sheet = event.target instanceof Element ? event.target.closest<HTMLElement>(sheetSelector) : null
    if (!sheet) return
    const y = event.touches[0].clientY
    if (y - sheet.getBoundingClientRect().top > handleHeight) return
    drag = { sheet, startY: y, distance: 0 }
    sheet.style.transition = 'none'
  }, { passive: true })

  document.addEventListener('touchmove', event => {
    if (!drag) return
    drag.distance = Math.max(0, event.touches[0].clientY - drag.startY)
    drag.sheet.style.transform = `translateY(${drag.distance}px)`
    if (event.cancelable) event.preventDefault()
  }, { passive: false })

  const release = () => {
    if (!drag) return
    const { sheet, distance } = drag
    drag = null
    sheet.style.transition = 'transform .18s ease'
    if (distance < closeDistance) {
      sheet.style.transform = ''
      window.setTimeout(() => { sheet.style.transition = '' }, 200)
      return
    }
    sheet.style.transform = 'translateY(100%)'
    const scrim = sheet.parentElement?.querySelector<HTMLElement>(':scope > .rr-fscrim, :scope > .rr-anchor-scrim')
    window.setTimeout(() => { scrim?.click(); sheet.style.transform = ''; sheet.style.transition = '' }, 160)
  }
  document.addEventListener('touchend', release)
  document.addEventListener('touchcancel', release)
}
