#!/usr/bin/env node
// Convert Lighthouse CI's .lighthouseci/ output into a Markdown summary
// suitable for GitHub Actions step summary and a sticky PR comment.
// Mirrors the style of scripts/ci-test-summary.mjs.

import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const lhciDir = resolve(process.cwd(), '.lighthouseci');

function tryReadJson(file, fallback) {
  try {
    return JSON.parse(readFileSync(resolve(lhciDir, file), 'utf8'));
  } catch {
    return fallback;
  }
}

let lhrFiles = [];
try {
  lhrFiles = readdirSync(lhciDir).filter(
    (f) => f.startsWith('lhr-') && f.endsWith('.json'),
  );
} catch {
  console.log(
    '### Lighthouse CI\n\n:warning: No `.lighthouseci/` directory found. lhci likely never ran.\n',
  );
  process.exit(0);
}

if (lhrFiles.length === 0) {
  console.log(
    '### Lighthouse CI\n\n:warning: No `lhr-*.json` reports in `.lighthouseci/`.\n',
  );
  process.exit(0);
}

const reports = lhrFiles.map((f) =>
  JSON.parse(readFileSync(resolve(lhciDir, f), 'utf8')),
);
const links = tryReadJson('links.json', {});
const assertions = tryReadJson('assertion-results.json', []);

const tidyUrl = (url) => {
  try {
    return new URL(url).pathname || '/';
  } catch {
    return url;
  }
};

const emoji = (score) => {
  if (score == null) return ':heavy_minus_sign:';
  if (score >= 0.9) return ':white_check_mark:';
  if (score >= 0.5) return ':warning:';
  return ':x:';
};

const pct = (score) => (score == null ? '—' : `${Math.round(score * 100)}`);

const errors = assertions.filter((a) => a.level === 'error');
const warns = assertions.filter((a) => a.level === 'warn');

const sha = (process.env.GITHUB_SHA ?? '').slice(0, 7);
const runUrl =
  process.env.GITHUB_SERVER_URL &&
  process.env.GITHUB_REPOSITORY &&
  process.env.GITHUB_RUN_ID
    ? `${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`
    : null;
const artifactUrl = process.env.ARTIFACT_URL || null;

const headline =
  errors.length > 0
    ? `:x: **${errors.length} budget violation${errors.length === 1 ? '' : 's'}** across ${reports.length} URL${reports.length === 1 ? '' : 's'}`
    : warns.length > 0
      ? `:warning: **${warns.length} warning${warns.length === 1 ? '' : 's'}** (budgets met) across ${reports.length} URL${reports.length === 1 ? '' : 's'}`
      : `:white_check_mark: **All budgets met** across ${reports.length} URL${reports.length === 1 ? '' : 's'}`;

const lines = [];
lines.push('### Lighthouse CI');
lines.push('');
lines.push(headline);
lines.push('');
lines.push(
  '| Page | Performance | Accessibility | Best&nbsp;Practices | SEO | Report |',
);
lines.push('|---|---:|---:|---:|---:|---|');
for (const r of reports) {
  const path = tidyUrl(r.finalDisplayedUrl || r.requestedUrl);
  const cats = r.categories ?? {};
  const reportUrl = links[r.requestedUrl];
  const reportLink = reportUrl ? `[view](${reportUrl})` : '—';
  lines.push(
    `| \`${path}\` | ${emoji(cats.performance?.score)} ${pct(cats.performance?.score)} | ${emoji(cats.accessibility?.score)} ${pct(cats.accessibility?.score)} | ${emoji(cats['best-practices']?.score)} ${pct(cats['best-practices']?.score)} | ${emoji(cats.seo?.score)} ${pct(cats.seo?.score)} | ${reportLink} |`,
  );
}

if (errors.length + warns.length > 0) {
  lines.push('');
  lines.push('<details><summary>Assertion details</summary>');
  lines.push('');
  for (const a of [...errors, ...warns]) {
    const path = tidyUrl(a.url ?? '');
    const verb = a.level === 'error' ? ':x:' : ':warning:';
    lines.push(
      `- ${verb} **${a.auditId}** on \`${path}\` — value \`${a.actual}\`, expected \`${a.expected}\` (${a.operator ?? ''})`,
    );
  }
  lines.push('');
  lines.push('</details>');
}

const footerBits = [];
if (sha) footerBits.push(`commit \`${sha}\``);
if (runUrl) footerBits.push(`[workflow run](${runUrl})`);
if (artifactUrl) footerBits.push(`[reports artifact](${artifactUrl})`);
if (footerBits.length > 0) {
  lines.push('');
  lines.push(`<sub>${footerBits.join(' · ')}</sub>`);
}

console.log(lines.join('\n'));
