import {
  ApplicationRef,
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  ViewChild,
  afterNextRender,
  afterRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DOCUMENT } from '@angular/common';
import type { HttpErrorResponse } from '@angular/common/http';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { NavigationStart, Router, RouterLink, type Params } from '@angular/router';
import { filter, finalize, take } from 'rxjs';

import { AuthApiService } from '../../core/api/auth-api.service';
import type { RegisterRequest } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { fieldErrorIds, toUiError, withoutFieldErrors, type UiError } from '../../core/http/problem-details';
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
 * page. After the 201, once the login page has rendered, it goes to that
 * page's heading.
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

  /**
   * Registers the focus move onto the login page a registration lands on: the
   * application's root injector, because that navigation destroys this page
   * and its own injector before the login page renders.
   */
  private readonly appInjector = inject(ApplicationRef).injector;

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

  constructor() {
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
   * router state; on failure it shows the inline error.
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
          void this.router.navigate(['/login'], { queryParams: { registered: 1 }, state }).then((navigated) => {
            if (navigated) {
              focusLandingPageAfterRender(this.document, this.appInjector, this.router);
            }
          });
        },
        error: (err: HttpErrorResponse) => {
          this.showError(
            err.status === 409
              ? { ...toUiError(err), title: EMAIL_TAKEN_TITLE, detail: null, fieldErrors: [] }
              : toUiError(err),
          );
          this.restoreFocusAfterError(trigger, focusMoves);
        },
      });
  }

  /**
   * Shows a failed attempt's error in a new alert region. The inline error
   * updates an existing region in place, and assistive technology may not
   * announce an alert whose text did not change, such as a repeated 409. So
   * an error still on screen is removed by one synchronous render of this
   * page and the new one inserted by a second, both in the task that received
   * the answer: the browser never paints the page without an error, so the
   * region is new but nothing on screen moves.
   */
  private showError(error: UiError): void {
    if (this.error() === null) {
      this.error.set(error);
      return;
    }
    this.error.set(null);
    this.changeDetector.detectChanges();
    this.error.set(error);
    this.changeDetector.detectChanges();
  }

  /**
   * Moves focus once a failed registration's error has rendered and the
   * answer has re-enabled the submit button. Disabling the focused button
   * while the request is pending drops focus to the document body, and only
   * that drop is undone: focus moves only when nothing took focus while the
   * request was pending, the document still has focus, and focus is on the
   * body, on nothing, or still on that button. Anything else keeps it: the
   * field Enter was pressed in, an element the guest or the page focused
   * meanwhile, even one focus has since left, and a place outside the
   * document, such as the browser's own controls a Tab past the page's last
   * element reaches, where the body is active but the document has no focus.
   * It goes to the first input, in form order, that the error marks invalid,
   * and otherwise back to `trigger`, or to the submit button when `trigger`
   * has left the document; only to an element that is still in the document
   * and enabled. The page's own injector registers it, so a page destroyed
   * before that render moves nothing.
   *
   * @param trigger - The element focused when the guest submitted, or `null`.
   * @param focusMoves - The record, started at submit, of the focus moves
   *   made while the request was pending.
   */
  private restoreFocusAfterError(trigger: HTMLElement | null, focusMoves: FocusMoves): void {
    afterNextRender(
      () => {
        const active = this.document.activeElement;
        const button = this.submitButton?.nativeElement ?? null;
        if (
          focusMoves.moved() ||
          !this.document.hasFocus() ||
          (active !== null && active !== this.document.body && active !== button)
        ) {
          return;
        }

        const target = this.firstInvalidInput() ?? (trigger?.isConnected ? trigger : button);
        if (target !== null && target.isConnected && !target.matches(':disabled')) {
          target.focus();
        }
      },
      { injector: this.injector },
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

/** Whether focus moved while a submit was pending, as {@link watchFocusMoves} records it. */
interface FocusMoves {
  /** Whether an element has taken focus since the record started, up to `stop`. */
  readonly moved: () => boolean;
  /** Ends the record; a later focus move no longer counts. Calling it again does nothing. */
  readonly stop: () => void;
}

/**
 * Starts recording whether any element of `document` takes focus, through a
 * `focusin` listener on the document in the capture phase, so a move counts
 * whatever the focused element's own handlers do. Disabling a focused button
 * fires `focusout` only, so while a submit is pending every `focusin` is a
 * move the guest or the page made. Focus leaving the document fires no
 * `focusin`; `document.hasFocus()` tells that case.
 *
 * @param document - The document the app renders into.
 * @returns The record, which keeps listening until its `stop` is called.
 */
function watchFocusMoves(document: Document): FocusMoves {
  let moved = false;
  const onFocusIn = (): void => {
    moved = true;
  };
  document.addEventListener('focusin', onFocusIn, true);
  return {
    moved: () => moved,
    stop: () => document.removeEventListener('focusin', onFocusIn, true),
  };
}

/**
 * Moves focus onto the heading, `main h1` with `tabindex="-1"`, of the page a
 * navigation has just landed on, as soon as a render shows it. A page may
 * render its heading only after its first frame, as the hotel page does once
 * its details load, or inside content that is still hidden and cannot take
 * focus, so each render tries again until the heading has focus. The control
 * that started the navigation left the document with the previous page, so
 * focus is normally on the body. The wait ends without moving focus once focus
 * is on another element or outside the document, because the guest or the
 * page moved it, and when the next navigation starts, so focus never moves on
 * a later page.
 *
 * @param document - The document the app renders into.
 * @param injector - An injector that outlives the page that navigated away.
 * @param router - The router whose next navigation ends the wait.
 */
function focusLandingPageAfterRender(document: Document, injector: Injector, router: Router): void {
  const watch = afterRender(
    () => {
      const active = document.activeElement;
      // A Tab from the removed control's place, with nothing focusable after
      // it, takes focus out of the document and leaves the body active.
      if ((active !== null && active !== document.body) || !document.hasFocus()) {
        stop();
        return;
      }

      const heading = document.querySelector<HTMLElement>('main h1');
      if (heading === null) {
        return;
      }
      heading.focus();
      // A heading inside hidden content does not take focus, which stays on
      // the body until a later render shows it.
      if (document.activeElement === heading) {
        stop();
      }
    },
    { injector },
  );
  const nextNavigation = router.events
    .pipe(
      filter((event) => event instanceof NavigationStart),
      take(1),
    )
    .subscribe(() => watch.destroy());
  const stop = (): void => {
    watch.destroy();
    nextNavigation.unsubscribe();
  };
}

/**
 * The `returnUrl` that the login page handed over in the router state of the
 * navigation in progress, or `null` when that state carries none or a value
 * that is not a string.
 */
function handedOverReturnUrl(router: Router): string | null {
  const value: unknown = router.getCurrentNavigation()?.extras.state?.['returnUrl'];
  return typeof value === 'string' ? value : null;
}
