---
layout: post
title:  "Add Github pipelines/workflows to Episerver 12 project"
date:   2022-07-30 17:31:48 +0200
author: Stanisław Szołkowski
comments: true
published: true
tags:
- episerver
- optimizely
- github
- pipeline
- workflow
- ci
- devops
---

With Episerver 12 moving to .NET 5/6 civilization come to devops handling of Episerver projects and we can easily integrate modern CI approaches using pipelines.
For this example I will add simple building of [my Foundation fork](https://github.com/szolkowski/Foundation/tree/4289d35ea5feb49daf03ca18151c9e04fb910514) using Github version of pipelines called "workflows" on each pull request and on main branch.

## How does it works?

Pipeline/workflows are stored in code repositories as a file or files and depending on provider they might need to have specific name or they need to be put in specific folder like in Github.
Usually they are using `yaml` syntax, but some using something else for example Jenkins is using `groovy`.

Here I will describe how it is working on Github. Repository in Github can have multiple workflows (visible in Github under `Actions` tab) and they need to be stored in following directory : `.github/workflows/XXXXXX.yml`.
Every file in this folder is separate workflow/pipeline and will be executed depending on its configuration.

Below I will describe how pipeline file is build and what each step is doing. If you aren't interesting in it there is snipped of whole pipeline/workflow to copy in last section of this post.

First line of each file is just pipeline/workflow name that will be used in Github. It is basically label used across your Github repository.

```yaml
name: Episerver CI Build
```

Configuration that is deciding when workflow should be executed. Here it is executed on pushes to `main` branch and on each pull request.

```yaml
on:
  push:
    branches:
      - main
  pull_request:
    branches: [ '**' ]
```

This line let as choose on what runner pipeline will run. Full list of GitHub-hosted runners is available [here](https://docs.github.com/en/actions/using-github-hosted-runners/about-github-hosted-runners#supported-runners-and-hardware-resources).

```yaml
runs-on: ubuntu-latest
```

Now we are adding sequential steps needed for our build.
First one is prebuild action that will checkout our repository. Github have a lot of actions availabe in its [marketplace](https://github.com/marketplace?type=actions).

```yaml
    steps:
      - uses: actions/checkout@v2
        with:
          fetch-depth: 0
```

Here I am preparing base runner for our project, by using predefined actions that will set up `dotnet`, `NodeJS` and my shell command that will add Optimizely feed.

```yaml
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: 6.0.x

      - name: Setup Node
        uses: actions/setup-node@v2
        with:
          node-version: '14.18.2'

      - name: Setup Episerver/Optimizely nuget feed
        shell: pwsh
        run: dotnet nuget add source https://nuget.optimizely.com/feed/packages.svc -n Optimizely
```

Dotnet build part, just restoring packages and building Foundation app with `Release` configuration

```yaml
      - name: Restore dependencies
        run: dotnet restore

      - name: Dotnet build
        shell: pwsh
        run: dotnet build --no-restore --configuration Release
```

Java script part, installing `node_modules` (commonly referred as "black hole" by backend developers) and building JS.

```yaml
      - name: NPM install
        shell: pwsh
        working-directory: ./src/Foundation
        run: npm ci

      - name: NPM build
        shell: pwsh
        working-directory: ./src/Foundation
        run:  npm run dev
```

That is all, with these steps we can make sure that both our backend and frontend code build and we don't end up with broken main branch.

More details on Github workflows/pipelines can be found [here](https://docs.github.com/en/actions/using-workflows).

## Full workflow snippet and steps to do it in a simple pill

Here is code snipped for my simple workflow described above. Create file: `.github/workflows/ci-episerver.yml` (file name is up to you) and copy below code.

```yaml
name: Episerver CI Build

on:
  push:
    branches:
      - main
  pull_request:
    branches: [ '**' ]

jobs:
  build:

    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v2
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: 6.0.x

      - name: Setup Node
        uses: actions/setup-node@v2
        with:
          node-version: '14.18.2'

      - name: Setup Episerver/Optimizely nuget feed
        shell: pwsh
        run: dotnet nuget add source https://nuget.optimizely.com/feed/packages.svc -n Optimizely

      - name: Restore dependencies
        run: dotnet restore

      - name: Dotnet build
        shell: pwsh
        run: dotnet build --no-restore --configuration Release

      - name: NPM install
        shell: pwsh
        working-directory: ./src/Foundation
        run: npm ci

      - name: NPM build
        shell: pwsh
        working-directory: ./src/Foundation
        run:  npm run dev
```

Now the only thing to do is to "git commit & git push". Github workflows will be now building Episerver in both PR's and main branch, so no more broken code in our main branch!

I will extend this pipeline in future post with more interesting features!

SonarCloud integration: [Add SonarCloud/SonarQube to Episerver/Optimizely 12 project]({% post_url 2023-08-15-add-sonarcloud-to-epi-12-pipeline %})