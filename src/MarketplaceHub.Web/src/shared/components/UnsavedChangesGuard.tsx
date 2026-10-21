import { useContext, useEffect, useRef, type RefObject } from 'react'
import { UNSAFE_DataRouterContext, useBlocker, type BlockerFunction } from 'react-router'
import { Button, Modal } from './DesignSystem'

// Holds back in-app navigation (menu, links, back button) while a form has unsaved edits and asks first; a reload or
// closing the tab is the page's own beforeunload warning. Navigation that stays on the same path (a tab or a filter
// in the address) is never held back.

type GuardProps = {
  /** The form holds edits that leaving would lose. */
  when: boolean
  title?: string
  message?: string
  /** Set to true right before a navigation the screen makes itself (after a save) so it is not asked about. */
  allowNavigationRef?: RefObject<boolean>
}

/** Renders nothing outside a data router (tests with MemoryRouter), where useBlocker is not available. */
export function UnsavedChangesGuard(props: GuardProps) {
  const inDataRouter = useContext(UNSAFE_DataRouterContext) != null
  return inDataRouter ? <NavigationBlocker {...props} /> : null
}

export function shouldBlockNavigation(when: boolean, { currentLocation, nextLocation }: Parameters<BlockerFunction>[0]) {
  return when && currentLocation.pathname !== nextLocation.pathname
}

function NavigationBlocker({ when, allowNavigationRef, title = 'Kaydedilmemiş Değişiklikler', message = 'Formda kaydedilmemiş değişiklikler var. Sayfadan çıkarsanız bu değişiklikler kaybolur.' }: GuardProps) {
  // Read at navigation time, so a save that navigates right after it finished is not held back by a stale render.
  const whenRef = useRef(when)
  useEffect(() => { whenRef.current = when }, [when])
  const blocker = useBlocker(args => shouldBlockNavigation(whenRef.current && !allowNavigationRef?.current, args))
  // The edits were saved or dropped while the question was open: let the navigation go on.
  useEffect(() => { if (blocker.state === 'blocked' && !when) blocker.proceed() }, [blocker, when])
  if (blocker.state !== 'blocked') return null
  return <Modal open title={title} onClose={() => blocker.reset()} className="rv-confirm-dialog" footer={<>
    <Button variant="secondary" onClick={() => blocker.reset()} data-autofocus>Sayfada Kal</Button>
    <Button variant="danger" onClick={() => blocker.proceed()}>Değişiklikleri Bırak</Button>
  </>}><div className="rv-confirm-message">{message}</div></Modal>
}
