import { DownloadProtocol } from "./DownloadProtocolConfig"

interface DownloadClientConfig {
    short: string, // Short handle that identify this client: Used for ressources
    label: string, // Label that can be shown in the UI
    port: number,
    url: string,
    requiresApiKey: boolean, // If false, basic auth with user/password is used
    protocols: DownloadProtocol[]
}

export enum DownloadClient {
  qbittorrent = "qbittorrent",
  transmission = "transmission",
  sabnzbd = "sabnzbd",
  nzbget = "nzbget",
  slskd = "slskd"
}

export const DownloadClientConfigs: Record<DownloadClient, DownloadClientConfig> = {
  [DownloadClient.nzbget]: {
    "short": DownloadClient.nzbget,
    "label": "NZBGet",
    "port": 6789,
    "url": 'nzbget.tld.com',
    "requiresApiKey": false,
    "protocols": [
      DownloadProtocol.Usenet
    ]
  },
  [DownloadClient.sabnzbd]: {
    "short": DownloadClient.sabnzbd,
    "label": "SABnzbd",
    "port": 8080,
    "url": 'sabnzbd.tld.com',
    "requiresApiKey": true,
    "protocols": [
      DownloadProtocol.Usenet
    ]
  },
  [DownloadClient.transmission]: {
    "short": DownloadClient.transmission,
    "label": "Transmission",
    "port": 9091,
    "url": 'transmission.tld.com',
    "requiresApiKey": false,
    "protocols": [
      DownloadProtocol.Torrent
    ]
  },
  [DownloadClient.qbittorrent]: {
    "short": DownloadClient.qbittorrent,
    "label": "qBittorrent",
    "port": 8080,
    "url": 'qbittorrent.tld.com',
    "requiresApiKey": false,
    "protocols": [
      DownloadProtocol.Torrent
    ]
  },
  [DownloadClient.slskd]: {
    "short": DownloadClient.slskd,
    "label": "Slskd",
    "port": 5030,
    "url": 'slskd.tld.com',
    "requiresApiKey": true,
    "protocols": [
      DownloadProtocol.Soulseek
    ]
  }
}

export const getLogo = (client: DownloadClient) => {
  return new URL('../assets/icons/clients/' + DownloadClientConfigs[client].short + '.svg', import.meta.url).href
}