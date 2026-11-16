import { useState, type SetStateAction } from 'react'

/** The settings as the server holds them, with the version a save is written over (If-Match). */
export type ServerCopy<T> = { value: T; version: number | undefined }

/**
 * What a newer server copy (another session's save, a 412, a focus refetch) does to the form:
 * - keep-edits (WEB-SAYFALAR-4): it replaces only a draft still equal to the copy it was made from; an edited draft is
 *   kept and the newer copy waits as a conflict, offered by acceptServer (Güncel Ayarı Yükle) and by a save over the
 *   newer version (Benimkiyle Üzerine Yaz). A newer version of the same settings only moves the version.
 * - replace: every new version starts the form again from it, edits included (a later refetch of the same version
 *   keeps them). There is never a conflict to offer.
 */
export type ServerDraftPolicy = 'keep-edits' | 'replace'

export type ServerDraftOptions<T, D> = {
  /** The copy read now; null or undefined while nothing (or no record) is read. */
  server: ServerCopy<T> | null | undefined
  onServerChange: ServerDraftPolicy
  /** The form made from a server copy. */
  toDraft: (value: T) => D
  /** The form while nothing is read yet. */
  initialDraft: () => D
  /** Two forms hold the same settings; JSON equality by default. */
  same?: (left: D, right: D) => boolean
  /** The form differs from the server copy; by default it is not the same as the form made from it. */
  isDirty?: (draft: D, server: T) => boolean
}

export type ServerDraft<T, D> = {
  draft: D
  setDraft: (next: SetStateAction<D>) => void
  /** The server copy the draft was made from, with its version; a plain save is written over this version. */
  base: ServerCopy<T> | null
  dirty: boolean
  /** Newer settings were saved elsewhere while the draft held edits (keep-edits only). */
  conflict: boolean
  /** The draft gives way to the newer settings. */
  acceptServer: () => void
  /** A save went through: the saved copy becomes the base, and the form starts from it (or from the form given). */
  commit: (saved: ServerCopy<T>, draft?: D) => void
}

type DraftState<T, D> = { draft: D; base: ServerCopy<T> | null; seen: string | null }

const jsonSame = (left: unknown, right: unknown) => JSON.stringify(left) === JSON.stringify(right)

// keep-edits notices a change of the settings or of the version; replace notices only a new version (a status refetch
// of the same version leaves the form alone).
function serverKey<T>(server: ServerCopy<T> | null | undefined, policy: ServerDraftPolicy) {
  if (server == null) return null
  return policy === 'replace' ? `v${server.version}` : `v${server.version}\u0000${JSON.stringify(server.value)}`
}

/**
 * One form over one server record: the draft, the copy it was made from, whether it changed, and what a newer copy
 * does to it (ServerDraftPolicy). Reading and writing stay with the caller, who writes over base.version and calls
 * commit with what the server answered. The form follows a newer copy during the same render, before anything shows.
 */
export function useServerDraft<T, D = T>(options: ServerDraftOptions<T, D>): ServerDraft<T, D> {
  const { server, onServerChange, toDraft, initialDraft } = options
  const same = options.same ?? jsonSame
  const key = serverKey(server, onServerChange)
  const [state, setState] = useState<DraftState<T, D>>(() => ({ draft: server ? toDraft(server.value) : initialDraft(), base: server ?? null, seen: key }))

  let current = state
  if (key !== state.seen) {
    current = { ...state, seen: key }
    if (server != null) {
      if (onServerChange === 'replace') current = { draft: toDraft(server.value), base: server, seen: key }
      else {
        const base = state.base
        if (base === null || same(state.draft, toDraft(base.value))) current = { draft: toDraft(server.value), base: server, seen: key }
        // Only the version moved: nothing to choose between.
        else if (same(toDraft(server.value), toDraft(base.value))) current = { ...state, base: { value: base.value, version: server.version }, seen: key }
      }
    }
    setState(current)
  }

  const { draft, base } = current
  const dirty = server == null ? false : options.isDirty ? options.isDirty(draft, server.value) : !same(draft, toDraft(server.value))
  const conflict = onServerChange === 'keep-edits' && base?.version != null && server?.version != null && server.version !== base.version

  return {
    draft,
    base,
    dirty,
    conflict,
    setDraft: next => setState(previous => ({ ...previous, draft: typeof next === 'function' ? (next as (value: D) => D)(previous.draft) : next })),
    acceptServer: () => {
      if (server == null) return
      setState(previous => ({ ...previous, draft: toDraft(server.value), base: server }))
    },
    commit: (saved, savedDraft) => setState(previous => ({ ...previous, draft: savedDraft ?? toDraft(saved.value), base: saved }))
  }
}
