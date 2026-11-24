import { chromium, expect } from '@playwright/test'
import { _android } from 'playwright'

const site = new URL(process.env.POMOGATOR_PAGES_URL)
const sitePath = site.pathname
const android = process.env.POMOGATOR_ANDROID === '1'
const device = android ? (await _android.devices())[0] : undefined
if (android && !device) throw new Error('Android device unavailable')
const browser = !device ? await chromium.launch({ executablePath: process.env.POMOGATOR_CHROMIUM }) : undefined
const context = device ? await device.launchBrowser({ hasTouch: true }) :
  await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })
try {
  const page = context.pages()[0] ?? await context.newPage()
  const activate = target => android ? target.tap() : target.click()
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  const notes = () => page.getByRole('navigation', { name: 'Разделы' }).getByRole('button', { name: 'Заметки', exact: true })
  async function folder(title) {
    await activate(notes())
    await page.getByRole('button', { name: 'Папка', exact: true }).click()
    await page.getByLabel('Название').fill(title)
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.getByRole('button', { name: new RegExp(title + ' 0 пап') })).toBeVisible()
  }

  if (process.env.POMOGATOR_V01_URL) {
    const old = await page.goto(process.env.POMOGATOR_V01_URL)
    expect(old.status()).toBe(200)
    await folder('Папка v01')
  }

  const response = await page.goto(site.href)
  expect(response.status()).toBe(200)
  expect(new URL(page.url()).href).toBe(site.href)
  await expect(page.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible()
  const urls = await page.locator('script[src], link[rel=stylesheet], link[rel=icon], link[rel=manifest]')
    .evaluateAll(nodes => nodes.map(node => ({ url: node.src ?? node.href, rel: node.rel || 'script' })))
  expect(urls.map(asset => asset.rel)).toEqual(expect.arrayContaining(['script', 'stylesheet', 'icon', 'manifest']))
  for (const { url, rel } of urls) {
    expect(new URL(url).origin).toBe(site.origin)
    expect(new URL(url).pathname.startsWith(sitePath)).toBe(true)
    const asset = await context.request.get(url)
    expect(asset.status()).toBe(200)
    if (rel === 'script') expect(asset.headers()['content-type']).toMatch(/javascript|ecmascript/)
    if (rel === 'stylesheet') expect(asset.headers()['content-type']).toMatch(/text\/css/)
    if (rel === 'manifest') {
      const manifest = await asset.json()
      expect(new URL(manifest.start_url, url).href).toBe(site.href)
      for (const icon of manifest.icons) expect((await context.request.get(new URL(icon.src, url).href)).status()).toBe(200)
    }
  }
  const { width, scroll } = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }))
  expect(width).toBeLessThanOrEqual(500)
  expect(scroll).toBeLessThanOrEqual(width)

  const expected = process.env.EXPECTED_COMMIT
  const metadata = await context.request.get(new URL('build.json?v=' + expected, site).href)
  expect(metadata.status()).toBe(200)
  expect((await metadata.json()).commit).toBe(expected)
  await expect(page.locator('meta[name=pomogator-build]')).toHaveAttribute('content', expected)

  await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller?.scriptURL)).toBe(new URL('sw.js', site).href)
  const session = await context.newCDPSession(page)
  const { installabilityErrors } = await session.send('Page.getInstallabilityErrors')
  expect(installabilityErrors).toEqual([])
  const { data: manifestText } = await session.send('Page.getAppManifest')
  expect(JSON.parse(manifestText).display).toBe('standalone')
  await session.detach()

  await activate(page.getByRole('button', { name: 'Предстоящие платежи', exact: true }))
  await expect(page.getByRole('heading', { name: 'Предстоящие платежи', level: 1 })).toBeVisible()

  await activate(notes())
  await expect(page.getByRole('button', { name: /Папка v01 0 пап/ })).toHaveCount(0)
  await folder('Папка проверки')
  await page.getByRole('button', { name: /Папка проверки 0 пап/ }).click()
  await page.getByRole('button', { name: 'Новая карточка' }).click()
  await page.getByLabel('Название').fill('Карточка проверки')
  await expect(page.getByRole('status').filter({ hasText: 'Сохранено в этом браузере' })).toBeVisible()

  await page.reload()
  await activate(notes())
  await page.getByRole('button', { name: /Папка проверки 0 пап/ }).click()
  await expect(page.getByRole('heading', { name: 'Карточка проверки' })).toBeVisible()
  const names = await page.evaluate(async () => (await indexedDB.databases()).map(database => database.name).sort())
  expect(names).toEqual(process.env.POMOGATOR_V01_URL ? ['pomogator', 'pomogator-v02-pages'] : ['pomogator-v02-pages'])

  if (process.env.POMOGATOR_V01_URL) {
    await page.goto(process.env.POMOGATOR_V01_URL)
    await activate(notes())
    await expect(page.getByRole('button', { name: /Папка v01 0 пап/ })).toBeVisible()
    await expect(page.getByRole('button', { name: /Папка проверки/ })).toHaveCount(0)
  }
  expect(errors).toEqual([])
  console.log(`Проверено ${site.href} · HTTP 200, ревизия ${expected}, критерии установки Chrome, карточка после обновления, отдельная IndexedDB`)
} finally {
  try { await context.close() } finally {
    if (device) await device.close()
    if (browser) await browser.close()
  }
}
