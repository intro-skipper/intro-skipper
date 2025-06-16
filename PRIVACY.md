# Privacy Policy

## Overview
This document describes how this project collects, handles, and processes data.

## Data Collection
- No personal information is collected without explicit user consent.

## Data Storage
- User preferences and settings are stored locally on your device.

## Network Communications
- No tracking or analytics services are implemented.

## Third-Party Services
- Server hosting services collect no sensitive data.
- The data collected does not allow users to be identified.

<details>
<summary>Example log entry</summary>

```JSON
{
  "request": {
    "headers": {
      "Accept": [
        "application/json; q=1.0, application/xml; q=0.9, */*; q=0.8"
      ],
      "Accept-Encoding": [
        "gzip, deflate, br"
      ],
      "User-Agent": [
        "Jellyfin-Server/10.10.6"
      ]
    },
    "tls": {
      "cipher_suite": 4865,
      "resumed": false,
      "server_name": "manifest.intro-skipper.org",
      "version": 772
    },
    "host": "manifest.intro-skipper.org",
    "method": "GET",
    "proto": "HTTP/1.1",
    "remote_port": "39338",
    "uri": "/manifest.json"
  },
  "resp_headers": {
    "Alt-Svc": [
      "h3=\":443\"; ma=2592000"
    ],
    "Location": [
      "https://raw.githubusercontent.com/intro-skipper/manifest/refs/heads/main/10.10/manifest.json"
    ],
    "Server": [
      "Caddy"
    ]
  },
  "agent": "Jellyfin-Server/10.10.6",
  "bytes_read": 0,
  "duration": 0.000038705,
  "level": "info",
  "logger": "http.log.access.log0",
  "msg": "handled request",
  "request_url": "http://manifest.intro-skipper.org/manifest.json",
  "server": "AMS",
  "size": 0,
  "status": 302,
  "timestamp": "2025-06-14 18:50:09.863",
  "ts": 1749927009.8635788
}
```
</details>

## Changes to Privacy Policy
- Users will be notified of any privacy policy updates
- Changes will be documented in the project's changelog

## Contact
For privacy concerns or questions, please open an issue in the project repository or contact the team via [Discord](https://discord.gg/AYZ7RJ3BuA)

Last updated: 2025-06-14
