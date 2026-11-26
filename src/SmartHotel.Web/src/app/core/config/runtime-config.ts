/**
 * Runtime configuration of the SPA, read from `/config.json` before Angular
 * bootstraps.
 *
 * The document is fetched with the global `fetch`, not Angular's HTTP client:
 * it is loaded before bootstrap, so no HTTP client exists yet, and the HTTP
 * interceptors (the loading counter and bearer-token attachment) must never
 * handle `/config.json`.
 *
 * Transport rule: the API base URL must use `https` in every environment.
 * Plain `http` is accepted only for `localhost` or `127.0.0.1`, and only when
 * `/config.json` declares `environment` exactly `Development`, which is how the
 * local Compose stack runs. Any other `environment` value, a missing, empty or
 * unrendered one included, applies the strict rule, so outside local
 * development the SPA never relies on an HTTP-to-HTTPS redirect.
 */
import { InjectionToken } from '@angular/core';

const API_PATH = '/api';

/** The validated API boundary the SPA calls and attaches bearer tokens to. */
export interface ApiConfig {
  /**
   * `apiOrigin` followed by `/api`, never with a trailing slash, for example
   * `http://localhost:8080/api`. Every API request URL starts with it.
   */
  apiBaseUrl: string;
  /**
   * The serialized origin of the API (`scheme://host[:port]`, host lower-cased
   * and default port elided), compared against each request URL's origin
   * before a bearer token is attached.
   */
  apiOrigin: string;
  /** Always `/api`: the path boundary within `apiOrigin` for token attachment. */
  apiPath: '/api';
}

/**
 * Injection token for the validated {@link ApiConfig}.
 *
 * It has no factory: `main.ts` provides it, and only it, with the value
 * {@link loadRuntimeConfig} resolved, so the configuration exists before any
 * service that injects it is created.
 */
export const API_CONFIG = new InjectionToken<ApiConfig>('API_CONFIG');

/**
 * Validates a parsed `/config.json` document and derives the API boundary.
 *
 * The rules apply in this order, and the first one that fails throws:
 * 1. The document is a JSON object (not `null` and not an array).
 * 2. `apiBaseUrl` is a string that parses as an absolute URL.
 * 3. The URL has no user name, password, query or fragment. A bare `?` or
 *    `#` counts as a query or fragment, even though it is empty.
 * 4. Its path, after one trailing `/` is trimmed, is exactly `/api`.
 * 5. Its scheme is `https`, or `http` when `environment` is exactly
 *    `Development` and the host is `localhost` or `127.0.0.1`.
 *
 * Error messages name the failed rule and never quote the rejected value.
 *
 * @param raw - The JSON-parsed body of `/config.json`.
 * @returns The normalized configuration: `apiBaseUrl` is rebuilt from the
 *   parsed origin, so any trailing slash in the input is dropped.
 * @throws Error when any rule above is not met.
 */
export function parseRuntimeConfig(raw: unknown): ApiConfig {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    throw new Error('Runtime configuration must be a JSON object.');
  }
  const record = raw as Record<string, unknown>;

  const apiBaseUrl = record['apiBaseUrl'];
  if (typeof apiBaseUrl !== 'string') {
    throw new Error('apiBaseUrl must be a string.');
  }

  let url: URL;
  try {
    // No base URL, so a relative value such as `/api` is rejected.
    url = new URL(apiBaseUrl);
  } catch {
    // The parser's own message quotes the input, so it is not passed on.
    throw new Error('apiBaseUrl is not an absolute URL.');
  }

  if (url.username !== '' || url.password !== '') {
    throw new Error('apiBaseUrl must not contain credentials.');
  }
  // `search` and `hash` are empty both for an absent component and for a bare
  // `?` or `#`, so presence is read from the serialized URL instead. There the
  // first `#` always starts the fragment and a `?` before it always starts the
  // query: the parser ends every other component at either character or
  // percent-encodes it.
  const fragmentStart = url.href.indexOf('#');
  const beforeFragment = fragmentStart === -1 ? url.href : url.href.slice(0, fragmentStart);
  if (beforeFragment.includes('?')) {
    throw new Error('apiBaseUrl must not contain a query.');
  }
  if (fragmentStart !== -1) {
    throw new Error('apiBaseUrl must not contain a fragment.');
  }

  const path = url.pathname.endsWith('/') ? url.pathname.slice(0, -1) : url.pathname;
  if (path !== API_PATH) {
    throw new Error('apiBaseUrl path must be exactly /api.');
  }

  if (url.protocol === 'http:') {
    const isDevelopment = record['environment'] === 'Development';
    const isLoopback = url.hostname === 'localhost' || url.hostname === '127.0.0.1';
    if (!isDevelopment || !isLoopback) {
      throw new Error(
        'apiBaseUrl must use https; http is allowed only in Development for localhost or 127.0.0.1.',
      );
    }
  } else if (url.protocol !== 'https:') {
    throw new Error('apiBaseUrl must use http or https.');
  }

  return { apiBaseUrl: url.origin + API_PATH, apiOrigin: url.origin, apiPath: API_PATH };
}

/**
 * Fetches `/config.json`, bypassing the HTTP cache, and validates it with
 * {@link parseRuntimeConfig}.
 *
 * The promise rejects on a network failure, a non-2xx status, a body that is
 * not valid JSON, or a configuration the validation rejects. A network failure
 * propagates unchanged; the other rejections carry messages that name the
 * failed check and never quote the response body. `main.ts` turns any
 * rejection into the boot-error state and does not bootstrap Angular.
 *
 * @returns The validated API configuration.
 */
export async function loadRuntimeConfig(): Promise<ApiConfig> {
  const response = await fetch('/config.json', { cache: 'no-store' });
  if (!response.ok) {
    throw new Error(`Runtime configuration request failed with HTTP status ${response.status}.`);
  }
  // The body is read before it is decoded, so a failure while reading it
  // propagates unchanged instead of being reported as invalid JSON.
  const text = await response.text();
  let body: unknown;
  try {
    body = JSON.parse(text);
  } catch {
    // The parser's own message can quote the body, so it is not passed on.
    throw new Error('Runtime configuration response must be valid JSON.');
  }
  return parseRuntimeConfig(body);
}
