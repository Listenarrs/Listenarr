# Security Policy

## Supported Versions

We release patches for security vulnerabilities for the following versions:

| Version | Supported          |
| ------- | ------------------ |
| 0.2.x   | :white_check_mark: |
| < 0.2.0 | :x:                |

## Reporting a Vulnerability

We take the security of Listenarr seriously. If you believe you have found a security vulnerability, please report it to us as described below.

### Please Do NOT:
- **Do NOT** open a public GitHub issue for security vulnerabilities
- **Do NOT** disclose the vulnerability publicly until it has been addressed

### Please DO:
1. **Use GitHub Private Vulnerability Reporting** — click **"Report a vulnerability"** on the [Security tab](https://github.com/Listenarrs/Listenarr/security/advisories/new) of this repository. This keeps the report private and tracked in one place.
2. **Include** as much information as possible:
   - Type of vulnerability
   - Step-by-step instructions to reproduce
   - Potential impact
   - Suggested fix (if available)
3. **Allow** us reasonable time to address the issue before public disclosure

### What to Expect:
- **Acknowledgment**: We will acknowledge receipt of your vulnerability report within 48 hours
- **Communication**: We will keep you informed of our progress
- **Timeline**: We aim to address critical vulnerabilities within 7 days
- **Credit**: We will credit you in the release notes (unless you prefer to remain anonymous)

## Security Best Practices

### Deployment Security

1. **Authentication**
   - Enable authentication in production environments
   - Use strong passwords for admin accounts
   - Change default credentials immediately after setup

2. **Network Security**
   - Deploy behind a reverse proxy (Nginx, Traefik, Caddy)
   - Use HTTPS/TLS for all external connections
   - Restrict access using firewall rules or VPN

3. **API Keys & Credentials**
   - Store download client credentials securely
   - Rotate API keys regularly
   - Never commit secrets to version control

4. **File System Access**
   - Run with minimal required permissions
   - Use read-only mounts where possible
   - Validate all file paths to prevent directory traversal

5. **Docker Security**
   - Use non-root user in containers
   - Keep base images updated
   - Scan images for vulnerabilities regularly

### Configuration Security

1. **Database**
   - SQLite database stored in `/app/config/database/` (Docker) or `.env/development/config/database/` (local development)
   - Ensure proper file permissions on database files
   - Regular backups recommended

2. **Image Cache**
   - Cached images stored in `/app/config/cache/images/` (Docker) or `.env/development/config/cache/images/` (local development)
   - Automatically cleaned up by background service
   - No user data stored in cache

3. **Logs**
   - Logs may contain sensitive information
   - Stored in `/app/config/logs/` (Docker) or `.env/development/config/logs/` (local development)
   - Implement log rotation to prevent disk space issues
   - Review log access permissions

### Known Security Considerations

1. **Download Client Integration**
   - Listenarr connects to external download clients (qBittorrent, Transmission, SABnzbd, NZBGet)
   - Ensure download clients are secured and not publicly exposed
   - Use HTTPS when possible for download client connections

2. **Metadata APIs**
   - Audible/Audnexus API used for metadata enrichment
   - Amazon URLs processed for ASIN extraction
   - No user credentials sent to external services

3. **Search Providers**
   - Multiple torrent/NZB APIs supported
   - API keys for indexers stored in configuration
   - Ensure indexer API keys are kept confidential

4. **WebHooks/Notifications**
   - Discord webhooks supported for notifications
   - Webhook URLs may contain sensitive tokens
   - Configure webhook URLs carefully to prevent leaks

## Security Updates

Security updates will be released as patch versions (e.g., 0.2.19 → 0.2.20) and documented in the [CHANGELOG.md](CHANGELOG.md).

Subscribe to GitHub releases to receive notifications about security updates:
- Watch this repository → Custom → Releases

## Security Audit History

| Date       | Version | Type     | Description                          | Status   |
|------------|---------|----------|--------------------------------------|----------|
| 2025-11-05 | 0.2.19  | Review   | Pre-release security review          | Complete |

---

## Additional Resources

- [CONTRIBUTING.md](CONTRIBUTING.md) - Contribution guidelines
- [CHANGELOG.md](CHANGELOG.md) - Release history and security fixes
- [README.md](README.md) - Installation and configuration guide

## Disclaimer

Listenarr is provided "as is" without warranty of any kind. Users are responsible for:
- Securing their deployment environment
- Complying with local laws regarding audiobook downloads
- Protecting their own credentials and API keys
- Regular backups of configuration and database

---

*Last Updated: November 2025*
