<template>
  <div class="login-page">
    <div class="login-card" role="main" aria-labelledby="login-title">
      <img :src="logoUrl" alt="Listenarr" class="login-logo" />

      <form class="login-form" @submit.prevent="onSubmit" aria-live="polite">
        <div class="form-row">
          <label class="form-label" for="username">Username</label>
          <input
            id="username"
            class="form-input"
            v-model="username"
            autocomplete="username"
            autofocus
            aria-required="true"
          />
        </div>

        <div class="form-row">
          <label class="form-label" for="password">Password</label>
          <PasswordInput
            id="password"
            class="form-input"
            v-model="password"
            autocomplete="current-password"
            aria-required="true"
            placeholder=""
          />
        </div>

        <div class="form-row form-remember">
          <Checkbox v-model="rememberMe">Remember me</Checkbox>
        </div>

        <div class="form-row">
          <button class="btn-primary" type="submit" :disabled="loading" :aria-busy="loading">
            <span v-if="loading" class="spinner" aria-hidden="true"></span>
            <span v-if="!loading">Sign in</span>
            <span v-else>Signing in...</span>
          </button>
        </div>
      </form>

      <div v-if="error" class="error" role="alert">{{ error }}</div>
      <div v-if="retrySeconds" class="info">
        Too many attempts. Try again in {{ retrySeconds }} seconds.
      </div>
    </div>
  </div>
</template>

<script lang="ts">
import { defineComponent, ref, onMounted } from 'vue'
import PasswordInput from '@/components/form/PasswordInput.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import { apiService } from '@/services/api'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
// Vite static import for the logo so bundler resolves the asset reliably

export default defineComponent({
  components: { PasswordInput, Checkbox },
  name: 'LoginView',
  setup() {
    const username = ref<string>('')
    const password = ref<string>('')
    const rememberMe = ref<boolean>(false)
    const loading = ref<boolean>(false)
    const error = ref<string | null>(null)
    const retrySeconds = ref<number | null>(null)
    const router = useRouter()

    const auth = useAuthStore()
    // The logo is placed in `fe/public/logo.png` and served from the app root.
    const logoUrl = '/logo.png'

    async function onSubmit() {
      error.value = null
      retrySeconds.value = null
      loading.value = true

      // Fetch CSRF token
      const token = await apiService.fetchAntiforgeryToken()

      try {
        await auth.login(username.value, password.value, rememberMe.value, token ?? undefined)

        // On success, prefer query param redirect (survives reload); fallback to store, then home
        // Prefer explicit query param redirect (survives reload). If missing, try the
        // fallback stored in sessionStorage by the ApiService when it had to perform
        // a full-page redirect. Always sanitize the redirect target.
        const rawQueryRedirect =
          (router.currentRoute.value.query?.redirect as string | undefined) ?? undefined
        const { normalizeRedirect } = await import('@/utils/redirect')
        let queryRedirect = normalizeRedirect(rawQueryRedirect)

          const startupConfigChecked = ref(false)
        if (!queryRedirect || queryRedirect === '/') {
          // On mount, fetch startup config ONCE and redirect if authentication is not required
          onMounted(async () => {
            if (startupConfigChecked.value) return
            try {
              const sc = await apiService.getStartupConfig()
              // Accept both camelCase and PascalCase variants for compatibility
              const rawAuth =
                sc?.authenticationRequired ??
                (sc as unknown as Record<string, unknown>)?.AuthenticationRequired
              const authEnabled =
                typeof rawAuth === 'boolean'
                  ? rawAuth
                  : typeof rawAuth === 'string'
                    ? rawAuth.toLowerCase() === 'enabled' || rawAuth.toLowerCase() === 'true'
                    : false
              startupConfigChecked.value = true
              if (!authEnabled) {
                // Redirect to main app if authentication is not required
                await router.replace({ name: 'home' })
              }
            } catch (e) {
              // If config fetch fails, show error (optional: could retry or show fallback UI)
              error.value = 'Failed to load configuration.'
            }
          })
          try {
            const pending = sessionStorage.getItem('listenarr_pending_redirect') ?? undefined
            const normalizedPending = normalizeRedirect(pending)
            if (normalizedPending && normalizedPending !== '/') {
              queryRedirect = normalizedPending
            }
          } catch {}
        }

        const dest =
          queryRedirect && queryRedirect !== '/'
            ? { path: queryRedirect }
            : (auth.redirectTo ?? { name: 'home' })
        auth.redirectTo = null
        await router.push(dest)
      } catch (err) {
        interface LoginError {
          status?: number
          retryAfter?: number
          message?: string
        }
        const e = err as LoginError
        if (e?.status === 429) {
          retrySeconds.value = e.retryAfter ?? 0
          error.value = 'Too many attempts, please wait.'
        } else {
          // Generic message for other failures
          error.value = e?.message ?? 'Login failed'
        }
      } finally {
        loading.value = false
      }
    }

    return { username, password, rememberMe, onSubmit, error, retrySeconds, loading, logoUrl }
  },
})
</script>

<style scoped>
/* Core layout */
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  background-color: #1a1a1a;
}

.login-card {
  width: 100%;
  max-width: 420px;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  padding: 2rem;
  color: white;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
}

.login-logo {
  display: block;
  width: 100%;
  height: 96px;
  object-fit: contain;
  margin: 0 auto 1rem;
}

.title {
  text-align: center;
  margin: 0 0 1.5rem;
  color: var(--brand-500);
  font-size: 1.5rem;
  font-weight: 500;
}

.login-form {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.form-row {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.form-row:has(.btn-primary) {
  align-items: center;
}

.login-form .form-row .btn-primary {
  width: auto;
  min-width: 200px;
  padding: 1rem 2rem;
  font-size: 1rem;
  font-weight: 600;
  border-radius: 8px;
  text-align: center;
  transition: background-color 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease;
  transform: none;
}

.login-form .form-row .btn-primary:hover:not(:disabled),
.login-form .form-row .btn-primary:active:not(:disabled),
.login-form .form-row .btn-primary:disabled {
  transform: none;
}

.login-form .form-row .btn-primary span {
  width: 100%;
}

.form-label {
  margin-bottom: 0;
  color: #ccc;
  font-size: 0.9rem;
}

.form-input {
  padding: 0.75rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: white;
  font-size: 1rem;
}

.form-input:focus {
  outline: none;
  border-color: #2196f3;
  box-shadow: 0 0 0 3px rgba(33, 150, 243, 0.1);
}

.form-remember {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #ccc;
  font-size: 0.9rem;
}

/* Styled checkbox to match app theme */
.form-remember input[type='checkbox'] {
  width: 18px;
  height: 18px;
  margin: 0;
  padding: 0;
  appearance: auto; /* allow native look unless accent-color supported */
  accent-color: var(--brand-500); /* modern browsers: sets the checked color */
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  background-color: #1a1a1a;
  display: inline-block;
}

.form-remember input[type='checkbox']:focus {
  outline: 2px solid rgba(var(--brand-rgb), 0.18);
  outline-offset: 2px;
}

/* Fallback for older browsers: slightly enlarge the hit area via label */
.form-remember label {
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
}

/* Use centralized `.btn` / `.btn-primary` in `src/assets/buttons.css` for primary button visuals */

.error {
  color: #ff6b6b;
  margin-top: 1rem;
  text-align: center;
  font-size: 0.9rem;
}

.info {
  color: #98a8b9;
  margin-top: 0.5rem;
  text-align: center;
  font-size: 0.9rem;
}

.spinner {
  display: inline-block;
  width: 26px !important;
  height: 20px;
  border: 2px solid rgba(255, 255, 255, 0.15);
  border-top-color: white;
  border-radius: 50%;
  margin-right: 0.5rem;
  animation: spin 0.8s linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */

@media (max-width: 480px) {
  .login-card {
    padding: 1.5rem;
  }
}
</style>
