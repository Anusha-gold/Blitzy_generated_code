import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { withRequestDeadline, withResponseBody } from '../http/problem-details';
import type { LoginRequest, LoginResponse, RegisterRequest, RegisterResponse } from './models';

/**
 * The compact serialization of a signed JWT (RFC 7515 §7.1): three non-empty
 * base64url segments, the header, the payload and the signature, joined by
 * dots. The API signs every token it issues with HS256, so the signature
 * segment is never empty.
 */
const COMPACT_JWS = /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/;

/** An exact whole-second UTC instant, `yyyy-MM-ddTHH:mm:ssZ`, the form the API writes `expiresAt` in. */
const UTC_INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/;

/**
 * Whether `value` is a string holding an exact {@link UTC_INSTANT} that names
 * a real instant. `Date.parse` alone also reads a day past its month's end,
 * such as `2026-02-30T00:00:00Z`, or the hour `24`, by rolling it over, so the
 * parsed instant must print back, without its `.000` milliseconds, as the same
 * text.
 */
function isUtcInstant(value: unknown): value is string {
  if (typeof value !== 'string' || !UTC_INSTANT.test(value)) {
    return false;
  }
  const ms = Date.parse(value);
  return Number.isFinite(ms) && new Date(ms).toISOString().replace('.000Z', 'Z') === value;
}

/**
 * Whether a login 200 body has the shape the API issues: a JSON object whose
 * `token` is a {@link COMPACT_JWS} and whose `expiresAt` is an exact UTC
 * instant ({@link isUtcInstant}). Only the shape is checked here: the token's
 * signature, issuer, audience and lifetime, and the access it grants, are the
 * server's to judge on every request that carries it. An expiry already past
 * is accepted, because the server's clock is the authority on it.
 */
function isLoginResponse(body: unknown): body is LoginResponse {
  return (
    typeof body === 'object' &&
    body !== null &&
    !Array.isArray(body) &&
    'token' in body &&
    typeof body.token === 'string' &&
    COMPACT_JWS.test(body.token) &&
    'expiresAt' in body &&
    isUtcInstant(body.expiresAt)
  );
}

/**
 * Typed client for the anonymous guest-account endpoints under `/api/auth`:
 * registration and login.
 *
 * Every URL starts with the validated `apiBaseUrl` from {@link API_CONFIG}.
 * Each request object is sent unchanged as the JSON body, so the server alone
 * normalizes and validates it, and its 400, 401 and 409 problems reach the
 * page inline.
 *
 * Each request is bounded by `REQUEST_DEADLINE_MS` through
 * {@link withRequestDeadline}: one with no response by then is aborted and
 * fails as a status-0 `HttpErrorResponse`, which `toUiError` shows as "The
 * service is unreachable.". The login body is checked through
 * {@link withResponseBody} before it is emitted: a 200 whose `token` is not a
 * compact signed JWT or whose `expiresAt` is not an exact UTC instant fails as
 * an `HttpErrorResponse` carrying that 200, which `toUiError` shows as "The
 * service sent an unexpected response.". That check is of shape only; the
 * server alone verifies the token and authorizes what it grants. Apart from
 * that the returned observables are the `HttpClient` requests unchanged:
 * pages map their errors through `toUiError`, and nothing here retries or
 * logs them. A registration above all is never re-sent,
 * because one whose wait expired may still have created the account.
 *
 * There is no logout endpoint: logout only discards the in-memory session in
 * `AuthService`, and an issued token expires on its own within 60 minutes.
 */
@Injectable({ providedIn: 'root' })
export class AuthApiService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(API_CONFIG);

  /**
   * Registers a guest: `POST /api/auth/register`, anonymous.
   *
   * Errors, as `application/problem+json`:
   * - 400 when a field check fails, with field errors in `errors` keyed
   *   `email`, `password` or `fullName`;
   * - 400 when the body cannot bind, before any field check. `$` reports the
   *   body as a whole: one that is empty or `null`, is not a JSON object (`[]`
   *   included), or is malformed JSON the parser cannot attribute to one of
   *   the three members, such as `{"email":"a",`. `$.email`, `$.password` or
   *   `$.fullName` reports the member at which binding failed: a value of the
   *   wrong JSON type, such as a number for `email`, or malformed JSON at that
   *   member, such as `{"fullName":!`. `fieldErrorIds` maps each member key to
   *   that field's input;
   * - 409 when the email is already registered, ignoring letter case;
   * - 415 when `Content-Type` is absent or is not JSON, a bodyless request
   *   included. `HttpClient` sends this object body as JSON, so the page
   *   normally never sees it.
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0. It is never re-sent: aborting stops only the wait, so
   * the server may still have created the account, and resubmitting the same
   * email can then get 409.
   *
   * @param req - The guest's email, password and full name from the
   *   registration form, sent unchanged.
   * @returns The 201 body, `{ guestId }`, the new guest's id. It is typed but
   *   not checked, because no caller reads it.
   */
  register(req: RegisterRequest): Observable<RegisterResponse> {
    const url = `${this.config.apiBaseUrl}/auth/register`;
    return this.http.post<RegisterResponse>(url, req).pipe(withRequestDeadline(url));
  }

  /**
   * Logs a guest in: `POST /api/auth/login`, anonymous.
   *
   * Errors, as `application/problem+json`:
   * - 400 only when the body cannot bind, before any credential check. `$`
   *   reports the body as a whole: one that is empty or `null`, is not a JSON
   *   object (`[]` included), or is malformed JSON the parser cannot attribute
   *   to `email` or `password`, such as `{"email":"a"`. `$.email` or
   *   `$.password` reports the member at which binding failed: a value that is
   *   neither a string nor `null`, or malformed JSON at that member, such as
   *   `{"email":"a","password":!}`. `fieldErrorIds` maps each member key to
   *   that field's input. Login validates no field, so no other key occurs;
   * - 401 when the credentials do not match. Its `type`, `title`, `status`
   *   and `detail` are identical whether or not the account exists, and only
   *   its per-request `traceId` differs, so it never reveals one;
   * - 415 when `Content-Type` is absent or is not JSON, as for
   *   {@link AuthApiService.register}.
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0. A 200 whose body is not an object with a `token` in
   * the compact form of a signed JWT, three non-empty base64url segments
   * joined by dots, and an `expiresAt` that is exactly `yyyy-MM-ddTHH:mm:ssZ`
   * and names a real UTC instant fails as an `HttpErrorResponse` with that 200
   * status and no body, which `toUiError` shows as "The service sent an
   * unexpected response.". Only that shape is checked here: the token's
   * signature and what it grants are the server's to judge.
   *
   * The caller, `AuthService`, keeps the returned token in memory only.
   *
   * @param req - The email and password from the login form, sent unchanged.
   * @returns The checked 200 body: `token`, a bearer token valid for 60
   *   minutes, and `expiresAt`, its UTC expiry instant.
   */
  login(req: LoginRequest): Observable<LoginResponse> {
    const url = `${this.config.apiBaseUrl}/auth/login`;
    return this.http
      .post<unknown>(url, req, { observe: 'response' })
      .pipe(withRequestDeadline(url), withResponseBody(isLoginResponse));
  }
}
