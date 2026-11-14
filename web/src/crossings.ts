import { readCrossingSheet } from './api'

export type Crossing = {
  id: string
  name: string
  type: string
  startKm: number
  endKm: number
  depth: number
}

export const crossingTemplate = [
  'özel geçiş adı;tip;başlangıç km;bitiş km;derinlik (m)',
  'Örnek yol;karayolu;1.200;1.280;1.2',
  'Örnek demiryolu;demiryolu;3.400;3.460;1.5',
  'Örnek dere;akarsu;5.100;5.180;2.5',
].join('\r\n')

let crossingSeq = 1

export function emptyCrossing(): Crossing {
  crossingSeq += 1
  return { id: `gecis-${crossingSeq}`, name: '', type: '', startKm: 0, endKm: 0, depth: 1.2 }
}

export function downloadCrossingTemplate() {
  const blob = new Blob([`\uFEFF${crossingTemplate}`], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = 'ozel-gecis-sablonu.csv'
  link.click()
  URL.revokeObjectURL(url)
}

export type CrossingImport = { crossings: Crossing[]; note: string }

/**
 * CSV/TXT tarayıcıda, .xlsx sunucuda okunur. İki düzen tanınır: başlıklı tek satır şablonu
 * (başlangıç km, bitiş km, derinlik) ve Python V33.1 düzeni (KM, cins, derinlik; her geçiş iki satır).
 * `profileEndKm` verilirse Python düzenindeki düz sayıların metre mi km mi olduğu buna göre seçilir.
 */
export async function readCrossingFile(file: File, profileEndKm?: number): Promise<CrossingImport> {
  const name = file.name.toLocaleLowerCase('tr')
  if (name.endsWith('.xls')) {
    throw new Error('Eski .xls biçimi okunamıyor. Excel’de «Farklı Kaydet → Excel Çalışma Kitabı (.xlsx)» ile kaydedin.')
  }
  const rows = name.endsWith('.xlsx') ? await readCrossingSheet(file) : splitTable(await file.text())
  return parseCrossingRows(rows, profileEndKm)
}

export function parseCrossingTable(text: string, profileEndKm?: number): CrossingImport {
  return parseCrossingRows(splitTable(text), profileEndKm)
}

function splitTable(text: string): string[][] {
  const lines = text.replace(/^\uFEFF/, '').split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  if (lines.length === 0) {
    return []
  }
  const delimiter = lines[0].includes(';') ? ';' : lines[0].includes('\t') ? '\t' : ','
  return lines.map((line) => line.split(delimiter))
}

export function parseCrossingRows(rows: string[][], profileEndKm?: number): CrossingImport {
  const cleaned = rows
    .map((row) => row.map((cell) => (cell ?? '').trim()))
    .filter((row) => row.some((cell) => cell.length > 0))
  if (cleaned.length === 0) {
    return { crossings: [], note: 'Liste boş.' }
  }

  const header = cleaned[0].map(normalizeHeader)
  const columns = {
    nameAt: columnIndex(header, ['ozel gecis adi', 'ad', 'adi', 'name']),
    typeAt: columnIndex(header, ['tip', 'type']),
    startAt: columnIndex(header, ['baslangic km', 'baslangic', 'start']),
    endAt: columnIndex(header, ['bitis km', 'bitis', 'end']),
    depthAt: columnIndex(header, ['derinlik (m)', 'derinlik', 'depth']),
  }
  if (columns.startAt >= 0 && columns.endAt >= 0 && columns.depthAt >= 0) {
    const crossings = parseTemplateRows(cleaned.slice(1), columns)
    return { crossings, note: `${crossings.length} özel geçiş okundu (şablon düzeni).` }
  }

  const paired = parsePairedRows(cleaned, profileEndKm)
  if (paired.crossings.length > 0) {
    return paired
  }
  throw new Error('Liste tanınmadı. Başlıklı şablonu (başlangıç km, bitiş km, derinlik) ya da Python düzenini (KM, cins, derinlik; her geçiş iki satır) kullanın.')
}

function parseTemplateRows(
  rows: string[][],
  columns: { nameAt: number; typeAt: number; startAt: number; endAt: number; depthAt: number },
): Crossing[] {
  const result: Crossing[] = []
  for (const cells of rows) {
    const startKm = parseKm(cells[columns.startAt] ?? '')
    const endKm = parseKm(cells[columns.endAt] ?? '')
    const depth = parseNumber(cells[columns.depthAt] ?? '')
    if (startKm === null || endKm === null || depth === null) {
      continue
    }
    crossingSeq += 1
    result.push({
      id: `gecis-${crossingSeq}`,
      name: columns.nameAt >= 0 ? cells[columns.nameAt] ?? '' : '',
      type: columns.typeAt >= 0 ? cells[columns.typeAt] ?? '' : '',
      startKm,
      endKm,
      depth,
    })
  }
  return result
}

/**
 * Python V33.1 listesi: 1. sütun KM, 2. sütun cins, 3. sütun derinlik; ardışık iki geçerli satır bir geçiştir.
 * Python bu KM değerini profil X koordinatı (metre) olarak kullanır; "1+250" yazımı her zaman km sayılır.
 */
function parsePairedRows(rows: string[][], profileEndKm?: number): CrossingImport {
  const points: { value: number; plus: boolean; kind: string; depth: number }[] = []
  for (const cells of rows) {
    const raw = cells[0] ?? ''
    const value = parseKm(raw)
    const depth = parseNumber(cells[2] ?? '')
    if (value === null || depth === null) {
      continue
    }
    points.push({ value, plus: raw.includes('+'), kind: cells[1] ?? '', depth })
  }

  const plainValues = points.filter((point) => !point.plus).map((point) => point.value)
  const plainAreMeters = plainValues.length > 0
    && (profileEndKm === undefined || Math.max(...plainValues) > profileEndKm + 0.1)
  const toKm = (point: (typeof points)[number]) => (!point.plus && plainAreMeters ? point.value / 1000 : point.value)

  const crossings: Crossing[] = []
  for (let i = 0; i + 1 < points.length; i += 2) {
    const first = points[i]
    const second = points[i + 1]
    const a = toKm(first)
    const b = toKm(second)
    crossingSeq += 1
    crossings.push({
      id: `gecis-${crossingSeq}`,
      name: first.kind,
      type: first.kind,
      startKm: Math.min(a, b),
      endKm: Math.max(a, b),
      depth: Math.max(first.depth, second.depth),
    })
  }

  const unit = plainAreMeters ? 'KM sütunu metre kabul edildi' : 'KM sütunu kilometre kabul edildi'
  const leftover = points.length % 2 === 1 ? ' Son satırın eşi olmadığı için atlandı.' : ''
  return {
    crossings,
    note: `${crossings.length} özel geçiş okundu (Python düzeni, ${unit}).${leftover}`,
  }
}

function normalizeHeader(value: string) {
  return value
    .toLocaleLowerCase('tr')
    .replace(/ı/g, 'i')
    .replace(/ş/g, 's')
    .replace(/ğ/g, 'g')
    .replace(/ü/g, 'u')
    .replace(/ö/g, 'o')
    .replace(/ç/g, 'c')
    .trim()
}

function columnIndex(header: string[], names: string[]) {
  return header.findIndex((cell) => names.includes(cell))
}

function parseKm(value: string): number | null {
  const text = value.trim()
  if (!text) {
    return null
  }
  const plus = text.match(/^(\d+)\s*\+\s*(\d+(?:[.,]\d+)?)$/)
  if (plus) {
    const km = Number(plus[1])
    const meters = parseNumber(plus[2])
    if (meters === null) {
      return null
    }
    return km + meters / 1000
  }
  return parseNumber(text)
}

function parseNumber(value: string): number | null {
  const number = Number(value.replace(',', '.'))
  return Number.isFinite(number) ? number : null
}
