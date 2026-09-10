---
layout: post
title:  "Building a Blazor Server UI Inside the Optimizely CMS 13 Admin Shell"
description: "What I learned shipping a Blazor Server UI inside Optimizely CMS 13 — Razor components in a NuGet package, the Blazor hub next to MapContent(), prerendering."
date:   2026-09-17 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-09-17-blazor-server-ui-optimizely-cms-13.png
   alt: "A Blazor Server execution detail view rendered inside the Optimizely CMS 13 admin shell"
   width: 1568
   height: 755
primary_tag: blazor
tags:
- episerver
- optimizely
- blazor
- .NET
- scheduled jobs
- nuget
- open-source
- OptiPowerTools
- OptiPowerTools.ScheduledJobsInsights
---

In the [previous post]({% post_url 2026-09-15-optipowertools-scheduledjobsinsights-execution-history-for-optimizely-scheduled-jobs %}) I released OptiPowerTools.ScheduledJobsInsights, which records what Optimizely's native scheduled jobs actually did. That post was about the package. This one is about its UI, which is Blazor Server rendered inside the CMS admin shell — why I went that way, and the three things that cost me the most time.

If you are thinking about putting Blazor into an Optimizely CMS 13 project, this is the post I would have wanted first.

## Why Blazor at all

The interface is a live console. Log lines arrive while a job runs, the status badge flips when it ends, metrics appear a moment later.

Building that over MVC and polling endpoints means hand-writing the diffing, the "only fetch lines newer than X" bookkeeping, and the DOM updates in JavaScript — against a CMS shell that already has opinions about the page. Blazor Server gives you the diffing for free, over a connection that is already open.

There was a second reason, and I will be honest about it. CMS 13 moved Optimizely onto .NET 10, and Blazor is now simply part of the platform the CMS runs on.

I wanted a real playground for it — something with authorization, prerendering, a database behind it and a hostile hosting environment, not a counter button. Job history turned out to fit: a list, a detail view, a live tail, and a settings screen.

## Shipping Razor components in a NuGet package

This is the one that will catch you first, and the failure mode is nasty because nothing looks broken.

A Blazor Server page needs the application to serve `_framework/blazor.server.js`. That file comes from the `Microsoft.AspNetCore.App.Internal.Assets` pack. The Web SDK references that pack only when the **application project itself** contains `.razor` files.

Components that live inside a NuGet package are invisible to that check. The SDK sees no `.razor` files, does not reference the pack, and the file is never served. Your page renders perfectly, looks finished, and does absolutely nothing. The only clue is a 404 for `blazor.server.js` in the browser console.

The fix is one property in the application's `.csproj`:

```xml
<PropertyGroup>
  <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
</PropertyGroup>
```

Applications that already have their own `.razor` files never hit this, which is exactly why it is easy to miss during development and easy to hit on a clean host.

Because the failure is silent, I made the package check for it at startup and log a named warning. If you ship Razor components in a library, do the same. A warning that names the property saves someone an afternoon.

## Mapping the Blazor hub next to MapContent()

This is the one that came in from a real site — CMS 13 with Commerce 15 — and it is the reason the setup instructions look the way they do.

Blazor Server needs a hub mapped at `/_blazor`. Map it in the wrong place in an Optimizely host and **every** Blazor request in the application fails with `AmbiguousMatchException`. Not just the package's pages. The host's own Blazor pages too.

Here is what happens. If the hub is published through a `UseEndpoints(...)` call of its own, before your own endpoint block, `MapContent()` then consolidates that already-published data source into its own snapshot. The hub ends up registered twice, on one route pattern. Nothing in the exception message names the package, or Optimizely, or `MapContent()`.

The shape that avoids the whole question is to map it on your own route builder, ahead of `MapContent()`:

```csharp
app.UseEndpoints(endpoints =>
{
    endpoints.MapOptiPowerToolsScheduledJobsInsights();

    endpoints.MapContent();
    endpoints.MapControllers();
});

app.UseOptiPowerToolsScheduledJobsInsights();
```

`Use…` is still needed — it applies migrations and runs the startup diagnostics — and it detects that the hub is already mapped rather than mapping it twice. Calling both is safe.

If your application already owns `/_blazor`, the package detects the existing mapping and skips its own. The one case detection cannot see is a host that maps its hub **after** this call, so there is an explicit escape hatch:

```csharp
options.MapBlazorHub = false;   // your application owns /_blazor
```

One detail worth keeping: endpoint matching runs *before* authentication. So this reproduces on an anonymous request, and you do not need a CMS login to diagnose it. That turns a "works on my machine, breaks in the client's environment" problem into a curl command.

## Prerendering and time zones

Timestamps in an admin UI should be in the reader's own time zone. In a prerendered Blazor app, that is harder than it sounds.

The usual approach is to ask the browser for its zone. But prerendering happens on the server, before any browser code runs, so the first paint is in the wrong zone and then flickers into place when the circuit connects. On a page that is mostly a table of timestamps, that flicker is the whole page moving.

What I did instead: the page writes the browser's IANA zone into a cookie, and the server reads that cookie and applies the zone during prerender. Every view after the first is correct with no flicker.

The trade-off is real and I decided to state it rather than hide it. The very first page view renders in UTC, and says so — the cookie is written by that page and does not exist until it has been served once. Nothing reloads to paper over that single occurrence.

One more decision that came out of this: only the *zone* follows the reader, not the format. Dates stay ISO-ordered and numbers stay invariant. `2026-08-19` never has to be read as either August or the 19th month, and a duration reads the same pasted into a support ticket as it did on the host that produced it.

## The live tail

The last piece is the part that justified Blazor in the first place.

A running execution re-reads itself every two seconds and appends only lines newer than those already shown, so following a long job stays cheap. Leave the page open while the job finishes and it catches up on its own — the badge flips, the duration fills in, and sections that did not exist yet appear.

There is one subtlety worth passing on. Log lines and metrics go through a buffered writer, while completion is written straight through. So the completion can land before the last buffered batch does.

The page reads once more a moment after the run ends, which picks up that final batch. Without it, a job would occasionally finish on screen with its last few log lines missing — the kind of bug that is very hard to reproduce deliberately and very obvious to the person watching.

## Wrapping up

Blazor inside the Optimizely CMS shell works, and for a live, stateful admin screen it is genuinely less work than the MVC equivalent. But the hosting details are where the time goes, not the components. Two of my three problems were about endpoints and build targets, not about Razor at all.

If you want to see the whole thing running, the package is [on GitHub](https://github.com/szolkowski/OptiPowerTools.ScheduledJobsInsights) and the [release post]({% post_url 2026-09-15-optipowertools-scheduledjobsinsights-execution-history-for-optimizely-scheduled-jobs %}) covers what it does.

Have you put Blazor into an Optimizely project? I would like to know what else bites. Let me know in the comments. Thank you for reading!
