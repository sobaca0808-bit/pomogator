import { blocksOf, cardStatus, firstValue, kindOf, tagsOf } from './cards'
import { validAmount, type Data, type Entry } from './model'

const roubles = new Intl.NumberFormat('ru-RU')
const kopecks = (amount: string) => {
  const negative = amount.startsWith('-')
  const [whole, cents = ''] = (negative ? amount.slice(1) : amount).split('.')
  return (BigInt(whole || '0') * 100n + BigInt(cents.padEnd(2, '0'))) * (negative ? -1n : 1n)
}

export function money(value: bigint) {
  const abs = value < 0n ? -value : value
  return (value < 0n ? '−' : '') + roubles.format(abs / 100n) + ',' + String(abs % 100n).padStart(2, '0') + ' ₽'
}

export type Operation = { entry: Entry; amount: bigint; day: string; category: string; planned: boolean }
export function operations(data: Data): Operation[] {
  return data.entries.flatMap(entry => {
    const kind = kindOf(data, entry)
    if (kind !== 'purchase' && kind !== 'payment') return []
    const values = blocksOf(entry).flatMap(block => block.type === 'amount' && block.value && validAmount(block.value) ?
      [kopecks(block.value)] : [])
    if (!values.length) return []
    const total = values.reduce((sum, value) => sum + (value < 0n ? -value : value), 0n)
    const income = entry.direction ? entry.direction === 'income' : values.every(value => value >= 0n)
    return [{ entry, amount: income ? total : -total, day: firstValue(entry, 'date'),
      category: entry.category || tagsOf(entry)[0] || 'Без категории', planned: Boolean(firstValue(entry, 'status')) && cardStatus(entry) !== 'done' }]
  }).sort((a, b) => b.day.localeCompare(a.day) || a.entry.title.localeCompare(b.entry.title, 'ru'))
}

export function financeTotals(items: Operation[]) {
  let income = 0n, expenses = 0n, planned = 0n
  const categories = new Map<string, bigint>()
  for (const item of items) {
    if (item.planned) { if (item.amount < 0n) planned -= item.amount; continue }
    if (item.amount >= 0n) income += item.amount
    else {
      expenses -= item.amount
      categories.set(item.category, (categories.get(item.category) ?? 0n) - item.amount)
    }
  }
  return { balance: income - expenses, income, expenses, planned, categories: [...categories].sort((a, b) =>
    a[1] === b[1] ? a[0].localeCompare(b[0], 'ru') : a[1] > b[1] ? -1 : 1) }
}

export function financeCsv(items: Operation[]) {
  const field = (value: string) => '"' + (/^[\s\uFEFF]*[=+@-]/.test(value) ? "'" : '') + value.replaceAll('"', '""') + '"'
  const rows = items.map(item => [field(item.day), field(item.entry.title), field(item.category),
    field(item.planned ? 'План' : 'Проведено'), '"' + (item.amount < 0n ? '-' : '') + String((item.amount < 0n ? -item.amount : item.amount) / 100n) +
      ',' + String((item.amount < 0n ? -item.amount : item.amount) % 100n).padStart(2, '0') + '"'].join(';'))
  return '\uFEFFДата;Название;Категория;Статус;Сумма (₽)\r\n' + rows.join('\r\n')
}
