export interface RegistrationReview {
  operationId: string
  audiobookId: number | null
  action: string
  protocolVersion: number
  state: string
  error: string | null
  sourcePath: string
  destinationPath: string
  source: { status: string; length: number | null; sha256: string | null }
  destination: { status: string; length: number | null; sha256: string | null }
  registrations: { id: number; audiobookId: number; path: string | null }[]
  contentEvidence: string
  canResolve: boolean
  reviewToken: string
  resolutionNote: string | null
  resolvedBy: string | null
  resolvedAtUtc: string | null
}
