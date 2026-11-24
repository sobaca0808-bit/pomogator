// Keep the shell current. IndexedDB and other applications' caches are left alone.
self.addEventListener('install', event => event.waitUntil(self.skipWaiting()))
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()))
self.addEventListener('fetch', event => {
  const request = event.request
  if (request.method === 'GET' && request.mode === 'navigate' && request.url.startsWith(self.registration.scope)) {
    event.respondWith(fetch(request, { cache: 'no-store' }))
  }
})
