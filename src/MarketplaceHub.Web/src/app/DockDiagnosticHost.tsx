// TEMPORARY DIAGNOSTIC (remove after the standalone bottom-gap fix)
// Lives in the app shell so the panel stays while the menu closes and opens (open and closed are compared side by side).
// The panel's code is a separate chunk, fetched on the first trigger only.
import { lazy, Suspense, useEffect, useState } from 'react'

const DockDiagnostic = lazy(() => import('./DockDiagnostic'))
export const dockDiagnosticEvent = 'rv-dock-diagnostic'

export function DockDiagnosticHost() {
  const [shown, setShown] = useState(false)
  useEffect(() => {
    const toggle = () => setShown(value => !value)
    window.addEventListener(dockDiagnosticEvent, toggle)
    return () => window.removeEventListener(dockDiagnosticEvent, toggle)
  }, [])
  return shown ? <Suspense fallback={null}><DockDiagnostic onClose={() => setShown(false)} /></Suspense> : null
}
