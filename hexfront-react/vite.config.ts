import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// 开发期把 REST 与原生 /ws 代理到 C# 后端（HexFront.Api @5018），前端代码一律同源相对路径。
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5018', changeOrigin: true },
      '/ws': { target: 'ws://localhost:5018', ws: true },
    },
  },
})
