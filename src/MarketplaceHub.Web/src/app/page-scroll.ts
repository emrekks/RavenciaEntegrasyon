// A new page opens at its top (owner, 12 Nov); filters and tabs on the same page keep the scroll position.
// - A page lock (the phone's full-screen menu, the notification sheet) holds the viewport while it is open: a scrollTo
//   made then is lost and the old offset comes back when the lock goes, so the reset waits until no lock is held.
// - Back and forward (POP) leave the position to the browser, which restores the one the page had.
export function shouldResetScroll({ pathname, lastPathname, locked, navigationType }: { pathname: string; lastPathname: string | null; locked: boolean; navigationType: string }) {
  if (locked) return false
  if (pathname === lastPathname) return false
  return navigationType !== 'POP' || lastPathname === null
}
