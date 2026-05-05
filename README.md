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

After an intentional UI / SEO change, refresh baselines and commit them:

```bash
npm run test:update
```

The suite runs on every pull request via [.github/workflows/jekyll.yml](.github/workflows/jekyll.yml). Committed baselines live in `tests/__snapshots__/` (JSON for structural diffs, PNG for visual diffs).
