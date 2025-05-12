import React from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './App.css'

// Create container and render App
const container = document.getElementById('root')
if (container) {
  const root = createRoot(container)
  root.render(
    <React.StrictMode>
      <App />
    </React.StrictMode>
  )
}
