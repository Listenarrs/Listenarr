<template>
  <div class="calendar-view">
    <div class="page-header">
      <h1>
        <PhCalendar />
        Calendar
      </h1>
      <div class="calendar-actions">
        <button class="btn btn-secondary" @click="previousMonth" aria-label="Previous month">
          <PhCaretLeft :size="16" />
        </button>
        <button class="btn btn-secondary" @click="nextMonth" aria-label="Next month">
          <PhCaretRight :size="16" />
        </button>
        <button class="btn btn-secondary" @click="goToday">Today</button>
      </div>
    </div>

    <div class="calendar-filters">
      <div class="month-picker-wrapper">
        <button class="current-month" @click="showMonthPicker = !showMonthPicker">
          {{ currentMonthYear }}
          <PhCaretDown :size="16" />
        </button>
        <div v-if="showMonthPicker" class="month-picker-dropdown">
          <div class="picker-section">
            <label>Month</label>
            <select v-model="selectedMonth" class="picker-select">
              <option v-for="(month, index) in monthNames" :key="index" :value="index">
                {{ month }}
              </option>
            </select>
          </div>
          <div class="picker-section">
            <label>Year</label>
            <select v-model="selectedYear" class="picker-select">
              <option v-for="year in yearRange" :key="year" :value="year">
                {{ year }}
              </option>
            </select>
          </div>
          <div class="picker-actions">
            <button class="btn btn-secondary btn-sm" @click="cancelPicker">Cancel</button>
            <button class="btn btn-primary btn-sm" @click="applyPicker">Apply</button>
          </div>
        </div>
      </div>
      <div class="filter-tabs">
        <button
          v-for="mode in viewModes"
          :key="mode.value"
          :class="['tab', { active: viewMode === mode.value }]"
          @click="viewMode = mode.value as 'month' | 'week' | 'forecast' | 'day' | 'agenda'"
        >
          {{ mode.label }}
        </button>
      </div>
    </div>

    <div class="calendar-layout">
      <!-- MONTH VIEW -->
      <div v-if="viewMode === 'month'" class="calendar-panel">
        <div class="calendar-grid">
          <div class="calendar-header">
            <div v-for="day in weekDays" :key="day" class="day-header">{{ day }}</div>
          </div>
          <div class="calendar-body">
            <div
              v-for="date in calendarDates"
              :key="date.date"
              :class="['calendar-day', { 'other-month': !date.currentMonth, today: date.isToday }]"
            >
              <div class="day-number">{{ date.day }}</div>
              <div class="day-items">
                <div
                  v-for="item in date.items.slice(0, 3)"
                  :key="item.id"
                  class="calendar-item"
                  :title="`${item.title}${item.author ? ' - ' + item.author : ''}`"
                  role="button"
                  tabindex="0"
                  @click="navigateToDetail(item.id)"
                  @keydown.enter="navigateToDetail(item.id)"
                  @keydown.space.prevent="navigateToDetail(item.id)"
                >
                  <span class="item-title">{{ item.title }}</span>
                </div>
                <div v-if="date.items.length > 3" class="more-items">
                  +{{ date.items.length - 3 }} more
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- WEEK VIEW -->
      <div v-else-if="viewMode === 'week'" class="calendar-panel week-view">
        <div class="week-grid">
          <div class="week-header">
            <div v-for="day in weekDates" :key="day.date" class="week-day-header">
              <div class="week-day-name">{{ day.name }}</div>
              <div :class="['week-day-date', { today: day.isToday }]">{{ day.day }}</div>
            </div>
          </div>
          <div class="week-body">
            <div v-for="day in weekDates" :key="day.date" class="week-day-column">
              <div
                v-for="item in day.items"
                :key="item.id"
                class="week-item"
                role="button"
                tabindex="0"
                @click="navigateToDetail(item.id)"
                @keydown.enter="navigateToDetail(item.id)"
                @keydown.space.prevent="navigateToDetail(item.id)"
              >
                <div class="week-item-title">{{ item.title }}</div>
                <div v-if="item.author" class="week-item-author">{{ item.author }}</div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- FORECAST VIEW (30-day upcoming) -->
      <div v-else-if="viewMode === 'forecast'" class="calendar-panel forecast-view">
        <div class="forecast-list">
          <div v-if="forecastItems.length > 0" class="forecast-items">
            <div
              v-for="item in forecastItems"
              :key="item.id"
              class="forecast-item"
              role="button"
              tabindex="0"
              @click="navigateToDetail(item.id)"
              @keydown.enter="navigateToDetail(item.id)"
              @keydown.space.prevent="navigateToDetail(item.id)"
            >
              <div class="forecast-date-badge">
                <div class="forecast-day">{{ new Date(item.date).getUTCDate() }}</div>
                <div class="forecast-month">{{ monthNames[new Date(item.date).getUTCMonth()]?.slice(0, 3) }}</div>
              </div>
              <div class="forecast-content">
                <h3>{{ item.title }}</h3>
                <p v-if="item.author">{{ item.author }}</p>
              </div>
            </div>
          </div>
          <div v-else class="empty-message">
            <PhInfo :size="32" />
            <p>No upcoming releases in the next 30 days</p>
          </div>
        </div>
      </div>

      <!-- DAY VIEW -->
      <div v-else-if="viewMode === 'day'" class="calendar-panel day-view">
        <div class="day-view-content">
          <h2 class="day-title">{{ selectedDayFormatted }}</h2>
          <div class="day-items-list">
            <div v-if="selectedDayItems.length > 0" class="day-items-container">
              <div
                v-for="item in selectedDayItems"
                :key="item.id"
                class="day-list-item"
                role="button"
                tabindex="0"
                @click="navigateToDetail(item.id)"
                @keydown.enter="navigateToDetail(item.id)"
                @keydown.space.prevent="navigateToDetail(item.id)"
              >
                <div class="day-item-icon">
                  <PhCalendar :size="24" />
                </div>
                <div class="day-item-content">
                  <h4>{{ item.title }}</h4>
                  <p v-if="item.author">{{ item.author }}</p>
                </div>
              </div>
            </div>
            <div v-else class="empty-message">
              <PhInfo :size="32" />
              <p>No releases on this day</p>
            </div>
          </div>
        </div>
      </div>

      <!-- AGENDA VIEW (list view) -->
      <div v-else-if="viewMode === 'agenda'" class="calendar-panel agenda-view">
        <div class="agenda-list">
          <div v-if="allItemsSorted.length > 0" class="agenda-items">
            <div
              v-for="item in allItemsSorted"
              :key="item.id"
              class="agenda-item"
              role="button"
              tabindex="0"
              @click="navigateToDetail(item.id)"
              @keydown.enter="navigateToDetail(item.id)"
              @keydown.space.prevent="navigateToDetail(item.id)"
            >
              <div class="agenda-date">{{ formatDate(item.date) }}</div>
              <div class="agenda-content">
                <h3>{{ item.title }}</h3>
                <p v-if="item.author">{{ item.author }}</p>
              </div>
            </div>
          </div>
          <div v-else class="empty-message">
            <PhInfo :size="32" />
            <p>No items in library</p>
          </div>
        </div>
      </div>

      <div class="calendar-sidebar">
        <div class="sidebar-card">
          <div class="sidebar-header">
            <PhClock :size="20" />
            Upcoming Releases
          </div>
          <div class="upcoming-list">
            <div
              v-for="item in upcomingItems"
              :key="item.id"
              class="upcoming-item"
              role="button"
              tabindex="0"
              @click="navigateToDetail(item.id)"
              @keydown.enter="navigateToDetail(item.id)"
              @keydown.space.prevent="navigateToDetail(item.id)"
            >
              <div class="upcoming-date">{{ formatDate(item.date) }}</div>
              <div class="upcoming-info">
                <h4>{{ item.title }}</h4>
                <p v-if="item.author">{{ item.author }}</p>
              </div>
            </div>
            <div v-if="upcomingItems.length === 0" class="empty-message">
              <PhInfo :size="32" />
              <p>No upcoming releases</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { PhCalendar, PhCaretLeft, PhCaretRight, PhCaretDown, PhClock, PhInfo } from '@phosphor-icons/vue'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook } from '@/types'

interface CalendarItem {
  id: number
  title: string
  author?: string
  dateKey: string
  date: Date
}

interface CalendarDate {
  date: string
  day: number
  currentMonth: boolean
  isToday: boolean
  items: CalendarItem[]
}

const currentDate = ref(new Date())
const weekDays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const monthNames = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
const libraryStore = useLibraryStore()
const router = useRouter()
const viewMode = ref<'month' | 'week' | 'forecast' | 'day' | 'agenda'>('month')
const calendarStorageKey = 'listenarr.calendar.currentDate'
const viewModes = [
  { value: 'month', label: 'Month' },
  { value: 'week', label: 'Week' },
  { value: 'forecast', label: 'Forecast' },
  { value: 'day', label: 'Day' },
  { value: 'agenda', label: 'Agenda' },
]

const showMonthPicker = ref(false)
const selectedMonth = ref(new Date().getMonth())
const selectedYear = ref(new Date().getFullYear())

const yearRange = computed(() => {
  const currentYear = new Date().getFullYear()
  const years = []
  for (let i = currentYear - 50; i <= currentYear + 10; i++) {
    years.push(i)
  }
  return years
})

watch(currentDate, (newDate) => {
  selectedMonth.value = newDate.getMonth()
  selectedYear.value = newDate.getFullYear()
  if (typeof window !== 'undefined') {
    window.localStorage.setItem(calendarStorageKey, newDate.toISOString())
  }
})

watch(viewMode, (mode) => {
  if (mode === 'day') {
    selectedDay.value = new Date(currentDate.value)
  }
})

const applyPicker = () => {
  const newDate = new Date(selectedYear.value, selectedMonth.value, 1)
  currentDate.value = newDate
  selectedDay.value = new Date(newDate)
  showMonthPicker.value = false
}

const cancelPicker = () => {
  selectedMonth.value = currentDate.value.getMonth()
  selectedYear.value = currentDate.value.getFullYear()
  showMonthPicker.value = false
}

const closePicker = () => {
  showMonthPicker.value = false
}

onMounted(() => {
  if (typeof window !== 'undefined') {
    const storedDate = window.localStorage.getItem(calendarStorageKey)
    if (storedDate) {
      const parsed = new Date(storedDate)
      if (!Number.isNaN(parsed.getTime())) {
        currentDate.value = parsed
      }
    }
  }
  if (!libraryStore.audiobooks.length) {
    void libraryStore.fetchLibrary()
  }
  
  // Close picker on outside click
  if (typeof window !== 'undefined') {
    document.addEventListener('click', (e: Event) => {
      const target = e.target as HTMLElement
      const wrapper = document.querySelector('.month-picker-wrapper')
      if (wrapper && !wrapper.contains(target) && showMonthPicker.value) {
        closePicker()
      }
    })
  }
})

const toDateKeyLocal = (date: Date): string => {
  const y = date.getFullYear()
  const m = String(date.getMonth() + 1).padStart(2, '0')
  const d = String(date.getDate()).padStart(2, '0')
  return `${y}-${m}-${d}`
}

const extractPublishedDateKey = (book: Audiobook): string | null => {
  if (book.publishedDate && book.publishedDate.length >= 10) {
    return book.publishedDate.slice(0, 10)
  }
  return null
}

const calendarItems = computed<CalendarItem[]>(() => {
  return libraryStore.audiobooks
    .map((book) => {
      const key = extractPublishedDateKey(book)
      if (!key) return null
      return {
        id: book.id,
        title: book.title || 'Untitled',
        author: book.authors?.length ? book.authors.join(', ') : undefined,
        dateKey: key,
        date: new Date(`${key}T00:00:00Z`),
      } as CalendarItem
    })
    .filter((b): b is CalendarItem => b !== null)
})

const itemsByDate = computed(() => {
  const map = new Map<string, CalendarItem[]>()
  for (const item of calendarItems.value) {
    const list = map.get(item.dateKey) || []
    list.push(item)
    map.set(item.dateKey, list)
  }
  return map
})

const currentMonthYear = computed(() => {
  return currentDate.value.toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
})

const calendarDates = computed(() => {
  const year = currentDate.value.getFullYear()
  const month = currentDate.value.getMonth()
  const firstDay = new Date(year, month, 1)
  const startDate = new Date(firstDay)
  startDate.setDate(startDate.getDate() - firstDay.getDay())

  const dates: CalendarDate[] = []
  const currentDateObj = new Date(startDate)

  for (let i = 0; i < 42; i++) {
    const isCurrentMonth = currentDateObj.getMonth() === month
    const isToday = currentDateObj.toDateString() === new Date().toDateString()
    const key = toDateKeyLocal(currentDateObj)

    dates.push({
      date: key,
      day: currentDateObj.getDate(),
      currentMonth: isCurrentMonth,
      isToday,
      items: itemsByDate.value.get(key) || [],
    })

    currentDateObj.setDate(currentDateObj.getDate() + 1)
  }

  return dates
})

const upcomingItems = computed(() => {
  const todayKey = toDateKeyLocal(new Date())
  const today = new Date(`${todayKey}T00:00:00Z`)
  return calendarItems.value
    .filter((item) => item.date >= today)
    .sort((a, b) => a.date.getTime() - b.date.getTime())
    .slice(0, 20)
})

const previousMonth = () => {
  if (viewMode.value === 'day') {
    selectedDay.value.setDate(selectedDay.value.getDate() - 1)
    selectedDay.value = new Date(selectedDay.value)
  } else if (viewMode.value === 'week') {
    currentDate.value = new Date(currentDate.value.getTime() - 7 * 24 * 60 * 60 * 1000)
  } else {
    currentDate.value = new Date(currentDate.value.getFullYear(), currentDate.value.getMonth() - 1, 1)
  }
}

const nextMonth = () => {
  if (viewMode.value === 'day') {
    selectedDay.value.setDate(selectedDay.value.getDate() + 1)
    selectedDay.value = new Date(selectedDay.value)
  } else if (viewMode.value === 'week') {
    currentDate.value = new Date(currentDate.value.getTime() + 7 * 24 * 60 * 60 * 1000)
  } else {
    currentDate.value = new Date(currentDate.value.getFullYear(), currentDate.value.getMonth() + 1, 1)
  }
}

const goToday = () => {
  const today = new Date()
  currentDate.value = new Date(today.getFullYear(), today.getMonth(), 1)
  selectedDay.value = new Date(today)
  selectedMonth.value = today.getMonth()
  selectedYear.value = today.getFullYear()
}

const navigateToDetail = (id: number) => {
  void router.push(`/audiobooks/${id}`)
}


const formatDate = (date: Date): string => {
  return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' })
}

// Week view computed properties
const weekDates = computed(() => {
  const date = new Date(currentDate.value)
  const day = date.getDay()
  const diff = date.getDate() - day
  const firstDay = new Date(date.setDate(diff))

  const week = []
  for (let i = 0; i < 7; i++) {
    const d = new Date(firstDay)
    d.setDate(d.getDate() + i)
    const key = toDateKeyLocal(d)
    const dayName = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][d.getDay()]
    const isToday = d.toDateString() === new Date().toDateString()
    week.push({
      date: key,
      day: d.getDate(),
      name: dayName,
      isToday,
      items: itemsByDate.value.get(key) || [],
    })
  }
  return week
})

// Forecast view (30 days from selected month)
const forecastItems = computed(() => {
  const year = currentDate.value.getFullYear()
  const month = currentDate.value.getMonth()
  const items: CalendarItem[] = []
  const startDate = new Date(year, month, 1)
  
  for (let i = 0; i < 30; i++) {
    const date = new Date(startDate)
    date.setDate(date.getDate() + i)
    const key = toDateKeyLocal(date)
    const dayItems = itemsByDate.value.get(key) || []
    items.push(...dayItems)
  }
  
  return items.sort((a, b) => a.date.getTime() - b.date.getTime())
})

// Day view (selected day - default today)
const selectedDay = ref(new Date())
const selectedDayFormatted = computed(() => {
  return selectedDay.value.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' })
})

const selectedDayItems = computed(() => {
  const key = toDateKeyLocal(selectedDay.value)
  return itemsByDate.value.get(key) || []
})

// Agenda view (items in selected month sorted by date)
const allItemsSorted = computed(() => {
  const year = currentDate.value.getFullYear()
  const month = currentDate.value.getMonth()
  return [...calendarItems.value]
    .filter((item) => item.date.getFullYear() === year && item.date.getMonth() === month)
    .sort((a, b) => a.date.getTime() - b.date.getTime())
})
</script>

<style scoped>
.calendar-view {
  padding: 1em;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-header h1 svg {
  width: 32px;
  height: 32px;
}

.calendar-actions {
  display: flex;
  gap: 0.75rem;
}

.calendar-actions .btn.btn-secondary {
  min-width: unset;
  width: fit-content;
}

.calendar-filters {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1.5rem;
  border-bottom: 2px solid rgba(255, 255, 255, 0.1);
}

.filter-tabs {
  display: flex;
  gap: 0.5rem;
  overflow-x: auto;
  scrollbar-width: none;
}

.filter-tabs::-webkit-scrollbar {
  display: none;
}

.tab {
  background: none;
  border: none;
  color: #adb5bd;
  cursor: pointer;
  padding: 0.875rem 1.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  white-space: nowrap;
  position: relative;
}

.tab::after {
  content: '';
  position: absolute;
  bottom: -2px;
  left: 0;
  right: 0;
  height: 2px;
  background: transparent;
  transition: background 0.2s;
}

.tab:hover {
  background-color: rgba(255, 255, 255, 0.05);
  color: white;
}

.tab.active {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  font-weight: 500;
}

.tab.active::after {
  background: #4dabf7;
}

.current-month {
  color: white;
  font-weight: 600;
  font-size: 1.1rem;
  padding: 0.875rem 1rem;
  background: none;
  border: none;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.2s;
  border-radius: 6px;
}

.current-month:hover {
  background-color: rgba(255, 255, 255, 0.05);
}

.month-picker-wrapper {
  position: relative;
}

.month-picker-dropdown {
  position: absolute;
  top: 100%;
  left: 0;
  margin-top: 0.5rem;
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  padding: 1rem;
  min-width: 280px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  z-index: 1000;
  animation: slideDown 0.2s ease;
  max-height: 90vh;
  overflow-y: auto;
}

@keyframes slideDown {
  from {
    opacity: 0;
    transform: translateY(-10px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.picker-section {
  margin-bottom: 1rem;
}

.picker-section:last-of-type {
  margin-bottom: 1.25rem;
}

.picker-section label {
  display: block;
  color: #adb5bd;
  font-size: 0.85rem;
  margin-bottom: 0.5rem;
  font-weight: 500;
}

.picker-select {
  width: 100%;
  padding: 0.75rem;
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  color: #e6eef8;
  font-size: 1rem;
  cursor: pointer;
  transition: all 0.2s;
  -webkit-appearance: none;
  -moz-appearance: none;
  appearance: none;
}

.picker-select:hover {
  border-color: rgba(255, 255, 255, 0.12);
  background-color: #2a2a2a;
}

.picker-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.picker-select option {
  background: #2a2a2a;
  color: #e6eef8;
}

.picker-actions {
  display: flex;
  gap: 0.5rem;
  justify-content: flex-end;
}

.btn-sm {
  padding: 0.5rem 1rem;
  font-size: 0.875rem;
}

.btn-primary {
  background: #1e88e5;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  transition: all 0.2s;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
}

.btn-primary:hover {
  background: #1565c0;
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.4);
}

.btn-primary:active {
  transform: translateY(0);
}

.calendar-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 1.25rem;
}

.calendar-grid {
  overflow: hidden;
}

.calendar-header {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  background-color: rgba(0, 0, 0, 0.3);
}

.day-header {
  padding: 0.875rem;
  text-align: center;
  color: white;
  font-weight: 500;
  border-right: 1px solid rgba(255, 255, 255, 0.1);
  font-size: 0.9rem;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.day-header:last-child {
  border-right: none;
}

.calendar-body {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
}

.calendar-day {
  min-height: 100px;
  padding: 0.625rem;
  border-right: 1px solid rgba(255, 255, 255, 0.05);
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  background-color: #2a2a2a;
  transition: background-color 0.2s;
  min-width: 0;
  overflow: hidden;
  box-sizing: border-box;
}

.calendar-day:hover {
  background-color: #2f2f2f;
}

.calendar-day.other-month {
  background-color: rgba(0, 0, 0, 0.2);
  opacity: 0.5;
}

.calendar-day.today {
  background-color: rgba(77, 171, 247, 0.15);
  border: 1px solid rgba(77, 171, 247, 0.3);
}

.day-number {
  color: white;
  font-weight: 600;
  margin-bottom: 0.5rem;
  font-size: 0.95rem;
}

.calendar-day.other-month .day-number {
  color: #868e96;
}

.calendar-day.today .day-number {
  color: #4dabf7;
}

.day-items {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  width: 100%;
  min-width: 0;
  max-width: 100%;
  box-sizing: border-box;
  overflow: hidden;
}

.calendar-item {
  display: block;
  font-size: 0.75rem;
  color: white;
  background: #1e88e5;
  border-radius: 4px;
  padding: 0.25rem 0.5rem;
  cursor: pointer;
  transition: all 0.2s;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.2);
  max-width: 100%;
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
}

.calendar-item:hover {
  transform: translateY(-1px);
  box-shadow: 0 2px 6px rgba(30, 136, 229, 0.4);
  background: #1565c0;
}

.item-title {
  display: block;
  font-weight: 500;
  max-width: 100%;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  box-sizing: border-box;
}

.more-items {
  font-size: 0.7rem;
  color: #868e96;
  padding: 0.125rem 0.5rem;
  margin-top: 0.125rem;
}

.calendar-sidebar {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.sidebar-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  overflow: hidden;
}

.sidebar-header {
  padding: 1rem 1.25rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
  color: white;
  font-weight: 600;
  font-size: 1.05rem;
  display: flex;
  align-items: center;
  gap: 0.625rem;
  background-color: rgba(0, 0, 0, 0.2);
}

.upcoming-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1rem;
  max-height: calc(100vh - 320px);
  overflow-y: auto;
}

.upcoming-item {
  display: flex;
  gap: 0.875rem;
  padding: 1rem;
  background-color: rgba(0, 0, 0, 0.2);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  transition: all 0.2s;
  cursor: pointer;
  border-left: 3px solid #4dabf7;
}

.upcoming-item:hover {
  background-color: #2f2f2f;
  border-color: rgba(77, 171, 247, 0.3);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
  transform: translateX(2px);
}

.upcoming-date {
  font-weight: 600;
  min-width: 90px;
  font-size: 0.85rem;
}

.upcoming-info h4 {
  color: white;
  margin: 0 0 0.35rem 0;
  font-size: 0.95rem;
  font-weight: 500;
}

.upcoming-info p {
  color: #868e96;
  margin: 0;
  font-size: 0.85rem;
}

.empty-message {
  text-align: center;
  padding: 3rem 1rem;
  color: #868e96;
}

.empty-message svg {
  margin-bottom: 1rem;
  opacity: 0.5;
}

.empty-message p {
  margin: 0;
  font-size: 0.95rem;
}

.calendar-panel {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  overflow: hidden;
}

/* WEEK VIEW STYLES */
.week-view {
  padding: 1.25rem;
}

.week-grid {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.week-header {
  display: grid;
  grid-template-columns: repeat(7, minmax(0, 1fr));
  gap: 0.5rem;
  margin-bottom: 1rem;
}

.week-day-header {
  background: rgba(0, 0, 0, 0.2);
  padding: 1rem;
  border-radius: 6px;
  text-align: center;
  border: 1px solid rgba(255, 255, 255, 0.05);
}

.week-day-name {
  color: #868e96;
  font-size: 0.85rem;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin-bottom: 0.5rem;
}

.week-day-date {
  color: white;
  font-size: 1.5rem;
  font-weight: 600;
}

.week-day-date.today {
  color: #4dabf7;
}

.week-body {
  display: grid;
  grid-template-columns: repeat(7, minmax(0, 1fr));
  gap: 0.5rem;
}

.week-day-column {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  min-height: 200px;
  min-width: 0;
  overflow: hidden;
}

.week-item {
  width: 100%;
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
  overflow: hidden;
  background: #1e88e5;
  color: white;
  padding: 0.75rem;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
  box-shadow: 0 2px 6px rgba(30, 136, 229, 0.3);
  border-left: 3px solid #4dabf7;
}

.week-item:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.4);
}

.week-item-title {
  font-weight: 500;
  font-size: 0.9rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-bottom: 0.25rem;
}

.week-item-author {
  font-size: 0.8rem;
  opacity: 0.85;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* FORECAST VIEW STYLES */
.forecast-view {
  padding: 0;
}

.forecast-list {
  max-height: calc(100vh - 320px);
  overflow-y: auto;
}

.forecast-items {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1.25rem;
}

.forecast-item {
  display: flex;
  gap: 1rem;
  padding: 1rem;
  background-color: rgba(0, 0, 0, 0.2);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  transition: all 0.2s;
  cursor: pointer;
}

.forecast-item:hover {
  background-color: #2f2f2f;
  border-color: rgba(77, 171, 247, 0.3);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
  transform: translateX(4px);
}

.forecast-date-badge {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-width: 60px;
  background: #1e88e5;
  border-radius: 6px;
  padding: 0.75rem;
  color: white;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
}

.forecast-day {
  font-size: 1.5rem;
  font-weight: 600;
}

.forecast-month {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin-top: 0.25rem;
}

.forecast-content h3 {
  margin: 0 0 0.25rem 0;
  color: white;
  font-size: 1rem;
  font-weight: 500;
}

.forecast-content p {
  margin: 0;
  color: #868e96;
  font-size: 0.9rem;
}

/* DAY VIEW STYLES */
.day-view {
  padding: 0;
}

.day-view-content {
  padding: 1.25rem;
}

.day-title {
  color: white;
  font-size: 1.5rem;
  font-weight: 600;
  margin: 0 0 1.5rem 0;
}

.day-items-list {
  max-height: calc(100vh - 380px);
  overflow-y: auto;
}

.day-items-container {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.day-list-item {
  display: flex;
  gap: 1rem;
  padding: 1rem;
  background-color: rgba(0, 0, 0, 0.2);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  border-left: 4px solid #4dabf7;
  transition: all 0.2s;
}

.day-list-item:hover {
  background-color: #2f2f2f;
  border-color: rgba(77, 171, 247, 0.3);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.day-item-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  min-width: 48px;
  height: 48px;
  background: #1e88e5;
  border-radius: 6px;
  color: white;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
}

.day-item-content h4 {
  margin: 0 0 0.25rem 0;
  color: white;
  font-size: 1rem;
  font-weight: 500;
}

.day-item-content p {
  margin: 0;
  color: #868e96;
  font-size: 0.9rem;
}

/* AGENDA VIEW STYLES */
.agenda-view {
  padding: 0;
}

.agenda-list {
  max-height: calc(100vh - 320px);
  overflow-y: auto;
}

.agenda-items {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 1.25rem;
}

.agenda-item {
  display: flex;
  gap: 1rem;
  padding: 1rem;
  background-color: rgba(0, 0, 0, 0.2);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  border-left: 3px solid #4dabf7;
  transition: all 0.2s;
  cursor: pointer;
}

.agenda-item:hover {
background-color: #2f2f2f;
  border-color: rgba(77, 171, 247, 0.3);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
  transform: translateX(2px);
}

.agenda-date {
  font-weight: 600;
  min-width: 100px;
  font-size: 0.9rem;
}

.agenda-content h3 {
  margin: 0 0 0.25rem 0;
  color: white;
  font-size: 1rem;
  font-weight: 500;
}

.agenda-content p {
  margin: 0;
  color: #868e96;
  font-size: 0.9rem;
}

/* TABLET OPTIMIZATIONS (1100px and below) */
@media (max-width: 1100px) {
  .calendar-layout {
    grid-template-columns: 1fr;
  }
}

/* MOBILE OPTIMIZATIONS (768px and below) */
@media (max-width: 768px) {
  .calendar-view {
    padding: 0.75em;
  }

  .page-header {
    flex-direction: column;
    gap: 0.75rem;
    margin-bottom: 1rem;
    align-items: flex-start;
  }

  .page-header h1 {
    font-size: 1.5rem;
    margin-bottom: 0.5rem;
  }

  .calendar-actions {
    width: 100%;
    gap: 0.5rem;
  }

  .calendar-actions button {
    flex: 1;
    font-size: 0.85rem;
    padding: 0.5rem;
  }

  .calendar-filters {
    flex-direction: column;
    gap: 0.75rem;
    border-bottom: 1px solid rgba(255, 255, 255, 0.1);
    margin-bottom: 1rem;
  }

  .filter-tabs {
    width: 100%;
    flex-wrap: wrap;
    justify-content: flex-start;
  }

  .tab {
    padding: 0.625rem 0.875rem;
    font-size: 0.8rem;
    flex: 1;
    min-width: 60px;
    justify-content: center;
  }

  .current-month {
    width: 100%;
    padding: 0.75rem;
    font-size: 0.95rem;
    justify-content: space-between;
  }

  .month-picker-wrapper {
    position: relative;
    z-index: 1001;
  }

  .month-picker-dropdown {
    position: fixed;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    width: 90vw;
    max-width: 380px;
    border-radius: 8px;
    z-index: 1001;
    box-shadow: 0 20px 60px rgba(0, 0, 0, 0.5);
  }

  .month-picker-dropdown::before {
    content: '';
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background-color: rgba(0, 0, 0, 0.85);
    z-index: -1;
    border-radius: 0;
  }

  /* Calendar Grid Mobile */
  .calendar-grid {
    font-size: 0.85rem;
  }

  .calendar-header {
    grid-template-columns: repeat(7, 1fr);
  }

  .day-header {
    padding: 0.5rem 0.25rem;
    font-size: 0.7rem;
    text-transform: uppercase;
    border: none;
  }

  .calendar-body {
    grid-template-columns: repeat(7, 1fr);
  }

  .calendar-day {
    min-height: 70px;
    padding: 0.4rem;
    border: 1px solid rgba(255, 255, 255, 0.05);
  }

  .day-number {
    font-size: 0.8rem;
    margin-bottom: 0.25rem;
  }

  .calendar-item {
    font-size: 0.65rem;
    padding: 0.15rem 0.35rem;
    margin-bottom: 0.15rem;
  }

  .more-items {
    font-size: 0.6rem;
    padding: 0.1rem 0.35rem;
  }

  /* Week View Mobile */
  .week-view {
    padding: 0.75rem;
  }

  .week-header {
    gap: 0.25rem;
    margin-bottom: 0.75rem;
  }

  .week-day-header {
    padding: 0.75rem 0.5rem;
    border-radius: 4px;
  }

  .week-day-name {
    font-size: 0.75rem;
    margin-bottom: 0.35rem;
  }

  .week-day-date {
    font-size: 1.25rem;
  }

  .week-body {
    gap: 0.25rem;
  }

  .week-day-column {
    gap: 0.35rem;
    min-height: 150px;
  }

  .week-item {
    padding: 0.5rem;
    border-radius: 4px;
  }

  .week-item-title {
    font-size: 0.8rem;
    margin-bottom: 0.15rem;
  }

  .week-item-author {
    font-size: 0.7rem;
  }

  /* Forecast View Mobile */
  .forecast-view {
    padding: 0;
  }

  .forecast-list {
    max-height: calc(100vh - 300px);
  }

  .forecast-items {
    gap: 0.5rem;
    padding: 0.75rem;
  }

  .forecast-item {
    gap: 0.75rem;
    padding: 0.75rem;
  }

  .forecast-date-badge {
    min-width: 50px;
    padding: 0.5rem;
    border-radius: 4px;
  }

  .forecast-day {
    font-size: 1.25rem;
  }

  .forecast-month {
    font-size: 0.65rem;
    margin-top: 0.15rem;
  }

  .forecast-content h3 {
    font-size: 0.9rem;
    margin: 0 0 0.2rem 0;
  }

  .forecast-content p {
    font-size: 0.8rem;
  }

  /* Day View Mobile */
  .day-view {
    padding: 0;
  }

  .day-view-content {
    padding: 0.75rem;
  }

  .day-title {
    font-size: 1.25rem;
    margin: 0 0 1rem 0;
  }

  .day-items-list {
    max-height: calc(100vh - 300px);
  }

  .day-items-container {
    gap: 0.5rem;
  }

  .day-list-item {
    gap: 0.75rem;
    padding: 0.75rem;
    border-left: 3px solid #4dabf7;
  }

  .day-item-icon {
    min-width: 40px;
    height: 40px;
  }

  .day-item-content h4 {
    font-size: 0.9rem;
    margin: 0 0 0.2rem 0;
  }

  .day-item-content p {
    font-size: 0.8rem;
  }

  /* Agenda View Mobile */
  .agenda-view {
    padding: 0;
  }

  .agenda-list {
    max-height: calc(100vh - 300px);
  }

  .agenda-items {
    gap: 0.35rem;
    padding: 0.75rem;
  }

  .agenda-item {
    gap: 0.75rem;
    padding: 0.75rem;
    border-left: 3px solid #4dabf7
  }

  .agenda-date {
    min-width: 80px;
    font-size: 0.8rem;
  }

  .agenda-content h3 {
    font-size: 0.9rem;
    margin: 0 0 0.2rem 0;
  }

  .agenda-content p {
    font-size: 0.8rem;
  }

  /* Sidebar Mobile - Hide and move to bottom or collapsible */
  .calendar-sidebar {
    display: none;
  }

  .calendar-layout {
    grid-template-columns: 1fr;
  }
}

/* SMALL MOBILE (480px and below) */
@media (max-width: 480px) {
  .calendar-view {
    padding: 0.5em;
  }

  .page-header h1 {
    font-size: 1.25rem;
  }

  .calendar-actions button {
    padding: 0.4rem;
    font-size: 0.75rem;
  }

  .tab {
    padding: 0.5rem 0.625rem;
    font-size: 0.7rem;
    min-width: 50px;
  }

  .current-month {
    padding: 0.6rem;
    font-size: 0.9rem;
  }

  .calendar-day {
    min-height: 60px;
    padding: 0.3rem;
  }

  .day-header {
    padding: 0.4rem 0.2rem;
    font-size: 0.65rem;
  }

  .day-number {
    font-size: 0.75rem;
  }

  .calendar-item {
    font-size: 0.6rem;
    padding: 0.1rem 0.3rem;
  }

  .week-day-column {
    min-height: 120px;
  }

  .forecast-items {
    gap: 0.4rem;
  }

  .forecast-item {
    gap: 0.6rem;
  }

  .day-list-item {
    gap: 0.6rem;
    padding: 0.6rem;
  }

  .agenda-items {
    gap: 0.3rem;
  }

  .agenda-item {
    gap: 0.6rem;
    padding: 0.6rem;
  }

  .forecast-date-badge {
    min-width: 45px;
  }

  .day-item-icon {
    min-width: 36px;
    height: 36px;
  }
}
</style>
