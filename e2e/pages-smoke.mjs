import { chromium, expect } from '@playwright/test'

const site = new URL(process.env.POMOGATOR_PAGES_URL)
const sitePath = site.pathname
const browser = await chromium.launch({ executablePath: process.env.POMOGATOR_CHROMIUM })
try {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  const notes = () => page.getByRole('navigation', { name: 'Разделы' }).getByRole('button', { name: 'Заметки', exact: true })
  async function folder(title) {
    await notes().click()
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
  expect(await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth })))
    .toEqual({ width: 390, scroll: 390 })

  await notes().click()
  await expect(page.getByRole('button', { name: /Папка v01 0 пап/ })).toHaveCount(0)
  await folder('Папка проверки')
  await page.getByRole('button', { name: /Папка проверки 0 пап/ }).click()
  await page.getByRole('button', { name: 'Новая карточка' }).click()
  await page.getByLabel('Название').fill('Карточка проверки')
  await expect(page.getByRole('status').filter({ hasText: 'Сохранено в этом браузере' })).toBeVisible()

  await page.reload()
  await notes().click()
  await page.getByRole('button', { name: /Папка проверки 0 пап/ }).click()
  await expect(page.getByRole('heading', { name: 'Карточка проверки' })).toBeVisible()
  const names = await page.evaluate(async () => (await indexedDB.databases()).map(database => database.name).sort())
  expect(names).toEqual(process.env.POMOGATOR_V01_URL ? ['pomogator', 'pomogator-v02-pages'] : ['pomogator-v02-pages'])

  if (process.env.POMOGATOR_V01_URL) {
    await page.goto(process.env.POMOGATOR_V01_URL)
    await notes().click()
    await expect(page.getByRole('button', { name: /Папка v01 0 пап/ })).toBeVisible()
    await expect(page.getByRole('button', { name: /Папка проверки/ })).toHaveCount(0)
  }
  expect(errors).toEqual([])
  console.log(`Проверено ${site.href} · HTTP 200, JS/CSS/манифест/иконки, 390 × 844, папка и карточка после обновления, отдельная IndexedDB`)
} finally {
  await browser.close()
}
