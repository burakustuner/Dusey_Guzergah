import type { Guide, Point, Vertex } from './types'

export type CurveBuffer = {
  station: Float64Array
  elevation: Float64Array
  count: number
}

type Range = {
  active: boolean
  startStation: number
  endStation: number
  startElevation: number
  endElevation: number
  startSlope: number
  endSlope: number
  curveType: number
  radius: number
}

const rangePool: Range[] = []

export function createCurveBuffer(): CurveBuffer {
  return { station: new Float64Array(4096), elevation: new Float64Array(4096), count: 0 }
}

export function buildVerticalCurve(vertices: readonly Vertex[]): Point[] {
  const buffer = createCurveBuffer()
  refreshEditorGeometry(vertices, buffer, [])
  const points: Point[] = []
  for (let index = 0; index < buffer.count; index++) {
    points.push({ station: buffer.station[index], elevation: buffer.elevation[index] })
  }
  return points
}

export function buildVerticalCurveGuides(vertices: readonly Vertex[]): Guide[] {
  const guides: Guide[] = []
  refreshEditorGeometry(vertices, createCurveBuffer(), guides)
  return guides
}

export function refreshEditorGeometry(vertices: readonly Vertex[], buffer: CurveBuffer, guides: Guide[]) {
  const ranges = prepareRanges(vertices)
  fillCurve(vertices, ranges, buffer)
  fillGuides(vertices, ranges, guides)
}

const scratchBuffer = createCurveBuffer()
const slicePool: Vertex[] = []

export function patchEditorGeometry(vertices: readonly Vertex[], buffer: CurveBuffer, guides: Guide[], movedIndex: number) {
  if (buffer.count < 2 || movedIndex < 0 || vertices.length < 2) {
    refreshEditorGeometry(vertices, buffer, guides)
    return
  }
  const ranges = prepareRanges(vertices)
  const left = leftBoundary(vertices, ranges, movedIndex)
  const right = rightBoundary(vertices, ranges, movedIndex)
  fillGuides(vertices, ranges, guides)
  const lo = Math.max(0, movedIndex - 2)
  const hi = Math.min(vertices.length - 1, movedIndex + 2)
  slicePool.length = hi - lo + 1
  for (let index = lo; index <= hi; index++) {
    slicePool[index - lo] = vertices[index]
  }
  const sliceRanges = prepareRanges(slicePool)
  fillCurve(slicePool, sliceRanges, scratchBuffer)
  spliceRange(buffer, left, right, scratchBuffer)
}

function leftBoundary(vertices: readonly Vertex[], ranges: Range[], index: number) {
  const anchor = index - 2
  if (anchor < 0) {
    return Number.NEGATIVE_INFINITY
  }
  return ranges[anchor].active ? ranges[anchor].endStation : vertices[anchor].station
}

function rightBoundary(vertices: readonly Vertex[], ranges: Range[], index: number) {
  const anchor = index + 2
  if (anchor >= vertices.length) {
    return Number.POSITIVE_INFINITY
  }
  return ranges[anchor].active ? ranges[anchor].startStation : vertices[anchor].station
}

function spliceRange(buffer: CurveBuffer, left: number, right: number, insert: CurveBuffer) {
  let start = 0
  while (start < buffer.count && buffer.station[start] <= left) {
    start += 1
  }
  let end = start
  while (end < buffer.count && buffer.station[end] < right) {
    end += 1
  }
  let from = 0
  while (from < insert.count && insert.station[from] <= left) {
    from += 1
  }
  let to = from
  while (to < insert.count && insert.station[to] < right) {
    to += 1
  }
  const insertCount = to - from
  const removeCount = end - start
  const next = buffer.count - removeCount + insertCount
  ensure(buffer, next)
  if (insertCount !== removeCount) {
    buffer.station.copyWithin(start + insertCount, end, buffer.count)
    buffer.elevation.copyWithin(start + insertCount, end, buffer.count)
  }
  for (let index = 0; index < insertCount; index++) {
    buffer.station[start + index] = insert.station[from + index]
    buffer.elevation[start + index] = insert.elevation[from + index]
  }
  buffer.count = next
}

function ensure(buffer: CurveBuffer, count: number) {
  if (count <= buffer.station.length) {
    return
  }
  let capacity = buffer.station.length
  while (capacity < count) {
    capacity *= 2
  }
  const station = new Float64Array(capacity)
  const elevation = new Float64Array(capacity)
  station.set(buffer.station.subarray(0, buffer.count))
  elevation.set(buffer.elevation.subarray(0, buffer.count))
  buffer.station = station
  buffer.elevation = elevation
}

function prepareRanges(vertices: readonly Vertex[]) {
  while (rangePool.length < vertices.length) {
    rangePool.push({
      active: false,
      startStation: 0,
      endStation: 0,
      startElevation: 0,
      endElevation: 0,
      startSlope: 0,
      endSlope: 0,
      curveType: 0,
      radius: 0,
    })
  }
  for (let index = 0; index < vertices.length; index++) {
    rangePool[index].active = false
  }
  for (let index = 1; index < vertices.length - 1; index++) {
    const previous = vertices[index - 1]
    const vertex = vertices[index]
    const next = vertices[index + 1]
    const spanIn = vertex.station - previous.station
    const spanOut = next.station - vertex.station
    if (spanIn <= 1e-9 || spanOut <= 1e-9) {
      continue
    }
    let tangentIn = vertex.tangentIn > 0 ? vertex.tangentIn : vertex.curveLength / 2
    let tangentOut = vertex.tangentOut > 0 ? vertex.tangentOut : vertex.curveLength / 2
    tangentIn = Math.min(tangentIn, spanIn * 0.49)
    tangentOut = Math.min(tangentOut, spanOut * 0.49)
    if (tangentIn <= 0 || tangentOut <= 0) {
      continue
    }
    const incomingSlope = (vertex.elevation - previous.elevation) / spanIn
    const outgoingSlope = (next.elevation - vertex.elevation) / spanOut
    const range = rangePool[index]
    range.active = true
    range.startStation = vertex.station - tangentIn
    range.endStation = vertex.station + tangentOut
    range.startElevation = vertex.elevation - incomingSlope * tangentIn
    range.endElevation = vertex.elevation + outgoingSlope * tangentOut
    range.startSlope = incomingSlope
    range.endSlope = outgoingSlope
    range.curveType = vertex.curveType
    range.radius = vertex.radius
  }
  return rangePool
}

function fillCurve(vertices: readonly Vertex[], ranges: Range[], buffer: CurveBuffer) {
  buffer.count = 0
  if (vertices.length === 0) {
    return
  }
  addPoint(buffer, vertices[0].station, vertices[0].elevation)
  if (vertices.length === 1) {
    return
  }
  for (let index = 0; index < vertices.length - 1; index++) {
    const current = vertices[index]
    const next = vertices[index + 1]
    const outgoing = ranges[index].active ? ranges[index] : null
    const incoming = ranges[index + 1]?.active ? ranges[index + 1] : null
    const tangentStartStation = outgoing ? outgoing.endStation : current.station
    const tangentEndStation = incoming ? incoming.startStation : next.station
    if (tangentEndStation > tangentStartStation) {
      const span = next.station - current.station
      if (span > 0) {
        const gradeIn = (next.elevation - current.elevation) / span
        const tangentStartElevation = outgoing
          ? outgoing.endElevation
          : current.elevation + gradeIn * (tangentStartStation - current.station)
        const tangentEndElevation = incoming
          ? incoming.startElevation
          : current.elevation + gradeIn * (tangentEndStation - current.station)
        addPoint(buffer, tangentStartStation, tangentStartElevation)
        addPoint(buffer, tangentEndStation, tangentEndElevation)
      }
    }
    if (incoming) {
      addCurveSamples(buffer, incoming)
    }
  }
}

function fillGuides(vertices: readonly Vertex[], ranges: Range[], guides: Guide[]) {
  let count = 0
  for (let index = 0; index < vertices.length; index++) {
    const range = ranges[index]
    if (!range?.active) {
      continue
    }
    const guide = guides[count] ?? {
      vertexIndex: index,
      start: { station: 0, elevation: 0 },
      vertex: { station: 0, elevation: 0 },
      end: { station: 0, elevation: 0 },
    }
    guide.vertexIndex = index
    guide.start.station = range.startStation
    guide.start.elevation = range.startElevation
    guide.vertex.station = vertices[index].station
    guide.vertex.elevation = vertices[index].elevation
    guide.end.station = range.endStation
    guide.end.elevation = range.endElevation
    guides[count] = guide
    count += 1
  }
  guides.length = count
}

function addCurveSamples(buffer: CurveBuffer, range: Range) {
  const length = range.endStation - range.startStation
  const sampleCount = Math.min(80, Math.max(12, Math.ceil(length / 1.5)))
  const [a, b, c] = parabolicCoefficients(length, range.startElevation, range.endElevation, range.startSlope, range.endSlope)
  const circle = range.curveType === 2 && range.radius > 1e-9 ? circleModel(range) : null
  for (let index = 0; index <= sampleCount; index++) {
    const station = range.startStation + length * (index / sampleCount)
    const x = station - range.startStation
    const elevation = circle ? circleElevation(circle, station, range) : a * x * x + b * x + c
    addPoint(buffer, station, elevation)
  }
}

function elevationParabolic(range: Range, station: number) {
  const length = range.endStation - range.startStation
  if (length <= 1e-12) {
    return range.startElevation
  }
  const [a, b, c] = parabolicCoefficients(length, range.startElevation, range.endElevation, range.startSlope, range.endSlope)
  const x = station - range.startStation
  return a * x * x + b * x + c
}

function parabolicCoefficients(length: number, startElevation: number, endElevation: number, startSlope: number, endSlope: number) {
  let a11 = 0
  let a12 = 0
  let a13 = 0
  let a22 = 0
  let a23 = 0
  let a33 = 0
  let r1 = 0
  let r2 = 0
  let r3 = 0
  const accumulate = (ra: number, rb: number, rc: number, rhs: number) => {
    a11 += ra * ra
    a12 += ra * rb
    a13 += ra * rc
    a22 += rb * rb
    a23 += rb * rc
    a33 += rc * rc
    r1 += ra * rhs
    r2 += rb * rhs
    r3 += rc * rhs
  }
  accumulate(0, 0, 1, startElevation)
  accumulate(0, 1, 0, startSlope)
  accumulate(length * length, length, 1, endElevation)
  accumulate(2 * length, 1, 0, endSlope)
  const l11 = Math.sqrt(Math.max(a11, 1e-30))
  const l21 = a12 / l11
  const l31 = a13 / l11
  const l22 = Math.sqrt(Math.max(a22 - l21 * l21, 1e-30))
  const l32 = (a23 - l21 * l31) / l22
  const l33 = Math.sqrt(Math.max(a33 - l31 * l31 - l32 * l32, 1e-30))
  const z1 = r1 / l11
  const z2 = (r2 - l21 * z1) / l22
  const z3 = (r3 - l31 * z1 - l32 * z2) / l33
  const c = z3 / l33
  const b = (z2 - l32 * c) / l22
  const a = (z1 - l21 * b - l31 * c) / l11
  return [a, b, c] as const
}

function circleModel(range: Range) {
  const radius = range.radius
  const x0 = range.startStation
  const y0 = range.startElevation
  const x1 = range.endStation
  const y1 = range.endElevation
  const dx = x1 - x0
  const dy = y1 - y0
  const distance = Math.hypot(dx, dy)
  if (distance <= 1e-9 || distance > 2 * radius + 1e-6) {
    return null
  }
  const midX = (x0 + x1) * 0.5
  const midY = (y0 + y1) * 0.5
  const ux = dx / distance
  const uy = dy / distance
  const px = -uy
  const py = ux
  const height = Math.sqrt(Math.max(0, radius * radius - (distance * 0.5) * (distance * 0.5)))
  const centerA = { x: midX + height * px, y: midY + height * py }
  const centerB = { x: midX - height * px, y: midY - height * py }
  const center = slopeError(centerA, x0, y0, range.startSlope) <= slopeError(centerB, x0, y0, range.startSlope)
    ? centerA
    : centerB
  return { centerX: center.x, centerY: center.y, radius, referenceX: x0, referenceY: y0 }
}

function circleElevation(model: { centerX: number; centerY: number; radius: number; referenceX: number; referenceY: number }, station: number, range: Range) {
  const rootArg = model.radius * model.radius - (station - model.centerX) * (station - model.centerX)
  if (rootArg < 0) {
    return elevationParabolic(range, station)
  }
  return circleBranch(station, model.centerX, model.centerY, model.radius, model.referenceX, model.referenceY)
}

function slopeError(center: { x: number; y: number }, x: number, y: number, slope: number) {
  const dy = y - center.y
  if (Math.abs(dy) < 1e-12) {
    return Number.POSITIVE_INFINITY
  }
  return Math.abs(-(x - center.x) / dy - slope)
}

function circleBranch(x: number, centerX: number, centerY: number, radius: number, referenceX: number, referenceY: number) {
  const root = Math.sqrt(Math.max(0, radius * radius - (x - centerX) * (x - centerX)))
  const referenceRoot = Math.sqrt(Math.max(0, radius * radius - (referenceX - centerX) * (referenceX - centerX)))
  const plus = centerY + root
  const minus = centerY - root
  return Math.abs(referenceY - (centerY + referenceRoot)) <= Math.abs(referenceY - (centerY - referenceRoot)) ? plus : minus
}

function addPoint(buffer: CurveBuffer, station: number, elevation: number) {
  if (buffer.count > 0 && Math.abs(buffer.station[buffer.count - 1] - station) <= 0.000001) {
    return
  }
  if (buffer.count >= buffer.station.length) {
    const grown = new Float64Array(buffer.station.length * 2)
    const grownElevation = new Float64Array(buffer.elevation.length * 2)
    grown.set(buffer.station)
    grownElevation.set(buffer.elevation)
    buffer.station = grown
    buffer.elevation = grownElevation
  }
  buffer.station[buffer.count] = station
  buffer.elevation[buffer.count] = elevation
  buffer.count += 1
}
