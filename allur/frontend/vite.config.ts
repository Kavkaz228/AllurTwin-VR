import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // Rollup 4.64's call-argument tree shaking takes several minutes and >1 GB
  // for this React graph. Esbuild still minifies the complete 245 kB bundle.
  build: { rollupOptions: { treeshake: false } },
  server: { host: '127.0.0.1', port: 8089, strictPort: true, proxy: { '/api': 'http://127.0.0.1:8182' } },
  preview: { host: '127.0.0.1', port: 8089, strictPort: true, proxy: { '/api': 'http://127.0.0.1:8182' } },
});
