import ts from 'typescript'
import { describe, expect, it } from 'vitest'

// A query that refetches on a timer is a poll, not the person using the panel: its reads must go through hubPoll (or carry
// backgroundPollInit / backgroundPollHeaders themselves), so the server can leave the session's idle clock alone. This
// walks every query object in the panel that has a refetchInterval and checks its queryFn, following helpers declared in
// the same file.
const sources = import.meta.glob(['../../**/*.{ts,tsx}', '!../../**/*.test.{ts,tsx}', '!../../test/**'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>

const pollMarker = /\bhubPoll\b|\bbackgroundPollInit\b|\bbackgroundPollHeaders\b/

function declarationsByName(file: ts.SourceFile) {
  const map = new Map<string, ts.Node[]>()
  const add = (name: string, node: ts.Node) => map.set(name, [...(map.get(name) ?? []), node])
  const visit = (node: ts.Node) => {
    if (ts.isFunctionDeclaration(node) && node.name) add(node.name.text, node)
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name)) add(node.name.text, node)
    ts.forEachChild(node, visit)
  }
  visit(file)
  return map
}

function resolvedText(node: ts.Node, file: ts.SourceFile, declarations: Map<string, ts.Node[]>, seen = new Set<ts.Node>(), depth = 0): string {
  let text = node.getText(file)
  if (depth > 3) return text
  const visit = (child: ts.Node) => {
    if (ts.isIdentifier(child)) for (const declaration of declarations.get(child.text) ?? []) {
      if (seen.has(declaration)) continue
      seen.add(declaration)
      text += '\n' + resolvedText(declaration, file, declarations, seen, depth + 1)
    }
    ts.forEachChild(child, visit)
  }
  visit(node)
  return text
}

function pollingQueriesWithoutPollMarker(path: string, source: string) {
  const file = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true, path.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS)
  const declarations = declarationsByName(file)
  const offenders: string[] = []
  const property = (node: ts.ObjectLiteralExpression, name: string) => node.properties.find((item): item is ts.PropertyAssignment => ts.isPropertyAssignment(item) && item.name.getText(file) === name)
  const visit = (node: ts.Node) => {
    if (ts.isObjectLiteralExpression(node)) {
      const interval = property(node, 'refetchInterval')
      const queryFn = property(node, 'queryFn')
      if (interval && queryFn && interval.initializer.kind !== ts.SyntaxKind.FalseKeyword) {
        const own = queryFn.initializer.getText(file)
        const directHubApi = /\bhubApi\s*(<[^>]*>)?\s*\(/.test(own) && !/\bpoll\b/.test(own)
        if (directHubApi || !pollMarker.test(resolvedText(queryFn.initializer, file, declarations))) {
          offenders.push(`${path}:${file.getLineAndCharacterOfPosition(node.getStart(file)).line + 1}`)
        }
      }
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return offenders
}

describe('every polling query reads through hubPoll', () => {
  it('finds polling queries to check', () => {
    const count = Object.values(sources).filter(source => /refetchInterval:/.test(source)).length
    expect(count).toBeGreaterThan(10)
  })

  it('has no query with a refetchInterval whose queryFn skips hubPoll', () => {
    const offenders = Object.entries(sources).flatMap(([path, source]) => /refetchInterval/.test(source) ? pollingQueriesWithoutPollMarker(path, source) : [])
    expect(offenders).toEqual([])
  })

  it('catches a polling query that reads through hubApi', () => {
    const source = "const q = useQuery({ queryKey: ['x'], queryFn: () => hubApi('/x'), refetchInterval: 1000 })"
    expect(pollingQueriesWithoutPollMarker('sample.ts', source)).toEqual(['sample.ts:1'])
    const helper = "async function load() { return hubPoll('/x') }\nconst q = useQuery({ queryKey: ['x'], queryFn: () => load(), refetchInterval: 1000 })"
    expect(pollingQueriesWithoutPollMarker('sample.ts', helper)).toEqual([])
  })
})
