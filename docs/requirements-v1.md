
# RemoteFlow - Project Requirements (v1)

## Overview

**RemoteFlow** is an open-source, cross-platform desktop application built with **.NET 10** and **Avalonia UI**.

Its purpose is to provide a modern workspace for managing remote systems through SSH, SFTP and Remote Desktop (RDP).

Unlike traditional connection managers, RemoteFlow provides an integrated **tabbed terminal workspace** together with an integrated SFTP browser while relying on mature terminal technologies instead of implementing a terminal emulator from scratch.

---

# Vision

Create a lightweight, fast and maintainable remote workspace that allows developers and system administrators to organize and access remote systems from a single application.

The application should feel closer to an IDE than a launcher.

---

# Core Principles

- Open Source (GitHub)
- .NET 10
- Avalonia UI
- Cross-platform
- SQLite
- Clean Architecture
- MVVM
- Modular services
- Secure credential storage
- No telemetry
- No cloud dependency
- No licensing
- No user accounts

---

# Version 1 Scope

## SSH

- Store SSH profiles
- Multiple simultaneous SSH sessions
- SSH keys
- Password authentication
- Username / Host / Port

## Embedded Terminal

Provide an integrated terminal workspace with:

- Multiple tabs
- Open / Close / Reconnect
- ANSI + UTF-8 support
- Copy & Paste
- Resize support
- Correct keyboard shortcuts
- Compatibility with vim, nano, tmux and htop
- Optional "Open in System Terminal"

Use an existing mature terminal component. Do not build a terminal emulator.

## SFTP

- Browse directories
- Upload / Download
- Rename / Delete
- Create folders
- Permissions (where supported)

### Remote Editing

1. Download temporary file
2. Open with default editor
3. Detect save
4. Upload automatically
5. Warn on remote conflicts

## Remote Desktop

Launch the platform-native RDP client.

No embedded RDP in v1.

---

# Connection Management

Each connection supports:

- Name
- Host
- Port
- Protocol
- Username
- Authentication
- Notes
- Folder
- Tags
- Favorite

Operations:

- Create
- Edit
- Delete
- Duplicate
- Search
- Filter

---

# User Interface

- Navigation sidebar
- Connection explorer
- Search
- Favorites
- Folder hierarchy
- Connection details
- Terminal workspace
- SFTP workspace

Terminal tabs should support environment color coding (Production, Staging, Development).

---

# Persistence

SQLite + Entity Framework Core.

Store:

- Connections
- Settings
- Favorites
- Folders
- Tags
- Recent connections

---

# Credentials

Use secure platform credential storage where possible.

Never store credentials in plain text.

---

# Backup & Restore

Support:

- Export
- Import
- Merge
- Replace

Backup includes:

- Connections
- Settings
- Favorites
- Folders
- Tags

Optional encrypted credential export.

---

# Recommended Architecture

- Avalonia UI
- MVVM
- Dependency Injection
- Service Layer
- Repository Pattern
- EF Core
- SQLite

Platform abstractions:

- Terminal Service
- SSH Service
- SFTP Service
- RDP Launcher
- Credential Provider

---

# Version 1 Non-Goals

- Embedded RDP
- Cloud sync
- Team collaboration
- Docker/Kubernetes integration
- SSH tunnel UI
- Session recording
- VNC
- Telemetry

---

# Suggested Milestones

1. Foundation
2. Connection Management
3. Embedded Terminal
4. SSH
5. SFTP
6. Remote Desktop
7. Backup & Restore
8. Packaging & Release

---

# First Task for the Coding Agent

Before implementation:

1. Analyze requirements.
2. Identify ambiguities.
3. Propose architecture.
4. Recommend terminal component.
5. Design domain model.
6. Design database schema.
7. Generate GitHub milestones.
8. Generate GitHub issues grouped by milestone.
9. Define dependencies.
10. Wait for approval before coding.

---

# Technology Stack

- .NET 10
- C#
- Avalonia UI
- Entity Framework Core
- SQLite
- OpenSSH
- SSH.NET
- xUnit
- GitHub Actions

---

# Project Mission

**RemoteFlow** is a lightweight, open-source, cross-platform remote workspace combining SSH, SFTP and Remote Desktop management with an integrated tabbed terminal experience.
