import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { registerSW } from 'virtual:pwa-register'
import './index.css'
import App from './App.tsx'

// Register PWA Service Worker
const updateSW = registerSW({
  onNeedRefresh() {
    // Optional: Show prompt to user
    // For now, auto reload or just let it be.
  },
  onOfflineReady() {
    console.log('App ready to work offline')
  },
})

console.log(updateSW)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
