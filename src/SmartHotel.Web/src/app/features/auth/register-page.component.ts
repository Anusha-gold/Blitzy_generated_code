import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { HttpErrorResponse } from '@angular/common/http';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthApiService } from '../../core/api/auth-api.service';
import type { RegisterRequest } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { fieldErrorIds, toUiError, type UiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/** The inline title shown when the email is already registered (409). */
const EMAIL_TAKEN_TITLE = 'An account with this email already exists.';

/**
 * The registration page, route `register`: the guest enters an email, a
 * password and a full name, and `POST /api/auth/register` creates the account.
 *
 * Hand-off to login: on the 201 the page stores the email in
 * `AuthService.pendingLoginEmail`, in memory only, and navigates to
 * `/login?registered=1`. The login page reads that signal once, pre-fills its
 * email field and clears it. The email never travels in the URL, where it would
 * land in the browser history, server logs and `Referer` headers. The returned
 * `guestId` is not used, and the guest is not signed in automatically.
 *
 * Validation: the page adds no validators of its own and never gates
 * submission on validity; the template's `required` attributes mark the
 * fields and attach Angular's required validator, but nothing checks validity
 * before sending, and Angular adds `novalidate`, so the browser does not block
 * the submission either. The server is the single authority on the email,
 * password and full-name rules, and its 400 field errors render inline through
 * `ErrorMessageComponent`, so the client never repeats or contradicts them.
 * The form sends its controls' current values unchanged; the server trims and
 * normalizes its own copy.
 *
 * Errors: a 409 shows the fixed title "An account with this email already
 * exists." in place of the server's title and detail. Every other failure,
 * the 400, status 0 when the service is unreachable and any other status, is
 * shown as `toUiError` maps it. Nothing is logged or persisted.
 */
@Component({
  selector: 'app-register-page',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, ErrorMessageComponent],
  templateUrl: './register-page.component.html',
  styleUrl: './register-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RegisterPageComponent {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The registration fields as the inputs report them; a `type="email"` input
   * already drops surrounding whitespace. The group declares no validators of
   * its own; the template's `required` attributes add Angular's required
   * validator, which `submit` does not check.
   */
  protected readonly form = this.fb.group({ email: '', password: '', fullName: '' });

  /** Whether the registration request is in flight; disables the submit button. */
  protected readonly pending = signal(false);

  /** The inline error of the last attempt, or `null` when there is none. */
  protected readonly error = signal<UiError | null>(null);

  /** Prefix of the ids the inline error gives its field messages. */
  protected readonly errorIdPrefix = 'register-error';

  /**
   * The ids of the server's messages per rejected field (`email`, `password`,
   * `fullName`), which the inputs reference through `aria-describedby` and
   * mark with `aria-invalid`; empty while there is no error, so each new
   * attempt clears them. The 409 carries no field errors and so marks no field.
   */
  protected readonly fieldErrors = computed(() => fieldErrorIds(this.error(), this.errorIdPrefix));

  /**
   * Sends the registration. On the 201 it hands the email to the login page
   * and navigates to `/login?registered=1`; on failure it shows the inline
   * error.
   */
  protected submit(): void {
    // The disabled submit button already blocks this; the guard keeps a second
    // request from starting while one is in flight, whatever triggers submit.
    if (this.pending()) {
      return;
    }

    this.error.set(null);
    this.pending.set(true);

    const { email, password, fullName } = this.form.getRawValue();
    const request: RegisterRequest = { email, password, fullName };

    this.authApi
      .register(request)
      .pipe(
        finalize(() => this.pending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.auth.pendingLoginEmail.set(email);
          void this.router.navigate(['/login'], { queryParams: { registered: 1 } });
        },
        error: (err: HttpErrorResponse) => {
          this.error.set(
            err.status === 409
              ? { ...toUiError(err), title: EMAIL_TAKEN_TITLE, detail: null, fieldErrors: [] }
              : toUiError(err),
          );
        },
      });
  }
}
