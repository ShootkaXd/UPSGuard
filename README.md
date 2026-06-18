# UPSGuard.NotifyHost

## Overview
UPSGuard.NotifyHost is a Windows-based service component designed to monitor UPS (Uninterruptible Power Supply) events and ensure reliable communication between the system and monitoring infrastructure.

The application performs:
- Health-check monitoring via HTTP endpoint
- Detection of service availability (heartbeat mechanism)
- User session notifications
- Integration with centralized monitoring systems (e.g., Zabbix)

It helps prevent data loss and system instability by reacting to power-related events and ensuring that critical services remain observable.

---

## Features
- Lightweight background service
- HTTP health-check endpoint (`/health`)
- Service watchdog monitoring
- Logging system for diagnostics
- Integration-ready for monitoring systems
- Handles scenarios like:
  - No response from service
  - System hibernation
  - Network unavailability

---

## Requirements
- Windows OS (Windows 10 / 11 or Server)
- .NET 8 / .NET 9 Runtime
- Administrator privileges (for service installation)

---

## Build

### Using .NET CLI
```bash
dotnet restore
dotnet build -c Release