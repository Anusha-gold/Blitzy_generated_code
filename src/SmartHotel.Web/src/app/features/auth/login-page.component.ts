import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

const REGISTERED_NOTICE = 'Account created. Log in to continue.';
const SESSION_EXPIRED_NOTICE = 'Your session has expired. Log in again.';
const WRONG_CREDENTIALS_TITLE = 'Email or password is incorrect.';
const DEFAULT_RETURN_URL = '/search';

/**
 * The login page, route `login`, with the query parameters `returnUrl`,
 * `registered` and `reason`.
 *
 * Notice: `registered=1` shows "Account created. Log in to continue." and
 * `reason=session-expired` shows "Your session has expired. Log in again.";
 * `registered=1` wins when both are present. The query is observed rather than
 * read once, because the router reuses this component when only the query
 * changes.
 *
 * Email hand-off: with `registered=1`, the email the register page left in
 * `AuthService.pendingLoginEmail` pre-fills the form and the signal is set back
 * to `null`, so it is read once and lives only in memory, never in the URL.
 *
 * Return URL: after a successful login the guest goes to `returnUrl` only when
 * it is a same-app path starting with a single `/`; anything else, including
 * an absolute or protocol-relative URL, goes to `/search`. This prevents an
 * open redirect.
 *
 * Validation: there is none on the client. The form never blocks submission,
 * and the values are sent exactly as typed, so the server stays the single
 * authority and its 400 or 401 reaches the guest inline. Every 401 shows the
 * same "Email or password is incorrect.", so the page reveals no more than the
 * API about whether an account exists.
 */
@Component({
  selector: 'app-login-page',
  standalone: true,
  imports: [NgIf, ReactiveFormsModule, RouterLink, ErrorMessageComponent],
  templateUrl: './login-page.component.html',
  styleUrl: './login-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginPageComponent {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** The credentials as typed; deliberately without validators. */
  protected readonly form = this.fb.group({ email: '', password: '' });

  /** Whether the login request is in flight; disables the submit button. */
  protected readonly pending = signal(false);

  /** The inline error of the last attempt, or `null` when there is none. */
  protected readonly error = signal<UiError | null>(null);

  /** The notice derived from the query, or `null` when there is none. */
  protected readonly notice = signal<string | null>(null);

  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      if (params.get('registered') === '1') {
        this.notice.set(REGISTERED_NOTICE);
        const email = this.auth.pendingLoginEmail();
        if (email !== null) {
          this.form.controls.email.setValue(email);
          this.auth.pendingLoginEmail.set(null);
        }
      } else if (params.get('reason') === 'session-expired') {
        this.notice.set(SESSION_EXPIRED_NOTICE);
      } else {
        this.notice.set(null);
      }
    });
  }

  /**
   * Sends the credentials as typed. On success, `AuthService` has stored the
   * session and the guest continues to the safe return URL; on failure the
   * error is shown inline.
   */
  protected submit(): void {
    this.error.set(null);
    this.pending.set(true);
    const { email, password } = this.form.getRawValue();

    this.auth
      .login(email, password)
      .pipe(
        finalize(() => this.pending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          void this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
        },
        error: (err: HttpErrorResponse) => {
          if (err.status === 401) {
            this.error.set({ ...toUiError(err), title: WRONG_CREDENTIALS_TITLE, detail: null, fieldErrors: [] });
          } else {
            this.error.set(toUiError(err));
          }
        },
      });
  }
}

/**
 * Returns `value` when it is a same-app path starting with a single `/`, and
 * `/search` otherwise. A leading `//` or `/\` is refused, because browsers read
 * both as protocol-relative URLs to another host.
 */
function safeReturnUrl(value: string | null): string {
  if (value !== null && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\')) {
    return value;
  }
  return DEFAULT_RETURN_URL;
}
