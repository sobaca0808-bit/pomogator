import { chromium, expect } from '@playwright/test'
import { readFile, writeFile } from 'node:fs/promises'

const [phase, stateFile] = process.argv.slice(2)
const url = new URL(process.env.POMOGATOR_PAGES_URL).href
const folderName = 'Тест сохранности при публикации'
const cardName = 'Карточка прежней сборки'
const oldCache = 'pomogator-pages-upgrade-test'
const browser = await chromium.launch({ executablePath: process.env.POMOGATOR_CHROMIUM })

try {
  const previous = phase === 'verify' ? JSON.parse(await readFile(stateFile, 'utf8')) : undefined
  const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, storageState: previous?.storage })
  const page = await context.newPage()
  const button = name => page.getByRole('button', { name, exact: true })
  const folder = () => page.getByRole('button', { name: /^Тест сохранности при публикации 0 пап/ })
  async function notes() {
    const menu = page.locator('.navigation-menu')
    if (await menu.isVisible() && await menu.getAttribute('open') === null) await menu.locator('summary').click()
    await page.getByRole('navigation', { name: 'Разделы', exact: true }).getByRole('button', { name: 'Заметки', exact: true }).click()
  }

  const response = await page.goto(url)
  expect(response.status()).toBe(200)
  await expect(page.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible()
  const build = page.locator('meta[name=pomogator-build]')
  if (phase === 'capture') {
    const commit = await build.getAttribute('content')
    expect(commit).toMatch(/^[a-f0-9]{40}$/)
    const html = await response.text()
    await notes()
    await button('Папка').click()
    await page.getByLabel('Название').fill(folderName)
    await button('Сохранить').click()
    await folder().click()
    await button('Новая карточка').click()
    await page.getByLabel('Название').fill(cardName)
    await expect(page.getByRole('status').filter({ hasText: 'Сохранено в этом браузере' })).toBeVisible()
    const storage = await context.storageState({ indexedDB: true })
    expect(storage.origins.some(origin => origin.indexedDB?.some(db => db.name === 'pomogator-v02-pages'))).toBe(true)
    await writeFile(stateFile, JSON.stringify({ commit, html, storage }))
    console.log('Сохранена только тестовая запись из опубликованной ревизии ' + commit)
  } else if (phase === 'verify') {
    const commit = process.env.EXPECTED_COMMIT
    const version = process.env.EXPECTED_VERSION
    await expect(build).toHaveAttribute('content', commit)
    await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller?.scriptURL)).toBe(new URL('sw.js', url).href)
    await page.evaluate(async ({ key, html, url }) => {
      const cache = await caches.open(key)
      await cache.put(url, new Response(html, { headers: { 'Content-Type': 'text/html' } }))
    }, { key: oldCache, html: previous.html, url })
    const reloaded = await page.reload()
    expect(reloaded.status()).toBe(200)
    expect(reloaded.fromServiceWorker()).toBe(true)
    expect(page.url()).toBe(url)
    await expect(build).toHaveAttribute('content', commit)
    await expect(page.getByRole('contentinfo')).toHaveText(`ПОМОГАТОР · v${version} · build ${commit.slice(0, 7)}`)
    await expect(page.locator('.app-header')).toHaveCSS('backdrop-filter', /blur/)
    const cached = await page.evaluate(async ({ key, url }) => (await (await caches.open(key)).match(url))?.text(),
      { key: oldCache, url })
    expect(cached).toBe(previous.html)
    await notes()
    await folder().click()
    await expect(page.getByRole('heading', { name: cardName })).toBeVisible()
    await expect(page.getByRole('article').filter({ hasText: cardName })).toHaveClass(/glass-surface/)
    console.log(`HTTP 200 на компьютере · ${previous.commit} → ${commit} · старый HTML в CacheStorage пропущен · запись сохранена`)
  } else throw new Error('Expected capture or verify.')
} finally { await browser.close() }
