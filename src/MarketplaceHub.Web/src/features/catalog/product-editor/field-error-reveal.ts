// A refused save brings its field into view; once the field is on screen it shakes so the eye finds it. Without a
// target the first field marked invalid (or carrying a message under it) in the root is used.
export function revealFieldError(root: ParentNode | null, target?: Element | null) {
  const field = target ?? root?.querySelector('[aria-invalid="true"], label:has(> .product-field-error) :is(input, select, textarea), .product-field-error')
  if (!(field instanceof HTMLElement)) return
  field.scrollIntoView({ behavior: 'smooth', block: 'center' })
  if (field.matches('input, select, textarea')) field.focus({ preventScroll: true })
  const shake = () => {
    field.classList.remove('field-error-shake')
    void field.offsetWidth
    field.classList.add('field-error-shake')
    field.addEventListener('animationend', () => field.classList.remove('field-error-shake'), { once: true })
  }
  if (typeof IntersectionObserver === 'undefined') { shake(); return }
  const observer = new IntersectionObserver(entries => {
    if (!entries.some(entry => entry.intersectionRatio >= 0.9)) return
    observer.disconnect()
    shake()
  }, { threshold: [0.9] })
  observer.observe(field)
  window.setTimeout(() => observer.disconnect(), 3000)
}
