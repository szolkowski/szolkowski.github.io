---
layout: post
title:  "Running full Optimizely development setup on M1 (ARM) based machine"
date:   2025-05-26 11:29:19 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2025-05-26-cms-home-page.png
   alt: Running full Optimizely development setup on M1 (ARM) based machine"
tags:
- episerver
- optimizely
- m1
- arm
- apple silicon
- ms sql server
- sql server
---

A few years ago [I posted]({% post_url 2022-07-28-episerver-on-apple-silicon %}) post about how to run Optimizely/Episerver on M1 (ARM) based machine, so I think it is high time to revisit and check what did change.

## How it was in 2022

In 2022 when I wrote down the original post the biggest limitation was that we had to use [azure-sql-edge image](https://hub.docker.com/r/microsoft/azure-sql-edge) instead of image for MS SQL Server. Only `azure-sql-edge` had support for running on `ARM` architecture and MS SQL Server image at that time was hardly running and was not usable.

Why it was a problem? Azure-SQL isn't MS SQL Server and is missing some components like CLI and isn't guaranteed to work in exactly the same manner as MS SQL server, which limits our possibility of using it. It was still possible to develop an Optimizely solution that way, but from time to time it might require some support from another environment that was running SQL Server, for example, to make the initial DB setup.

## State for today (2025)

As for 2025 [azure-sql-edge image](https://hub.docker.com/r/microsoft/azure-sql-edge) is still the only image that natively works on `ARM`, however let's see how it will work after 3 years of development on both Microsoft and Apple end.

## Prerequisites

- Git
- Docker
- Azure Data Studio
- Dotnet
- Node JS

## Let's try it in 2025

At the date of writing this post `SQL Server 2025` is still marked as `Preview`, so we will be working on a stable `SQL Server 2022` tag for [SQL Server docker image](https://mcr.microsoft.com/en-us/artifact/mar/mssql/server/about).

First, let's pull the image:

```bash
docker pull mcr.microsoft.com/mssql/server:2022-latest
```

Then let's try to run it using a slightly modified command compared to `azure-sql-edge`:

```bash
docker run -d --name sql_server_optimizely_foundation -h localhost -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Episerver123!" \
 -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

In the docker engine we should see now a new container running with a warning label for the wrong CPU architecture:

![Docker Container Warning Message](/assets/img/2025-05-26-docker-container-warning-message.png)

So far so good, however how it will work with the Optimizely CMS + Commerce combo? In 2022 performance and stability were horrible.

## Testing on Optimizely CMS + Commerce foundation site

If you don't have a foundation repository, yet please pull it.  I recommend using this code version from [my Foundation fork](https://github.com/szolkowski/Foundation/tree/e436ac689be335f8ce506cc22d349371de72aba9) as I have DB's backpack files there.

Lets follow steps from [my previous post]({% post_url 2022-07-28-episerver-on-apple-silicon %}):

> After DB is up and running (it will take a moment after the image is up to start DB) connect to DB using `Azure Data studio` using the following credentials:
>
> - server: localhost
> - login: SA
> - password: Episerver123!
>
> Then using Data-tier Application Wizard Create databases from following .bacpac files (Import Bacpac):
>
> - FoundationAppleSiliconTest.Cms.bacpac
> - FoundationAppleSiliconTest.Commerce.bacpac
>
> Files are available on my Foundation fork linked above in `db_backups` directory.
>
> ![My helpful screenshot](/assets/img/2022-07-28-episerver-on-apple-silicon-db-import-1.png)
>
> After import is done execute following SQL query on database which will create login and users used by our Foundation site:
>
> ```sql
> CREATE LOGIN FoundationAppleSiliconTestOurCmsUser WITH PASSWORD = 'bNaK31CgBWPBT6SMF4Eu!&ZGN';
> GO
>
> use [FoundationAppleSiliconTest.Cms]
> CREATE USER FoundationAppleSiliconTestOurCmsUser FOR LOGIN FoundationAppleSiliconTestOurCmsUser;  
> ALTER ROLE db_owner ADD MEMBER FoundationAppleSiliconTestOurCmsUser
> GO
>
> use [FoundationAppleSiliconTest.Commerce]
> CREATE USER FoundationAppleSiliconTestOurCmsUser FOR LOGIN FoundationAppleSiliconTestOurCmsUser;  
> ALTER ROLE db_owner ADD MEMBER FoundationAppleSiliconTestOurCmsUser
> GO
>
> ALTER ROLE db_owner ADD MEMBER FoundationAppleSiliconTestOurCmsUser
> ```
>
> As last configuration step copy following configuration to `appsettings.Development.json` file:
>
> ```json
> "ConnectionStrings": {
>    "EPiServerDB": "Server=.;Database=FoundationAppleSiliconTest.Cms;User Id=FoundationAppleSiliconTestOurCmsUser;Password=bNaK31CgBWPBT6SMF4Eu!&ZGN;TrustServerCertificate=True",
>    "EcfSqlConnection": "Server=.;Database=FoundationAppleSiliconTest.Commerce;User Id=FoundationAppleSiliconTestOurCmsUser;Password=bNaK31CgBWPBT6SMF4Eu!&ZGN;TrustServerCertificate=True"
> },
>```
>
> That's it! Now you can run `npm` to build frontend and build and run Foundation site itself!

Now that we have all ready let's build & run our foundation solution to see how it works in our environment.

![CMS Home Page](/assets/img/2025-05-26-cms-home-page.png)

For me, its cold start took a few seconds, but then it started working very smoothly. I logged into the Optimizely back office and everything is working fast and very responsive including catalog browsing, page editing, and on-page editing. Experience is very good and it seems to be working very well for me. It was worth waiting 3 years to have the ability to work on an M1 processor on Optimizely projects!
