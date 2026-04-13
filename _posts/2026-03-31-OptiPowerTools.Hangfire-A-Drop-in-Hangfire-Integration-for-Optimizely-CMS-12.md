---
layout: post
title:  "OptiPowerTools.Hangfire: A Drop-in Hangfire Integration for Optimizely CMS 12"
date:   2026-03-31 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-03-31-OptiPowerTools.Hangfire-Dashboard.png
   alt: "OptiPowerTools.Hangfire: A Drop-in Hangfire Integration for Optimizely CMS 12"
tags:
- episerver
- optimizely
- scheduled jobs
- hangfire
- background jobs 
- .NET
- nuget
- open-source
- OptiPowerTools
- OptiPowerTools.Hangfire
---

Back in 2024, I wrote a post on [Adding Hangfire to Episerver/Optimizely CMS 12]({% post_url 2024-07-31-adding-hangfire-to-epi-12 %}), walking through each step of integrating Hangfire into an Optimizely project — the authorization filter, the controller, the view with an iframe, the menu provider, the startup wiring. It was a fair amount of boilerplate, but it worked and people found it useful.

What I didn't expect was the interest it would generate. The post kept getting traffic, and several readers asked whether this could be a reusable package instead of a manual setup guide. Having used Hangfire in multiple commercial Optimizely projects myself, I knew exactly which parts were repetitive and which needed flexibility.

Fair point. So I built one. Meet **OptiPowerTools.Hangfire** — a drop-in NuGet package that turns all of that manual setup into two lines of code.

<p style="text-align: center;">
  <img src="/assets/img/2026-03-31-OptiPowerTools.Hangfire-icon.png" alt="OptiPowerTools.Hangfire icon" style="max-width: 200px;" />
</p>

## Getting started

The package wraps everything from the original blog post — and more — into two extension methods. Here's the minimal setup in `Program.cs` or `Startup.cs`:

```csharp
// In Program.cs or Startup.cs
services.AddOptiPowerToolHangfire(options =>
{
    options.ConnectionString = Configuration.GetConnectionString("HangfireConnection");
});

// In the middleware pipeline (after UseAuthentication/UseAuthorization)
app.UseOptiPowerToolHangfire();
```

That's it. This registers Hangfire with SQL Server storage, starts the background server, enables the dashboard with Optimizely role-based authorization, and adds a menu item to the CMS navigation. Everything that took multiple files and classes in the original post is now two lines of code.

## Features out of the box

The dashboard is embedded in the CMS shell, so it feels like a native part of Optimizely rather than a separate tool:

- **Hangfire Dashboard** with proper Optimizely navigation chrome
- **Role-based authorization** — by default, only Administrators, CmsAdmins, and WebAdmins can access the dashboard
- **CMS menu integration** with three placement options: under the CMS section (default), as a top-level nav item, or in a custom section group

![Hangfire Dashboard in Optimizely CMS](/assets/img/2026-03-31-OptiPowerTools.Hangfire-Dashboard.png)

Jobs get rich console output out of the box — [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console) is enabled by default. Inject `PerformContext` into your job method and use `context.WriteLine()` to write directly to the job's console in the dashboard, just like I showed in the original post.

- **Configurable everything** — dashboard path, title, authorized roles, schema name, menu placement, and more
- **Feature toggles** — enable or disable the dashboard, console, CMS menu, or standard authorization independently
- Targets **net6.0**, **net8.0**, **net9.0**, and **net10.0**

## Configuration

All options except `ConnectionString` have sensible defaults. You can configure via code, `appsettings.json`, or both (code overrides config). The full options look like this:

```json
{
  "OptiPowerTools": {
    "Hangfire": {
      "ConnectionString": "Server=.;Database=MyDb;Trusted_Connection=True;",
      "DashboardPath": "/episerver/backoffice/Plugins/hangfire",
      "DashboardTitle": "OptiPowerTools Hangfire Dashboard",
      "AuthorizedRoles": ["Administrators", "CmsAdmins", "WebAdmins"],
      "SchemaName": "hangfire",
      "EnableDashboard": true,
      "EnableConsole": true,
      "EnableCmsMenu": true,
      "EnableStandardAuthorization": true,
      "MenuPlacement": "CmsSection",
      "JobExpirationCheckInterval": "00:15:00"
    }
  }
}
```

If the defaults work for you, you don't need any of this — just provide the connection string and go.

### Custom authorization

The built-in filter checks Optimizely roles, but you can bring your own `IDashboardAuthorizationFilter` using the generic overload:

```csharp
services.AddOptiPowerToolHangfire<MyCustomAuthFilter>(options =>
{
    options.ConnectionString = Configuration.GetConnectionString("HangfireConnection");
});
```

Or disable authorization entirely for development:

```csharp
services.AddOptiPowerToolHangfire(options =>
{
    options.ConnectionString = Configuration.GetConnectionString("HangfireConnection");
    options.EnableStandardAuthorization = false;
});
```

## Built-in job filters

This is where things go beyond the original blog post. The package ships with four attribute-based job filters for common patterns I've seen (and written about) in production Optimizely projects. In [Part 3: Hangfire Integration]({% post_url 2026-03-03-Catalog-Traversal-with-Hangfire-Part-3-Advanced-Job-Management %}), I covered job dependencies and concurrency challenges when running multiple Hangfire jobs against the same data. These filters address exactly those scenarios.

### MutualExclusion

Prevents concurrent execution of jobs sharing the same resource group using Hangfire distributed locks. No race conditions, no blocking — the worker thread is freed immediately and the job is rescheduled.

```csharp
[MutualExclusion("data-pipeline")]
public class DataImportJob
{
    public void Execute() { /* ... */ }
}

[MutualExclusion("data-pipeline")]
public class DataExportJob
{
    public void Execute() { /* ... */ }
}
```

When `DataImportJob` is running, `DataExportJob` gets rescheduled automatically (and vice versa).

### WaitForOtherJobs

One-directional dependency — prevents a job from executing while specific other job types are processing. Only the decorated job needs the attribute.

```csharp
[WaitForOtherJobs(typeof(DataImportJob))]
public class ReportGeneratorJob
{
    public void Execute() { /* ... */ }
}
```

### ExpireOnSuccess

Reduces retention for succeeded jobs. Hangfire keeps succeeded jobs for 24 hours by default, which is overkill for fire-and-forget tasks or health checks.

```csharp
[ExpireOnSuccess(60)]   // Expire 60 seconds after success
public class NotificationJob { /* ... */ }

[ExpireOnSuccess]       // Expire immediately
public class HealthCheckJob { /* ... */ }
```

### RetainOnSuccess

The opposite — extends retention beyond the default 24 hours. Useful for weekly reports or monthly audits where you want the job history to stick around in the dashboard.

```csharp
[RetainOnSuccess(180)]  // Keep for 180 days
public class MonthlyAuditJob { /* ... */ }
```

All four filters can be combined with Hangfire's built-in `[DisableConcurrentExecution]` for complete control over job execution.

## Removing the package

One thing I wanted to get right: this package is a thin configuration wrapper. It does not modify Hangfire internals or change how Hangfire stores data. If your project outgrows it and you need full control, you can remove the package and configure Hangfire manually. Your existing database, jobs, and history will continue to work without any migration or data changes.

## Where to get it

The source is on GitHub: [szolkowski/OptiPowerTools.Hangfire](https://github.com/szolkowski/OptiPowerTools.Hangfire)

Install via NuGet from the [Optimizely feed](https://nuget.optimizely.com/packages/optipowertools.hangfire/) or [nuget.org](https://www.nuget.org/packages/OptiPowerTools.Hangfire):

```bash
dotnet add package OptiPowerTools.Hangfire
```

The repository includes full documentation, a dev site using the [Optimizely Foundation](https://github.com/episerver/Foundation) project for testing, and xUnit tests.

## Wrapping up

What started as a blog post turned into a package because enough people asked for it. If you're running Hangfire in an Optimizely CMS 12 project — or considering it — this should save you the boilerplate and give you a few useful job filters on top.

If you run into issues or have feature requests, open an issue on [GitHub](https://github.com/szolkowski/OptiPowerTools.Hangfire/issues). Contributions are welcome too.

Are there other Hangfire patterns you'd like to see packaged up? Let me know in the comments. Thank you for reading!
