// View Transitions without stray errors. A transition the browser skips (a newer one started, the page is hidden or
// being resized, its state became invalid) rejects its `ready` promise, and `finished`/`updateCallbackDone` reject when
// the update fails. Nobody awaited them, so every skip surfaced as an unhandled rejection ("Transition was aborted
// because of invalid state", "Transition was skipped"; live on Raporlar tab changes, 1 Nov 2026). The page change is
// what matters, the animation is decoration: the promises are settled quietly, and a quick second click skips the
// running transition first.
type ViewTransitionLike = { ready?: Promise<unknown>; finished?: Promise<unknown>; updateCallbackDone?: Promise<unknown>; skipTransition?: () => void }
type StartViewTransition = (update: () => void | Promise<void>) => ViewTransitionLike

let running: ViewTransitionLike | null = null

const quiet = (promise: Promise<unknown> | undefined) => { promise?.catch(() => undefined) }

export function startViewTransitionSafely(update: () => void | Promise<void>, { respectReducedMotion = true } = {}) {
  const start = (document as Document & { startViewTransition?: StartViewTransition }).startViewTransition
  if (typeof start !== 'function' || (respectReducedMotion && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches)) { void update(); return }
  try { running?.skipTransition?.() } catch { /* an ended transition has nothing to skip */ }
  let transition: ViewTransitionLike
  try { transition = start.call(document, update) } catch { void update(); return }
  running = transition
  quiet(transition.ready)
  quiet(transition.updateCallbackDone)
  const finished = transition.finished
  if (finished) finished.then(() => undefined, () => undefined).finally(() => { if (running === transition) running = null })
  else running = null
}
