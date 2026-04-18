interface DownloadProtocolConfig {
    short: string, // Short handle that identify this protocol: Used for ressources
    label: string, // Label that can be shown in the UI
    hasLogo: boolean
}

export enum DownloadProtocol {
  DirectDownload = "DirectDownload",
  Soulseek = "Soulseek",
  Torrent = "Torrent",
  Usenet = "Usenet",
  Unknown = "Unknown"
}

export const DownloadProtocolConfigs: Record<DownloadProtocol, DownloadProtocolConfig> = {
  [DownloadProtocol.DirectDownload]: {
    "short": "ddl",
    "label": "DDL",
    "hasLogo": false
  },
  [DownloadProtocol.Soulseek]: {
    "short": "soulseek",
    "label": "SLSK",
    "hasLogo": true
  },
  [DownloadProtocol.Torrent]: {
    "short": "torrent",
    "label": "Torrent",
    "hasLogo": true
  },
  [DownloadProtocol.Usenet]: {
    "short": "usenet",
    "label": "NZB",
    "hasLogo": true
  },
  [DownloadProtocol.Unknown]: {
    "short": "unknown",
    "label": "?",
    "hasLogo": false
  },
}

export const hasLogo = (protocol: DownloadProtocol) => {
  return DownloadProtocolConfigs[protocol].hasLogo
}

export const getLogo = (protocol: DownloadProtocol) => {
  return new URL('../assets/icons/indexers/' + DownloadProtocolConfigs[protocol].short + '.svg', import.meta.url).href
}