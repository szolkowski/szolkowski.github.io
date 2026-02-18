---
layout: post
title:  "Memory-Efficient Catalog Traversal in Optimizely Commerce. Part 1: Building the Service"
date:   2026-02-18 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-02-18-Memory-Efficient-Catalog-Traversal-in-Optimizely-Commerce-Part-1-Building-the-Service.png
   alt: "Memory-Efficient Catalog Traversal in Optimizely Commerce. Part 1: Building the Service"
tags:
- episerver
- optimizely
- performance
- commerce
- catalog
- memory-optimization
- .NET
---

If you've worked with Optimizely Commerce for any length of time, you've probably faced this scenario: you need to process an entire product catalog in a scheduled job. Maybe you're syncing to an external PIM system, exporting data for analytics, or performing bulk updates. The straightforward approach of loading all products into memory works fine for small catalogs, but once you hit thousands of products, things start to get uncomfortable.

In this post, I'll show you how to build a memory-efficient catalog traversal service using streaming and lazy evaluation. Instead of loading everything at once, we'll process items one at a time as we traverse the catalog hierarchy. In the next post, I'll show practical examples of using this service in real-world scheduled jobs.

## The Problem

Optimizely Commerce organizes products in a hierarchical structure with catalogs, nodes (categories), products, and variants. When you need to process all items, you typically face a few options:

1. **Load everything at once** - Use `GetDescendants()` or similar methods to get all items in one go. This is simple but can consume hundreds of megabytes of memory for large catalogs.

2. **Query individually** - Loop through the hierarchy manually, making separate requests for each level. This works but can be slow and complex to implement correctly.

3. **Process in batches** - Try to load chunks of products at a time. This helps with memory but adds complexity around pagination and hierarchy traversal.

Each approach has its drawbacks:

- Memory consumption becomes problematic with large catalogs
- Deep catalog hierarchies can cause performance degradation
- Circular references in the catalog structure can cause infinite loops
- There's no easy way to filter for only changed items (incremental sync)

What we really need is a way to traverse the catalog structure efficiently while keeping memory usage low, handling edge cases gracefully, and supporting incremental updates.

## Why Streaming Matters

The solution lies in using `IEnumerable<>` with the `yield return` pattern. If you're familiar with LINQ, you already know this pattern—it's how LINQ methods like `Where()` and `Select()` work under the hood.

Here's the key insight: when you return `IEnumerable<T>` and use `yield return`, you're not creating a list in memory. Instead, you're creating an iterator that produces items one at a time as they're requested. This is called **lazy evaluation**.

```csharp
// This doesn't load all products into memory
public IEnumerable<ProductContent> GetProducts()
{
    foreach (var product in catalog)
    {
        yield return product; // One item at a time
    }
}

// Items are only loaded as you iterate
foreach (var product in GetProducts())
{
    ProcessProduct(product); // Only one product in memory here
}
```

For scheduled jobs processing large catalogs, this pattern is perfect. You traverse the hierarchy, yield items as you find them, and the caller processes them one at a time. Memory stays low, and you can handle catalogs of any size.

## The Solution: CatalogTraversalService

Let's build a service that traverses the Optimizely Commerce catalog hierarchy using a breadth-first search (BFS) algorithm, yielding items as it goes. Here's what we need:

**Key Design Decisions:**

- Use a queue-based BFS to traverse the hierarchy systematically
- Track visited nodes to detect and prevent circular references
- Support filtering by last updated date for incremental syncs
- Return a generic interface so the service works with any catalog content type
- Support cancellation tokens for long-running operations

### The Interface and Models

First, let's define our contracts:

```csharp
/// <summary>
/// Marker interface for items returned during catalog traversal.
/// Products and variants from your catalog should implement this interface.
/// </summary>
public interface ICatalogTraversalItem
{
    DateTime? LastUpdated { get; set; }
}

/// <summary>
/// Options for controlling catalog traversal behavior.
/// </summary>
public class CatalogTraversalOptions
{
    /// <summary>
    /// Optional: Name of the catalog to traverse. If null, all catalogs are processed.
    /// </summary>
    public string? CatalogName { get; set; }

    /// <summary>
    /// Optional: Specific catalog content reference to traverse. Takes priority over CatalogName.
    /// </summary>
    public ContentReference? CatalogLink { get; set; }

    /// <summary>
    /// Optional: Only return items updated after this date. Useful for incremental syncs.
    /// </summary>
    public DateTime? LastUpdated { get; set; }
}

/// <summary>
/// Service for traversing Optimizely Commerce catalog hierarchies.
/// </summary>
public interface ICatalogTraversalService
{
    /// <summary>
    /// Traverses the catalog hierarchy and returns matching products and variants as a stream.
    /// Items are yielded one at a time for memory-efficient processing.
    /// </summary>
    IEnumerable<ICatalogTraversalItem> GetAllProducts(
        CatalogTraversalOptions options,
        CancellationToken cancellationToken = default);
}
```

Your product and variant types need to implement `ICatalogTraversalItem`. For example:

```csharp
public class GenericProduct : ProductContent, ICatalogTraversalItem
{
    public virtual DateTime? LastUpdated { get; set; }
    // ... other properties
}

public class GenericVariant : VariationContent, ICatalogTraversalItem
{
    public virtual DateTime? LastUpdated { get; set; }
    // ... other properties
}
```

### The Core Implementation

Now for the main service. I'll show it in parts with detailed comments:

{% include code-modal.html
   id="CatalogTraversalService"
   lang="csharp"
   file="code-snippets/CatalogTraversalService.cs"
%}

### Registering the Service

Don't forget to register the service in your DI container:

```csharp
services.AddTransient<ICatalogTraversalService, CatalogTraversalService>();
```

## How It Works Under the Hood

Let's break down the key components that make this work:

### Breadth-First Search (BFS)

The service uses a queue-based BFS algorithm to traverse the catalog hierarchy. This approach has several benefits:

1. **Systematic traversal** - Process all items at one level before going deeper
2. **Memory efficient** - Only store references to unprocessed nodes, not the entire tree
3. **Predictable order** - Items are processed in a logical hierarchy order

```csharp
// Start with root catalog(s)
var queue = new Queue<ContentReference>();
queue.Enqueue(catalogRoot);

while (queue.Count > 0)
{
    var current = queue.Dequeue();
    
    // Process children
    foreach (var child in GetChildren(current))
    {
        if (child is Node)
            queue.Enqueue(child); // Add nodes for further processing
        else if (child is Product)
            yield return child;   // Yield products immediately
    }
}
```

### Circular Reference Detection

Catalog structures can sometimes have circular references (a node referencing itself or creating a loop). The `HashSet<ContentReference>` tracks visited nodes and prevents infinite loops:

```csharp
var visited = new HashSet<ContentReference>();

if (!visited.Add(current))
{
    // Already visited - circular reference detected
    continue;
}
```

### Lazy Evaluation with Yield Return

This is the heart of the memory efficiency. When you use `yield return`, the method doesn't run all at once. Instead, it pauses at each `yield` and resumes when the next item is requested:

```csharp
// This method doesn't execute until you start iterating
public IEnumerable<Product> GetProducts()
{
    foreach (var product in catalog)
    {
        yield return product;
    }
}
```

This means:

- Processing starts immediately (no waiting for all items to load)
- You can stop early without processing everything
- Cancellation tokens work naturally

### Date Filtering

The `LastUpdated` filter enables incremental syncs. It checks each item's timestamp and only yields items that changed after the specified date:

```csharp
if (filterLastUpdated == null)
    return true; // No filter - include everything

return item.LastUpdated > filterLastUpdated; // Only newer items
```

## What's Next?

You now have a complete, production-ready catalog traversal service. The implementation handles all the tricky parts: memory efficiency, circular references, flexible filtering, and cancellation support.

But how do you actually use this in your scheduled jobs? What patterns work best for different scenarios? In my next post, I'll walk through real-world examples including full catalog exports, incremental syncs, error handling strategies, and progress reporting.

## Summary

Processing large Optimizely Commerce catalogs efficiently requires a streaming approach. By using `IEnumerable<>` with `yield return`, we've built a service that:

- Traverses catalogs of any size with minimal memory footprint
- Detects and prevents circular references
- Supports incremental synchronization via date filtering
- Provides clean separation between traversal and processing logic
- Works seamlessly with cancellation tokens

The service is ready to be integrated into your scheduled jobs. In the next post, I'll show you exactly how to do that with practical, copy-paste-ready examples.

Do you have questions about the implementation or suggestions for improvements? Let me know in the comments!

Thank you for reading, and stay tuned for Part 2 where we put this service to work.

## This Post is Part of a Series

- Part 1: Building the Service - (this post)
- Part 2: Real-World Scheduled Job Patterns - wait for the future release!
- Part 3: Hangfire Integration - wait for the future release!
