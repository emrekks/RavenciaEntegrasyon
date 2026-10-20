import { useEffect, useState } from 'react'

/** Whether a CSS media query matches now; follows changes (and resizes, which some mobile browsers report late). */
export function useMediaQuery(query: string) {
  const [matches, setMatches] = useState(() => typeof window !== 'undefined' && window.matchMedia(query).matches)
  useEffect(() => {
    const list = window.matchMedia(query)
    const update = () => setMatches(list.matches)
    update()
    list.addEventListener('change', update)
    window.addEventListener('resize', update)
    return () => { list.removeEventListener('change', update); window.removeEventListener('resize', update) }
  }, [query])
  return matches
}
