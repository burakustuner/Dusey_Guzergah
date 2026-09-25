import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { formatChainage, parseChainage } from './chainage'
import { derivedCell, type EditorRow } from './editor'

type Props = {
  rows: EditorRow[]
  selectedId: string | null
  canUndo: boolean
  canRedo: boolean
  canUseKtb: boolean
  canUseCalculation: boolean
  canSave: boolean
  pickActive: boolean
  onLoadKtb: () => void
  onLoadCalculation: () => void
  onNewList: () => void
  onAddRow: () => void
  onRemoveRow: () => void
  onTogglePick: () => void
  onApply: () => void
  onSave: () => void
  onUndo: () => void
  onRedo: () => void
  onSelect: (id: string, zoom: boolean) => void
  onChange: (id: string, patch: Partial<EditorRow>, commit: boolean) => void
  dragPreview: { id: string; station: number; elevation: number } | null
}

function commitOnEnter(event: KeyboardEvent<HTMLInputElement>) {
  if (event.key !== 'Enter') {
    return
  }
  event.preventDefault()
  event.currentTarget.blur()
}

function NumberCell({
  value,
  digits,
  onCommit,
  onActivate,
}: {
  value: number
  digits: number
  onCommit: (value: number) => void
  onActivate?: () => void
}) {
  const [text, setText] = useState(value.toFixed(digits))
  const [focused, setFocused] = useState(false)
  const display = focused ? text : value.toFixed(digits)
  return (
    <input
      value={display}
      onFocus={() => {
        setFocused(true)
        onActivate?.()
      }}
      onChange={(event) => setText(event.target.value)}
      onBlur={() => {
        setFocused(false)
        const parsed = Number(text.replace(',', '.'))
        if (Number.isFinite(parsed)) {
          onCommit(parsed)
        } else {
          setText(value.toFixed(digits))
        }
      }}
      onKeyDown={commitOnEnter}
      onClick={(event) => event.stopPropagation()}
    />
  )
}

function StationCell({ value, onCommit, onActivate }: { value: number; onCommit: (value: number) => void; onActivate?: () => void }) {
  const [text, setText] = useState(formatChainage(value))
  const [focused, setFocused] = useState(false)
  const display = focused ? text : formatChainage(value)
  return (
    <input
      value={display}
      onFocus={() => {
        setFocused(true)
        onActivate?.()
      }}
      onChange={(event) => setText(event.target.value)}
      onBlur={() => {
        setFocused(false)
        const parsed = parseChainage(text)
        if (parsed === null) {
          setText(formatChainage(value))
          return
        }
        onCommit(parsed)
      }}
      onKeyDown={commitOnEnter}
      onClick={(event) => event.stopPropagation()}
    />
  )
}

export default function EditorPanel(props: Props) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const probeRef = useRef<HTMLTableRowElement>(null)
  const [scrollTop, setScrollTop] = useState(0)
  const [viewport, setViewport] = useState(160)
  const [rowHeight, setRowHeight] = useState(32)
  const rowCount = props.rows.length
  const overscan = 6
  const start = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan)
  const end = Math.min(rowCount, Math.ceil((scrollTop + viewport) / rowHeight) + overscan)
  const padTop = start * rowHeight
  const padBottom = Math.max(0, (rowCount - end) * rowHeight)
  const preview = props.dragPreview
  const shown = preview
    ? props.rows.map((row) => (
        row.id === preview.id
          ? { ...row, station: preview.station, elevation: preview.elevation }
          : row
      ))
    : props.rows

  useEffect(() => {
    const node = scrollRef.current
    if (!node) {
      return
    }
    const measure = () => setViewport(node.clientHeight)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(node)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const height = probeRef.current?.getBoundingClientRect().height
    if (height && Math.abs(height - rowHeight) > 0.5) {
      setRowHeight(height)
    }
  }, [start, rowCount, rowHeight])

  useEffect(() => {
    const node = scrollRef.current
    if (!node || !props.selectedId) {
      return
    }
    const index = props.rows.findIndex((row) => row.id === props.selectedId)
    if (index < 0) {
      return
    }
    const header = node.querySelector('thead')?.offsetHeight ?? 0
    const top = header + index * rowHeight
    const bottom = top + rowHeight
    const visibleTop = node.scrollTop + header
    const visibleBottom = node.scrollTop + node.clientHeight - 8
    if (top < visibleTop) {
      node.scrollTop = Math.max(0, top - header)
    } else if (bottom > visibleBottom) {
      node.scrollTop = Math.max(0, bottom - node.clientHeight + 8)
    }
  }, [props.selectedId, props.rows, rowHeight])

  return (
    <section className="editor-dock">
      <div className="editor-tools">
        <strong>Düşey düzenleyici</strong>
        <button type="button" onClick={props.onLoadKtb} disabled={!props.canUseKtb}>KTB'den</button>
        <button type="button" onClick={props.onLoadCalculation} disabled={!props.canUseCalculation}>Hesaptan</button>
        <button type="button" onClick={props.onNewList}>Yeni</button>
        <button type="button" onClick={props.onAddRow}>Satır +</button>
        <button type="button" onClick={props.onRemoveRow} disabled={props.rows.length === 0}>Satır −</button>
        <button type="button" className={props.pickActive ? 'active' : ''} onClick={props.onTogglePick}>Profilden</button>
        <button type="button" onClick={props.onApply} disabled={props.rows.length === 0}>Çizime uygula</button>
        <button type="button" onClick={props.onSave} disabled={!props.canSave || props.rows.length === 0}>KTB kaydet</button>
        <button type="button" onClick={props.onUndo} disabled={!props.canUndo}>Geri al</button>
        <button type="button" onClick={props.onRedo} disabled={!props.canRedo}>Yinele</button>
      </div>
      <div
        className="editor-scroll"
        ref={scrollRef}
        onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}
      >
        <table className="editor-table">
          <thead>
            <tr>
              <th>Kurp no</th>
              <th>İstasyon</th>
              <th>Kot (m)</th>
              <th>L (kurp)</th>
              <th>Eğim (%)</th>
              <th>Tip</th>
              <th>K</th>
              <th>Teğet giriş</th>
              <th>Teğet çıkış</th>
              <th>R (m)</th>
            </tr>
          </thead>
          <tbody>
            {padTop > 0 && (
              <tr className="spacer" style={{ height: padTop }}>
                <td colSpan={10} />
              </tr>
            )}
            {shown.slice(start, end).map((row, offset) => {
              const index = start + offset
              const derived = derivedCell(shown, index)
              const mark = () => props.onSelect(row.id, true)
              return (
                <tr
                  key={row.id}
                  ref={offset === 0 ? probeRef : undefined}
                  className={row.id === props.selectedId ? 'selected' : ''}
                  onClick={mark}
                  onDoubleClick={mark}
                >
                  <td>{derived.kurpNo}</td>
                  <td>
                    <StationCell value={row.station} onActivate={mark} onCommit={(station) => props.onChange(row.id, { station }, true)} />
                  </td>
                  <td>
                    <NumberCell value={row.elevation} digits={3} onActivate={mark} onCommit={(elevation) => props.onChange(row.id, { elevation }, true)} />
                  </td>
                  <td>
                    <NumberCell value={row.curveLength} digits={3} onActivate={mark} onCommit={(curveLength) => props.onChange(row.id, { curveLength }, true)} />
                  </td>
                  <td>{derived.grade}</td>
                  <td>
                    <NumberCell value={row.curveType} digits={0} onActivate={mark} onCommit={(curveType) => props.onChange(row.id, { curveType }, true)} />
                  </td>
                  <td>{derived.k}</td>
                  <td>
                    <NumberCell value={row.tangentIn} digits={4} onActivate={mark} onCommit={(tangentIn) => props.onChange(row.id, { tangentIn }, true)} />
                  </td>
                  <td>
                    <NumberCell value={row.tangentOut} digits={4} onActivate={mark} onCommit={(tangentOut) => props.onChange(row.id, { tangentOut }, true)} />
                  </td>
                  <td>
                    <NumberCell value={row.radius} digits={3} onActivate={mark} onCommit={(radius) => props.onChange(row.id, { radius }, true)} />
                  </td>
                </tr>
              )
            })}
            {padBottom > 0 && (
              <tr className="spacer" style={{ height: padBottom }}>
                <td colSpan={10} />
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </section>
  )
}
