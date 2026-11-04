import { useLayoutEffect, type RefObject } from 'react'

// Every block of a page (a direct block of `main > .content`, a panel, or an element marked data-motion="block") plays
// the page entry (styles/motion.css, `.rv-reveal`) once, when it is first on screen: as the page opens, when its data
// arrives later and it mounts, or when it is scrolled into view. A block on screen as it mounts takes the class in the
// mutation callback, before the browser paints it, so it never shows once and then fades in. A block inside one that is
// playing plays with it.
export const revealCandidateSelector = 'main > .content > *, main > .content :is(.rr-panel, .panel, [data-motion="block"])'
const excludedSelector = '.rv-action-bar, [role="dialog"], .notice, [class*="toast"], script, style, template'
export const revealClass = 'rv-reveal'
// Blocks shown together follow one another .06s apart (kit "A · iOS Yay"; styles/tokens.css --rv-motion-block-stagger).
const staggerMs = 60
const maxStaggerSteps = 4
const revealLifetimeMs = 2200

export function isRevealCandidate(element: Element) {
  return element.matches(revealCandidateSelector) && !element.matches(excludedSelector)
}

// One observer for the shell's life: a new page is new nodes, so its blocks are new candidates. Set up before the
// first paint so the first page's blocks do not show once before they fade in.
export function usePageEntryReveal(rootRef: RefObject<HTMLElement | null>) {
  useLayoutEffect(() => {
    const root = rootRef.current
    if (!root || typeof MutationObserver === 'undefined') return undefined
    if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return undefined
    const seen = new WeakSet<Element>()
    const pending = new Set<Element>()
    const timers = new Set<number>()

    const coveredByAncestor = (element: Element) => {
      for (let parent = element.parentElement; parent && parent !== root; parent = parent.parentElement) {
        if (pending.has(parent) || parent.classList.contains(revealClass)) return true
      }
      return false
    }
    const reveal = (elements: Element[]) => {
      elements.forEach((element, index) => {
        pending.delete(element)
        seen.add(element)
        element.querySelectorAll(revealCandidateSelector).forEach(inner => { seen.add(inner); pending.delete(inner) })
        ;(element as HTMLElement).style.setProperty('--rv-reveal-delay', `${Math.min(index, maxStaggerSteps) * staggerMs}ms`)
        element.classList.add(revealClass)
        const timer = window.setTimeout(() => {
          timers.delete(timer)
          element.classList.remove(revealClass)
          ;(element as HTMLElement).style.removeProperty('--rv-reveal-delay')
        }, revealLifetimeMs)
        timers.add(timer)
      })
    }
    const onScreen = (element: Element) => {
      const rect = element.getBoundingClientRect()
      return rect.bottom > 0 && rect.top < window.innerHeight
    }
    const intersection = typeof IntersectionObserver === 'undefined' ? null : new IntersectionObserver(entries => {
      const shown = entries.filter(entry => entry.isIntersecting && pending.has(entry.target)).map(entry => entry.target)
      shown.forEach(element => intersection?.unobserve(element))
      reveal(shown.filter(element => !coveredByAncestor(element)))
    })
    const consider = (candidates: Element[]) => {
      const now: Element[] = []
      for (const element of candidates) {
        if (seen.has(element) || pending.has(element) || coveredByAncestor(element)) continue
        if (onScreen(element) || !intersection) now.push(element)
        else { pending.add(element); intersection.observe(element) }
      }
      reveal(now)
    }
    const collect = (node: Node, into: Element[]) => {
      if (!(node instanceof Element)) return
      if (isRevealCandidate(node)) into.push(node)
      node.querySelectorAll(revealCandidateSelector).forEach(inner => { if (!inner.matches(excludedSelector)) into.push(inner) })
    }

    const initial: Element[] = []
    collect(root, initial)
    consider(initial)
    const mutations = new MutationObserver(records => {
      const added: Element[] = []
      for (const record of records) record.addedNodes.forEach(node => collect(node, added))
      if (added.length) consider(added)
    })
    mutations.observe(root, { childList: true, subtree: true })
    return () => {
      mutations.disconnect()
      intersection?.disconnect()
      timers.forEach(timer => window.clearTimeout(timer))
    }
  }, [rootRef])
}
