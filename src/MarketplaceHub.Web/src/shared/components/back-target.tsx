import { createContext, useContext, useEffect } from 'react'
import { useDockHost } from './dock-actions'

// A page that is reached from a list (a product from Ürünler, an invoice from Faturalar...) names that list here. On a phone the
// shell then offers the way back from everywhere on the long page: the task bar's left button and the top bar's "‹ Liste" - so
// nobody scrolls to the page's own back arrow at the very top.
export type BackTarget = { to: string; label: string }
export type BackTargetHost = { target: BackTarget | null; set: (target: BackTarget | null) => void }
export const BackTargetContext = createContext<BackTargetHost | null>(null)

// Returns true where the shell draws the way back itself (on a phone), so the page leaves out its own back link at the top.
export function useBackTarget(target: BackTarget | null) {
  const host = useContext(BackTargetContext)
  const shellBack = Boolean(useDockHost()?.phone) && Boolean(target)
  const set = host?.set
  const to = target?.to
  const label = target?.label
  useEffect(() => {
    if (!set || !to || !label) return undefined
    set({ to, label })
    return () => set(null)
  }, [set, to, label])
  return shellBack
}

// "Ürünler'e Dön", "Faturalar'a Dön": the dative suffix follows the last vowel of the list's name.
export function backLabel(listName: string) {
  const vowels = Array.from(listName.toLocaleLowerCase('tr-TR')).filter(letter => 'aıoueiöü'.includes(letter))
  const last = vowels[vowels.length - 1]
  return `${listName}'${last && 'aıou'.includes(last) ? 'a' : 'e'} Dön`
}
