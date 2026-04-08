---
layout: post
title:  "Running the Optimizely CMS 13 Alloy Site on macOS with Docker"
date:   2026-04-09 11:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-04-09-optimizely-alloy-on-macos-apple-silicon.png
   alt: "Running the Optimizely CMS 13 Alloy Site on macOS with Docker"
tags:
- optimizely
- episerver
- apple silicon
- m1
- arm
- docker
- sql server
- database
---

In my [first Apple Silicon post]({% post_url 2022-07-28-episerver-on-apple-silicon %}) and the [2025 revisit]({% post_url 2025-05-26-episerver-on-apple-silicon-2025 %}) I covered running an Optimizely Foundation site on an M1/ARM Mac. This time I wanted to try something smaller — the official Alloy template site for Optimizely CMS 13, which ships with a ready-made Docker Compose setup. On Windows it works out of the box, but on macOS with Apple Silicon a few adjustments are needed before everything runs smoothly.

All changes described below are available on [my GitHub repository](https://github.com/szolkowski/MyOptiAlloySite).

## What is the Alloy Site?

Alloy is the official demo and template site for Optimizely CMS 13 — the latest major version built on .NET 10. It's the go-to starting point when you want to spin up a new CMS 13 project or just experiment with the latest CMS features. Unlike previous CMS versions that were tightly coupled to Windows and IIS, CMS 13 runs cross-platform on .NET, which makes Docker-based development a natural fit. The repository includes a `docker-compose.yml` that brings up a SQL Server container and the web application together, so in theory you should be able to `docker compose up` and have a working site. In practice, the Docker configuration assumes a Windows/x64 host, and running it on macOS requires a handful of changes.

## Clean Up Stale LocalDB Files

If the site was previously run on Windows with LocalDB, the `App_Data/` directory may contain `.mdf` and `.ldf` files with internal references to Windows paths (e.g. `C:\Users\...\MSSQLLocalDB\empty.ldf`). The Linux SQL Server container can't use these, so they need to be removed before starting:

```bash
rm -f App_Data/*.mdf App_Data/*.ldf
```

Skip this step if you're working with a fresh clone.

## Upgrading the SQL Server Image

The original `db.dockerfile` uses `mcr.microsoft.com/mssql/server:2019-latest`. SQL Server 2019 doesn't have native ARM images, which means Docker Desktop would run it under x64 emulation — slow and unreliable. SQL Server 2025 ships with native ARM support, so switching to `2025-latest` gives us a proper native container.

I also removed the `USER root` line that was in the original Dockerfile. The `mssql` user is sufficient for what we need.

{% include code-modal.html
   id="2026-04-09-db-dockerfile"
   lang="dockerfile"
   file="post_assets/code-snippets/2026-04-09-db.dockerfile"
%}

## Fixing the Database Creation Script

This was the trickiest issue to track down. The original `create-db.sh` specified explicit `.mdf`/`.ldf` file paths pointing into a host-mounted directory:

```sql
CREATE DATABASE [${DB_NAME}]
  ON (NAME=[${DB_NAME}_data], FILENAME='/var/opt/mssql/host_data/...')
  LOG ON (NAME=[${DB_NAME}_log], FILENAME='/var/opt/mssql/host_data/...');
```

On macOS, Docker bind mounts don't grant the `mssql` container user write access to the host directory. SQL Server fails with `OS error 31 (A device attached to the system is not functioning)` when trying to create the database files there.

The fix is simple — remove the explicit file paths entirely and let SQL Server use its default internal data directory (`/var/opt/mssql/data/`), which the `mssql` user owns:

```bash
CREATE DATABASE [${DB_NAME}];
```

I also added the `-b` flag to `sqlcmd`. Without it, SQL errors are printed to stdout but `sqlcmd` still returns exit code 0, which means the retry loop would silently report success on failure.

{% include code-modal.html
   id="2026-04-09-create-db"
   lang="bash"
   file="post_assets/code-snippets/2026-04-09-create-db.sh"
%}

## Docker Compose Changes

The `docker-compose.yml` needed three separate adjustments.

### Read-Only App_Data Mount

Since we no longer write database files into the host-mounted directory (the `CREATE DATABASE` now uses SQL Server's internal path), the `App_Data` volume only needs to provide the `.episerverdata` import file. Making it read-only (`:ro`) makes this explicit:

```yaml
volumes:
  - ./App_Data:/var/opt/mssql/host_data/${DB_DIRECTORY}:ro
```

### Healthcheck for the Database Service

The original configuration used a simple `depends_on`:

```yaml
web:
  depends_on:
    - db
```

This only waits for the `db` container to *start* — not for SQL Server to actually be ready and the database to exist. The `web` container would attempt to connect too early and crash with `Login failed for user 'sa'`.

The fix is a healthcheck that verifies the application database actually exists before the web container starts:

```yaml
db:
  healthcheck:
    test: /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$$SA_PASSWORD" -Q "SELECT 1 FROM sys.databases WHERE name = '$$DB_NAME'" -C -h -1 | grep -q 1
    interval: 5s
    timeout: 5s
    retries: 30
    start_period: 15s
web:
  depends_on:
    db:
      condition: service_healthy
```

Note the `$$` syntax — Docker Compose requires double dollar signs to reference environment variables inside healthcheck commands (single `$` would be interpreted by the compose file parser).

### Port Change: 5000 to 5100

Starting with macOS Monterey, Apple uses port 5000 for AirPlay Receiver. If you try to bind to port 5000, it either fails silently or conflicts with the system service. Changing the host port mapping to 5100 avoids this:

```yaml
ports:
  - 5100:80
```

Here is the complete `docker-compose.yml` with all changes applied:

{% include code-modal.html
   id="2026-04-09-docker-compose"
   lang="yaml"
   file="post_assets/code-snippets/2026-04-09-docker-compose.yml"
%}

## Web Dockerfile Port Update

To match the port change in `docker-compose.yml`, the `web.dockerfile` also needs its `EXPOSE` directive updated from `5000`/`5001` to `5100`/`5101`:

{% include code-modal.html
   id="2026-04-09-web-dockerfile"
   lang="dockerfile"
   file="post_assets/code-snippets/2026-04-09-web.dockerfile"
%}

## Running It

With all changes in place, start everything up:

```bash
docker compose up
```

The first run will take a moment — Docker needs to build the images, restore NuGet packages, and initialize the database. Once you see the healthcheck passing and the web container starting, open [http://localhost:5100](http://localhost:5100) in your browser.

## Summary

Getting the Optimizely CMS 13 Alloy site running on macOS with Docker required six changes:

1. **Delete stale LocalDB files** — remove `.mdf`/`.ldf` files from `App_Data/` if they exist from a prior Windows setup
2. **Upgrade SQL Server** — switch from `2019-latest` to `2025-latest` for native ARM support
3. **Simplify the DB creation script** — remove explicit file paths and add the `-b` flag for proper error handling
4. **Read-only App_Data mount** — the volume only provides the import file, not database storage
5. **Add a healthcheck** — ensure the database is ready before the web container starts
6. **Change ports** — move from 5000 to 5100 to avoid the macOS AirPlay Receiver conflict

All changes are on [my GitHub repository](https://github.com/szolkowski/MyOptiAlloySite) if you want to see the full code. Let me know in the comments if you run into any other issues!
