import { useLayoutEffect, useRef, useState } from 'react'
import type { WidgetView } from './Overview'

export type View = 'home' | 'folders' | 'calendar' | 'settings' | 'center' | WidgetView
type Screen = { view: View; folder: string | null; query: string }
type Frame = Screen & { session: string; index: number; dialog?: number }
const initial: Screen = { view: 'home', folder: null, query: '' }

export function useNavigation<T>(canLeave: () => boolean) {
  const [frame, setFrame] = useState<Frame>(() => ({ ...initial, session: crypto.randomUUID(), index: 0 }))
  const current = useRef(frame)
  const dialogs = useRef(new Map<number, T>())
  const scroll = useRef(new Map<number, number>())
  const restoreScroll = useRef<number>(0)
  const nextDialog = useRef(0)
  const destination = useRef<Frame | null>(null)
  const accepting = useRef<(() => void) | null>(null)
  const guard = useRef<(() => boolean | Promise<boolean>) | null>(null)

  function activate(next: Frame) {
    current.current = next
    setFrame(next)
  }

  useLayoutEffect(() => {
    const original = history.scrollRestoration
    history.scrollRestoration = 'manual'
    history.replaceState(current.current, '', location.pathname + location.search)
    async function pop(event: PopStateEvent) {
      const previous = current.current
      const next = event.state as Frame | null
      if (!next || next.session !== previous.session) return
      if (destination.current) {
        destination.current = next
        return
      }
      if (next.index === previous.index) return
      destination.current = next
      const done = accepting.current
      accepting.current = null
      const allowed = done || (canLeave() && (!previous.dialog || previous.dialog === next.dialog || !guard.current || await guard.current()))
      const target = destination.current
      destination.current = null
      if (target.index === previous.index) return
      if (!allowed) {
        history.go(previous.index - target.index)
        return
      }
      if (previous.dialog !== target.dialog && previous.dialog) dialogs.current.delete(previous.dialog)
      scroll.current.set(previous.index, window.scrollY)
      restoreScroll.current = scroll.current.get(target.index) ?? 0
      activate(target)
      done?.()
    }
    window.addEventListener('popstate', pop)
    return () => {
      history.scrollRestoration = original
      window.removeEventListener('popstate', pop)
    }
  }, [])

  useLayoutEffect(() => {
    window.scrollTo({ top: restoreScroll.current, behavior: 'instant' })
  }, [frame.index])

  function push(screen: Screen, dialog?: number, restore = 0) {
    const previous = current.current
    scroll.current.set(previous.index, window.scrollY)
    for (const index of scroll.current.keys()) if (index > previous.index) scroll.current.delete(index)
    const next = { ...screen, session: previous.session, index: previous.index + 1, dialog }
    // Android Chrome's system Back uses the browser's navigation entries.
    // Give each same-document entry a distinct URL while keeping drafts in memory.
    history.pushState(next, '', '#screen-' + next.index)
    restoreScroll.current = restore
    activate(next)
  }

  function open(view: View, folder: string | null = null) {
    const previous = current.current
    if (view === previous.view && folder === previous.folder && !previous.query && !previous.dialog) return
    push({ view, folder, query: '' })
  }

  function search(query: string) {
    const next = { ...current.current, query }
    history.replaceState(next, '')
    activate(next)
  }

  function show(value: T) {
    const previous = current.current
    const token = previous.dialog ?? ++nextDialog.current
    dialogs.current.set(token, value)
    const next = { ...previous, dialog: token }
    if (previous.dialog) {
      history.replaceState(next, '')
      activate(next)
    } else push(previous, token, window.scrollY)
  }

  function dismiss(done: () => void = () => {}) {
    if (destination.current || accepting.current || !current.current.dialog) return
    accepting.current = done
    history.back()
  }

  function back() {
    if (destination.current || accepting.current) return
    if (current.current.index > 0) history.back()
    else open('home')
  }

  return { frame, guard, open, search, show, dismiss, back,
    dialog: frame.dialog ? dialogs.current.get(frame.dialog) : undefined, canBack: frame.index > 0 }
}
