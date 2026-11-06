/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { onBeforeUnmount, ref, watch, type ComponentPublicInstance, type Ref } from 'vue'
import { columnGap, oneLineWidth, pixels } from '@/utils/measure'

// Where Repair's footer puts its legend, its warning and its skipped count, measured again when
// the modal resizes, the skipped count or the button changes, or the legend or the warning's
// longest text shows or goes.
// The legend stays in the footer, in sight however long the list is: between Cancel and the
// skipped count when the line has footerRoom for all of them, on its own line above them otherwise.
// Wrapping alone would put it under them, hence the measure. Centred on the dialog either way: on
// the buttons' line, moved towards the side with more footerRoom only as far as the buttons need.
export function useRepairFooterLayout(footerRefs: {
  footerRef: Ref<ComponentPublicInstance | null>
  cancelButtonRef: Ref<HTMLElement | null>
  legendRef: Ref<ComponentPublicInstance | null>
  // The warning's longest text, unseen
  problemsSizerRef: Ref<HTMLElement | null>
  skippedCountRef: Ref<HTMLElement | null>
}) {
  const isLegendAbove = ref(false)
  // How far the legend on the buttons' line stands from Cancel, to be centred on the dialog
  const legendShift = ref(0)
  // Step 2: the warning's place on its own line, above the buttons
  const isWarningAbove = ref(false)
  // The skipped count on two lines, the last resort: even Cancel, it and Repair don't fit one line
  const isCountWrapped = ref(false)
  let resizeObserver: ResizeObserver | null = null

  function placeFooterItems() {
    const footerElement = footerRefs.footerRef.value?.$el as HTMLElement | undefined
    const legendElement = footerRefs.legendRef.value?.$el as HTMLElement | undefined
    const cancelButton = footerRefs.cancelButtonRef.value
    const footerLeft = footerElement?.querySelector<HTMLElement>('.modal-footer-left')
    const footerActions = footerElement?.querySelector<HTMLElement>('.modal-footer-actions')
    if (!footerElement || !footerLeft || !footerActions || !cancelButton) return
    const footerStyle = getComputedStyle(footerElement)
    const footerRoom =
      footerElement.clientWidth - pixels(footerStyle.paddingLeft) - pixels(footerStyle.paddingRight)
    // The legend's width on one line, whatever line it is on now (none once repaired)
    const legendItems = legendElement ? (Array.from(legendElement.children) as HTMLElement[]) : []
    const legendWidth =
      legendItems.reduce((width, legendItem) => width + legendItem.offsetWidth, 0) +
      (legendElement ? columnGap(legendElement) : 0) * Math.max(legendItems.length - 1, 0)
    // The skipped count and Repair, the count on one line whatever it shows now
    const skippedCount = footerRefs.skippedCountRef.value
    const actionsWidth = skippedCount
      ? footerActions.offsetWidth - skippedCount.offsetWidth + oneLineWidth(skippedCount)
      : footerActions.offsetWidth
    const cancelAndActions = cancelButton.offsetWidth + columnGap(footerElement) + actionsWidth
    isLegendAbove.value =
      !!legendElement && cancelAndActions + columnGap(footerLeft) + legendWidth > footerRoom
    // Its left edge, from the footer's content box: centred, but between Cancel and the skipped
    // count
    const nearest = cancelButton.offsetWidth + columnGap(footerLeft)
    const farthest = footerRoom - actionsWidth - columnGap(footerElement) - legendWidth
    legendShift.value = isLegendAbove.value
      ? 0
      : Math.max(0, Math.min((footerRoom - legendWidth) / 2, farthest) - nearest)
    const problemsSizer = footerRefs.problemsSizerRef.value
    isWarningAbove.value =
      !!problemsSizer &&
      cancelAndActions + columnGap(footerElement) + problemsSizer.offsetWidth > footerRoom
    isCountWrapped.value = cancelAndActions > footerRoom
  }

  watch(
    [footerRefs.footerRef, footerRefs.legendRef, footerRefs.problemsSizerRef],
    ([footerComponent, legendComponent, problemsSizer]) => {
      resizeObserver?.disconnect()
      resizeObserver = null
      if (!footerComponent || typeof ResizeObserver !== 'function') return
      resizeObserver = new ResizeObserver(placeFooterItems)
      const footerElement = footerComponent.$el as HTMLElement
      resizeObserver.observe(footerElement)
      if (legendComponent) resizeObserver.observe(legendComponent.$el as HTMLElement)
      if (problemsSizer) resizeObserver.observe(problemsSizer)
      const footerActions = footerElement.querySelector('.modal-footer-actions')
      if (footerActions) resizeObserver.observe(footerActions)
    },
    { flush: 'post' },
  )
  onBeforeUnmount(() => resizeObserver?.disconnect())

  return { isLegendAbove, legendShift, isWarningAbove, isCountWrapped }
}
