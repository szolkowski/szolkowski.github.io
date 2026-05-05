#!/usr/bin/env node
// Convert Playwright's results.json into a Markdown summary suitable for
// GitHub Actions step summary and a sticky PR comment.

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const reportPath = resolve(process.cwd(), 'playwright-report/results.json');

let report;
try {
  report = JSON.parse(readFileSync(reportPath, 'utf8'));
} catch (err) {
  console.error(`### UI Regression Tests\n\n:warning: No \`results.json\` found at \`${reportPath}\`. Tests likely never ran.\n`);
  process.exit(0);
}

const specs = collectSpecs(report.suites ?? []);
const stats = report.stats ?? {};
const passed = specs.filter((s) => s.status === 'passed').length;
const failed = specs.filter((s) => s.status === 'failed').length;
const flaky = specs.filter((s) => s.status === 'flaky').length;
const skipped = specs.filter((s) => s.status === 'skipped').length;
const durationSec = ((stats.duration ?? 0) / 1000).toFixed(1);

const sha = (process.env.GITHUB_SHA ?? '').slice(0, 7);
const runUrl =
  process.env.GITHUB_SERVER_URL && process.env.GITHUB_REPOSITORY && process.env.GITHUB_RUN_ID
    ? `${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`
    : null;

const headline =
  failed > 0
    ? `:x: **${failed} of ${specs.length} tests failed** (${durationSec}s)`
    : flaky > 0
      ? `:warning: **${specs.length} tests passed with ${flaky} flaky** (${durationSec}s)`
      : `:white_check_mark: **All ${specs.length} tests passed** (${durationSec}s)`;

const lines = [];
lines.push('### :performing_arts: UI Regression Tests');
lines.push('');
lines.push(headline);
lines.push('');
lines.push('| Status | Count |');
lines.push('| --- | ---: |');
lines.push(`| :white_check_mark: Passed | ${passed} |`);
lines.push(`| :x: Failed | ${failed} |`);
if (flaky > 0) lines.push(`| :warning: Flaky | ${flaky} |`);
if (skipped > 0) lines.push(`| :fast_forward: Skipped | ${skipped} |`);
lines.push('');

if (failed > 0) {
  lines.push('#### Failures');
  lines.push('');
  for (const spec of specs.filter((s) => s.status === 'failed')) {
    const errMsg = stripAnsi(spec.error ?? '').trim().slice(0, 600);
    lines.push(`<details><summary><code>${escapeHtml(spec.file)} › ${escapeHtml(spec.title)}</code></summary>`);
    lines.push('');
    lines.push('```');
    lines.push(errMsg || '(no error message captured)');
    lines.push('```');
    lines.push('</details>');
    lines.push('');
  }
}

lines.push('<sub>');
const refs = [];
if (runUrl) refs.push(`[Workflow run](${runUrl})`);
if (failed > 0 && runUrl) refs.push(`[Download HTML report](${runUrl}#artifacts)`);
if (sha) refs.push(`commit \`${sha}\``);
lines.push(refs.join(' · '));
lines.push('</sub>');

process.stdout.write(lines.join('\n') + '\n');

// ---

function collectSpecs(suites, file = '') {
  const out = [];
  for (const suite of suites) {
    const suiteFile = suite.file || file;
    for (const spec of suite.specs ?? []) {
      const test = (spec.tests ?? [])[0];
      const result = (test?.results ?? [])[0];
      const status = !test
        ? 'skipped'
        : test.results.length > 1 && spec.ok
          ? 'flaky'
          : spec.ok
            ? 'passed'
            : 'failed';
      out.push({
        file: suiteFile,
        title: [...(suite.title ? [suite.title] : []), spec.title].filter(Boolean).join(' › '),
        status,
        error: result?.error?.message ?? result?.errors?.[0]?.message ?? '',
      });
    }
    if (suite.suites) out.push(...collectSpecs(suite.suites, suiteFile));
  }
  return out;
}

function stripAnsi(s) {
  return s.replace(/\[[0-9;]*m/g, '');
}

function escapeHtml(s) {
  return String(s).replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
}
