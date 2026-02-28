export const SECURITY_WARNING_BANNER_PREF_KEY = 'listenarr.hideNoAuthSecurityBanner'
export const SECURITY_WARNING_BANNER_PREF_EVENT = 'listenarr:security-warning-banner-preference-changed'

export function getSecurityWarningBannerHiddenPreference(): boolean {
  try {
    return window.localStorage.getItem(SECURITY_WARNING_BANNER_PREF_KEY) === 'true'
  } catch {
    return false
  }
}

export function setSecurityWarningBannerHiddenPreference(hidden: boolean): void {
  try {
    window.localStorage.setItem(SECURITY_WARNING_BANNER_PREF_KEY, hidden ? 'true' : 'false')
  } catch {
    // Ignore storage failures (private mode, disabled storage, quota, etc.)
  }

  try {
    window.dispatchEvent(
      new CustomEvent<boolean>(SECURITY_WARNING_BANNER_PREF_EVENT, {
        detail: hidden,
      }),
    )
  } catch {
    // Ignore dispatch failures
  }
}

