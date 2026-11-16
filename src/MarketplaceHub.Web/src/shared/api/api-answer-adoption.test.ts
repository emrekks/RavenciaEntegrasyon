import { describe, expect, it } from 'vitest'

// API-TYPES: a read whose answer the API contract declares takes its type from the contract (ApiAnswer<'operationId'>,
// shared/api/api-answer.ts) or from a type built on it, not from an object type written inline at the call (an
// operation the contract leaves undeclared does not count). Every declared operation is read through the contract since
// 16 Nov 2026 (the last 39 inline types converted); the number stays at 0.
const inlineAnswerTypes = 0

const sources = import.meta.glob<string>(['../../**/*.ts', '../../**/*.tsx', '!../../**/*.test.ts', '!../../**/*.test.tsx', '!./generated/**'], { query: '?raw', import: 'default', eager: true })
const contract = import.meta.glob<string>('./generated/api-types.ts', { query: '?raw', import: 'default', eager: true })['./generated/api-types.ts']

type Operation = { id: string; method: string; path: RegExp; declared: boolean }
const operations: Operation[] = [...contract.matchAll(/^ {2}(\w+): \{\n {4}method: "(\w+)"\n {4}path: "([^"]+)"[\s\S]*?\n {4}response: ([^\n]+)\n/gm)]
  .map(([, id, method, path, response]) => ({
    id, method,
    path: new RegExp(`^${path.replace(/^\/api\/v1/, '').replace(/\{[^}]+\}/g, '[^/?]+')}$`),
    declared: response !== 'unknown' && response !== 'void'
  }))
  // The most literal path first: /jobs/page before /jobs/{id}.
  .sort((left, right) => right.path.source.replace(/\[\^\/\?\]\+/g, '').length - left.path.source.replace(/\[\^\/\?\]\+/g, '').length)

// The text from an opening bracket to its closing one (quotes skipped).
function enclosed(text: string, start: number, open: string, close: string) {
  let depth = 0
  let quote: string | null = null
  for (let index = start; index < text.length; index++) {
    const char = text[index]
    if (quote) { if (char === '\\') index++; else if (char === quote) quote = null; continue }
    if (char === '\'' || char === '"' || char === '`') { if (open !== '<') quote = char; continue }
    if (char === open) depth++
    else if (char === close && --depth === 0) return text.slice(start + 1, index)
  }
  return null
}

// The calls of a declared operation whose answer type passes `counts` (an inline object type, or a hand-written name).
function answerCalls(files: Record<string, string>, counts: (answerType: string) => boolean) {
  const found: string[] = []
  for (const [file, text] of Object.entries(files)) {
    for (const match of text.matchAll(/\b(?:hubApi|hubPoll|loadAllPages)</g)) {
      const typeStart = match.index + match[0].length - 1
      const answerType = enclosed(text, typeStart, '<', '>')
      if (answerType === null || !counts(answerType)) continue
      const callStart = typeStart + answerType.length + 2
      if (text[callStart] !== '(') continue
      const args = enclosed(text, callStart, '(', ')') ?? ''
      const path = /^\s*['`]([^'`]*)['`]/.exec(args)?.[1]
      if (!path?.startsWith('/')) continue
      const method = /method:\s*'(\w+)'/.exec(args)?.[1] ?? 'GET'
      const route = path.replace(/\$\{[^}]*\}/g, 'x').split('?')[0]
      const operation = operations.find(candidate => candidate.method === method && candidate.path.test(route))
      if (operation?.declared) found.push(`${file}:${text.slice(0, match.index).split('\n').length} ${operation.id}`)
    }
  }
  return found
}

const inlineAnswerCalls = (files: Record<string, string>) => answerCalls(files, answerType => answerType.includes('{'))

// API-TYPES, round 7: a hand-written object type named as the answer of a declared operation (`type Payouts = { … }`);
// an alias of the contract (`= ApiAnswer<…>`, a narrowed `Omit<…> & …`) does not count. The number may only go down;
// round 8 took it from 29 to 3, round 9 to 0 (Payouts and PayoutLines narrowed at their fetches).
const handWrittenAnswerTypes = 0
function handWrittenNames(files: Record<string, string>) {
  const names = new Set<string>()
  for (const text of Object.values(files)) for (const match of text.matchAll(/^(?:export )?type (\w+)(?:<[^=]*>)? = \{/gm)) names.add(match[1])
  return names
}
function handWrittenAnswerCalls(files: Record<string, string>) {
  const names = handWrittenNames(files)
  return answerCalls(files, answerType => names.has(answerType.trim().replace(/\[\]$/, '')))
}

describe('contract answer types', () => {
  it('reads the contract and the app sources', () => {
    expect(operations.length).toBeGreaterThan(300)
    expect(Object.keys(sources).length).toBeGreaterThan(50)
  })

  it('finds an inline answer type of a declared operation, and only that', () => {
    const files = {
      'a.ts': "hubApi<{ id: string }>(`/orders/${id}`, { signal })\nhubApi<ApiAnswer<'getOrdersById'>>(`/orders/${id}`)\nhubPoll<{ total: number }>('/notifications/summary')",
      'b.ts': "hubApi<{ jobs: string[] }>('/not-in-the-contract', { method: 'POST' })\nhubApi<{ id: string }>(path)"
    }
    expect(inlineAnswerCalls(files)).toEqual(['a.ts:1 getOrdersById', 'a.ts:3 getNotificationsSummary'])
  })

  it('finds a hand-written named answer type of a declared operation, not an alias', () => {
    const files = {
      'a.ts': "type Hand = { id: string }\ntype Alias = ApiAnswer<'getOrdersById'>\nhubApi<Hand>(`/orders/${id}`)\nhubApi<Alias>(`/orders/${id}`)\nhubApi<Hand[]>('/not-declared')"
    }
    expect(handWrittenAnswerCalls(files)).toEqual(['a.ts:3 getOrdersById'])
  })

  it(`leaves no more than ${handWrittenAnswerTypes} hand-written answer types for declared operations`, () => {
    const found = handWrittenAnswerCalls(sources)
    expect(found.length, `hand-written answer types:\n${found.join('\n')}`).toBeLessThanOrEqual(handWrittenAnswerTypes)
  })

  it(`leaves no more than ${inlineAnswerTypes} inline answer types for declared operations`, () => {
    const found = inlineAnswerCalls(sources)
    expect(found.length, `inline answer types:\n${found.join('\n')}`).toBeLessThanOrEqual(inlineAnswerTypes)
  })
})
