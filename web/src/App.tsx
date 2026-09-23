import { useEffect, useRef, useState } from 'react'
import { calculateDesign, createSession, exportEditorKtb, exportKtb, fetchWarnings } from './api'
import {
  createEditorRow,
  EditorHistory,
  rowFromVertex,
  sortRows,
  suggestElevation,
  type EditorRow,
} from './editor'
import EditorPanel from './EditorPanel'
import ProfileCanvas, { type ProfileCanvasHandle } from './ProfileCanvas'
import { buildVerticalCurve, buildVerticalCurveGuides } from './verticalCurve'
import {
  defaultForm,
  defaultLayers,
  defaultThresholds,
  toDesignParameters,
  type CanvasTool,
  type DesignForm,
  type DesignResult,
  type Guide,
  type Layers,
  type Point,
  type SessionResponse,
  type Vertex,
  type WarningThresholds,
} from './types'

const layerItems: { key: keyof Layers; label: string }[] = [
  { key: 'terrain', label: 'Arazi' },
  { key: 'pipe', label: 'Boru' },
  { key: 'ktb', label: 'Mevcut KTB' },
  { key: 'someInfo', label: 'Someler' },
  { key: 'ktbGuides', label: 'KTB aliyman' },
  { key: 'calculatedCurve', label: 'Hesap eğrisi' },
  { key: 'calculatedVertices', label: 'Hesap some' },
  { key: 'calculatedGuides', label: 'Hesap aliyman' },
  { key: 'corridor', label: 'Koridor' },
  { key: 'issues', label: 'Kaplama uyarısı' },
]

const fields: { key: keyof DesignForm; label: string; step: string }[] = [
  { key: 'targetCover', label: 'Hedef derinlik (m)', step: '0.01' },
  { key: 'extraDepth', label: 'Ek derinlik (m)', step: '0.01' },
  { key: 'strippingDepth', label: 'Sıyırma (m)', step: '0.01' },
  { key: 'pipeDiameter', label: 'Boru çapı (m)', step: '0.01' },
  { key: 'pipeLength', label: 'Boru boyu (m)', step: '0.01' },
  { key: 'minimumSegmentLength', label: 'Min. ara (m)', step: '0.1' },
  { key: 'minimumRadius', label: 'Min. R (m)', step: '1' },
]

type AppliedAlignment = { vertices: Vertex[]; curve: Point[]; guides: Guide[] }
type MenuState = { x: number; y: number; station: number; elevation: number }

const emptyAlignment: AppliedAlignment = { vertices: [], curve: [], guides: [] }

export default function App() {
  const canvasRef = useRef<ProfileCanvasHandle>(null)
  const historyRef = useRef(new EditorHistory())
  const rowsRef = useRef<EditorRow[]>([])
  const [kse, setKse] = useState<File | null>(null)
  const [ktb, setKtb] = useState<File | null>(null)
  const [session, setSession] = useState<SessionResponse | null>(null)
  const [design, setDesign] = useState<DesignResult | null>(null)
  const [form, setForm] = useState<DesignForm>(defaultForm)
  const [layers, setLayers] = useState<Layers>(defaultLayers)
  const [tool, setTool] = useState<CanvasTool>('select')
  const [busy, setBusy] = useState<'load' | 'calc' | 'export' | null>(null)
  const [status, setStatus] = useState('KSE ve isteğe bağlı KTB seçin.')
  const [error, setError] = useState('')
  const [rows, setRows] = useState<EditorRow[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [applied, setApplied] = useState<AppliedAlignment | null>(null)
  const [historyFlags, setHistoryFlags] = useState({ undo: false, redo: false })
  const [menu, setMenu] = useState<MenuState | null>(null)
  const [thresholds, setThresholds] = useState<WarningThresholds>(defaultThresholds)
  const [warnings, setWarnings] = useState<string[]>([])

  function syncHistory() {
    setHistoryFlags({ undo: historyRef.current.canUndo, redo: historyRef.current.canRedo })
  }

  function setEditorRows(next: EditorRow[], mode: 'none' | 'commit' | 'clear') {
    const sorted = mode === 'none' ? next : sortRows(next)
    rowsRef.current = sorted
    setRows(sorted)
    if (mode === 'clear') {
      historyRef.current.clear(sorted)
    }
    if (mode === 'commit') {
      historyRef.current.commit(sorted)
    }
    if (mode !== 'none') {
      syncHistory()
    }
  }

  function selectRow(id: string, zoom: boolean) {
    setSelectedId(id)
    if (!zoom) {
      return
    }
    const row = rowsRef.current.find((item) => item.id === id)
    if (row) {
      canvasRef.current?.zoomTo(row.station, row.elevation)
    }
  }

  useEffect(() => {
    if (!session) {
      setWarnings([])
      return
    }
    const vertices = rows.length > 0 ? rows : (applied?.vertices ?? session.ktbVertices)
    const handle = window.setTimeout(() => {
      fetchWarnings(session.sessionId, thresholds, toDesignParameters(form), vertices)
        .then(setWarnings)
        .catch(() => setWarnings([]))
    }, 300)
    return () => window.clearTimeout(handle)
  }, [session, rows, applied, thresholds, form])

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      const typing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z' && !typing) {
        event.preventDefault()
        const restored = historyRef.current.undo()
        if (restored) {
          rowsRef.current = restored
          setRows(restored)
          syncHistory()
          setStatus('Düzenleyici: geri alındı.')
        }
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y' && !typing) {
        event.preventDefault()
        const restored = historyRef.current.redo()
        if (restored) {
          rowsRef.current = restored
          setRows(restored)
          syncHistory()
          setStatus('Düzenleyici: yinelendi.')
        }
      }
      if (typing || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) {
        return
      }
      if (!(document.activeElement instanceof Element) || !document.activeElement.closest('.editor-table')) {
        return
      }
      event.preventDefault()
      const ordered = rowsRef.current
      if (ordered.length === 0) {
        return
      }
      const index = ordered.findIndex((row) => row.id === selectedId)
      const nextIndex = index < 0
        ? (event.key === 'ArrowRight' ? 0 : ordered.length - 1)
        : Math.min(ordered.length - 1, Math.max(0, index + (event.key === 'ArrowRight' ? 1 : -1)))
      selectRow(ordered[nextIndex].id, true)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [selectedId])

  const fileAlignment: AppliedAlignment = applied
    ?? (session
      ? { vertices: session.ktbVertices, curve: session.ktbCurve, guides: session.ktbGuides }
      : emptyAlignment)
  const alignment: AppliedAlignment = rows.length > 0
    ? {
        vertices: rows,
        curve: rows,
        guides: [],
      }
    : fileAlignment

  async function loadFiles() {
    if (!kse) {
      setError('KSE dosyası seçin.')
      return
    }
    setBusy('load')
    setError('')
    setDesign(null)
    setApplied(null)
    setEditorRows([], 'clear')
    setSelectedId(null)
    try {
      const created = await createSession(kse, ktb)
      setSession(created)
      const issueCount = created.issues.length
      setStatus(
        `Kesit: ${created.sectionCount.toLocaleString('tr-TR')} · Arazi noktası: ${created.terrainPointCount.toLocaleString('tr-TR')} · KTB some: ${created.ktbVertices.length.toLocaleString('tr-TR')}${issueCount ? ` · Doğrulama: ${issueCount}` : ''}`,
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Dosya okunamadı.')
    } finally {
      setBusy(null)
    }
  }

  async function runCalculate() {
    if (!session) {
      setError('Önce profili yükleyin.')
      return
    }
    setBusy('calc')
    setError('')
    try {
      const result = await calculateDesign(session.sessionId, toDesignParameters(form))
      setDesign(result)
      setLayers((current) => ({
        ...current,
        calculatedCurve: true,
        calculatedVertices: true,
        calculatedGuides: true,
      }))
      setStatus(
        `Hesap some: ${result.vertices.length.toLocaleString('tr-TR')} · Eğri noktası: ${result.curve.length.toLocaleString('tr-TR')} · Örtü uyarısı: ${result.coverIssues.length.toLocaleString('tr-TR')}`,
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Hesap başarısız.')
    } finally {
      setBusy(null)
    }
  }

  async function runExport() {
    if (!session) {
      return
    }
    setBusy('export')
    setError('')
    try {
      await exportKtb(session.sessionId)
      setStatus('GÜZERGAH_dusey.KTB indirildi.')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'KTB yazılamadı.')
    } finally {
      setBusy(null)
    }
  }

  function loadEditorFrom(vertices: Vertex[], label: string) {
    const next = sortRows(vertices.map(rowFromVertex))
    setEditorRows(next, 'clear')
    setSelectedId(next[0]?.id ?? null)
    setLayers((current) => ({ ...current, ktb: true }))
    setStatus(label)
  }

  function loadFromKtb() {
    if (!session || session.ktbVertices.length === 0) {
      setStatus('Önce seçili KTB ile «Oku ve çiz» çalıştırın.')
      return
    }
    loadEditorFrom(session.ktbVertices, `Düzenleyici: KTB’den ${session.ktbVertices.length} some.`)
  }

  function loadFromCalculation() {
    if (!design || design.vertices.length === 0) {
      setStatus('Önce «Hesapla».')
      return
    }
    loadEditorFrom(design.vertices, `Düzenleyici: hesaptan ${design.vertices.length} some.`)
  }

  function newList() {
    const terrain = session?.terrain ?? []
    const pipeLength = form.pipeLength
    if (terrain.length === 0) {
      setEditorRows([createEditorRow(0, 0, pipeLength)], 'clear')
      setStatus('Düzenleyici: KSE yok — satırları elle doldurun.')
      return
    }
    const first = terrain[0]
    const last = terrain[terrain.length - 1]
    const created = [createEditorRow(first.station, suggestElevation(terrain, first.station, form.strippingDepth, form.targetCover), pipeLength)]
    if (Math.abs(last.station - first.station) > 0.001) {
      created.push(createEditorRow(last.station, suggestElevation(terrain, last.station, form.strippingDepth, form.targetCover), pipeLength))
    }
    setEditorRows(created, 'clear')
    setStatus('Düzenleyici: yeni liste (uç KM + yaklaşık kot).')
  }

  function addRow() {
    const terrain = session?.terrain ?? []
    const ordered = rowsRef.current
    const selected = ordered.find((row) => row.id === selectedId)
    let station = terrain[0]?.station ?? 0
    if (ordered.length > 0 && selected) {
      const index = ordered.findIndex((row) => row.id === selected.id)
      station = index >= 0 && index < ordered.length - 1
        ? (selected.station + ordered[index + 1].station) / 2
        : ordered[ordered.length - 1].station + form.pipeLength
    } else if (ordered.length > 0) {
      station = ordered[ordered.length - 1].station + form.pipeLength
    }
    const elevation = terrain.length >= 2
      ? suggestElevation(terrain, station, form.strippingDepth, form.targetCover)
      : 0
    const next = sortRows([...ordered, createEditorRow(station, elevation, form.pipeLength)])
    setEditorRows(next, 'commit')
    setSelectedId(next.find((row) => Math.abs(row.station - station) < 1e-6)?.id ?? null)
  }

  function removeRow() {
    if (!selectedId) {
      return
    }
    const next = rowsRef.current.filter((row) => row.id !== selectedId)
    setEditorRows(next, 'commit')
    setSelectedId(next[0]?.id ?? null)
  }

  function pickAt(station: number) {
    const terrain = session?.terrain ?? []
    if (terrain.length < 2) {
      setStatus('Profilden some eklemek için önce KSE yükleyin.')
      return
    }
    const elevation = suggestElevation(terrain, station, form.strippingDepth, form.targetCover)
    const row = createEditorRow(station, elevation, form.pipeLength)
    setEditorRows(sortRows([...rowsRef.current, row]), 'commit')
    setSelectedId(row.id)
    setStatus(`Profilden some eklendi: KM ${station.toFixed(3)}, kot ~${elevation.toFixed(3)} m.`)
  }

  function changeRow(id: string, patch: Partial<EditorRow>, commit: boolean) {
    const next = rowsRef.current.map((row) => (row.id === id ? { ...row, ...patch } : row))
    setEditorRows(next, commit ? 'commit' : 'none')
  }

  function undo() {
    const restored = historyRef.current.undo()
    if (!restored) {
      return
    }
    rowsRef.current = restored
    setRows(restored)
    syncHistory()
    setStatus('Düzenleyici: geri alındı.')
  }

  function redo() {
    const restored = historyRef.current.redo()
    if (!restored) {
      return
    }
    rowsRef.current = restored
    setRows(restored)
    syncHistory()
    setStatus('Düzenleyici: yinelendi.')
  }

  async function applyToProfile() {
    if (rowsRef.current.length === 0) {
      setStatus('Çizime uygulamak için tabloda en az bir some satırı olmalı.')
      return
    }
    const vertices = sortRows(rowsRef.current)
    const curve = buildVerticalCurve(vertices)
    const guides = buildVerticalCurveGuides(vertices)
    setApplied({ vertices, curve, guides })
    setStatus(`Tablo KTB görünümüne uygulandı (${vertices.length} some). Kurp güncellendi.`)
  }

  async function saveEditor() {
    if (!session) {
      return
    }
    if (rowsRef.current.length === 0) {
      setStatus('KTB yazmak için tabloda some satırı olmalı.')
      return
    }
    setError('')
    try {
      await exportEditorKtb(session.sessionId, sortRows(rowsRef.current))
      setStatus('Düzenleyici KTB yazıldı: GÜZERGAH_dusey_duzen.KTB')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'KTB yazılamadı.')
    }
  }

  function toggleTool(next: CanvasTool) {
    setTool((current) => (current === next ? 'select' : next))
    setMenu(null)
  }

  return (
    <div className="app" onClick={() => setMenu(null)}>
      <header className="toolbar">
        <div className="brand">Düşey Güzergâh</div>
        <label className="file">
          Arazi (KSE)
          <input type="file" accept=".kse,.KSE" onChange={(event) => setKse(event.target.files?.[0] ?? null)} />
        </label>
        <label className="file">
          KTB
          <input type="file" accept=".ktb,.KTB" onChange={(event) => setKtb(event.target.files?.[0] ?? null)} />
        </label>
        <button type="button" onClick={loadFiles} disabled={busy !== null}>
          {busy === 'load' ? 'Okunuyor…' : 'Oku ve çiz'}
        </button>
        <span className="sep" />
        <button type="button" onClick={() => canvasRef.current?.zoomBy(1 / 1.25)}>Uzak</button>
        <button type="button" onClick={() => canvasRef.current?.zoomBy(1.25)}>Yakın</button>
        <button type="button" onClick={() => canvasRef.current?.resetView()}>Tam</button>
        <button type="button" className={tool === 'window' ? 'active' : ''} onClick={() => toggleTool('window')}>Pencere</button>
        <button type="button" className={tool === 'measure' ? 'active' : ''} onClick={() => toggleTool('measure')}>Ölçüm</button>
        <button type="button" className={tool === 'pan' ? 'active' : ''} onClick={() => toggleTool('pan')}>Pan</button>
      </header>
      <div className="layers">
        {layerItems.map((item) => (
          <button
            key={item.key}
            type="button"
            className={layers[item.key] ? 'chip on' : 'chip'}
            onClick={() => setLayers((current) => ({ ...current, [item.key]: !current[item.key] }))}
          >
            {item.label}
          </button>
        ))}
      </div>
      <div className="workspace">
        <ProfileCanvas
          ref={canvasRef}
          session={session}
          design={design}
          layers={layers}
          pipeOffset={form.strippingDepth + form.targetCover}
          tool={tool}
          alignmentVertices={alignment.vertices}
          alignmentCurve={alignment.curve}
          alignmentGuides={alignment.guides}
          editorRows={rows}
          selectedEditorId={selectedId}
          onSelectEditor={(id) => selectRow(id, false)}
          onMoveEditor={(id, station, elevation) => changeRow(id, { station, elevation }, false)}
          onFinishEditorDrag={() => setEditorRows(rowsRef.current, 'commit')}
          onPick={pickAt}
          onContextMenuAt={(x, y, station, elevation) => setMenu({ x, y, station, elevation })}
        />
        <aside className="side">
          <h2>Hesap</h2>
          {fields.map((field) => (
            <label key={field.key} className="field">
              {field.label}
              <input
                type="number"
                step={field.step}
                min="0"
                value={form[field.key]}
                onChange={(event) => {
                  const value = Number(event.target.value)
                  setForm((current) => ({ ...current, [field.key]: Number.isFinite(value) ? value : 0 }))
                }}
              />
            </label>
          ))}
          <p className="hint">Örtü derinliği: {(form.strippingDepth + form.targetCover).toFixed(2)} m. Tolerans 0,05 m, minimum örtü hedef derinliğe eşit.</p>
          <button type="button" className="primary" onClick={runCalculate} disabled={busy !== null || !session}>
            {busy === 'calc' ? 'Hesaplanıyor…' : 'Hesapla'}
          </button>
          <button type="button" onClick={runExport} disabled={busy !== null || !session?.hasKtbTemplate || !design}>
            {busy === 'export' ? 'Yazılıyor…' : 'KTB indir'}
          </button>
          <h2>Uyarı eşikleri</h2>
          <label className="field">
            Min. L ara PI (m)
            <input type="number" min="0" step="0.1" value={thresholds.minInteriorCurveLengthM} onChange={(event) => setThresholds((current) => ({ ...current, minInteriorCurveLengthM: Number(event.target.value) }))} />
          </label>
          <label className="field">
            Sıyırılmış zemine mesafe (m)
            <input type="number" min="0" step="0.01" value={thresholds.minStrippedTerrainToAxisGapM} onChange={(event) => setThresholds((current) => ({ ...current, minStrippedTerrainToAxisGapM: Number(event.target.value) }))} />
          </label>
          <label className="field">
            Keskin giriş eğimi (%)
            <input type="number" min="0" step="1" value={thresholds.maxAbsGradePercent} onChange={(event) => setThresholds((current) => ({ ...current, maxAbsGradePercent: Number(event.target.value) }))} />
          </label>
          <label className="check">
            <input type="checkbox" checked={thresholds.warnRadiusBelowDesignMinimum} onChange={(event) => setThresholds((current) => ({ ...current, warnRadiusBelowDesignMinimum: event.target.checked }))} />
            R &lt; Hesap Min.R
          </label>
          {warnings.length > 0 && (
            <div className="issues">
              <h2>Tasarım uyarıları</h2>
              <ul>
                {warnings.slice(0, 20).map((message) => <li key={message}>{message}</li>)}
              </ul>
            </div>
          )}
          {session && session.issues.length > 0 && (
            <div className="issues">
              <h2>Doğrulama</h2>
              <ul>
                {session.issues.map((issue, index) => (
                  <li key={`${issue.source}-${index}`} className={issue.severity.toLowerCase()}>
                    <strong>{issue.source}</strong> {issue.message}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </aside>
      </div>
      <EditorPanel
        rows={rows}
        selectedId={selectedId}
        canUndo={historyFlags.undo}
        canRedo={historyFlags.redo}
        canUseKtb={(session?.ktbVertices.length ?? 0) > 0}
        canUseCalculation={(design?.vertices.length ?? 0) > 0}
        canSave={Boolean(session?.hasKtbTemplate)}
        pickActive={tool === 'pick'}
        onLoadKtb={loadFromKtb}
        onLoadCalculation={loadFromCalculation}
        onNewList={newList}
        onAddRow={addRow}
        onRemoveRow={removeRow}
        onTogglePick={() => toggleTool('pick')}
        onApply={applyToProfile}
        onSave={saveEditor}
        onUndo={undo}
        onRedo={redo}
        onSelect={selectRow}
        onChange={changeRow}
      />
      {menu && (
        <div className="context-menu" style={{ left: menu.x, top: menu.y }} onClick={(event) => event.stopPropagation()}>
          <button type="button" onClick={() => { pickAt(menu.station); setMenu(null) }}>Bu noktaya some ekle</button>
          <button type="button" onClick={() => { removeRow(); setMenu(null) }} disabled={!selectedId}>Seçili someyi sil</button>
          <button type="button" onClick={() => { loadFromKtb(); setMenu(null) }}>KTB'den al</button>
          <button type="button" onClick={() => { loadFromCalculation(); setMenu(null) }}>Hesaptan al</button>
          <button type="button" onClick={() => { void applyToProfile(); setMenu(null) }}>Çizime uygula</button>
        </div>
      )}
      <footer className="status">
        <span>{status}</span>
        {error && <span className="error">{error}</span>}
      </footer>
    </div>
  )
}
