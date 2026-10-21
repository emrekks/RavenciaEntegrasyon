import { useEffect, useRef } from 'react'
import { useSearchParams } from 'react-router'

// List screens keep their page, page size and filters in the address (W16): going back from a detail page, a reload
// or a shared link opens the same page with the same filters. A screen reads its starting state from the address
// once and writes its applied state back; a value equal to its default is left out so plain addresses stay plain.

export type ListUrlValue = string | number | readonly string[] | null | undefined

export function readUrlText(params: URLSearchParams, name: string, fallback = '') {
  return params.get(name)?.trim() || fallback
}

/** A choice from a fixed set; anything else in the address falls back. */
export function readUrlChoice<T extends string>(params: URLSearchParams, name: string, allowed: readonly T[], fallback: T): T {
  const value = params.get(name)?.trim().toUpperCase()
  return allowed.find(item => item.toUpperCase() === value) ?? fallback
}

/** A positive whole number (a page or a page size from the offered sizes); anything else falls back. */
export function readUrlNumber(params: URLSearchParams, name: string, fallback: number, allowed?: readonly number[]) {
  const value = Number(params.get(name))
  if (!Number.isInteger(value) || value < 1) return fallback
  return allowed && !allowed.includes(value) ? fallback : value
}

export function readUrlList(params: URLSearchParams, name: string) {
  return (params.get(name) ?? '').split(',').map(item => item.trim()).filter(Boolean)
}

/** The address parameters with `values` written over them; a value equal to its default (or empty) is removed. */
export function withListParams(current: URLSearchParams, values: Record<string, ListUrlValue>, defaults: Record<string, ListUrlValue> = {}) {
  const next = new URLSearchParams(current)
  for (const [name, value] of Object.entries(values)) {
    const text = Array.isArray(value) ? value.join(',') : value == null ? '' : String(value)
    const fallback = defaults[name]
    const fallbackText = Array.isArray(fallback) ? fallback.join(',') : fallback == null ? '' : String(fallback)
    if (!text || text === fallbackText) next.delete(name)
    else next.set(name, text)
  }
  return next
}

/** Writes the screen's applied list state into the address (replacing the entry, so Back leaves the list). */
export function useListUrlSync(values: Record<string, ListUrlValue>, defaults: Record<string, ListUrlValue> = {}) {
  const [searchParams, setSearchParams] = useSearchParams()
  // The address is read when the state changes, not watched: a link that changes the address (a search from the
  // quick search) is picked up by the screen first and written back from its state, never overwritten by old state.
  const latestParams = useRef(searchParams)
  useEffect(() => { latestParams.current = searchParams })
  const serialized = JSON.stringify(values)
  const defaultsSerialized = JSON.stringify(defaults)
  useEffect(() => {
    const current = latestParams.current
    const next = withListParams(current, JSON.parse(serialized) as Record<string, ListUrlValue>, JSON.parse(defaultsSerialized) as Record<string, ListUrlValue>)
    if (next.toString() !== current.toString()) setSearchParams(next, { replace: true })
  }, [defaultsSerialized, serialized, setSearchParams])
}

/** Runs `effect` when `key` changes, but not for the value the screen opened with (restored from the address). */
export function useChangeEffect(key: string, effect: () => void) {
  const previous = useRef(key)
  const latest = useRef(effect)
  useEffect(() => { latest.current = effect })
  useEffect(() => {
    if (previous.current === key) return
    previous.current = key
    latest.current()
  }, [key])
}
