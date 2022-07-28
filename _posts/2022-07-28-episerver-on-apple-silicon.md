---
layout: post
title:  "Episerver Foundation on Apple Silicon (M1)!"
date:   2022-07-28 21:29:19 +0200
author: Stanisław Szołkowski
comments: true
published: true
tags:
- episerver
- optimizely
- m1
- arm
- apple silicon
---

With introduction of .NET 6 Episerver/Optimizely packages developing it on M1 machine is now possible, however there are still some problems with initial configuration, but lets not go ahead of ourself.

## Prerequisites

- Git
- Docker
- Azure Data Studio
- Dotnet 6.0
- Node JS

## Let's make Episerver run on M1!

Lets start working on Episerver Foundation example site project. I recommend to use this code version from [my Foundation fork](https://github.com/szolkowski/Foundation/tree/e436ac689be335f8ce506cc22d349371de72aba9), but feel free to use Foundation repository as well.
After cloning repository building it will work however on ARM machine `setup.sh/setup.cmd` script won't work, so we need to work around it.

First of all we need change SQL Server docker image to `azure-sql-edge` which is right now the only product from SQL Server family with image available on ARM. We can't just replace it in `setup.sh/setup.cmd` script, because ARM version of this image comes without `sqlcmd` in it.

Run following command in terminal, it will fetch and start our DB instance:

```bash
docker run -d --name sql_server_optimizely -h localhost -e 'ACCEPT_EULA=Y' -e 'SA_PASSWORD=Episerver123!' \
    -p 1433:1433 mcr.microsoft.com/azure-sql-edge:latest
```

After DB is up and running (it will take a moment after image is up to start DB) connect to DB using `Azure Data studio` using following credentials:
- server: localhost
- login: SA
- password: Episerver123!

Then using Data-tier Application Wizard Create databases from following .bacpac files (Import Bacpac):
- FoundationAppleSiliconTest.Cms.bacpac
- FoundationAppleSiliconTest.Commerce.bacpac

Files are available on my Foundation fork linked above in `db_backups` directory.

![My helpful screenshot](/assets/img/2022-07-28-episerver-on-apple-silicon-db-import-1.png)

After import is done execute following SQL query on database which will create login and users used by our Foundation site:

```sql
CREATE LOGIN FoundationAppleSiliconTestOurCmsUser WITH PASSWORD = 'bNaK31CgBWPBT6SMF4Eu!&ZGN';
GO

use [FoundationAppleSiliconTest.Cms]
CREATE USER FoundationAppleSiliconTestOurCmsUser FOR LOGIN FoundationAppleSiliconTestOurCmsUser;  
ALTER ROLE db_owner ADD MEMBER FoundationAppleSiliconTestOurCmsUser
GO

use [FoundationAppleSiliconTest.Commerce]
CREATE USER FoundationAppleSiliconTestOurCmsUser FOR LOGIN FoundationAppleSiliconTestOurCmsUser;  
ALTER ROLE db_owner ADD MEMBER FoundationAppleSiliconTestOurCmsUser
GO

ALTER ROLE db_owner ADD MEMBER FoundationAppleSiliconTestOurCmsUser
```

As last configuration step copy following configuration to `appsettings.Development.json` file:

```json
"ConnectionStrings": {
    "EPiServerDB": "Server=.;Database=FoundationAppleSiliconTest.Cms;User Id=FoundationAppleSiliconTestOurCmsUser;Password=bNaK31CgBWPBT6SMF4Eu!&ZGN;TrustServerCertificate=True",
    "EcfSqlConnection": "Server=.;Database=FoundationAppleSiliconTest.Commerce;User Id=FoundationAppleSiliconTestOurCmsUser;Password=bNaK31CgBWPBT6SMF4Eu!&ZGN;TrustServerCertificate=True"
},
```

That's it! Now you can run `npm` to build frontend and build and run Foundation site itself!

![My helpful screenshot](/assets/img/2022-07-28-episerver-on-apple-silicon-episerver-running-on-m1-1.png)

## Known issues

There is a [bug](https://github.com/episerver/Foundation/issues/832) in in Foundation which cause crashing app on initialization of missing Episerver form module. Right now best solution is to manually copy:
- configuration and zip files manually from `.nuget/packages/episerver.forms/5.2.0/contentFiles/any/any/modules/_protected/EPiServer.Forms/` to `/src/Foundation/modules/_protected/episerver.forms/`
- zip file from `.nuget/packages/episerver.forms.ui/5.2.0/contentFiles/any/any/modules/_protected/EPiServer.Forms.UI/` to `/src/Foundation/modules/_protected/episerver.forms.ui/` 