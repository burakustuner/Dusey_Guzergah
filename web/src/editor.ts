import type { Point, Vertex } from './types'

export type EditorRow = Vertex & { id: string }

export type DerivedCell = { kurpNo: string; grade: string; k: string }

const maxSnapshots = 50

export function newEditorId() {
  return crypto.randomUUID()
}

export function rowFromVertex(vertex: Vertex): EditorRow {
  return { ...vertex, id: newEditorId() }
}

export function createEditorRow(
  station: number,
  elevation: number,
  pipeLength: number,
  extra?: Partial<Vertex>,
): EditorRow {
  return {
    id: newEditorId(),
    station,
    elevation,
    curveLength: extra?.curveLength ?? pipeLength,
    curveType: extra?.curveType ?? 1,
    tangentIn: extra?.tangentIn ?? pipeLength / 2,
    tangentOut: extra?.tangentOut ?? pipeLength / 2,
    radius: extra?.radius ?? 0,
  }
}

export function sortRows(rows: EditorRow[]) {
  return [...rows].sort((left, right) => left.station - right.station)
}

export function interpolateElevation(profile: Point[], station: number): number {
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

export function suggestElevation(terrain: Point[], station: number, stripping: number, targetCover: number) {
  return interpolateElevation(terrain, station) - stripping - targetCover
}

export function derivedCell(rows: EditorRow[], index: number): DerivedCell {
  const row = rows[index]
  const kurpNo = index > 0 && index < rows.length - 1 ? String(index) : ''
  let grade = ''
  let k = ''
  if (index > 0) {
    const previous = rows[index - 1]
    const span = row.station - previous.station
    if (Math.abs(span) > 1e-12) {
      grade = (((row.elevation - previous.elevation) / span) * 100).toFixed(3)
    }
  }
  if (index > 0 && index < rows.length - 1 && row.curveLength > 1e-12) {
    const previous = rows[index - 1]
    const next = rows[index + 1]
    const spanIn = row.station - previous.station
    const spanOut = next.station - row.station
    if (Math.abs(spanIn) > 1e-12 && Math.abs(spanOut) > 1e-12) {
      const gradeIn = (row.elevation - previous.elevation) / spanIn
      const gradeOut = (next.elevation - row.elevation) / spanOut
      const delta = (gradeOut - gradeIn) * 100
      if (Math.abs(delta) > 1e-12) {
        k = (row.curveLength / Math.abs(delta)).toFixed(3)
      }
    }
  }
  return { kurpNo, grade, k }
}

export function derivedCells(rows: EditorRow[]): DerivedCell[] {
  return rows.map((_, index) => derivedCell(rows, index))
}

function cloneRows(rows: EditorRow[]): EditorRow[] {
  return rows.map((row) => ({ ...row }))
}

function sameRows(left: EditorRow[], right: EditorRow[]) {
  if (left.length !== right.length) {
    return false
  }
  return left.every((row, index) => {
    const other = right[index]
    return row.station === other.station
      && row.elevation === other.elevation
      && row.curveLength === other.curveLength
      && row.curveType === other.curveType
      && row.tangentIn === other.tangentIn
      && row.tangentOut === other.tangentOut
      && row.radius === other.radius
  })
}

export class EditorHistory {
  private snapshots: EditorRow[][] = []
  private cursor = -1

  get canUndo() {
    return this.cursor > 0
  }

  get canRedo() {
    return this.cursor >= 0 && this.cursor < this.snapshots.length - 1
  }

  clear(rows: EditorRow[]) {
    this.snapshots = [cloneRows(rows)]
    this.cursor = 0
  }

  commit(rows: EditorRow[]) {
    const next = cloneRows(rows)
    if (this.cursor >= 0 && sameRows(this.snapshots[this.cursor], next)) {
      return
    }
    this.snapshots = this.snapshots.slice(0, this.cursor + 1)
    this.snapshots.push(next)
    this.cursor = this.snapshots.length - 1
    if (this.snapshots.length > maxSnapshots) {
      this.snapshots.shift()
      this.cursor -= 1
    }
  }

  undo(): EditorRow[] | null {
    if (!this.canUndo) {
      return null
    }
    this.cursor -= 1
    return cloneRows(this.snapshots[this.cursor])
  }

  redo(): EditorRow[] | null {
    if (!this.canRedo) {
      return null
    }
    this.cursor += 1
    return cloneRows(this.snapshots[this.cursor])
  }
}
