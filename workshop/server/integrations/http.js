/** Bounded HTTP transport shared by optional integrations. No external dependencies. */
export function checkedUrl(value, protocols = ['http:', 'https:']) {
  const url = new URL(value);
  if (!protocols.includes(url.protocol) || url.username || url.password) throw new Error('Unsupported endpoint URL');
  return url;
}

export function boundedNumber(value, fallback, min, max) {
  if (value === undefined) return fallback;
  if (!Number.isFinite(value) || value < min || value > max) throw new Error(`Expected number between ${min} and ${max}`);
  return value;
}

export async function postJson(url, payload, {
  timeoutMs = 5000, maxBytes = 65536, bearerToken, fetchImpl = globalThis.fetch
} = {}) {
  checkedUrl(url);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new Error('Integration deadline exceeded')), timeoutMs);
  // Keep the deadline active while reading the body, not just until headers arrive.
  try {
    const headers = { 'content-type': 'application/json', accept: 'application/json' };
    if (bearerToken) headers.authorization = `Bearer ${bearerToken}`;
    const response = await fetchImpl(url, {
      method: 'POST', headers, body: JSON.stringify(payload), signal: controller.signal, redirect: 'error'
    });
    if (!response.ok) {
      await response.body?.cancel();
      throw new Error(`Integration HTTP ${response.status}`);
    }
    const declaredLength = Number(response.headers.get('content-length'));
    if (declaredLength > maxBytes) {
      await response.body?.cancel();
      throw new Error('Integration response too large');
    }
    if (!response.body) throw new Error('Integration response is empty');
    const reader = response.body.getReader();
    const chunks = [];
    let bytes = 0;
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        bytes += value.byteLength;
        if (bytes > maxBytes) throw new Error('Integration response too large');
        chunks.push(Buffer.from(value));
      }
    } catch (error) {
      await reader.cancel().catch(() => {});
      throw error;
    } finally { reader.releaseLock(); }
    if (controller.signal.aborted) throw new Error('Integration deadline exceeded');
    return JSON.parse(Buffer.concat(chunks, bytes).toString('utf8'));
  } catch (error) {
    if (controller.signal.aborted) throw new Error('Integration deadline exceeded');
    throw error;
  } finally { clearTimeout(timer); }
}
