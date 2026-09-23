import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react'
import { formatChainage } from './chainage'
import type { EditorRow } from './editor'
import type { Bounds, CanvasTool, DesignResult, Guide, Layers, Point, SessionResponse, Vertex } from './types'
import { createCurveBuffer, patchEditorGeometry, refreshEditorGeometry, type CurveBuffer } from './verticalCurve'

const EXAG = 10
const MIN_ZOOM = 0.35
const MAX_ZOOM = 600

type View = { scale: number; ox: number; oy: number }

export type ProfileCanvasHandle = {
  zoomBy: (factor: number) => void
  resetView: () => void
  zoomTo: (station: number, elevation: number) => void
}

type Props = {
  session: SessionResponse | null
  design: DesignResult | null
  layers: Layers
  pipeOffset: number
  tool: CanvasTool
  alignmentVertices: Vertex[]
  alignmentCurve: Point[]
  alignmentGuides: Guide[]
  editorRows: EditorRow[]
  selectedEditorId: string | null
  onSelectEditor: (id: string) => void
  onMoveEditor: (id: string, station: number, elevation: number) => void
  onFinishEditorDrag: () => void
  onPick: (station: number) => void
  onContextMenuAt: (x: number, y: number, station: number, elevation: number) => void
}

function screenX(view: View, station: number) {
  return view.ox + station * view.scale
}

function screenY(view: View, elevation: number) {
  return view.oy - elevation * view.scale * EXAG
}

function stationAt(view: View, x: number) {
  return (x - view.ox) / view.scale
}

function elevationAt(view: View, y: number) {
  return (view.oy - y) / (view.scale * EXAG)
}

function expandBounds(bounds: Bounds, paddingMeters: number): Bounds {
  return {
    minStation: bounds.minStation,
    maxStation: Math.max(bounds.maxStation, bounds.minStation + 1),
    minElevation: bounds.minElevation - paddingMeters,
    maxElevation: bounds.maxElevation + paddingMeters,
  }
}

function unionBounds(current: Bounds | null, points: Point[]): Bounds | null {
  if (points.length === 0) {
    return current
  }
  let minStation = current?.minStation ?? points[0].station
  let maxStation = current?.maxStation ?? points[0].station
  let minElevation = current?.minElevation ?? points[0].elevation
  let maxElevation = current?.maxElevation ?? points[0].elevation
  for (const point of points) {
    minStation = Math.min(minStation, point.station)
    maxStation = Math.max(maxStation, point.station)
    minElevation = Math.min(minElevation, point.elevation)
    maxElevation = Math.max(maxElevation, point.elevation)
  }
  return { minStation, maxStation, minElevation, maxElevation }
}

function fitView(bounds: Bounds, width: number, height: number, baseScale: number | null): View {
  const padLeft = 76
  const padRight = 24
  const padTop = 28
  const padBottom = 52
  const spanS = Math.max(bounds.maxStation - bounds.minStation, 1e-6)
  const spanE = Math.max(bounds.maxElevation - bounds.minElevation, 1e-6)
  const availW = Math.max(40, width - padLeft - padRight)
  const availH = Math.max(40, height - padTop - padBottom)
  let scale = Math.min(availW / spanS, availH / (spanE * EXAG))
  if (baseScale && baseScale > 0) {
    const zoom = scale / baseScale
    if (zoom > MAX_ZOOM) {
      scale = baseScale * MAX_ZOOM
    } else if (zoom < MIN_ZOOM) {
      scale = baseScale * MIN_ZOOM
    }
  }
  const contentW = spanS * scale
  const contentH = spanE * scale * EXAG
  const left = padLeft + (availW - contentW) / 2
  const top = padTop + (availH - contentH) / 2
  return {
    scale,
    ox: left - bounds.minStation * scale,
    oy: top + bounds.maxElevation * scale * EXAG,
  }
}

function zoomView(view: View, baseScale: number, factor: number, x: number, y: number): View {
  const station = stationAt(view, x)
  const elevation = elevationAt(view, y)
  const zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, (view.scale / baseScale) * factor))
  const scale = baseScale * zoom
  return {
    scale,
    ox: x - station * scale,
    oy: y + elevation * scale * EXAG,
  }
}

function lowerBound(points: { station: number }[], station: number) {
  let lo = 0
  let hi = points.length
  while (lo < hi) {
    const mid = (lo + hi) >> 1
    if (points[mid].station < station) {
      lo = mid + 1
    } else {
      hi = mid
    }
  }
  return lo
}

function visibleStationSpan(view: View, width: number, padPx = 64) {
  const left = stationAt(view, -padPx)
  const right = stationAt(view, width + padPx)
  return left <= right ? [left, right] as const : [right, left] as const
}

function visibleElevationSpan(view: View, height: number, padPx = 48) {
  const top = elevationAt(view, -padPx)
  const bottom = elevationAt(view, height + padPx)
  return top >= bottom ? [bottom, top] as const : [top, bottom] as const
}

function sliceByStation<T extends { station: number }>(points: T[], minStation: number, maxStation: number) {
  if (points.length === 0) {
    return points
  }
  let start = lowerBound(points, minStation)
  let end = lowerBound(points, maxStation)
  if (start > 0) {
    start -= 1
  }
  if (end < points.length) {
    end += 1
  }
  if (start === 0 && end === points.length) {
    return points
  }
  return points.slice(start, end)
}

function strokePolyline(
  ctx: CanvasRenderingContext2D,
  points: Point[],
  view: View,
  color: string,
  width: number,
  dash?: number[],
  elevationOffset = 0,
) {
  if (points.length < 2) {
    return
  }
  ctx.beginPath()
  ctx.moveTo(screenX(view, points[0].station), screenY(view, points[0].elevation - elevationOffset))
  for (let i = 1; i < points.length; i++) {
    ctx.lineTo(screenX(view, points[i].station), screenY(view, points[i].elevation - elevationOffset))
  }
  ctx.strokeStyle = color
  ctx.lineWidth = width
  ctx.setLineDash(dash ?? [])
  ctx.stroke()
  ctx.setLineDash([])
}

function strokeStationBuffer(
  ctx: CanvasRenderingContext2D,
  buffer: CurveBuffer,
  view: View,
  minStation: number,
  maxStation: number,
  color: string,
  width: number,
) {
  const { station, elevation, count } = buffer
  if (count < 2) {
    return
  }
  let start = lowerBoundStations(station, count, minStation)
  let end = lowerBoundStations(station, count, maxStation)
  if (start > 0) {
    start -= 1
  }
  if (end < count) {
    end += 1
  }
  if (end - start < 2) {
    return
  }
  ctx.beginPath()
  ctx.moveTo(screenX(view, station[start]), screenY(view, elevation[start]))
  for (let index = start + 1; index < end; index++) {
    ctx.lineTo(screenX(view, station[index]), screenY(view, elevation[index]))
  }
  ctx.strokeStyle = color
  ctx.lineWidth = width
  ctx.setLineDash([])
  ctx.stroke()
}

function lowerBoundStations(station: Float64Array, count: number, value: number) {
  let lo = 0
  let hi = count
  while (lo < hi) {
    const mid = (lo + hi) >> 1
    if (station[mid] < value) {
      lo = mid + 1
    } else {
      hi = mid
    }
  }
  return lo
}

function sameGeometry(left: EditorRow, right: EditorRow) {
  return left.station === right.station
    && left.elevation === right.elevation
    && left.curveLength === right.curveLength
    && left.curveType === right.curveType
    && left.tangentIn === right.tangentIn
    && left.tangentOut === right.tangentOut
    && left.radius === right.radius
}

function changedVertex(previous: EditorRow[], next: EditorRow[]) {
  if (previous.length !== next.length) {
    return -2
  }
  let index = -1
  for (let cursor = 0; cursor < next.length; cursor++) {
    if (previous[cursor].id !== next[cursor].id) {
      return -2
    }
    if (!sameGeometry(previous[cursor], next[cursor])) {
      if (index >= 0) {
        return -2
      }
      index = cursor
    }
  }
  return index
}

function rememberBuilt(target: EditorRow[], ids: string[], ordered: EditorRow[]) {
  target.length = ordered.length
  ids.length = ordered.length
  for (let index = 0; index < ordered.length; index++) {
    target[index] = ordered[index]
    ids[index] = ordered[index].id
  }
}

function interpolateElevation(profile: Point[], station: number): number {
  if (profile.length === 0) {
    return 0
  }
  if (station <= profile[0].station) {
    return profile[0].elevation
  }
  const last = profile[profile.length - 1]
  if (station >= last.station) {
    return last.elevation
  }
  let lo = 0
  let hi = profile.length - 1
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1
    if (profile[mid].station < station) {
      lo = mid
    } else {
      hi = mid
    }
  }
  const left = profile[lo]
  const right = profile[hi]
  const span = right.station - left.station
  if (Math.abs(span) < 1e-9) {
    return left.elevation
  }
  const ratio = (station - left.station) / span
  return left.elevation + (right.elevation - left.elevation) * ratio
}

function niceStep(span: number, divisions: number) {
  const rough = Math.max(span / divisions, 1e-6)
  const power = 10 ** Math.floor(Math.log10(rough))
  const fraction = rough / power
  const nice = fraction >= 7.5 ? 10 : fraction >= 3.5 ? 5 : fraction >= 1.5 ? 2 : 1
  return nice * power
}

function drawGrid(ctx: CanvasRenderingContext2D, view: View, width: number, height: number) {
  const minStation = stationAt(view, 70)
  const maxStation = stationAt(view, width - 16)
  const maxElevation = elevationAt(view, 20)
  const minElevation = elevationAt(view, height - 36)
  const stationStep = niceStep(Math.max(maxStation - minStation, 1), 8)
  const elevationStep = niceStep(Math.max(maxElevation - minElevation, 0.1), 6)
  ctx.save()
  ctx.strokeStyle = '#e6edf3'
  ctx.fillStyle = '#57606a'
  ctx.lineWidth = 1
  ctx.font = '11px Segoe UI, sans-serif'
  ctx.beginPath()
  const firstStation = Math.ceil(minStation / stationStep) * stationStep
  for (let station = firstStation; station <= maxStation; station += stationStep) {
    const x = screenX(view, station)
    ctx.moveTo(x, 8)
    ctx.lineTo(x, height - 28)
    ctx.fillText(formatChainage(station), x + 4, height - 12)
  }
  const firstElevation = Math.ceil(minElevation / elevationStep) * elevationStep
  for (let elevation = firstElevation; elevation <= maxElevation; elevation += elevationStep) {
    const y = screenY(view, elevation)
    ctx.moveTo(64, y)
    ctx.lineTo(width - 8, y)
    ctx.fillText(elevation.toFixed(1), 8, y - 3)
  }
  ctx.stroke()
  ctx.strokeStyle = '#57606a'
  ctx.lineWidth = 1.5
  ctx.beginPath()
  ctx.moveTo(64, 8)
  ctx.lineTo(64, height - 28)
  ctx.lineTo(width - 8, height - 28)
  ctx.stroke()
  ctx.fillText('KM', width - 28, height - 12)
  ctx.fillText('Kot', 8, 16)
  ctx.restore()
}

function drawScale(
  ctx: CanvasRenderingContext2D,
  view: View,
  width: number,
  baseScale: number,
) {
  const horizontal = 1 / view.scale
  const vertical = 1 / (view.scale * EXAG)
  const zoom = baseScale > 0 ? view.scale / baseScale : 1
  const text = [
    'Ölçek',
    `Yatay: 1 px = ${horizontal.toFixed(3)} m`,
    `Düşey: 1 px = ${vertical.toFixed(3)} m`,
    'Düşey büyütme: ×10',
    `Zoom: ${Math.round(zoom * 100)}%`,
  ]
  ctx.save()
  ctx.font = '11px Segoe UI, sans-serif'
  const boxW = 168
  const boxH = 78
  const x = width - boxW - 12
  const y = 12
  ctx.fillStyle = 'rgba(255,255,255,0.92)'
  ctx.strokeStyle = '#d0d7de'
  ctx.lineWidth = 1
  ctx.fillRect(x, y, boxW, boxH)
  ctx.strokeRect(x, y, boxW, boxH)
  ctx.fillStyle = '#57606a'
  text.forEach((line, index) => ctx.fillText(line, x + 8, y + 16 + index * 14))
  ctx.restore()
}

function drawVertices(
  ctx: CanvasRenderingContext2D,
  vertices: Vertex[],
  view: View,
  minElevation: number,
  maxElevation: number,
) {
  ctx.fillStyle = '#f6f8ff'
  ctx.strokeStyle = '#8250df'
  ctx.lineWidth = 2
  for (const vertex of vertices) {
    if (vertex.elevation < minElevation || vertex.elevation > maxElevation) {
      continue
    }
    const x = screenX(view, vertex.station)
    const y = screenY(view, vertex.elevation)
    ctx.fillRect(x - 5, y - 5, 10, 10)
    ctx.strokeRect(x - 5, y - 5, 10, 10)
  }
}

function drawGuides(
  ctx: CanvasRenderingContext2D,
  guides: SessionResponse['ktbGuides'],
  view: View,
  stroke: string,
  fill: string,
  minStation: number,
  maxStation: number,
) {
  ctx.lineWidth = 1.15
  ctx.fillStyle = fill
  ctx.strokeStyle = stroke
  for (const guide of guides) {
    const lo = Math.min(guide.start.station, guide.vertex.station, guide.end.station)
    const hi = Math.max(guide.start.station, guide.vertex.station, guide.end.station)
    if (hi < minStation || lo > maxStation) {
      continue
    }
    ctx.beginPath()
    ctx.moveTo(screenX(view, guide.start.station), screenY(view, guide.start.elevation))
    ctx.lineTo(screenX(view, guide.vertex.station), screenY(view, guide.vertex.elevation))
    ctx.lineTo(screenX(view, guide.end.station), screenY(view, guide.end.elevation))
    ctx.closePath()
    ctx.fill()
    ctx.stroke()
  }
}

function drawSomeLabels(
  ctx: CanvasRenderingContext2D,
  vertices: Vertex[],
  view: View,
  width: number,
  height: number,
) {
  const [minStation, maxStation] = visibleStationSpan(view, width)
  const [minElevation, maxElevation] = visibleElevationSpan(view, height)
  const visible: number[] = []
  for (let index = 0; index < vertices.length; index++) {
    const vertex = vertices[index]
    if (vertex.station < minStation || vertex.station > maxStation || vertex.elevation < minElevation || vertex.elevation > maxElevation) {
      continue
    }
    visible.push(index)
  }
  if (visible.length === 0) {
    return
  }
  const step = Math.max(1, Math.ceil(visible.length / 80))
  ctx.save()
  ctx.font = '11px Segoe UI, sans-serif'
  ctx.textBaseline = 'top'
  ctx.fillStyle = '#fff'
  ctx.strokeStyle = '#d1242f'
  ctx.lineWidth = 1.5
  ctx.beginPath()
  const placed: { index: number; x: number; y: number }[] = []
  for (let seen = 0; seen < visible.length; seen++) {
    const index = visible[seen]
    const vertex = vertices[index]
    const x = screenX(view, vertex.station)
    const y = screenY(view, vertex.elevation)
    ctx.moveTo(x, y - 7)
    ctx.lineTo(x - 6, y + 5)
    ctx.lineTo(x + 6, y + 5)
    ctx.closePath()
    if (seen % step === 0) {
      placed.push({ index, x, y })
    }
  }
  ctx.fill()
  ctx.stroke()
  for (const mark of placed) {
    const vertex = vertices[mark.index]
    const { index, x, y } = mark
    const text = `S${index + 1}  ${formatChainage(vertex.station)}  kot ${vertex.elevation.toFixed(2)}`
    const boxW = ctx.measureText(text).width + 10
    const boxX = index % 2 === 0 ? x + 10 : x - boxW - 10
    const boxY = index % 2 === 0 ? y - 28 : y + 10
    ctx.fillStyle = 'rgba(255,255,255,0.94)'
    ctx.strokeStyle = '#d0d7de'
    ctx.lineWidth = 1
    ctx.fillRect(boxX, boxY, boxW, 18)
    ctx.strokeRect(boxX, boxY, boxW, 18)
    ctx.fillStyle = '#57606a'
    ctx.fillText(text, boxX + 5, boxY + 3)
    ctx.lineWidth = 1.5
    ctx.strokeStyle = '#d1242f'
  }
  ctx.restore()
}

function hitEditorRow(view: View, rows: EditorRow[], x: number, y: number) {
  let best: { row: EditorRow; distance: number } | null = null
  for (const row of rows) {
    const dx = screenX(view, row.station) - x
    const dy = screenY(view, row.elevation) - y
    const distance = dx * dx + dy * dy
    if (distance <= 14 * 14 && (!best || distance < best.distance)) {
      best = { row, distance }
    }
  }
  return best?.row ?? null
}

const ProfileCanvas = forwardRef<ProfileCanvasHandle, Props>(function ProfileCanvas(
  {
    session,
    design,
    layers,
    pipeOffset,
    tool,
    alignmentVertices,
    alignmentCurve,
    alignmentGuides,
    editorRows,
    selectedEditorId,
    onSelectEditor,
    onMoveEditor,
    onFinishEditorDrag,
    onPick,
    onContextMenuAt,
  },
  ref,
) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const hostRef = useRef<HTMLDivElement>(null)
  const viewRef = useRef<View | null>(null)
  const baseScaleRef = useRef(1)
  const userAdjustedRef = useRef(false)
  const dataRef = useRef({ session, design, alignmentCurve, editorRows })
  dataRef.current = { session, design, alignmentCurve, editorRows }
  const [size, setSize] = useState({ width: 0, height: 0 })
  const frameRef = useRef<number | null>(null)
  const focusFrameRef = useRef<number | null>(null)
  const readoutRef = useRef<HTMLDivElement>(null)
  const paintRef = useRef<() => void>(() => {})
  const redrawRef = useRef<() => void>(() => {})
  const sceneRef = useRef({
    session,
    design,
    layers,
    pipeOffset,
    size,
    alignmentCurve,
    alignmentGuides,
    alignmentVertices,
    editorRows,
    selectedEditorId,
    tool,
  })
  sceneRef.current = {
    session,
    design,
    layers,
    pipeOffset,
    size,
    alignmentCurve,
    alignmentGuides,
    alignmentVertices,
    editorRows,
    selectedEditorId,
    tool,
  }
  const draftRef = useRef<EditorRow[] | null>(null)
  const heldRef = useRef<{ id: string; station: number; elevation: number } | null>(null)
  const orderedRef = useRef<EditorRow[]>([])
  const curveBufRef = useRef<CurveBuffer>(createCurveBuffer())
  const guidePoolRef = useRef<Guide[]>([])
  const orderIdsRef = useRef<string[]>([])
  const builtRef = useRef<EditorRow[]>([])
  const dragRef = useRef<
    | { kind: 'pan'; x: number; y: number; view: View }
    | { kind: 'measure'; x: number; y: number; currentX: number; currentY: number }
    | { kind: 'window'; x: number; y: number; currentX: number; currentY: number }
    | { kind: 'vertex'; id: string }
    | null
  >(null)

  function redraw() {
    if (frameRef.current !== null) {
      return
    }
    frameRef.current = requestAnimationFrame(() => {
      frameRef.current = null
      paintRef.current()
    })
  }
  redrawRef.current = redraw

  function dataBounds(): Bounds | null {
    const current = dataRef.current
    if (!current.session) {
      return null
    }
    let bounds: Bounds | null = current.session.bounds
    bounds = unionBounds(bounds, current.session.terrain)
    bounds = unionBounds(bounds, current.session.ktbCurve)
    bounds = unionBounds(bounds, current.alignmentCurve)
    bounds = unionBounds(
      bounds,
      current.editorRows.map((row) => ({ station: row.station, elevation: row.elevation })),
    )
    if (current.design) {
      bounds = unionBounds(bounds, current.design.curve)
      bounds = unionBounds(
        bounds,
        current.design.vertices.map((vertex) => ({ station: vertex.station, elevation: vertex.elevation })),
      )
    }
    return bounds ? expandBounds(bounds, 1) : null
  }

  function fitTo(bounds: Bounds | null, asBase: boolean) {
    if (!bounds || size.width < 20 || size.height < 20) {
      return
    }
    const base = asBase ? null : baseScaleRef.current
    const view = fitView(bounds, size.width, size.height, base)
    if (asBase) {
      baseScaleRef.current = view.scale
    }
    viewRef.current = view
    redraw()
  }

  useImperativeHandle(ref, () => ({
    zoomBy(factor: number) {
      const view = viewRef.current
      if (!view) {
        return
      }
      userAdjustedRef.current = true
      viewRef.current = zoomView(view, baseScaleRef.current, factor, size.width / 2, size.height / 2)
      redraw()
    },
    resetView() {
      userAdjustedRef.current = false
      fitTo(dataBounds(), true)
    },
    zoomTo(station: number, elevation: number) {
      const view = viewRef.current
      if (!view || size.width < 40 || size.height < 40) {
        return
      }
      const currentSpan = Math.abs(stationAt(view, size.width) - stationAt(view, 0))
      const lightSpan = 1200
      const scale = currentSpan > lightSpan ? Math.max(size.width - 96, 80) / lightSpan : view.scale
      const zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, scale / baseScaleRef.current))
      const nextScale = baseScaleRef.current * zoom
      const centerX = size.width / 2
      const centerY = size.height / 2
      const target: View = {
        scale: nextScale,
        ox: centerX - station * nextScale,
        oy: centerY + elevation * nextScale * EXAG,
      }
      const start = view
      if (focusFrameRef.current !== null) {
        cancelAnimationFrame(focusFrameRef.current)
      }
      userAdjustedRef.current = true
      const started = performance.now()
      const frame = (now: number) => {
        const t = Math.min(1, (now - started) / 180)
        const ease = 1 - (1 - t) * (1 - t)
        viewRef.current = {
          scale: start.scale + (target.scale - start.scale) * ease,
          ox: start.ox + (target.ox - start.ox) * ease,
          oy: start.oy + (target.oy - start.oy) * ease,
        }
        redraw()
        if (t < 1) {
          focusFrameRef.current = requestAnimationFrame(frame)
        } else {
          focusFrameRef.current = null
        }
      }
      focusFrameRef.current = requestAnimationFrame(frame)
    },
  }))

  useEffect(() => {
    const host = hostRef.current
    if (!host) {
      return
    }
    const observer = new ResizeObserver(() => {
      const rect = host.getBoundingClientRect()
      setSize({ width: rect.width, height: rect.height })
    })
    observer.observe(host)
    return () => {
      observer.disconnect()
      if (frameRef.current !== null) {
        cancelAnimationFrame(frameRef.current)
      }
    }
  }, [])

  useEffect(() => {
    userAdjustedRef.current = false
  }, [session?.sessionId])

  useEffect(() => {
    if (!userAdjustedRef.current) {
      fitTo(dataBounds(), true)
    }
  }, [session?.sessionId, design, size.width, size.height])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) {
      return
    }
    const onWheel = (event: WheelEvent) => {
      event.preventDefault()
      const view = viewRef.current
      if (!view) {
        return
      }
      const rect = canvas.getBoundingClientRect()
      userAdjustedRef.current = true
      const factor = event.deltaY < 0 ? 1.25 : 1 / 1.25
      viewRef.current = zoomView(view, baseScaleRef.current, factor, event.clientX - rect.left, event.clientY - rect.top)
      redrawRef.current()
    }
    canvas.addEventListener('wheel', onWheel, { passive: false })
    return () => canvas.removeEventListener('wheel', onWheel)
  }, [])

  paintRef.current = () => {
    const canvas = canvasRef.current
    const view = viewRef.current
    const scene = sceneRef.current
    const { size } = scene
    if (!canvas || size.width < 20 || size.height < 20) {
      return
    }
    const dpr = window.devicePixelRatio || 1
    const pixelWidth = Math.floor(size.width * dpr)
    const pixelHeight = Math.floor(size.height * dpr)
    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
      canvas.width = pixelWidth
      canvas.height = pixelHeight
    }
    const ctx = canvas.getContext('2d')
    if (!ctx) {
      return
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.clearRect(0, 0, size.width, size.height)
    ctx.fillStyle = '#f7fafc'
    ctx.fillRect(0, 0, size.width, size.height)
    const {
      session,
      design,
      layers,
      pipeOffset,
      alignmentCurve,
      alignmentGuides,
      alignmentVertices,
      editorRows,
      selectedEditorId,
      tool,
    } = scene
    if (!session || !view) {
      ctx.fillStyle = '#57606a'
      ctx.font = '14px Segoe UI, sans-serif'
      ctx.fillText('KSE seçip Oku ve çiz.', 24, 40)
      return
    }

    ctx.lineJoin = 'round'
    ctx.lineCap = 'round'
    drawGrid(ctx, view, size.width, size.height)
    const [minStation, maxStation] = visibleStationSpan(view, size.width)
    const [minElevation, maxElevation] = visibleElevationSpan(view, size.height)
    const held = heldRef.current
    if (held && draftRef.current) {
      const match = editorRows.find((row) => row.id === held.id)
      if (match && Math.abs(match.station - held.station) < 1e-4 && Math.abs(match.elevation - held.elevation) < 1e-4) {
        draftRef.current = null
        heldRef.current = null
      }
    }
    const liveRows = draftRef.current ?? editorRows
    const editorActive = liveRows.length > 0
    if (editorActive) {
      const ordered = orderedRef.current
      ordered.length = liveRows.length
      for (let index = 0; index < liveRows.length; index++) {
        ordered[index] = liveRows[index]
      }
      ordered.sort((left, right) => left.station - right.station)
      const drafting = draftRef.current !== null
      const drag = dragRef.current
      const movedIndex = drafting && drag?.kind === 'vertex' ? ordered.findIndex((row) => row.id === drag.id) : -1
      const sameOrder = orderIdsRef.current.length === ordered.length
        && ordered.every((row, index) => orderIdsRef.current[index] === row.id)
      if (movedIndex >= 0 && sameOrder && curveBufRef.current.count > 1) {
        patchEditorGeometry(ordered, curveBufRef.current, guidePoolRef.current, movedIndex)
      } else if (drafting) {
        refreshEditorGeometry(ordered, curveBufRef.current, guidePoolRef.current)
        orderIdsRef.current = ordered.map((row) => row.id)
      } else {
        const changed = changedVertex(builtRef.current, ordered)
        if (changed >= 0 && curveBufRef.current.count > 1) {
          patchEditorGeometry(ordered, curveBufRef.current, guidePoolRef.current, changed)
          rememberBuilt(builtRef.current, orderIdsRef.current, ordered)
        } else if (changed !== -1) {
          refreshEditorGeometry(ordered, curveBufRef.current, guidePoolRef.current)
          rememberBuilt(builtRef.current, orderIdsRef.current, ordered)
        }
      }
    }
    const terrain = sliceByStation(session.terrain, minStation, maxStation)

    if (layers.corridor && design) {
      strokePolyline(ctx, sliceByStation(design.preferredLowerProfile, minStation, maxStation), view, 'rgba(191,219,254,0.95)', 1, [4, 4])
      strokePolyline(ctx, sliceByStation(design.preferredUpperProfile, minStation, maxStation), view, 'rgba(191,219,254,0.95)', 1, [4, 4])
      strokePolyline(ctx, sliceByStation(design.minimumCoverUpperProfile, minStation, maxStation), view, 'rgba(36,41,47,0.7)', 1.1, [7, 5])
    }

    if (layers.terrain) {
      strokePolyline(ctx, terrain, view, '#8b5e34', 2)
    }

    if (layers.pipe) {
      strokePolyline(ctx, terrain, view, '#1f6feb', 2.2, undefined, pipeOffset)
    }

    if (layers.ktb) {
      if (editorActive) {
        strokeStationBuffer(ctx, curveBufRef.current, view, minStation, maxStation, '#d1242f', 2.2)
      } else {
        strokePolyline(ctx, sliceByStation(alignmentCurve, minStation, maxStation), view, '#d1242f', 2.2)
      }
    }

    if (layers.ktbGuides) {
      const guides = editorActive ? guidePoolRef.current : alignmentGuides
      if (guides.length > 0) {
        drawGuides(ctx, guides, view, 'rgb(251,143,36)', 'rgba(251,143,36,0.35)', minStation, maxStation)
      }
    }

    if (layers.someInfo) {
      drawSomeLabels(ctx, editorActive ? liveRows : alignmentVertices, view, size.width, size.height)
    }

    if (layers.calculatedGuides && design) {
      if (design.guides.length > 0) {
        drawGuides(ctx, design.guides, view, '#218bff', 'rgba(33,139,255,0.28)', minStation, maxStation)
      }
      strokePolyline(
        ctx,
        sliceByStation(design.vertices, minStation, maxStation),
        view,
        '#218bff',
        2.4,
        [10, 6],
      )
    }

    if (layers.calculatedCurve && design) {
      const curve = design.curve.length >= 2 ? design.curve : design.vertices
      strokePolyline(ctx, sliceByStation(curve, minStation, maxStation), view, '#8250df', 4)
    }

    if (layers.calculatedVertices && design) {
      drawVertices(ctx, sliceByStation(design.vertices, minStation, maxStation), view, minElevation, maxElevation)
    }

    if (layers.issues && design) {
      const curve = design.curve.length >= 2 ? design.curve : design.vertices
      let drawn = 0
      for (const issue of design.coverIssues) {
        if (issue.station < minStation || issue.station > maxStation) {
          continue
        }
        const elevation = interpolateElevation(curve, issue.station)
        if (elevation < minElevation || elevation > maxElevation) {
          continue
        }
        const x = screenX(view, issue.station)
        const y = screenY(view, elevation)
        ctx.beginPath()
        ctx.fillStyle = '#ffebcf'
        ctx.strokeStyle = '#bc4c00'
        ctx.lineWidth = 1.5
        ctx.arc(x, y, 4.5, 0, Math.PI * 2)
        ctx.fill()
        ctx.stroke()
        drawn += 1
        if (drawn >= 250) {
          break
        }
      }
    }

    const drag = dragRef.current
    if (drag?.kind === 'window') {
      ctx.save()
      ctx.strokeStyle = '#1f6feb'
      ctx.fillStyle = 'rgba(31,111,235,0.16)'
      ctx.lineWidth = 1
      const x = Math.min(drag.x, drag.currentX)
      const y = Math.min(drag.y, drag.currentY)
      const w = Math.abs(drag.currentX - drag.x)
      const h = Math.abs(drag.currentY - drag.y)
      ctx.fillRect(x, y, w, h)
      ctx.strokeRect(x, y, w, h)
      ctx.restore()
    }

    if (drag?.kind === 'measure') {
      const horizontal = Math.abs(drag.currentX - drag.x) >= Math.abs(drag.currentY - drag.y)
      const endX = horizontal ? drag.currentX : drag.x
      const endY = horizontal ? drag.y : drag.currentY
      ctx.save()
      ctx.strokeStyle = '#8250df'
      ctx.lineWidth = 1.5
      ctx.setLineDash([6, 4])
      ctx.beginPath()
      ctx.moveTo(drag.x, drag.y)
      ctx.lineTo(endX, endY)
      ctx.stroke()
      const distance = horizontal
        ? Math.abs(stationAt(view, endX) - stationAt(view, drag.x))
        : Math.abs(elevationAt(view, endY) - elevationAt(view, drag.y))
      const label = `${distance.toFixed(2)} m ${horizontal ? 'yatay' : 'düşey'}`
      ctx.font = '12px Segoe UI, sans-serif'
      ctx.setLineDash([])
      ctx.fillStyle = 'rgba(255,255,255,0.94)'
      ctx.fillRect(endX + 8, endY - 18, ctx.measureText(label).width + 10, 18)
      ctx.fillStyle = '#8250df'
      ctx.fillText(label, endX + 12, endY - 5)
        ctx.restore()
    }

    if (liveRows.length > 0 && tool !== 'pan') {
      ctx.lineWidth = 2.5
      for (const row of liveRows) {
        if (row.station < minStation || row.station > maxStation || row.elevation < minElevation || row.elevation > maxElevation) {
          continue
        }
        const x = screenX(view, row.station)
        const y = screenY(view, row.elevation)
        const selected = row.id === selectedEditorId
        ctx.beginPath()
        ctx.moveTo(x, y - 9)
        ctx.lineTo(x - 8, y + 7)
        ctx.lineTo(x + 8, y + 7)
        ctx.closePath()
        ctx.fillStyle = selected ? 'rgba(0,120,212,0.63)' : 'rgba(209,36,47,0.35)'
        ctx.strokeStyle = selected ? '#0078d4' : '#d1242f'
        ctx.fill()
        ctx.stroke()
      }
    }

    drawScale(ctx, view, size.width, baseScaleRef.current)
  }

  useEffect(() => {
    paintRef.current()
  }, [session, design, layers, pipeOffset, size.width, size.height, alignmentCurve, alignmentGuides, alignmentVertices, editorRows, selectedEditorId, tool])

  function commitDraft(id: string) {
    const draft = draftRef.current
    const row = draft?.find((item) => item.id === id)
    if (!row) {
      draftRef.current = null
      onFinishEditorDrag()
      return
    }
    heldRef.current = { id: row.id, station: row.station, elevation: row.elevation }
    onMoveEditor(row.id, row.station, row.elevation)
    onFinishEditorDrag()
  }

  function localPoint(event: { currentTarget: HTMLCanvasElement; clientX: number; clientY: number }) {
    const rect = event.currentTarget.getBoundingClientRect()
    return { x: event.clientX - rect.left, y: event.clientY - rect.top }
  }

  return (
    <div ref={hostRef} className="stage">
      <canvas
        ref={canvasRef}
        className={`profile-canvas tool-${tool}`}
        onContextMenu={(event) => {
          event.preventDefault()
          const view = viewRef.current
          const point = localPoint(event)
          const dragging = dragRef.current
          dragRef.current = null
          if (dragging?.kind === 'vertex') {
            commitDraft(dragging.id)
          }
          if (!view || tool === 'measure' || tool === 'window' || tool === 'pick' || dragging) {
            redraw()
            return
          }
          onContextMenuAt(event.clientX, event.clientY, stationAt(view, point.x), elevationAt(view, point.y))
          redraw()
        }}
        onPointerDown={(event) => {
          if (focusFrameRef.current !== null) {
            cancelAnimationFrame(focusFrameRef.current)
            focusFrameRef.current = null
          }
          if (event.button === 2) {
            return
          }
          if (event.button !== 0 || !viewRef.current) {
            return
          }
          const point = localPoint(event)
          const view = viewRef.current
          if (tool === 'pick') {
            onPick(stationAt(view, point.x))
            return
          }
          if (tool !== 'pan' && tool !== 'measure' && tool !== 'window' && editorRows.length > 0) {
            const hit = hitEditorRow(view, editorRows, point.x, point.y)
            if (hit) {
              event.currentTarget.setPointerCapture(event.pointerId)
              onSelectEditor(hit.id)
              draftRef.current = editorRows.map((row) => ({ ...row }))
              heldRef.current = null
              dragRef.current = { kind: 'vertex', id: hit.id }
              return
            }
          }
          event.currentTarget.setPointerCapture(event.pointerId)
          if (tool === 'pan') {
            dragRef.current = { kind: 'pan', x: point.x, y: point.y, view }
          } else if (tool === 'measure') {
            dragRef.current = { kind: 'measure', x: point.x, y: point.y, currentX: point.x, currentY: point.y }
          } else if (tool === 'window') {
            dragRef.current = { kind: 'window', x: point.x, y: point.y, currentX: point.x, currentY: point.y }
          }
        }}
        onPointerMove={(event) => {
          const point = localPoint(event)
          const view = viewRef.current
          if (view && readoutRef.current) {
            readoutRef.current.textContent = `${formatChainage(stationAt(view, point.x))}  ·  kot ${elevationAt(view, point.y).toFixed(2)} m`
          }
          const drag = dragRef.current
          if (!drag || !view) {
            return
          }
          if (drag.kind === 'pan') {
            userAdjustedRef.current = true
            viewRef.current = {
              ...drag.view,
              ox: drag.view.ox + (point.x - drag.x),
              oy: drag.view.oy + (point.y - drag.y),
            }
          } else if (drag.kind === 'vertex') {
            const row = draftRef.current?.find((item) => item.id === drag.id)
            if (row) {
              row.station = stationAt(view, point.x)
              row.elevation = elevationAt(view, point.y)
            }
          } else {
            drag.currentX = point.x
            drag.currentY = point.y
          }
          redraw()
        }}
        onPointerUp={(event) => {
          const drag = dragRef.current
          dragRef.current = null
          if (drag?.kind === 'vertex') {
            commitDraft(drag.id)
          }
          if (drag?.kind === 'window' && viewRef.current) {
            const point = localPoint(event)
            const width = Math.abs(point.x - drag.x)
            const height = Math.abs(point.y - drag.y)
            if (width >= 10 && height >= 10) {
              const view = viewRef.current
              const stations = [stationAt(view, drag.x), stationAt(view, point.x)]
              const elevations = [elevationAt(view, drag.y), elevationAt(view, point.y)]
              userAdjustedRef.current = true
              viewRef.current = fitView(
                {
                  minStation: Math.min(...stations),
                  maxStation: Math.max(...stations),
                  minElevation: Math.min(...elevations),
                  maxElevation: Math.max(...elevations),
                },
                size.width,
                size.height,
                baseScaleRef.current,
              )
            }
          }
          redraw()
        }}
      />
      <div ref={readoutRef} className="readout">Profil hazır.</div>
    </div>
  )
})

export default ProfileCanvas
