import { createRoot } from 'react-dom/client'
import { App } from './App'
import './style.css'

createRoot(document.getElementById('root')!).render(<App />)

if (import.meta.env.VITE_POMOGATOR_PAGES === 'true' && 'serviceWorker' in navigator) {
  void navigator.serviceWorker.register(new URL('sw.js', document.baseURI), { updateViaCache: 'none' })
    .catch(error => console.warn('Не удалось включить обновление веб-приложения.', error))
}
