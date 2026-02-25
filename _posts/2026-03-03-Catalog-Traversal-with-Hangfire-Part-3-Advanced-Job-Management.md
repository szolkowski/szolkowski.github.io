---
layout: post
title:  "Catalog Traversal with Hangfire. Part 3 Advanced Job Management"
date:   2026-03-03 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-03-03-Catalog-Traversal-with-Hangfire-Part-3-Advanced-Job-Management.png
   alt: "Catalog Traversal with Hangfire. Part 3 Advanced Job Management"
tags:
- episerver
- optimizely
- commerce
- catalog
- patterns
- scheduled jobs
- .NET
- hangfire
- background-jobs 
---

In [Part 1]({% post_url 2026-02-18-Memory-Efficient-Catalog-Traversal-in-Optimizely-Commerce-Part-1-Building-the-Service %}), I showed how to build a memory-efficient catalog traversal service, and in [Part 2]({% post_url 2026-02-24-Catalog-Traversal-in-Action-Part-2-Real-World-Scheduled-Job-Patterns %}), I demonstrated practical patterns using Optimizely's built-in scheduled jobs.

While Optimizely's scheduled job system works well for basic scenarios, you might find yourself wanting more: better monitoring, automatic retries, distributed execution, or more flexible scheduling. This is where Hangfire shines.

In this post, I'll show how to use the same catalog traversal service with Hangfire, taking advantage of its advanced features for production-grade job management.

## Why Hangfire?

Optimizely's built-in scheduled jobs are perfectly adequate for many scenarios, but Hangfire offers several compelling advantages:

**Better Monitoring:**

- Rich dashboard with job history and real-time status
- Detailed execution logs with console output
- Visual progress indicators and statistics

**Reliability:**

- Automatic retry logic with configurable policies
- Job continuation and chaining
- Persistent job queue (survives app restarts)

**Flexibility:**

- Run jobs on-demand via API or dashboard
- Schedule one-time or recurring jobs programmatically
- Distribute jobs across multiple servers

**Developer Experience:**

- Console output during execution (like `Console.WriteLine` but persisted)
- Progress bars and color-coded messages
- Better debugging capabilities

For catalog processing jobs that may run for extended periods or need robust error handling, these features can be invaluable.

## Setting Up Hangfire in Optimizely

If you haven't already added Hangfire to your Optimizely project, here's a quick setup guide. For a more detailed walkthrough, check out my [previous post on adding Hangfire to Optimizely CMS 12](/2024/07/31/adding-hangfire-to-epi-12.html).

### Install NuGet Packages

```bash
dotnet add package Hangfire.Core
dotnet add package Hangfire.SqlServer
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.Console
```

### Configure in Startup

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // ... other services

    // Add Hangfire with SQL Server storage
    services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(
            _configuration.GetConnectionString("EPiServerDB"),
            new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            })
        .UseConsole());

    services.AddHangfireServer(options =>
    {
        options.WorkerCount = 2; // Adjust based on your needs
    });
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // ... other middleware

    // Add Hangfire dashboard
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter() }
    });
}
```

### Authorization Filter

```csharp
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        
        // In production, implement proper authorization
        // For example, check if user is in admin role
        return httpContext.User.IsInRole("WebAdmins") || 
               httpContext.User.IsInRole("Administrators");
    }
}
```

## Converting Patterns to Hangfire

Let's revisit the patterns from Part 2 and see how they work with Hangfire. The catalog traversal service stays exactly the same—only the job wrapper changes.

## Pattern 1: Full Catalog Export with Hangfire

Here's the full export pattern, now with Hangfire's console output and progress tracking:

{% include code-modal.html
   id="2026-03-03-Hangfire-Catalog-Export-Job"
   lang="csharp"
   file="post_assets/code-snippets/2026-03-03-Hangfire-Catalog-Export-Job.cs"
%}

**Schedule the job:**

```csharp
// One-time execution
BackgroundJob.Enqueue<HangfireCatalogExportJob>(job => 
    job.Execute(null, CancellationToken.None));

// Recurring job - runs daily at 2 AM
RecurringJob.AddOrUpdate<HangfireCatalogExportJob>(
    "catalog-export-daily",
    job => job.Execute(null, CancellationToken.None),
    "0 2 * * *"); // Cron expression
```

**Key Differences from Standard Jobs:**

- `PerformContext` provides console output methods
- Color-coded messages in the Hangfire dashboard
- Automatic retry on exceptions (configurable)
- Can trigger via API or dashboard

## Pattern 2: Incremental Sync with State Management

The incremental sync pattern works beautifully with Hangfire's persistence:

{% include code-modal.html
   id="2026-03-03-Hangfire-Incremental-Catalog-Sync-Job"
   lang="csharp"
   file="post_assets/code-snippets/2026-03-03-Hangfire-Incremental-Catalog-Sync-Job.cs"
%}

**Schedule with automatic retries:**

```csharp
RecurringJob.AddOrUpdate<HangfireIncrementalCatalogSyncJob>(
    "catalog-sync-incremental",
    job => job.Execute(null, CancellationToken.None),
    "0 */4 * * *", // Every 4 hours
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time")
    });
```

**Note on Progress Bars:** In this example, I'm using `ToList()` to get the total count for the progress bar. This goes against our streaming principle, but it's a trade-off for better UX. If your catalogs are very large, you can skip the progress bar or estimate the count differently.

## Pattern 3: Batch Processing with Hangfire Continuation

Hangfire's job continuation feature lets you chain jobs together. Here's a pattern that processes batches and then runs a cleanup job:

{% include code-modal.html
   id="2026-03-03-Hangfire-Batch-Catalog-Export-Job"
   lang="csharp"
   file="post_assets/code-snippets/2026-03-03-Hangfire-Batch-Catalog-Export-Job.cs"
%}

**Schedule with continuation:**

```csharp
// Schedule main job
var jobId = BackgroundJob.Enqueue<HangfireBatchCatalogExportJob>(job => 
    job.Execute(null, CancellationToken.None));

// Schedule cleanup job to run after main job completes
BackgroundJob.ContinueJobWith<HangfireCleanupJob>(
        jobId,
        job => job.Execute(null, null));
```

## Pattern 4: Multi-Catalog with Parallel Processing

Hangfire makes it easy to process multiple catalogs in parallel:

{% include code-modal.html
   id="2026-03-03-Hangfire-Parallel-Multi-Catalog-Sync-Job"
   lang="csharp"
   file="post_assets/code-snippets/2026-03-03-Hangfire-Parallel-Multi-Catalog-Sync-Job.cs"
%}

**Schedule the orchestrator:**

```csharp
// Schedule orchestrator job that creates individual catalog jobs
BackgroundJob.Enqueue<HangfireParallelMultiCatalogSyncJob>(job => 
    job.ExecuteOrchestrator(null));

// Or as recurring job
RecurringJob.AddOrUpdate<HangfireParallelMultiCatalogSyncJob>(
    "hangfire-multi-catalog-parallel",
    job => job.ExecuteOrchestrator(null),
    "0 3 * * *"); // Daily at 3 AM
```

**Benefits:**

- Catalogs are processed in parallel (limited by Hangfire worker count)
- Each catalog gets its own job in the dashboard
- Individual catalog failures don't affect others
- Better monitoring and debugging per catalog

## Advanced Hangfire Features

### Automatic Retry with Custom Logic

Configure how Hangfire retries failed jobs:

```csharp
[AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class RetryableCatalogExportJob
{
    public void Execute(PerformContext context, CancellationToken cancellationToken)
    {
        // Job implementation
    }
}

// Or configure globally
GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute 
{ 
    Attempts = 5,
    DelaysInSeconds = new[] { 60, 300, 900 } // 1 min, 5 min, 15 min
});
```

### Job Filters for Logging

Create a filter to automatically log job execution:

```csharp
public class JobExecutionLogFilter : IElectStateFilter, IApplyStateFilter
{
    private readonly ILogger<JobExecutionLogFilter> _logger;

    public JobExecutionLogFilter(ILogger<JobExecutionLogFilter> logger)
    {
        _logger = logger;
    }

    public void OnStateElection(ElectStateContext context)
    {
        var failedState = context.CandidateState as FailedState;
        if (failedState != null)
        {
            _logger.LogError(
                failedState.Exception,
                "Job {JobId} ({JobType}) failed: {ErrorMessage}",
                context.BackgroundJob.Id,
                context.BackgroundJob.Job.Type.Name,
                failedState.Exception.Message);
        }
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is SucceededState)
        {
            _logger.LogInformation(
                "Job {JobId} ({JobType}) completed successfully",
                context.BackgroundJob.Id,
                context.BackgroundJob.Job.Type.Name);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
```

### Delayed Jobs

Schedule a job to run after a specific delay:

```csharp
// Run in 5 minutes
BackgroundJob.Schedule<HangfireCatalogExportJob>(
    job => job.Execute(null, CancellationToken.None),
    TimeSpan.FromMinutes(5));

// Run at specific time
BackgroundJob.Schedule<HangfireCatalogExportJob>(
    job => job.Execute(null, CancellationToken.None),
    DateTimeOffset.Now.AddHours(2));
```

### Job Cancellation

Hangfire supports cancellation tokens natively:

```csharp
public void Execute(PerformContext context, CancellationToken cancellationToken)
{
    foreach (var item in _catalogTraversal.GetAllProducts(options, cancellationToken))
    {
        // If job is cancelled in dashboard, token will be signaled
        cancellationToken.ThrowIfCancellationRequested();
        
        // Process item
    }
}
```

## Monitoring and Dashboard

One of Hangfire's best features is its dashboard. Access it at `/hangfire` in your application to see:

- **Real-time job status** - See what's running right now
- **Console output** - All `WriteLine` calls appear here
- **Job history** - Success/failure rates, execution times
- **Retry tracking** - See which jobs failed and why
- **Queue management** - Manually trigger or delete jobs

The dashboard makes it easy for operations teams to monitor catalog processing without developer intervention.

## Best Practices for Hangfire Jobs

**Keep Job Methods Simple:**

```csharp
// Good - simple method signature
public void Execute(PerformContext context, int complexObjectId, CancellationToken cancellationToken)

// Avoid - complex parameters that need serialization
public void Execute(PerformContext context, ComplexObject data, CancellationToken cancellationToken)
```

**Use Dependency Injection:**
Hangfire resolves dependencies from your DI container, so inject services instead of passing data:

```csharp
public class MyJob
{
    private readonly IMyService _service;
    
    public MyJob(IMyService service) // Injected by Hangfire
    {
        _service = service;
    }
}
```

**Use Meaningful Job Names:**

```csharp
[JobDisplayName("Catalog Export - {1}")]
public void Execute(PerformContext context, string catalogName)
{
    // Job will show as "Catalog Export - Fashion" in dashboard
}
```

## When to Use Hangfire vs Standard Jobs

Use **Hangfire** when you need:

- Better monitoring and visibility
- Automatic retry logic
- Job chaining and workflows
- Distributed execution across multiple servers
- API-triggered or on-demand jobs
- Complex scheduling requirements

Use **Standard Optimizely Jobs** when:

- Simple scheduled tasks are sufficient
- You want minimal external dependencies
- Jobs are tightly integrated with Optimizely features
- You prefer the native CMS interface

For catalog processing specifically, Hangfire's console output and progress bars make it much easier to monitor long-running jobs.

## Summary

Hangfire brings enterprise-grade job management to your Optimizely Commerce catalog processing. By combining the memory-efficient catalog traversal service from Part 1 with Hangfire's advanced features, you get:

- Real-time monitoring with rich console output
- Automatic retries with configurable policies
- Job chaining and parallel processing
- Better debugging and troubleshooting
- Persistent job queue that survives app restarts

The catalog traversal service works seamlessly with both standard Optimizely scheduled jobs (Part 2) and Hangfire (this post). Choose the approach that best fits your requirements, or use both in the same solution for different scenarios.

The patterns we've covered—full export, incremental sync, batch processing, and parallel multi-catalog—all benefit from Hangfire's capabilities. The console output alone makes long-running catalog jobs much more transparent for operations teams.

Do you have other Hangfire patterns you've found useful for Optimizely Commerce? Share them in the comments!

Thank you for reading, and I hope this series helps you build robust, scalable catalog processing solutions.

- [Part 1: Building the Service]({% post_url 2026-02-18-Memory-Efficient-Catalog-Traversal-in-Optimizely-Commerce-Part-1-Building-the-Service %})
- [Part 2: Real-World Scheduled Job Patterns ]({% post_url 2026-02-24-Catalog-Traversal-in-Action-Part-2-Real-World-Scheduled-Job-Patterns%})
- Part 3: Hangfire Integration - (this post)
