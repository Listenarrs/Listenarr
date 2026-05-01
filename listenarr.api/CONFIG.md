# Listenarr API Configuration

This document describes the configuration folder structure for the Listenarr API.

## Config Folder Structure

```
config/
├── appsettings/          # Application configuration files
│   ├── appsettings.json
│   └── appsettings.Development.json
├── cache/                # Image cache storage
│   └── images/
│       ├── temp/         # Temporary image cache
│       └── library/      # Permanent image cache
├── database/             # SQLite database files
````markdown
# Listenarr API Configuration

This document describes the configuration folder structure for the Listenarr API.

## Config Folder Structure

```
config/
├── appsettings/          # Application configuration files
│   ├── appsettings.json
│   └── appsettings.Development.json
├── cache/                # Image cache storage
│   └── images/
│       ├── temp/         # Temporary image cache
│       └── library/      # Permanent image cache
├── database/             # SQLite database files
│   └── listenarr.db
├── logs/                 # Application log files
│   └── listenarr-YYYY-MM-DD.log
└── temp/                 # Temporary download storage
        └── downloads/        # DDL download staging area
```

## Directory Purposes

### appsettings/
Contains application configuration files:
- `appsettings.json` - Base configuration
- `appsettings.Development.json` - Development-specific settings

### cache/images/
Stores cached book cover images:
- `temp/` - Temporary cache for newly downloaded images
- `library/` - Permanent storage for processed images

### database/
Contains the SQLite database file:
- `listenarr.db` - Main application database

### logs/
Contains application log files:
- Daily log files named `listenarr-YYYY-MM-DD.log`
- Automatically cleaned up by the application

### temp/downloads/
Temporary storage for DDL (Direct Download Link) files:
- Files download here first, then get processed and moved to final locations
- Automatically cleaned up after successful processing or after 24 hours

## Important Notes

- The `config/` folder contains all user-specific data and should be backed up
- Database files are SQLite-based and contain all application data
- Temp and cache directories are automatically managed by background services
- Log files are rotated daily and older logs may be cleaned up automatically

## ExternalRequests configuration (US domain preference)

The application supports an `ExternalRequests` configuration section that controls retry behavior for external scrapes (Amazon/Audible) and a named `us` HttpClient used for US-domain retries when necessary.

Example settings (see `appsettings.Development.json`):

```json
{
    "ExternalRequests": {
        "PreferUsDomain": true
    }
}
```

Notes:
- `PreferUsDomain`: when true the services will attempt a retry using a `.com` (US) domain if a localized/redirect/noise page is detected.

Auto-created configuration (convenience)

On first startup the application will create a minimal `config/appsettings/appsettings.json` if one does not already exist. This file contains a safe default logging configuration and is intended as a place for administrators to persist settings (logging, etc.). You can safely edit that file to change settings such as `Serilog:MinimumLevel:Default` or add other configuration sections.

If you prefer to prepare production config ahead of time, copy `config/appsettings/appsettings.Production.json.sample` to `config/appsettings/appsettings.json` and edit values (for example, set `Serilog:MinimumLevel:Default` to `Debug` to enable debug logs).
````