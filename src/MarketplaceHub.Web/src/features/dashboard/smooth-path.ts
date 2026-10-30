// An SVG path through every point, drawn as a monotone cubic curve (Fritsch–Carlson): it bends smoothly between days
// but never rises above or dips below its neighbours, so a peak on the chart is a real peak and no day looks negative.
export function smoothPath(points: Array<[number, number]>): string {
  if (!points.length) return ''
  const at = ([x, y]: [number, number]) => `${x.toFixed(1)} ${y.toFixed(1)}`
  if (points.length < 3) return points.map((point, index) => `${index ? 'L' : 'M'}${at(point)}`).join(' ')
  const count = points.length
  const slopes = points.slice(0, -1).map(([x, y], index) => (points[index + 1][1] - y) / (points[index + 1][0] - x))
  const tangents = points.map((_, index) => {
    if (index === 0) return slopes[0]
    if (index === count - 1) return slopes[count - 2]
    const before = slopes[index - 1], after = slopes[index]
    return before * after <= 0 ? 0 : (before + after) / 2
  })
  for (let index = 0; index < count - 1; index++) {
    if (slopes[index] === 0) { tangents[index] = 0; tangents[index + 1] = 0; continue }
    const a = tangents[index] / slopes[index], b = tangents[index + 1] / slopes[index]
    const length = Math.hypot(a, b)
    if (length > 3) { const scale = 3 / length; tangents[index] = scale * a * slopes[index]; tangents[index + 1] = scale * b * slopes[index] }
  }
  let path = `M${at(points[0])}`
  for (let index = 0; index < count - 1; index++) {
    const [x0, y0] = points[index], [x1, y1] = points[index + 1], third = (x1 - x0) / 3
    path += ` C${at([x0 + third, y0 + tangents[index] * third])} ${at([x1 - third, y1 - tangents[index + 1] * third])} ${at([x1, y1])}`
  }
  return path
}
