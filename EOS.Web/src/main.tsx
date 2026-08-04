import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@tabler/core/dist/css/tabler.min.css'
import './styles/app.css'
import { AppProviders } from './app/providers'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppProviders />
  </StrictMode>,
)
