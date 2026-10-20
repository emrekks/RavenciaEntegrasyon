import { failedWithHttpAnswer, newIdempotencyKey } from '../../../shared/idempotency'

// The stock decisions of a return line still waiting for an answer, by line: the body sent and its key.
export type PendingDispositionKeys = Map<string, { body: string; key: string }>

/**
 * Sends one stock decision with a key stable for that pending decision. A decision does not change the return's
 * version, so the key cannot come from the version (two equal decisions would replay the first). Instead the same
 * decision sent again after a lost answer (a network error, no HTTP answer) reuses its key and the server replays it,
 * so a "Satılabilir" quantity is not put into stock twice; once the server answered (success or error) the decision is
 * settled and the next one, even an equal one made on purpose, gets a fresh key.
 */
export async function sendStockDisposition<T>(pending: PendingDispositionKeys, returnId: string, lineId: string, body: string, send: (key: string) => Promise<T>): Promise<T> {
  const waiting = pending.get(lineId)
  const key = waiting && waiting.body === body ? waiting.key : `return-stock-disposition:${returnId}:${newIdempotencyKey()}`
  pending.set(lineId, { body, key })
  try {
    const result = await send(key)
    pending.delete(lineId)
    return result
  } catch (error) {
    if (failedWithHttpAnswer(error)) pending.delete(lineId)
    throw error
  }
}
