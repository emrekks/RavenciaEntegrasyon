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
