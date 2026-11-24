import { expect, test } from '@playwright/test'
import { mkdir, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { exportBackup } from '../src/backup'
import { demo } from './demo-data'

const releases = [
  { name: 'before', version: '0.2.3', path: 'preview-v0.2.3' },
  { name: 'after', version: '0.2.4', path: 'preview-v0.2.4' },
]
const viewports = [
  { width: 320, height: 700 }, { width: 390, height: 844 },
  { width: 800, height: 1000 }, { width: 1366, height: 900 },
]
const themes = ['dark', 'light'] as const

test('Снимки главного экрана до и после на тех же данных', async ({ browser, baseURL }, testInfo) => {
  test.skip(!process.env.POMOGATOR_EVIDENCE_ROOT, 'Требуются две собранные версии для сравнения.')
  test.setTimeout(120_000)
  const output = process.env.POMOGATOR_EVIDENCE_DIR || testInfo.outputDir
  await mkdir(output, { recursive: true })
  const backup = Buffer.from(await exportBackup(demo()))

  for (const viewport of viewports) {
    for (const theme of themes) {
      for (const release of releases) {
        const mobile = viewport.width < 1000
        const context = await browser.newContext({ viewport, isMobile: mobile, hasTouch: mobile,
          locale: 'ru-RU', timezoneId: 'Europe/Moscow', reducedMotion: 'reduce' })
        try {
          const page = await context.newPage()
          await page.clock.setFixedTime(new Date('2026-11-23T10:00:00Z'))
          await page.goto(new URL(`/${release.path}/`, baseURL).href)
          const heading = page.getByRole('heading', { name: 'Обзор', exact: true, level: 1 })
          await expect(heading).toBeVisible()
          await page.getByRole('button', { name: 'Настройки', exact: true }).click()
          if (theme === 'light') await page.getByRole('checkbox', { name: 'Светлая тема' }).check()
          await page.locator('.app-settings input[type=file]').setInputFiles({ name: 'example.json', mimeType: 'application/json', buffer: backup })
          await page.getByRole('button', { name: 'Подтвердить', exact: true }).click()
          await expect(heading).toBeVisible()
          await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
          await expect(page.getByRole('contentinfo')).toContainText('v' + release.version)
          await expect(page.getByText('Подготовить материалы встречи').first()).toBeVisible()
          await expect(page.getByRole('button', { name: 'Мой центр', exact: true })).toBeInViewport()

          if (release.name === 'after') {
            const header = page.locator('.app-header')
            await expect(header).toHaveCSS('backdrop-filter', /blur/)
            expect((await header.boundingBox())!.height).toBeLessThan(70)
            const dimensions = await page.evaluate(() => [document.documentElement.clientWidth, document.documentElement.scrollWidth])
            expect(dimensions[1]).toBe(dimensions[0])
            await expect(page.locator('.overview-places > article')).toHaveCount(6)
            await page.locator('.shell').evaluate(async shell => {
              const backdrop = getComputedStyle(shell, '::before').backgroundImage
              const url = backdrop.match(/url\("([^"]+)"\)/)?.[1]
              if (!url) throw new Error('Фон главного экрана не загрузился.')
              const image = new Image()
              image.src = url
              await image.decode()
            })
          }

          await page.evaluate(async () => {
            await document.fonts.ready
            window.scrollTo(0, 0)
          })
          await page.mouse.move(0, 0)
          await page.screenshot({
            path: join(output, `${release.name}-${theme}-${viewport.width}.jpg`),
            type: 'jpeg', quality: 87, animations: 'disabled',
          })
        } finally { await context.close() }
      }
    }
  }

  const pairs = viewports.flatMap(({ width }) => themes.map(theme => {
    const label = `${width} px · ${theme === 'dark' ? 'тёмная тема' : 'светлая тема'}`
    return `<section><h2>${label}</h2><div class="pair">${releases.map(release => {
      const file = `${release.name}-${theme}-${width}.jpg`
      return `<figure><figcaption>ПОМОГАТОР ${release.version}</figcaption><a href="${file}"><img src="${file}" loading="lazy" alt="Главный экран ${release.version} · ${label}"></a></figure>`
    }).join('')}</div></section>`
  })).join('')
  await writeFile(join(output, 'index.html'), `<!doctype html><html lang="ru"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>ПОМОГАТОР · сравнение 0.2.3 и 0.2.4</title>
<style>
body{margin:0;background:#e6f0e8;color:#193629;font:16px/1.5 system-ui}
main{max-width:1100px;margin:auto;padding:clamp(12px,4vw,40px)}
section{margin-block:24px}h1{line-height:1.15}h2{font-size:1.1rem}
.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}
figure{margin:0}figcaption{margin-bottom:.5rem}img{display:block;width:100%;border-radius:10px}
a{color:#185e5b}a:focus-visible{outline:3px solid currentColor;outline-offset:3px}
</style>
<main><a href="../">Открыть ПОМОГАТОР 0.2.4</a><h1>Главный экран до и после</h1>
<p>Обе версии показаны с одинаковыми тестовыми карточками и одной датой. Нажмите снимок, чтобы открыть его в исходном размере.</p>${pairs}</main></html>`)
})
