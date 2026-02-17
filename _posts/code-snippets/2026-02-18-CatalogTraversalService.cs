public class CatalogTraversalService : ICatalogTraversalService
{
    private readonly IContentLoader _contentLoader;
    private readonly ILogger<CatalogTraversalService> _logger;
    private readonly ReferenceConverter _referenceConverter;

    public CatalogTraversalService(
        IContentLoader contentLoader,
        ILogger<CatalogTraversalService> logger,
        ReferenceConverter referenceConverter)
    {
        _contentLoader = contentLoader;
        _logger = logger;
        _referenceConverter = referenceConverter;
    }

    public IEnumerable<ICatalogTraversalItem> GetAllProducts(
        CatalogTraversalOptions options,
        CancellationToken cancellationToken = default)
    {
        // Initialize a queue with the starting catalog(s)
        var queue = InitializeCatalogQueue(options.CatalogName, options.CatalogLink);
        
        // Track visited nodes to prevent circular references
        var visited = new HashSet<ContentReference>();
        
        var itemCount = 0;
        var stopwatch = Stopwatch.StartNew();

        // Breadth-first traversal of the catalog hierarchy
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var current = queue.Dequeue();

            // Prevent infinite loops from circular references
            if (!visited.Add(current))
            {
                _logger.LogWarning("Circular reference detected at {ContentLink}", current);
                continue;
            }

            // Get all children of the current node
            var children = _contentLoader.GetChildren<IContent>(current);
            
            foreach (var child in children)
            {
                switch (child)
                {
                    // If it's a node (category), add to queue for further traversal
                    case NodeContent node:
                        queue.Enqueue(node.ContentLink);
                        break;

                    // If it's a product, process and yield it
                    case ProductContent product when product is ICatalogTraversalItem:
                        itemCount++;
                        if (ShouldIncludeItem(product, options.LastUpdated))
                        {
                            yield return (ICatalogTraversalItem)product;
                        }
                        break;

                    // If it's a variant, process and yield it
                    case VariationContent variant when variant is ICatalogTraversalItem:
                        itemCount++;
                        if (ShouldIncludeItem(variant, options.LastUpdated))
                        {
                            yield return (ICatalogTraversalItem)variant;
                        }
                        break;

                    default:
                        _logger.LogDebug(
                            "Skipping unsupported content type: {ContentType} at {ContentLink}",
                            child.GetOriginalType().Name,
                            child.ContentLink);
                        break;
                }
            }
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Catalog traversal completed: {ItemCount} items processed in {ElapsedMs}ms",
            itemCount,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Initializes the traversal queue with the starting catalog content references.
    /// </summary>
    private Queue<ContentReference> InitializeCatalogQueue(
        string? catalogName,
        ContentReference? catalogLink)
    {
        var queue = new Queue<ContentReference>();

        // If a specific catalog link is provided, use only that one
        if (!ContentReference.IsNullOrEmpty(catalogLink))
        {
            if (_contentLoader.TryGet<CatalogContentBase>(catalogLink, out var catalog))
            {
                _logger.LogInformation(
                    "Found catalog '{CatalogName}' at {ContentLink}",
                    catalog.Name,
                    catalog.ContentLink);
                queue.Enqueue(catalogLink!);
            }
            else
            {
                _logger.LogWarning("No catalog found for {ContentLink}", catalogLink);
            }

            return queue;
        }

        // Otherwise, find all catalogs (optionally filtered by name)
        var rootLink = _referenceConverter.GetRootLink();
        foreach (var rootContent in _contentLoader.GetChildren<CatalogContentBase>(rootLink))
        {
            if (rootContent is { } catalog &&
                (catalogName == null || 
                 string.Equals(catalog.Name, catalogName, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation(
                    "Found catalog '{CatalogName}' at {ContentLink}",
                    catalog.Name,
                    catalog.ContentLink);
                queue.Enqueue(catalog.ContentLink);
            }
        }

        if (queue.Count == 0)
        {
            _logger.LogWarning(
                "No catalogs found matching '{CatalogName}'",
                catalogName ?? "any");
        }

        return queue;
    }

    /// <summary>
    /// Determines if an item should be included based on the last updated filter.
    /// </summary>
    private static bool ShouldIncludeItem(
        IContent content,
        DateTime? filterLastUpdated)
    {
        // If no date filter is specified, include all items
        if (filterLastUpdated == null)
        {
            return true;
        }

        // Try to get the LastUpdated property via reflection
        // This assumes your catalog items have a LastUpdated property
        var lastUpdatedProperty = content.GetType()
            .GetProperty("LastUpdated");
        
        if (lastUpdatedProperty == null)
        {
            return false;
        }

        var lastUpdated = lastUpdatedProperty.GetValue(content) as DateTime?;
        return lastUpdated > filterLastUpdated;
    }
}