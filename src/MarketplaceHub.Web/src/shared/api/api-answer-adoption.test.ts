import { describe, expect, it } from 'vitest'

// API-TYPES: a read whose answer the API contract declares takes its type from the contract (ApiAnswer<'operationId'>,
// shared/api/api-answer.ts) or from a type built on it, not from an object type written inline at the call (an
// operation the contract leaves undeclared does not count). The product editor's publication check, channel pricing and
// attribute values write seven inline; 28 more are calls of operations the contract declared later the same day
// (16 Nov 2026: files, mappings, invoice workspace actions, settings). The number may only go down.
const inlineAnswerTypes = 35

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

function inlineAnswerCalls(files: Record<string, string>) {
  const found: string[] = []
  for (const [file, text] of Object.entries(files)) {
    for (const match of text.matchAll(/\bhub(?:Api|Poll)</g)) {
      const typeStart = match.index + match[0].length - 1
      const answerType = enclosed(text, typeStart, '<', '>')
      if (answerType === null || !answerType.includes('{')) continue
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

  it(`leaves no more than ${inlineAnswerTypes} inline answer types for declared operations`, () => {
    const found = inlineAnswerCalls(sources)
    expect(found.length, `inline answer types:\n${found.join('\n')}`).toBeLessThanOrEqual(inlineAnswerTypes)
  })
})
