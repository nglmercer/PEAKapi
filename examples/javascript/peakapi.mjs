// Talk to a running PEAKapi.
//
//   PEAK_API_TOKEN=... node examples/javascript/peakapi.mjs health
//   PEAK_API_TOKEN=... node examples/javascript/peakapi.mjs items
//   PEAK_API_TOKEN=... node examples/javascript/peakapi.mjs spawn
//
// The token can also be read from the PEAKapi configuration with
// PEAK_CONFIG=/path/to/org.peak.rest-api.cfg.

import { readFileSync } from 'node:fs';

const baseUrl = process.env.PEAK_API_URL ?? 'http://127.0.0.1:17454';

function readToken() {
  if (process.env.PEAK_API_TOKEN) return process.env.PEAK_API_TOKEN.trim();

  const candidates = [
    process.env.PEAK_CONFIG,
    `${process.env.HOME}/.local/share/Steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg`,
  ].filter(Boolean);

  for (const path of candidates) {
    try {
      const text = readFileSync(path, 'utf8');
      const match = text.match(/^AuthenticationToken\s*=\s*(.+)$/m);
      if (match) return match[1].trim();
    } catch {
      // Try the next candidate.
    }
  }

  throw new Error('Set PEAK_API_TOKEN or PEAK_CONFIG.');
}

const token = readToken();

async function peakGet(path) {
  const response = await fetch(`${baseUrl}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return { status: response.status, body: await response.json() };
}

async function peakPost(path, body) {
  const response = await fetch(`${baseUrl}${path}`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
      'Idempotency-Key': crypto.randomUUID(),
    },
    body: JSON.stringify(body),
  });
  return { status: response.status, body: await response.json() };
}

const command = process.argv[2] ?? 'health';

if (command === 'health') {
  const { status, body } = await peakGet('/health');
  console.log(status, JSON.stringify(body, null, 2));
} else if (command === 'capabilities') {
  const { status, body } = await peakGet('/api/capabilities');
  console.log(status, JSON.stringify(body, null, 2));
} else if (command === 'items') {
  const { status, body } = await peakGet('/api/catalog/items');
  console.log(status, JSON.stringify(body, null, 2));
} else if (command === 'spawn') {
  // Take an id that the running game really has.
  const catalog = await peakGet('/api/catalog/items');
  const item = catalog.body.items?.find((entry) => entry.spawnable);
  if (!item) {
    console.error('No spawnable item. Is a level loaded?');
    process.exit(1);
  }

  const result = await peakPost('/api/spawn/item', {
    itemId: item.id,
    quantity: 1,
    position: { mode: 'random_player' },
  });
  console.log(result.status, JSON.stringify(result.body, null, 2));
} else {
  console.error(`Unknown command: ${command}`);
  process.exit(1);
}
