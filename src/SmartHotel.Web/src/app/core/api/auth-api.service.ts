import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import type { LoginRequest, LoginResponse, RegisterRequest, RegisterResponse } from './models';

/**
 * Typed client for the anonymous guest-account endpoints under `/api/auth`:
 * registration and login.
 *
 * Every URL starts with the validated `apiBaseUrl` from {@link API_CONFIG}.
 * Each request object is sent unchanged as the JSON body, so the server alone
 * normalizes and validates it, and its 400, 401 and 409 problems reach the
 * page inline. The returned observables are the raw `HttpClient` requests:
 * pages map their errors through `toUiError`, and nothing here retries,
 * transforms or logs them.
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
   * - 400 when a field is invalid, with field errors in `errors` keyed
   *   `email`, `password` or `fullName`;
   * - 409 when the email is already registered, ignoring letter case;
   * - 415 when the body is not JSON.
   *
   * @param req - The guest's email, password and full name from the
   *   registration form, sent unchanged.
   * @returns The 201 body, `{ guestId }`, the new guest's id.
   */
  register(req: RegisterRequest): Observable<RegisterResponse> {
    return this.http.post<RegisterResponse>(`${this.config.apiBaseUrl}/auth/register`, req);
  }

  /**
   * Logs a guest in: `POST /api/auth/login`, anonymous.
   *
   * Error: 401 when the credentials do not match. Its `type`, `title`,
   * `status` and `detail` are identical whether or not the account exists, and
   * only its per-request `traceId` differs, so it never reveals one.
   *
   * The caller, `AuthService`, keeps the returned token in memory only.
   *
   * @param req - The email and password from the login form, sent unchanged.
   * @returns The 200 body: `token`, a bearer token valid for 60 minutes, and
   *   `expiresAt`, its UTC expiry instant.
   */
  login(req: LoginRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${this.config.apiBaseUrl}/auth/login`, req);
  }
}
