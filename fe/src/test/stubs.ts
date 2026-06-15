import type { Component } from 'vue'

export const modalStubs: Record<string, Component> = {
  Modal: {
    emits: ['close'],
    props: ['visible', 'title', 'showClose', 'size'],
    template:
      '<div v-if="visible" v-bind="$attrs" role="dialog" aria-modal="true" aria-labelledby="modal-title"><header id="modal-title"><slot name="header" /></header><div class="modal-body"><slot /></div><footer class="modal-footer"><slot name="footer" /></footer></div>',
    mounted() {
      this._onKey = (e: KeyboardEvent) => {
        if (e.key === 'Escape') this.$emit?.('close')
      }
      document.addEventListener('keydown', this._onKey)
    },
    unmounted() {
      if (this._onKey) document.removeEventListener('keydown', this._onKey)
    },
  },
  BaseModal: {
    emits: ['close'],
    props: ['visible', 'title', 'showClose', 'size'],
    template:
      '<div v-if="visible" v-bind="$attrs" role="dialog" aria-modal="true" aria-labelledby="modal-title"><header id="modal-title"><slot name="header" /></header><div class="modal-body"><slot /></div><footer class="modal-footer"><slot name="footer" /></footer></div>',
    mounted() {
      this._onKey = (e: KeyboardEvent) => {
        if (e.key === 'Escape') this.$emit?.('close')
      }
      document.addEventListener('keydown', this._onKey)
    },
    unmounted() {
      if (this._onKey) document.removeEventListener('keydown', this._onKey)
    },
  },
  ModalHeader: {
    props: ['title', 'icon', 'iconLabel'],
    emits: ['close'],
    template:
      '<div class="modal-header"><component v-if="icon" :is="icon" /><h2 v-if="title">{{ title }}</h2><button @click="$emit(\'close\')" class="close-btn">x</button><slot /></div>',
  },
  ModalBody: {
    template: '<div class="modal-body"><slot /></div>',
  },
  ModalFooter: {
    template: '<div class="modal-footer"><slot /></div>',
  },
  ModalForm: {
    template: '<form data-modal-body><slot /></form>',
  },
  ModalActions: {
    template: '<div class="modal-actions"><slot /></div>',
  },
  ModalSpinnerOverlay: {
    template: '<div class="modal-spinner-overlay"><slot /></div>',
  },
}

export const baseStubs: Record<string, Component> = {
  BrandLogo: {
    template: '<div class="brand-logo-stub" />',
  },
  LoadingState: {
    props: ['message', 'size'],
    template:
      '<div class="loading-state"><div class="spinner" /><p v-if="message" class="loading-message">{{ message }}</p></div>',
  },
  PhSpinner: {
    props: ['size'],
    template: '<i class="ph-spinner" aria-hidden="true"></i>',
  },
}

export const appStubs: Record<string, Component> = {
  ...baseStubs,
  ...modalStubs,
}
