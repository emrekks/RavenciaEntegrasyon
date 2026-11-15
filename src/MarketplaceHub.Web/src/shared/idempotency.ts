// Idempotency-Key values. The server replays the stored answer of any finished request (below 500) that comes again
// with the same key and the same body, and refuses the same key with a different body.

/** A fresh key for one attempt; `scope` only helps reading server logs. */
export function newIdempotencyKey(scope?: string) {
  const id = crypto.randomUUID()
  return scope ? `${scope}:${id}` : id
}

// FNV-1a: a short, stable fingerprint of the request body (not a security measure).
function fingerprint(text: string) {
  let hash = 0x811c9dc5
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193)
  }
  return (hash >>> 0).toString(16).padStart(8, '0')
}

/**
 * The same key for the same request on the same record version: a double click or a resend after a lost answer
 * reaches the marketplace once. A new version (after the change went through) or a different body gives a new key.
 */
export function versionedIdempotencyKey(scope: string, id: string, version: number | string, body = '') {
  return `${scope}:${id}:v${version}${body ? `:${fingerprint(body)}` : ''}`
}

// Versioned keys that already received an HTTP answer, mapped to the fresh key that replaces them. The server stores
// every answered key (a 4xx is replayed, a 5xx leaves it UNKNOWN and later uses get 409), so only a resend after a
// network error with no answer may reuse the versioned key. Kept in localStorage so a reload or another tab does not
// send the answered key again and get the old refusal replayed; bounded so it cannot grow without limit.
const answeredVersionedKeyStorage = 'rv-answered-idempotency-keys'
const answeredVersionedKeys = readAnsweredVersionedKeys()
const answeredVersionedKeyLimit = 200

function readAnsweredVersionedKeys() {
  try {
    const stored = JSON.parse(localStorage.getItem(answeredVersionedKeyStorage) ?? '[]') as unknown
    return new Map(Array.isArray(stored) ? stored.filter((pair): pair is [string, string] => Array.isArray(pair) && typeof pair[0] === 'string' && typeof pair[1] === 'string') : [])
  } catch {
    return new Map<string, string>()
  }
}

function writeAnsweredVersionedKeys() {
  try { localStorage.setItem(answeredVersionedKeyStorage, JSON.stringify([...answeredVersionedKeys])) } catch { /* storage unavailable: the in-memory map still covers this page */ }
}

/**
 * True when the failed request reached the server and got an HTTP answer (any status); false for a network error, an
 * abort, or a read that ran out of time on the panel's side (status 0, CLIENT_TIMEOUT in shared/api/client.ts).
 */
export function failedWithHttpAnswer(error: unknown) {
  const status = (error as { status?: unknown } | null)?.status
  return error instanceof Error && typeof status === 'number' && status > 0
}

/** Title of the information notice shown for a still-running answer. */
export const stillRunningTitle = 'İşlem Sürüyor'

/**
 * True when the server is still working on the same key (409 IDEMPOTENCY_IN_PROGRESS): the first attempt has not
 * finished, so the key must stay. A fresh key would let a third click start the same work a second time.
 */
export function isStillRunning(error: unknown) {
  // Read by shape like failedWithHttpAnswer: tests that mock the API client replace the ApiRequestError class.
  return failedWithHttpAnswer(error) && (error as { status: number }).status === 409 && (error as { code?: unknown }).code === 'IDEMPOTENCY_IN_PROGRESS'
}

/** The key for the next attempt of a versioned request: the versioned key until the server answered it with an error. */
export function idempotencyKeyForAttempt(versionedKey: string) {
  return answeredVersionedKeys.get(versionedKey) ?? versionedKey
}

/**
 * Sends a versioned request with the right key for this attempt. A double click or a resend after a lost answer
 * (network error, no HTTP answer) or "still running" (409 IDEMPOTENCY_IN_PROGRESS) reuses the key; after any other HTTP
 * error the next attempt gets a fresh key, so a stored
 * 4xx is not replayed and a 5xx cannot lock the action behind 409 IDEMPOTENCY_RESULT_UNKNOWN for 24 hours. After a
 * success the key in use is kept, so a repeat on the same version replays the success instead of acting twice.
 */
export async function sendWithVersionedIdempotencyKey<T>(versionedKey: string, send: (key: string) => Promise<T>): Promise<T> {
  const key = idempotencyKeyForAttempt(versionedKey)
  try {
    return await send(key)
  } catch (error) {
    if (failedWithHttpAnswer(error) && !isStillRunning(error)) {
      answeredVersionedKeys.delete(versionedKey)
      answeredVersionedKeys.set(versionedKey, newIdempotencyKey(versionedKey.split(':')[0] || undefined))
      while (answeredVersionedKeys.size > answeredVersionedKeyLimit) {
        const oldest = answeredVersionedKeys.keys().next().value
        if (oldest === undefined) break
        answeredVersionedKeys.delete(oldest)
      }
      writeAnsweredVersionedKeys()
    }
    throw error
  }
}

/** Test hook: forgets which versioned keys were answered. */
export function resetAnsweredIdempotencyKeys() {
  answeredVersionedKeys.clear()
  writeAnsweredVersionedKeys()
}
