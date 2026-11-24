import { blocksOf, cardStatus, firstValue, kindOf, type Repeat } from './cards'
import type { Data, Entry } from './model'

export function dayKey(date = new Date()) {
  return [date.getFullYear(), date.getMonth() + 1, date.getDate()].map((part, i) => String(part).padStart(i ? 2 : 4, '0')).join('-')
}
export const fromDay = (key: string) => new Date(key + 'T12:00:00')
export const shiftDay = (key: string, count: number) => {
  const date = fromDay(key)
  date.setDate(date.getDate() + count)
  return dayKey(date)
}
const utcDay = (key: string) => Date.parse(key + 'T00:00:00Z') / 86_400_000
export const datesOf = (entry: Entry) => [...new Set(blocksOf(entry).flatMap(block => block.type === 'date' && block.value ? [block.value] : []))]

export function occursOn(first: string, target: string, repeat: Repeat) {
  if (target < first) return false
  if (repeat === 'none') return first === target
  if (repeat === 'daily') return true
  if (repeat === 'weekly') return (utcDay(target) - utcDay(first)) % 7 === 0
  const date = fromDay(target)
  const days = new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate()
  return date.getDate() === Math.min(Number(first.slice(8)), days)
}

export function dayEntries(data: Data, day: string) {
  return data.entries.filter(entry => {
    const repeat = cardStatus(entry) === 'done' ? 'none' : entry.repeat ?? 'none'
    return datesOf(entry).some(date => occursOn(date, day, repeat)) ||
      blocksOf(entry).some(block => block.type === 'reminder' && block.value.slice(0, 10) === day)
  }).sort((a, b) => firstValue(a, 'time').localeCompare(firstValue(b, 'time')) || a.title.localeCompare(b.title, 'ru'))
}

export function upcoming(data: Data, kind: 'task' | 'payment', after: string, limit = 5) {
  const matches: { entry: Entry; day: string }[] = []
  for (const entry of data.entries) {
    if (kindOf(data, entry) !== kind || cardStatus(entry) === 'done') continue
    let next: string | undefined
    for (const date of datesOf(entry)) {
      if (date >= after) {
        if (!next || date < next) next = date
        continue
      }
      if (!entry.repeat || entry.repeat === 'none') continue
      for (let days = 0; days <= 31; days++) {
        const candidate = shiftDay(after, days)
        if (!occursOn(date, candidate, entry.repeat)) continue
        if (!next || candidate < next) next = candidate
        break
      }
    }
    if (next) matches.push({ entry, day: next })
  }
  return matches.sort((a, b) => a.day.localeCompare(b.day) || a.entry.title.localeCompare(b.entry.title, 'ru')).slice(0, limit)
}

export function dueReminders(data: Data, now: number, seen: string[]) {
  const dismissed = new Set(seen)
  return data.entries.flatMap(entry => cardStatus(entry) === 'done' ? [] : blocksOf(entry).flatMap(block => {
    if (block.type !== 'reminder' || !block.value || Date.parse(block.value) > now || !Number.isFinite(Date.parse(block.value))) return []
    const key = [entry.id, block.id, block.value].join('|')
    return dismissed.has(key) ? [] : [{ entry, key, date: block.value }]
  })).sort((a, b) => a.date.localeCompare(b.date))
}


export function deadlineState(day: string, today: string, done: boolean) {
  if (done) return 'done'
  if (day < today) return 'late'
  if (day === today) return 'today'
  return day <= shiftDay(today, 3) ? 'soon' : 'upcoming'
}
