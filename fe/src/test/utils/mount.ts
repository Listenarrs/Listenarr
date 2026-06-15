import { mount, type ComponentMountingOptions } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter, type RouteRecordRaw } from 'vue-router'
import type { Component } from 'vue'

type MountOptions = ComponentMountingOptions<Component>

const defaultRoutes: RouteRecordRaw[] = [
  { path: '/', name: 'home', component: { template: '<div />' } },
]

type RouterOptions = {
  initialPath?: string
  routes?: RouteRecordRaw[]
}

export function createTestRouter({
  initialPath = '/',
  routes = defaultRoutes,
}: RouterOptions = {}) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes,
  })

  return {
    router,
    ready: async () => {
      await router.push(initialPath)
      await router.isReady().catch(() => {})
      return router
    },
  }
}

export function createTestPinia() {
  const pinia = createPinia()
  setActivePinia(pinia)
  return pinia
}

export function mountWithPinia(component: Component, options: MountOptions = {}) {
  const pinia = createTestPinia()

  return mount(component, {
    ...options,
    global: {
      ...options.global,
      plugins: [...(options.global?.plugins ?? []), pinia],
    },
  })
}

export async function mountWithRouter(
  component: Component,
  options: MountOptions = {},
  routerOptions: RouterOptions = {},
) {
  const { router, ready } = createTestRouter(routerOptions)
  await ready()

  return mount(component, {
    ...options,
    global: {
      ...options.global,
      plugins: [...(options.global?.plugins ?? []), router],
    },
  })
}

export async function mountWithPiniaAndRouter(
  component: Component,
  options: MountOptions = {},
  routerOptions: RouterOptions = {},
) {
  const { router, ready } = createTestRouter(routerOptions)
  const pinia = createTestPinia()
  await ready()

  return mount(component, {
    ...options,
    global: {
      ...options.global,
      plugins: [...(options.global?.plugins ?? []), pinia, router],
    },
  })
}

export function withStubs(
  options: MountOptions,
  stubs: NonNullable<MountOptions['global']>['stubs'],
) {
  return {
    ...options,
    global: {
      ...options.global,
      stubs: {
        ...(options.global?.stubs ?? {}),
        ...stubs,
      },
    },
  } satisfies MountOptions
}
