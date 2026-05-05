# Readme

Repository for blog [https://szolkowski.github.io](https://szolkowski.github.io).

## Useful commands

### Running Jekyll locally

Run locally:
`bundle exec jekyll serve`

Run locally with live changes update:
`bundle exec jekyll serve --livereload`

### Generating tag pages

After adding new tags to posts, run:

`ruby _gentags.rb`

### Updating Jekyll

`gem update jekyll`

### Running UI regression tests

Tests are Playwright + TypeScript and live in `tests/`. They build the site
into an isolated `_site_test/` and serve it on `localhost:4321`, so they don't
clash with `bundle exec jekyll serve`. Requires Node 18+.

First-time setup:

```bash
npm install
npx playwright install chromium
```

Run the suite:

```bash
npm test          # all 24 tests across 7 page types
npm run report    # open the last HTML report
```

After an intentional UI / SEO change, refresh the local (darwin) baselines:

```bash
npm run test:update
```

Screenshot baselines are platform-suffixed (`*-darwin.png`, `*-linux.png`) because fonts and antialiasing render differently across OSes. macOS regeneration covers `*-darwin.png`. To also refresh the Linux baselines that CI compares against, run Playwright inside the same Docker image CI uses:

```bash
docker run --rm --platform linux/amd64 \
  -v "$(pwd):/work" -w /work \
  mcr.microsoft.com/playwright:v1.59.1-jammy bash -c '
    set -e
    apt-get update -qq
    apt-get install -y -qq --no-install-recommends \
      libyaml-0-2 build-essential libssl-dev ruby-full
    gem install bundler --silent --no-document
    bundle install --quiet
    npm ci --silent
    npx playwright test --update-snapshots --project=chromium
  '
```

Notes:

- `--platform linux/amd64` is required on Apple Silicon Macs — without it, Docker pulls the ARM image and screenshots come out subtly different from x86_64 CI baselines.
- The four apt packages mirror [.github/workflows/ci.yml](.github/workflows/ci.yml). Drop any of `libyaml-0-2` / `libssl-dev` and `bundle install` fails on the Jammy slim image.
- Image tag (`v1.59.1-jammy`) must match `@playwright/test` in [package.json](package.json). Bump together.
- The command refreshes **all** snapshots, not just visuals — handy when a content change (e.g. adding a Person headshot file) flips a JSON-LD value the structural baseline pinned. JSON snapshots are platform-agnostic, so this also covers what `npm run test:update` would regenerate locally.
- First run pulls ~700 MB and installs Ruby + gems (~3–5 min). Subsequent runs hit the Docker layer cache.

Commit both `*-darwin.png` and `*-linux.png` together so the next CI run sees a consistent baseline pair.

The suite runs on every pull request via [.github/workflows/jekyll.yml](.github/workflows/jekyll.yml). Committed baselines live in `tests/__snapshots__/` (JSON for structural diffs, PNG for visual diffs).
