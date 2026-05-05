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

Screenshot baselines are platform-suffixed (`*-darwin.png`, `*-linux.png`) because fonts and antialiasing render differently across OSes. macOS regeneration covers `*-darwin.png`. To also refresh the Linux baselines that CI compares against, run Playwright inside the matching Docker image:

```bash
docker run --rm -v "$(pwd):/work" -w /work -e CI=1 \
  mcr.microsoft.com/playwright:v1.59.1-jammy bash -c '
    apt-get update -qq && apt-get install -y -qq ruby-full build-essential >/dev/null
    gem install bundler --silent
    bundle install --quiet
    npm ci --silent
    npx playwright test --update-snapshots --grep "visual:"
  '
```

Commit both `*-darwin.png` and `*-linux.png` together. Structural JSON snapshots are platform-agnostic and only need a single regeneration via `npm run test:update`.

The suite runs on every pull request via [.github/workflows/jekyll.yml](.github/workflows/jekyll.yml). Committed baselines live in `tests/__snapshots__/` (JSON for structural diffs, PNG for visual diffs).
