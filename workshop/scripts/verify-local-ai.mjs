import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { OllamaAdvisor } from '../server/integrations/ollama.js';
import { loadConfig } from '../server/config.js';
import { WorkshopController } from '../server/controller.js';

// Opt-in real-model verification, deliberately excluded from npm test.
// No request can grant a robot lease or start a production line.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const settings = JSON.parse(await readFile(path.join(root, 'config.ai.local.json'), 'utf8'));
const model = process.env.VERIFY_OLLAMA_MODEL || settings.model;
const reportDirectory = path.join(root, 'data/ollama-logs');
await mkdir(reportDirectory, { recursive: true });
const controller = new WorkshopController(await loadConfig({ DATA_DIR: reportDirectory }));
const advisor = new OllamaAdvisor({ model, url: 'http://127.0.0.1:11434', timeoutMs: 90000 });
const request = {
  task: 'Сколько роботов на линии, сколько готовых панелей выпущено и почему сейчас конвейер стоит?',
  telemetry: controller.snapshot(controller.robots.get('training-arm')),
};
await writeFile(path.join(reportDirectory, 'advisor-request.json'), JSON.stringify(request, null, 2), 'utf8');
const started = performance.now();
const result = await advisor.advise(request.telemetry, request.task);
const report = { checkedAt: new Date().toISOString(), elapsedMs: Math.round(performance.now() - started), model, result, adapter: advisor.snapshot() };
await writeFile(path.join(reportDirectory, 'advisor-verification.json'), JSON.stringify(report, null, 2), 'utf8');
await controller.close();
assert.equal(result.ok, true, result.error);
assert.equal(result.source, 'ollama');
assert.equal(result.executable, false);
assert.match(result.summary, /[А-Яа-яЁё]/, 'The real model must return a Russian explanation.');
const visibleText = [result.title, result.summary, ...result.notes].join('\n');
assert.doesNotMatch(visibleText, /production\.|completed|running|stopReason|beltSpeed|stationCount|отсутствия оператора/i, 'Operator-facing text must use Russian state labels without assuming a missing human.');
assert.equal(advisor.snapshot().status, 'READY');
console.log(JSON.stringify(report, null, 2));
