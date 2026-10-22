import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 开发期由 Vite 代理到后端 5180：前端同源调用 /api 与 /hubs，无需处理 CORS
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'http://localhost:5180',
        changeOrigin: true
      },
      '/hubs': {
        target: 'http://localhost:5180',
        changeOrigin: true,
        ws: true
      }
    }
  },
  build: {
    outDir: 'dist'
  }
})
