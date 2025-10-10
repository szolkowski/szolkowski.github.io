---
layout: post
title:  "Quiet Performance Wins: Scheduled Job for SQL Index Maintenance in Optimizely"
date:   2025-10-08 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2025-10-08-Quiet-Performance-Wins-Scheduled-Job-for-SQL-Index-Maintenance-in-Optimizely.jpeg
   alt: "Quiet Performance Wins: Scheduled Job for SQL Index Maintenance in Optimizely"
tags:
- episerver
- optimizely
- jobs
- scheduled jobs
- database
- db
- indexes
- maintenance
- performance
---

As Optimizely CMS projects grow, it's not uncommon to introduce custom tables—whether for integrations, caching, or specialized business logic. But with great schema comes great responsibility: SQL Server indexes and statistics need love too.

While Optimizely handles its own data structures well, custom tables can quietly degrade performance if left unchecked. Optimizely Commerce includes a built-in job for index maintenance, but if your solution uses only CMS, this functionality will be missing. In this post, I’ll show how to automate index and statistics maintenance using a scheduled job.

## Why You Should Care

SQL Server relies heavily on up-to-date statistics and healthy indexes to optimize query execution. Fragmented indexes and stale stats can lead to slow queries, increased CPU usage, and unhappy editors.

If you're adding custom tables to your CMS database, especially ones that grow over time, you should consider regular maintenance. And what better way than a scheduled job that runs quietly in the background?

## The Scheduled Job

Here’s a simple implementation of a scheduled job that performs index and statistics maintenance on selected custom tables. You can trigger it manually or schedule it via Optimizely’s job system.

{% include code-modal.html
   id="database-index-maintenance-scheduled-job"
   lang="csharp"
   file="code-snippets/database-index-maintenance-scheduled-job.cs"
%}

## Performance Considerations

### During Execution

**REBUILD operations:**

- High CPU usage (50-80% spike for 1-5 minutes)
- Locks the table (unless `ONLINE = ON` on Enterprise Edition)
- Should be run during maintenance windows

**REORGANIZE operations:**

- Minimal CPU impact (10-20%)
- Online operation (no blocking)
- Safe to run during business hours

### After Execution

Typical improvements on databases with 30%+ fragmentation:

- Query performance: 15-40% faster
- CPU usage: 10-15% reduction
- Page I/O: 20-30% reduction

**Note:** Benefits are most noticeable on:

- Tables with > 1M rows
- Queries with table/index scans
- Reports and analytics queries

## What Gets Maintained?

This job analyzes **all indexes** across your entire database, including:

| Table Type | Examples | Maintained? |
|------------|----------|-------------|
| Your custom tables | `CustomOrderCache`, `IntegrationLog` | Yes |
| Optimizely CMS core | `tblContent`, `tblContentProperty`, `tblWorkContent` | Yes |
| Commerce tables | `OrderGroup`, `Shipment`, `LineItem` | Yes (if installed) |
| ASP.NET Identity | `AspNetUsers`, `AspNetRoles` | Yes |

### Is This Safe?

**Yes, generally.** Index maintenance operations are safe on all tables. However:

- **REBUILD operations** may briefly lock tables on Standard Edition
- Large Optimizely tables (like `tblContentProperty`) may take several minutes
- First run might take 10-20 minutes on established sites

### Should You Filter?

**For production safety, consider filtering to custom tables only** if:

- You have a very large CMS database (50GB+)
- You're on SQL Server Standard Edition (no ONLINE rebuilds)
- You want to minimize maintenance window impact

## Understanding Statistics Updates

`UPDATE STATISTICS` ensures the query optimizer has accurate data about:

- Row counts
- Data distribution
- Index selectivity

The `SAMPLE 50 PERCENT` option:

- Faster than FULLSCAN
- Accurate enough for most scenarios
- Use `WITH FULLSCAN` for critical tables if needed

## Notes

UPDATE STATISTICS ensures the query planner has fresh data to work with.

You can extend the job to log execution time or errors using Optimizely’s logging framework.

Provided job targets ALL indexes in the database, including Optimizely's core tables. Logic can be adjusted to target only whitelisted tables by changing index query:

```sql
    SELECT OBJECT_SCHEMA_NAME(s.[object_id]) AS SchemaName,
           OBJECT_NAME(s.[object_id]) AS TableName,
           i.name AS IndexName,
           s.avg_fragmentation_in_percent AS Frag
    FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') s
    JOIN sys.indexes i ON s.[object_id] = i.[object_id] AND s.index_id = i.index_id
    WHERE i.type_desc IN ('CLUSTERED', 'NONCLUSTERED')
        AND s.page_count > 100
        AND OBJECT_NAME(s.[object_id]) IN (XXXX)
```

Query can be also adjusted to filter by schema or prefix.

## Troubleshooting

**Job times out:**

- Increase the SQL command timeout
- Consider running REORGANIZE on specific indexes rather than ALL

**Permission errors:**

- Ensure the app pool identity has `db_ddladmin` role
- Check Azure SQL firewall rules if using DXP

**High CPU during execution:**

- Move to off-peak hours
- Add `WITH (ONLINE = ON)` option if using Enterprise edition

## Summary

This kind of job is especially useful in environments where custom tables are updated frequently but not covered by Optimizely’s internal maintenance routines. It’s a small addition that can yield big performance wins.

If you’re deploying to DXP, make sure the job is safe to run in production and doesn’t interfere with other scheduled tasks. It is usually good to run this job weekly during the low traffic hours.
