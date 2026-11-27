import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { distinctUntilChanged, finalize, map, of, startWith, switchMap, timer } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { UiError, fieldErrorIds, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

const REGISTERED_NOTICE = 'Account created. Log in to continue.';
const SESSION_EXPIRED_NOTICE = 'Your session has expired. Log in again.';
const WRONG_CREDENTIALS_TITLE = 'Email or password is incorrect.';
const DEFAULT_RETURN_URL = '/search';

/**
 * How long after the empty notice region renders its text is written, so
 * assistive technology registers the region before its content changes; Angular
 * CDK's `LiveAnnouncer` waits the same 100 ms.
 */
const NOTICE_ANNOUNCE_DELAY_MS = 100;

/**
 * The login page, route `login`, with the query parameters `returnUrl`,
 * `registered` and `reason`.
 *
 * Notice: `registered=1` shows "Account created. Log in to continue." and
 * `reason=session-expired` shows "Your session has expired. Log in again.";
 * `registered=1` wins when both are present. The query is observed rather than
 * read once, because the router reuses this component when only the query
 * changes. The notice is one `role="status"` region that stays mounted and is
 * visually hidden while empty; a new notice's text is written into it shortly
 * after it renders empty, so screen readers announce it on entry and on change.
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
 * Validation: the page adds no validators of its own and never gates
 * submission on validity; the template's `required` attributes mark the
 * fields and attach Angular's required validator, but nothing checks validity
 * before sending, and Angular adds `novalidate`, so the browser does not block
 * the submission either. The form sends its controls' current values
 * unchanged, so the server stays the single authority on what it accepts and
 * its 400 or 401 reaches the guest inline. Every 401 shows the same "Email or
 * password is incorrect.", so the page reveals no more than the API about
 * whether an account exists.
 */
@Component({
  selector: 'app-login-page',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, ErrorMessageComponent],
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

  /**
   * The credentials as the inputs report them; a `type="email"` input already
   * drops surrounding whitespace. The group declares no validators of its own;
   * the template's `required` attributes add Angular's required validator,
   * which `submit` does not check.
   */
  protected readonly form = this.fb.group({ email: '', password: '' });

  /** Whether the login request is in flight; disables the submit button. */
  protected readonly pending = signal(false);

  /** The inline error of the last attempt, or `null` when there is none. */
  protected readonly error = signal<UiError | null>(null);

  /** Prefix of the ids the inline error gives its field messages. */
  protected readonly errorIdPrefix = 'login-error';

  /**
   * The ids of the server's messages per rejected field (`email`, `password`),
   * which the inputs reference through `aria-describedby` and mark with
   * `aria-invalid`; empty while there is no error, so each new attempt clears
   * them. The 401 carries no field errors and so marks no field.
   */
  protected readonly fieldErrors = computed(() => fieldErrorIds(this.error(), this.errorIdPrefix));

  /**
   * The text of the always-mounted notice region, or `null` while it is empty.
   * A new notice from the query is cleared first and set after
   * `NOTICE_ANNOUNCE_DELAY_MS`, so it is announced; an unchanged one stays put.
   */
  protected readonly notice = signal<string | null>(null);

  constructor() {
    this.route.queryParamMap
      .pipe(
        map((params) => {
          if (params.get('registered') === '1') {
            const email = this.auth.pendingLoginEmail();
            if (email !== null) {
              this.form.controls.email.setValue(email);
              this.auth.pendingLoginEmail.set(null);
            }
            return REGISTERED_NOTICE;
          }
          return params.get('reason') === 'session-expired' ? SESSION_EXPIRED_NOTICE : null;
        }),
        distinctUntilChanged(),
        switchMap((message) =>
          message === null
            ? of(null)
            : timer(NOTICE_ANNOUNCE_DELAY_MS).pipe(
                map(() => message),
                startWith(null),
              ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((message) => this.notice.set(message));
  }

  /**
   * Sends the form controls' current values unchanged, whatever their
   * validity. A submit while a login is already pending is ignored. On
   * success, `AuthService` has stored the session and the guest continues to
   * the safe return URL; on failure the error is shown inline.
   */
  protected submit(): void {
    // The disabled submit button already blocks this; the guard keeps a second
    // request from starting while one is in flight, whatever triggers submit.
    if (this.pending()) {
      return;
    }

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
