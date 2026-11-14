import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  base: '/dusey/',
  plugins: [react()],
  server: {
    proxy: {
      '/dusey/api': {
        target: 'http://localhost:5298',
        rewrite: (path) => path.replace(/^\/dusey/, ''),
      },
    },
  },
})
