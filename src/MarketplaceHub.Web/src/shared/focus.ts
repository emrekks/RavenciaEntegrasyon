/**
 * Callback ref that focuses an element once, when it appears after the person opened something (a search popover, an
 * inline editor, the sign-in form). Windows use `data-autofocus` instead: the dialog surface moves focus on open.
 */
export function focusOnMount(element: HTMLElement | null) {
  element?.focus({ preventScroll: true })
}
