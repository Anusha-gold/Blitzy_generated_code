import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink, type Params } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthApiService } from '../../core/api/auth-api.service';
import type { RegisterRequest } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { fieldErrorIds, toUiError, withoutFieldErrors, type UiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';
import {
  handedOverReturnUrl,
  restoreFocusAfterFailedSubmit,
  showErrorInNewAlert,
  watchFocusMoves,
  type FocusMoves,
} from './login-page.component';

/** The inline title shown when the email is already registered (409). */
const EMAIL_TAKEN_TITLE = 'An account with this email already exists.';

/** The inline title shown when the account was created but the login page was not opened. */
const LOGIN_PAGE_UNOPENED_TITLE =
  'Your account was created, but the login page could not be opened. Reload the page, then log in.';

/**
 * The registration page, route `register`: the guest enters an email, a
 * password and a full name, and `POST /api/auth/register` creates the account.
 *
 * Hand-off to login: on the 201 the page stores the email in
 * `AuthService.pendingLoginEmail`, in memory only, and navigates to
 * `/login?registered=1`. The login page reads that signal once, pre-fills its
 * email field and clears it. The email never travels in the URL, where it would
 * land in the browser history, server logs and `Referer` headers. The returned
 * `guestId` is not used, and the guest is not signed in automatically. When
 * that navigation does not reach the login page, because the router resolves
 * it with anything but `true` or rejects it, the page withdraws the email
 * while it is still this registration's and stays here with the inline error
 * "Your account was created, but the login page could not be opened. Reload
 * the page, then log in."; the rejection reason is neither shown nor logged.
 *
 * Return URL: when the login page's "Create an account" link handed over a
 * return URL in its router state, the page keeps it and hands it back, in the
 * router state of the navigation to `/login?registered=1` and in the query of
 * "Already registered? Log in", so a guest sent to log in by "Book" still
 * returns to the hotel after registering. Without one, that link is exactly
 * `/login` and no state is passed.
 *
 * Focus: the submit button stays natively disabled while the registration is
 * pending, which drops its focus to the document body. After a failure, once
 * the error has rendered, focus that was dropped goes to the first field the
 * error marks invalid, or back to the submit button, unless the guest or the
 * page moved focus while the registration was pending or focus has left the
 * page. After the 201 the login page moves it onto its own heading once that
 * renders, as on every entry from within the app; when the login page could
 * not be opened, it returns once the inline error has rendered, as after a
 * failure.
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

  /** Renders this page synchronously, to replace the alert region of a shown error. */
  private readonly changeDetector = inject(ChangeDetectorRef);

  /** Tells which element has focus, at submit and once a failed registration's answer has rendered. */
  private readonly document = inject(DOCUMENT);

  /** Registers the focus restore after the next render, outside an injection context. */
  private readonly injector = inject(Injector);

  /** The email input, a target when the error marks that field invalid. */
  @ViewChild('emailInput') private emailInput?: ElementRef<HTMLInputElement>;

  /** The password input, a target when the error marks that field invalid. */
  @ViewChild('passwordInput') private passwordInput?: ElementRef<HTMLInputElement>;

  /** The full-name input, a target when the error marks that field invalid. */
  @ViewChild('fullNameInput') private fullNameInput?: ElementRef<HTMLInputElement>;

  /** The "Create account" button, which the pending state disables. */
  @ViewChild('submitButton') private submitButton?: ElementRef<HTMLButtonElement>;

  /**
   * The registration fields as the inputs report them; a `type="email"` input
   * already drops surrounding whitespace. The group declares no validators of
   * its own; the template's `required` attributes add Angular's required
   * validator, which `submit` does not check.
   */
  protected readonly form = this.fb.group({ email: '', password: '', fullName: '' });

  /** Whether the registration request is in flight; disables the submit button. */
  protected readonly pending = signal(false);

  /**
   * The inline error of the last failed attempt, or `null` when there is none.
   * It stays while the next attempt is pending, until that attempt's answer
   * replaces it or, on the 201, clears it. Editing a field drops that field's
   * messages, and the whole error once no field message is left; an error
   * without field messages, such as the 409, stays.
   */
  protected readonly error = signal<UiError | null>(null);

  /** Prefix of the ids the inline error gives its field messages. */
  protected readonly errorIdPrefix = 'register-error';

  /**
   * The ids of the server's messages per rejected field (`email`, `password`,
   * `fullName`), which the inputs reference through `aria-describedby` and
   * mark with `aria-invalid`; empty while there is no error. A field's entry
   * lasts until the guest edits that field or the next attempt's answer
   * replaces the error. The 409 carries no field errors and so marks no field.
   */
  protected readonly fieldErrors = computed(() => fieldErrorIds(this.error(), this.errorIdPrefix));

  /**
   * The return URL the login page handed over in the router state of the
   * navigation that opened this page, which history traversal and a reload
   * restore too; `null` when there is none. It travels on to the login page
   * unchecked, and only the login page decides whether it is safe to follow.
   */
  private readonly returnUrl = handedOverReturnUrl(this.router);

  /**
   * The query of the "Already registered? Log in" link: the held return URL,
   * or `null` so that the link is exactly `/login`.
   */
  protected readonly loginLinkQuery: Params | null = this.returnUrl === null ? null : { returnUrl: this.returnUrl };

  /** `true` once this page is destroyed; a navigation that ends later shows nothing here. */
  private readonly destroyed = signal(false);

  constructor() {
    this.destroyRef.onDestroy(() => this.destroyed.set(true));

    // Editing a field the server rejected ends that field's error, which
    // also removes its aria-invalid mark and description.
    for (const [field, control] of Object.entries(this.form.controls)) {
      control.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
        this.error.update((shown) => (shown === null ? null : withoutFieldErrors(shown, field)));
      });
    }
  }

  /**
   * Sends the registration. On the 201 it hands the email to the login page
   * and navigates to `/login?registered=1`, with any held return URL in the
   * router state, or stays here with an inline error when that page cannot be
   * opened; on failure it shows the inline error.
   */
  protected submit(): void {
    // The disabled submit button already blocks this; the guard keeps a second
    // request from starting while one is in flight, whatever triggers submit.
    if (this.pending()) {
      return;
    }

    const focused = this.document.activeElement;
    const trigger = focused instanceof HTMLElement && focused !== this.document.body ? focused : null;
    // Started before the pending state disables the focused button, which
    // fires only `focusout`: every `focusin` from here on is a move the guest
    // or the page made.
    const focusMoves = watchFocusMoves(this.document);
    // The previous error stays on screen until this attempt's answer replaces
    // it, so nothing below it moves while the request is pending.
    this.pending.set(true);

    const { email, password, fullName } = this.form.getRawValue();
    const request: RegisterRequest = { email, password, fullName };

    this.authApi
      .register(request)
      .pipe(
        // Runs once the answer has been handled, and when the page is
        // destroyed before it arrives.
        finalize(() => {
          focusMoves.stop();
          this.pending.set(false);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.error.set(null);
          this.auth.pendingLoginEmail.set(email);
          const state = this.returnUrl === null ? undefined : { returnUrl: this.returnUrl };
          // The login page moves focus onto its own heading on entry.
          void this.router.navigate(['/login'], { queryParams: { registered: 1 }, state }).then(
            (navigated) => {
              if (!navigated) {
                this.showUnopenedLoginPage(email, trigger, focusMoves);
              }
            },
            () => this.showUnopenedLoginPage(email, trigger, focusMoves),
          );
        },
        error: (err: unknown) => {
          this.showError(
            err instanceof HttpErrorResponse && err.status === 409
              ? { ...toUiError(err), title: EMAIL_TAKEN_TITLE, detail: null, fieldErrors: [] }
              : toUiError(err),
          );
          this.restoreFocusAfterError(trigger, focusMoves);
        },
      });
  }

  /**
   * Shows that the account was created but the login page was not opened: the
   * router resolved the navigation to `/login?registered=1` with anything but
   * `true`, as when another navigation superseded it, or rejected it, as when
   * the browser threw on its history update or the login page's code failed
   * to load. The email handed to the login page is withdrawn while it is still
   * this registration's, so no later `/login?registered=1` pre-fills it. The
   * page stays here with the inline error "Your account was created, but the
   * login page could not be opened. Reload the page, then log in." through
   * `ErrorMessageComponent`: the browser keeps failing a module it could not
   * load until the page reloads, after which "Already registered? Log in" and
   * the nav bar's "Log in" lead on. The rejection reason is neither shown nor
   * logged. Focus that the pending state dropped then returns as after a
   * failed registration. A page that a navigation has destroyed by then shows
   * nothing.
   *
   * @param email - The email this registration handed to the login page.
   * @param trigger - The element focused when the guest submitted, or `null`.
   * @param focusMoves - The record, started at submit, of the focus moves
   *   made while the request was pending.
   */
  private showUnopenedLoginPage(email: string, trigger: HTMLElement | null, focusMoves: FocusMoves): void {
    if (this.auth.pendingLoginEmail() === email) {
      this.auth.pendingLoginEmail.set(null);
    }
    if (this.destroyed()) {
      return;
    }
    this.showError({ status: 0, title: LOGIN_PAGE_UNOPENED_TITLE, detail: null, fieldErrors: [] });
    this.restoreFocusAfterError(trigger, focusMoves);
  }

  /**
   * Shows `error` inline in a new alert region, so assistive technology
   * announces it even when its text repeats, such as a repeated 409, as
   * {@link showErrorInNewAlert} sets out.
   */
  private showError(error: UiError): void {
    showErrorInNewAlert(this.error, error, this.changeDetector);
  }

  /**
   * Moves focus that the pending state dropped, once the error on screen has
   * rendered, to the first input the error marks invalid, back to `trigger`,
   * or to the "Create account" button, as
   * {@link restoreFocusAfterFailedSubmit} sets out. This page's own injector
   * registers it, so a page destroyed before that render moves nothing.
   *
   * @param trigger - The element focused when the guest submitted, or `null`.
   * @param focusMoves - The record, started at submit, of the focus moves
   *   made while the request was pending.
   */
  private restoreFocusAfterError(trigger: HTMLElement | null, focusMoves: FocusMoves): void {
    restoreFocusAfterFailedSubmit(
      this.document,
      this.injector,
      trigger,
      focusMoves,
      () => this.submitButton?.nativeElement ?? null,
      () => this.firstInvalidInput(),
    );
  }

  /**
   * The first input, in form order, that the error on screen marks invalid,
   * or `null` when it marks none, as with the 409.
   */
  private firstInvalidInput(): HTMLInputElement | null {
    const invalid = this.fieldErrors();
    const inputs: readonly (readonly [string, ElementRef<HTMLInputElement> | undefined])[] = [
      ['email', this.emailInput],
      ['password', this.passwordInput],
      ['fullName', this.fullNameInput],
    ];
    return inputs.find(([field, input]) => input !== undefined && invalid.has(field))?.[1]?.nativeElement ?? null;
  }
}
