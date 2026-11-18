// A number box under the pointer must not change its value when the page is scrolled with the wheel, and must not swallow
// the wheel either: a focused one loses focus (so the browser leaves its value alone) and the event goes on to scroll
// whatever lies under it. Never preventDefault here.
export function blurNumberInputOnWheel(event: WheelEvent) {
  const target = event.target
  if (target instanceof HTMLInputElement && target.type === 'number' && document.activeElement === target) target.blur()
}
