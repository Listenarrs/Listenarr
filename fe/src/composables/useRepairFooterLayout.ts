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
import { columnGap, oneLineWidth, px } from '@/utils/measure'

// Where Repair's footer puts its legend, its warning and its skipped count, measured again when
// the modal resizes, the skipped count or the button changes, or the legend or the warning's
// longest text shows or goes.
// The legend stays in the footer, in sight however long the list is: between Cancel and the
// skipped count when the line has room for all of them, on its own line above them otherwise.
// Wrapping alone would put it under them, hence the measure. Centred on the dialog either way: on
// the buttons' line, moved towards the side with more room only as far as the buttons need.
export function useRepairFooterLayout(elements: {
  footer: Ref<ComponentPublicInstance | null>
  cancel: Ref<HTMLElement | null>
  legend: Ref<ComponentPublicInstance | null>
  // The warning's longest text, unseen
  problemsSizer: Ref<HTMLElement | null>
  count: Ref<HTMLElement | null>
}) {
  const legendAbove = ref(false)
  // How far the legend on the buttons' line stands from Cancel, to be centred on the dialog
  const legendShift = ref(0)
  // Step 2: the warning's place on its own line, above the buttons
  const problemsAbove = ref(false)
  // The skipped count on two lines, the last resort: even Cancel, it and Repair don't fit one line
  const countWraps = ref(false)
  let observer: ResizeObserver | null = null

  function place() {
    const footer = elements.footer.value?.$el as HTMLElement | undefined
    const legend = elements.legend.value?.$el as HTMLElement | undefined
    const cancel = elements.cancel.value
    const left = footer?.querySelector<HTMLElement>('.modal-footer-left')
    const actions = footer?.querySelector<HTMLElement>('.modal-footer-actions')
    if (!footer || !left || !actions || !cancel) return
    const footerStyle = getComputedStyle(footer)
    const room = footer.clientWidth - px(footerStyle.paddingLeft) - px(footerStyle.paddingRight)
    // The legend's width on one line, whatever line it is on now (none once repaired)
    const entries = legend ? (Array.from(legend.children) as HTMLElement[]) : []
    const legendWidth =
      entries.reduce((width, entry) => width + entry.offsetWidth, 0) +
      (legend ? columnGap(legend) : 0) * Math.max(entries.length - 1, 0)
    // The skipped count and Repair, the count on one line whatever it shows now
    const count = elements.count.value
    const actionsWidth = count
      ? actions.offsetWidth - count.offsetWidth + oneLineWidth(count)
      : actions.offsetWidth
    const cancelAndActions = cancel.offsetWidth + columnGap(footer) + actionsWidth
    legendAbove.value = !!legend && cancelAndActions + columnGap(left) + legendWidth > room
    // Its left edge, from the footer's content box: centred, but between Cancel and the skipped
    // count
    const nearest = cancel.offsetWidth + columnGap(left)
    const farthest = room - actionsWidth - columnGap(footer) - legendWidth
    legendShift.value = legendAbove.value
      ? 0
      : Math.max(0, Math.min((room - legendWidth) / 2, farthest) - nearest)
    const sizer = elements.problemsSizer.value
    problemsAbove.value = !!sizer && cancelAndActions + columnGap(footer) + sizer.offsetWidth > room
    countWraps.value = cancelAndActions > room
  }

  watch(
    [elements.footer, elements.legend, elements.problemsSizer],
    ([footer, legend, sizer]) => {
      observer?.disconnect()
      observer = null
      if (!footer || typeof ResizeObserver !== 'function') return
      observer = new ResizeObserver(place)
      const footerElement = footer.$el as HTMLElement
      observer.observe(footerElement)
      if (legend) observer.observe(legend.$el as HTMLElement)
      if (sizer) observer.observe(sizer)
      const actions = footerElement.querySelector('.modal-footer-actions')
      if (actions) observer.observe(actions)
    },
    { flush: 'post' },
  )
  onBeforeUnmount(() => observer?.disconnect())

  return { legendAbove, legendShift, problemsAbove, countWraps }
}
