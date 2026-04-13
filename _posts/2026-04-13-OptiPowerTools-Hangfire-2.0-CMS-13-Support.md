---
layout: post
title:  "OptiPowerTools.Hangfire 2.0.0: CMS 13 Support and Sample Jobs"
date:   2026-04-13 11:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-04-13-OptiPowerTools-Hangfire-2.0.0.png
   alt: "OptiPowerTools.Hangfire 2.0.0: CMS 13 Support and Sample Jobs"
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

When I [released OptiPowerTools.Hangfire]({% post_url 2026-03-31-OptiPowerTools.Hangfire-A-Drop-in-Hangfire-Integration-for-Optimizely-CMS-12 %}) back in March, it targeted Optimizely CMS 12. With CMS 13 now out and running on .NET 10, it was time to bring the package along. Version 2.0.0 adds full CMS 13 support, and I also shipped 1.0.1 on the CMS 12 line with sample jobs to help people get started faster.

## What's new in 2.0.0

The headline change is **Optimizely CMS 13 support**. The package now targets .NET 10 exclusively and works with the updated CMS 13 shell and UI.

Beyond the framework bump, there's one notable change: the **default dashboard path** moved from `/episerver/backoffice/Plugins/hangfire` to `/optimizely/backoffice/Plugins/hangfire` to align with the CMS 13 URL structure. If you're migrating from 1.x, update any bookmarks or documentation that reference the old path. The path is still fully configurable via `DashboardPath` in the options if you need a custom location.

Everything else — the two-line setup, the job filters, the CMS menu integration, configurable roles — works the same as in 1.x. If you're already running OptiPowerTools.Hangfire on CMS 12, upgrading to 2.0.0 on CMS 13 should be straightforward.

A follow-up patch **2.0.1** was released shortly after with minor fixes to the Hangfire route mapping for improved compatibility.

## Version 1.0.1: Sample Jobs

On the CMS 12 side, version 1.0.1 adds a set of sample jobs to the repository's dev site. These are practical examples that demonstrate common Hangfire patterns within Optimizely — useful as a reference when building your own jobs.

- **ConsoleShowcaseJob** — demonstrates Hangfire.Console features: colored text output, progress bars, and structured multi-phase processing with a simulated product catalog sync
- **OrderPipelineJob** — shows job continuations by chaining a four-step workflow (Validate → Payment → Ship → Notify) using `ContinueJobWith`
- **ScheduledCleanupJob** — demonstrates delayed/scheduled job execution by planning cleanup tasks with varying delays using `Schedule`
- **CancellableExportJob** — demonstrates cancellation token support for long-running operations with graceful shutdown and cleanup when a job is deleted from the dashboard

You can find all four samples in the [`Samples`](https://github.com/szolkowski/OptiPowerTools.Hangfire/tree/main/src/OptiPowerTools.Hangfire.Web/Samples) directory of the repository.

## Version compatibility

| Package Version | Optimizely CMS | .NET            |
|-----------------|----------------|-----------------|
| 2.x             | CMS 13         | .NET 10         |
| 1.x             | CMS 12         | 6.0, 8.0, 9.0, 10.0 |

The 1.x line continues to receive maintenance updates on the [`releases/v1-release`](https://github.com/szolkowski/OptiPowerTools.Hangfire/tree/releases/v1-release) branch.

## Where to get it

The source is on GitHub: [szolkowski/OptiPowerTools.Hangfire](https://github.com/szolkowski/OptiPowerTools.Hangfire)

Install via NuGet from the [Optimizely feed](https://nuget.optimizely.com/packages/optipowertools.hangfire/) or [nuget.org](https://www.nuget.org/packages/OptiPowerTools.Hangfire):

```bash
dotnet add package OptiPowerTools.Hangfire
```

If you have questions, run into issues, or want to request a feature — open an issue on [GitHub](https://github.com/szolkowski/OptiPowerTools.Hangfire/issues). Let me know in the comments how the upgrade goes!
