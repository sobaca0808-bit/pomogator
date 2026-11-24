import { reorder } from './cards'
import type { Data } from './model'

export const shortcutCatalog = {
  custom: { name: 'Своя ссылка', url: '' },
  telegram: { name: 'Telegram', url: 'https://t.me/' },
  whatsapp: { name: 'WhatsApp', url: 'https://wa.me/' },
  vk: { name: 'ВКонтакте', url: 'https://vk.com/' },
  yandexMusic: { name: 'Яндекс Музыка', url: 'https://music.yandex.ru/' },
  youtube: { name: 'YouTube', url: 'https://www.youtube.com/' },
  github: { name: 'GitHub', url: 'https://github.com/' },
  steam: { name: 'Steam', url: 'https://store.steampowered.com/' },
  browser: { name: 'Браузер', url: '' },
  mail: { name: 'Почта', url: '' },
  gmail: { name: 'Gmail', url: 'https://mail.google.com/' },
  discord: { name: 'Discord', url: 'https://discord.com/' },
  notion: { name: 'Notion', url: 'https://www.notion.so/' },
  maps: { name: 'Google Карты', url: 'https://maps.google.com/' },
} as const

export type ShortcutIcon = keyof typeof shortcutCatalog
export type Shortcut = { id: string; title: string; url: string; icon: ShortcutIcon }

export function shortcutUrl(value: string): string | undefined {
  const address = value.trim()
  if (!/^https:\/\//i.test(address) || /[\s\\\x00-\x1f\x7f]/u.test(address)) return
  try {
    const parsed = new URL(address)
    if (parsed.protocol === 'https:' && parsed.hostname && !parsed.username && !parsed.password) return parsed.href
  } catch { /* The editor reports an invalid address. */ }
}

function readShortcut(raw: unknown): Shortcut {
  const invalid = new Error('Обнаружен недопустимый ярлык «Моего центра». Укажите название и полную HTTPS-ссылку без учётных данных.')
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw invalid
  const { id, title, url, icon } = raw as Record<string, unknown>
  if (typeof id !== 'string' || !id.trim() || typeof title !== 'string' || !title.trim() ||
      typeof url !== 'string' || typeof icon !== 'string' || !Object.hasOwn(shortcutCatalog, icon)) throw invalid
  const address = shortcutUrl(url)
  if (!address) throw invalid
  return { id, title: title.trim(), url: address, icon: icon as ShortcutIcon }
}

export function readShortcuts(value: unknown): Shortcut[] {
  if (!Array.isArray(value)) throw new Error('Ярлыки «Моего центра» должны быть списком.')
  const shortcuts = value.map(readShortcut)
  if (new Set(shortcuts.map(item => item.id)).size !== shortcuts.length)
    throw new Error('Резервная копия содержит ярлыки с одинаковыми идентификаторами.')
  return shortcuts
}

function indexOfShortcut(shortcuts: Shortcut[], previous: Shortcut) {
  const index = shortcuts.findIndex(item => item.id === previous.id)
  const current = shortcuts[index]
  if (!current || current.title !== previous.title || current.url !== previous.url || current.icon !== previous.icon)
    throw new Error('Ярлык изменился или был удалён. Откройте актуальный список.')
  return index
}

export function saveShortcut(data: Data, value: Shortcut, previous?: Shortcut): Data {
  const saved = readShortcut(value)
  const shortcuts = [...(data.shortcuts ?? [])]
  if (previous) {
    if (saved.id !== previous.id) throw new Error('Идентификатор ярлыка изменился.')
    shortcuts[indexOfShortcut(shortcuts, previous)] = saved
  } else {
    if (shortcuts.some(item => item.id === value.id)) throw new Error('Ярлык уже сохранён.')
    shortcuts.push(saved)
  }
  return { ...data, shortcuts }
}

export function removeShortcut(data: Data, previous: Shortcut): Data {
  const shortcuts = [...(data.shortcuts ?? [])]
  shortcuts.splice(indexOfShortcut(shortcuts, previous), 1)
  return { ...data, shortcuts }
}

export function moveShortcut(data: Data, id: string, offset: -1 | 1): Data {
  const shortcuts = data.shortcuts ?? []
  return { ...data, shortcuts: reorder(shortcuts, shortcuts.findIndex(item => item.id === id), offset) }
}
