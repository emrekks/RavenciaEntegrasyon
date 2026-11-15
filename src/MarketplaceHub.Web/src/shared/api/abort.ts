import { isCancelledError } from '@tanstack/react-query'

/**
 * True for a request the panel itself stopped: a query left the screen or was replaced by a newer one (TanStack Query
 * aborts its signal and cancels it), or sign-out cancelled what was running. Nothing failed, so nothing is shown.
 * A read that ran out of time is not this: it is an ApiRequestError with code CLIENT_TIMEOUT and is shown as an error.
 * Kept apart from client.ts so a test that mocks the API client still has it.
 */
export function isAbortedRequest(error: unknown) {
  if (isCancelledError(error)) return true
  return typeof error === 'object' && error !== null && (error as { name?: unknown }).name === 'AbortError'
}
