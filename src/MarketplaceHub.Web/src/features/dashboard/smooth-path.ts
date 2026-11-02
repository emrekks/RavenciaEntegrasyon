// An SVG path through every point, drawn as a Catmull-Rom curve (each point's tangent is the slope between its two
// neighbours), so the line flows through small rises and dips without flattening at every one of them. The control
// points are kept inside the chart's own range (the lowest and highest point), so the curve never dips below the
// baseline (no hour looks negative) and never rises above the highest real value.
export function smoothPath(points: Array<[number, number]>): string {
  if (!points.length) return ''
  const at = ([x, y]: [number, number]) => `${x.toFixed(1)} ${y.toFixed(1)}`
  if (points.length < 3) return points.map((point, index) => `${index ? 'L' : 'M'}${at(point)}`).join(' ')
  const count = points.length
  const ys = points.map(([, y]) => y)
  const low = Math.min(...ys), high = Math.max(...ys)
  const clamp = (y: number) => Math.min(high, Math.max(low, y))
  const slope = (from: number, to: number) => (points[to][1] - points[from][1]) / (points[to][0] - points[from][0])
  const tangents = points.map((_, index) => index === 0 ? slope(0, 1) : index === count - 1 ? slope(count - 2, count - 1) : slope(index - 1, index + 1))
  let path = `M${at(points[0])}`
  for (let index = 0; index < count - 1; index++) {
    const [x0, y0] = points[index], [x1, y1] = points[index + 1], third = (x1 - x0) / 3
    // A flat run stays flat: between two equal points the curve does not bulge.
    const flat = y0 === y1
    const c0 = flat ? y0 : clamp(y0 + tangents[index] * third)
    const c1 = flat ? y1 : clamp(y1 - tangents[index + 1] * third)
    path += ` C${at([x0 + third, c0])} ${at([x1 - third, c1])} ${at([x1, y1])}`
  }
  return path
}
