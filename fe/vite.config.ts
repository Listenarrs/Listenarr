import { fileURLToPath, URL } from 'node:url'
import type { ServerResponse } from 'node:http'

import { defineConfig } from 'vite'
import type { PluginOption } from 'vite'
import vue from '@vitejs/plugin-vue'
import { visualizer } from 'rollup-plugin-visualizer'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const analyzeBundle =
    mode === 'analysis' ||
    process.env.LISTENARR_BUNDLE_ANALYZE === 'true' ||
    process.env.ANALYZE === 'true'

  return {
    plugins: [
      vue(),
      ...(analyzeBundle
        ? [
            (visualizer({
              filename: 'dist/stats.html',
              title: 'Listenarr bundle analysis',
              open: false,
            }) as unknown as PluginOption),
          ]
        : []),
    ],
    build: {
      sourcemap: analyzeBundle,
      rollupOptions: {
        onLog(level, log, handler) {
          const code = typeof log === 'object' && log ? String(log.code ?? '') : ''
          const id = typeof log === 'object' && log ? String(log.id ?? '') : ''
          const message = typeof log === 'object' && log ? String(log.message ?? '') : String(log)

          if (
            level === 'warn' &&
            code === 'INVALID_ANNOTATION' &&
            id.includes('node_modules/@vueuse/core/')
          ) {
            return
          }

          if (
            level === 'warn' &&
            code === 'INEFFECTIVE_DYNAMIC_IMPORT' &&
            message.includes('src/router/index.ts') &&
            message.includes('src/stores/auth.ts')
          ) {
            return
          }

          handler(level, log)
        },
      },
    },
    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url))
      },
    },
    server: {
      // Use a fixed port so local development URLs remain stable
      port: 5173,
      proxy: {
        '/api': {
          target: 'http://localhost:4545',
          changeOrigin: true,
          // Rewrite cookie domains coming from the backend so the browser will
          // accept Set-Cookie when the backend sets cookies for its own host.
          // Use the object form which is more explicit and reliable across
          // environments. Also rewrite path to '/' to ensure cookie applies.
          cookieDomainRewrite: { '*': '' },
          cookiePathRewrite: '/',
          // Ensure the original Cookie header from the browser is forwarded to
          // the backend. Some proxy environments do not forward cookies by
          // default; adding this configure hook forces the header through.
          configure: (proxy) => {
            if (proxy && typeof proxy.on === 'function') {
              proxy.on('error', (err, _req, res) => {
                if ((err as NodeJS.ErrnoException).code === 'ECONNREFUSED') {
                  // API not ready yet - return 503 silently instead of logging to console
                  try {
                    if (res && typeof (res as ServerResponse).writeHead === 'function') {
                      const httpRes = res as ServerResponse
                      if (!httpRes.headersSent) {
                        httpRes.writeHead(503, { 'Content-Type': 'application/json' })
                        httpRes.end(JSON.stringify({ message: 'API is starting, please retry.' }))
                      }
                    }
                  } catch { /* ignore */ }
                  return
                }
              })
              proxy.on('proxyReq', (proxyReq, req) => {
                try {
                  const origCookie = req && req.headers && (req.headers['cookie'] || req.headers.cookie)
                  if (origCookie) {
                    proxyReq.setHeader('cookie', origCookie)
                  }
                } catch {}
              })
            }
          }
        },
        '/hubs': {
          target: 'http://localhost:4545',
          changeOrigin: true,
          ws: true,
          configure: (proxy) => {
            if (proxy && typeof proxy.on === 'function') {
              proxy.on('error', (err) => {
                if ((err as NodeJS.ErrnoException).code !== 'ECONNREFUSED') {
                  console.error('[vite proxy /hubs]', err)
                }
                // ECONNREFUSED during startup - ignore silently
              })
            }
          }
        }
      }
    }
  }
})
