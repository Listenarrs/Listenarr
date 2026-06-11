import type { Router } from 'vue-router'

let routerInstance: Router | null = null

export function setRouter(router: Router) {
  routerInstance = router
}

export function getRouter() {
  if (!routerInstance) {
    throw new Error('Router not initialized - call createAppRouter() first')
  }

  return routerInstance
}
