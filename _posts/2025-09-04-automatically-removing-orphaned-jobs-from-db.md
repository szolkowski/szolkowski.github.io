---
layout: post
title:  "How to automatically remove orphaned Opti jobs from the DB"
date:   2025-09-04 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2025-06-12-optimizely-scheduled-jobs-dashboard.png
   alt: "How to automatically remove orphaned Opti jobs from the DB"
tags:
- episerver
- optimizely
- jobs
- scheduled jobs
---

Optimizely CMS provides a simple yet powerful built-in job system that handles most standard scheduling scenarios with ease. Developers can easily implement these jobs to run automatically on a defined schedule or trigger them manually through the CMS interface. From my experience as an Optimizely/Episerver developer, manual jobs are especially useful for one-off operations or bulk content updates. Typically, these jobs serve a temporary purpose and are removed from the codebase in subsequent releases once they’ve fulfilled their role.

## What is the problem?

Optimizely/Episerver supports both code-based and CMS-based content management. Because of this duality, changes made in code aren’t always reflected in the database. This includes scheduled jobs, which—although not content items—can become orphaned when their class definitions are removed from code but remain in the database.

Is this a critical issue? No. The system continues to function, and removed jobs no longer appear in the dashboard.

However, orphaned jobs can leave behind:

- Entries in tblScheduledItem and tblScheduledItemLog
- Startup exceptions during application initialization
- Unnecessary noise in logging systems like Application Insights

## Solution: Automating Cleanup of Orphaned Scheduled Jobs

While orphaned scheduled jobs don’t break anything in Optimizely CMS, they do leave behind unnecessary clutter—both in the database and in application logs. Over time, this can make your solution harder to maintain and less transparent.

A quick fix might be to run a one-off SQL query to delete these entries from `tblScheduledItem` and `tblScheduledItemLog`. But manual cleanup tends to become a recurring task, and I prefer to automate wherever possible. Out of sight, out of mind—forever.

### My Approach: InitializableModule for Automatic Cleanup

To automate the cleanup, I created a lightweight `InitializableModule` that runs on application startup. It identifies orphaned jobs—those that exist in the database but no longer have a corresponding class in code—and removes them safely.

Here’s how it works:

- Fetch all scheduled jobs from the database using IScheduledJobRepository.
- Scan all loaded assemblies for types that inherit from ScheduledJobBase and are decorated with ScheduledPlugInAttribute.
- Extract GUIDs from those job types to identify valid jobs.
- Compare database entries against the GUIDs to find orphaned jobs.
- Optionally filter by LastExecutionMessage to avoid edge cases.
- Delete orphaned jobs and log the operation.

```c#
[InitializableModule]
[ModuleDependency(typeof(CmsCoreInitialization))]
public sealed class OrphanedJobRemoverModule : IInitializableModule
{
    public void Initialize(InitializationEngine context)
    {
        using var scope = ServiceLocator.Current.GetInstance<IServiceProvider>().CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IScheduledJobRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<OrphanedJobRemoverModule>>();

        // retrieve a list of all jobs in the database
        var dbJobs = jobRepository.List();

        // next, let's get job types from classes that inherit from ScheduledJobBase and are decorated with ScheduledPlugInAttribute
        var jobTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(ScheduledJobBase).IsAssignableFrom(type) && type.GetCustomAttribute<ScheduledPlugInAttribute>() != null)
            .ToList();

        // now let's extract the guids of jobs that have types from code from the previous step
        var codeJobGuids = jobTypes
            .Select(type => type.GetCustomAttribute<ScheduledPlugInAttribute>()?.GUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .ToHashSet();

        // finally, lets filter out jobs from Databases that don't have their representation in code using guids from previous steps, and also as an optional check for the last execution message to be extra safe.  
        // LastExecutionMessage check is optional and was added just to make sure that no extra edge case will be removed. It was a specific requirement, so in a standard project, it can be removed.
        var orphanedJobs = dbJobs
            .Where(job => !codeJobGuids.Contains(job.ID.ToString()))
            .Where(job => job.LastExecutionMessage != null && job.LastExecutionMessage.StartsWith("Could not load type '<assembly_path_to_jobs_directory>"))
            .ToList();

        foreach (var job in orphanedJobs)
        {
            try
            {
                logger.LogInformation("Removing: {JobName} (ID: {JobID})", job.Name, job.ID);
                // removes the job from both tblScheduledItem and tblScheduledItemLog tables
                jobRepository.Delete(job.ID);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to remove: {JobName} (ID: {JobID}) with message: {Message}", job.Name, job.ID, ex.Message);
            }
        }
    }

    public void Uninitialize(InitializationEngine context)
    {
    }
}
```

### Why Not Use a Scheduled Job?

You could implement this as a scheduled job, but I chose an InitializableModule to keep the job dashboard clean and avoid exposing low-level maintenance tasks to CMS users. This module runs silently and efficiently—no manual steps, no clutter

## Summary

That's it! Simple module with logging ready to be copied/pasted into your project. Do you think that my approach has its drawbacks? Let me know in the comments.

Thank you for reading, and I hope you will find it useful.
