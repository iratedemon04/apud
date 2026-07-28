# Apud

Free, offline, Aleph-style **original cataloguing** software for MARC21. Windows desktop.

*Apud* — from the imprint line of early printed books (*apud* + publisher): the place where records are made.

- Two bases (BIB / AUT), template-driven record creation
- Position-aware fixed-field forms (LDR/006/007/008)
- Authority control with stored heading links
- Keyboard-first, Aleph-familiar workflow (F-keys, Ctrl+L validate & push)
- SQLite storage; `.mrk` / `.mrc` import & export
- Optional SFTP backup/publish to a Linux server
- No network dependence for cataloguing; single-machine by philosophy

## Layout

```
src/Marc.Core/     MARC domain engine (no UI, no DB, no network)
src/Apud.Data/     SQLite storage, import/export
src/Apud.Sync/     SSH/SFTP backup+publish (the only assembly with network access)
src/Apud.App/      WinForms application
tests/Apud.Tests/  xUnit test suite
docs/              PLAN.md (design), STATE.md (session-survival state), DEFERRED.md
```

## Build

.NET 8 SDK required.

```
dotnet build
dotnet test
.\publish.ps1    # self-contained build → publish/Apud/
```

Status: **Module 1 (scaffold)** — see `docs/STATE.md`.
