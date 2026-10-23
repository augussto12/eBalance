import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import basicSsl from '@vitejs/plugin-basic-ssl';

// Desarrollo: HTTPS local (las cookies __Host- exigen Secure) y proxy /api → API local. Sin CORS.
export default defineConfig(({ command }) => ({
  plugins: [react(), ...(command === 'serve' ? [basicSsl()] : [])],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // xfwd: envía X-Forwarded-Proto=https; la API confía solo en loopback (App:TrustedProxies en Development).
      '/api': { target: 'http://localhost:5080', changeOrigin: false, xfwd: true },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
    // Las pruebas de estilos leen las hojas con ?raw (sin esto Vitest las devuelve vacías).
    css: { include: [/styles\/.+\.css/] },
  },
}));
