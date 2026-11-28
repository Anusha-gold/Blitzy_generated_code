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
import { DOCUMENT, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationEnd, NavigationStart, PRIMARY_OUTLET, Router, RouterLink } from '@angular/router';
import { distinctUntilChanged, filter, finalize, map, of, startWith, switchMap, take, tap, timer } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { UiError, fieldErrorIds, toUiError, withoutFieldErrors } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

const REGISTERED_NOTICE = 'Account created. Log in to continue.';
const SESSION_EXPIRED_NOTICE = 'Your session has expired. Log in again.';
const WRONG_CREDENTIALS_TITLE = 'Email or password is incorrect.';
const DEFAULT_RETURN_URL = '/search';

/** The path of this page's route, `login` in `app.routes.ts`, which a return URL may not lead back to. */
const LOGIN_PATH = 'login';

/**
 * How long after the empty live region renders the notice's text is written
 * into it, so assistive technology registers the region before its content
 * changes; Angular CDK's `LiveAnnouncer` waits the same 100 ms. The visible
 * notice box does not wait.
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
 * changes. The notice renders twice. The visible box is set as soon as the
 * query is read, so it is in the page's first frame and never pushes the form
 * down; it is hidden from assistive technology. A separate `role="status"`
 * region stays mounted and visually hidden, and the notice's text is written
 * into it shortly after it renders empty, so screen readers announce it on
 * entry and on change, once.
 *
 * Email hand-off: with `registered=1`, the email the register page left in
 * `AuthService.pendingLoginEmail` pre-fills the form and the signal is set back
 * to `null`, so it is read once and lives only in memory, never in the URL.
 *
 * Return URL: the query's `returnUrl`, or else one the register page handed
 * back in the router state, so a guest sent here by "Book" who creates an
 * account first still returns to the hotel. "Create an account" carries it to
 * the register page in its router state, never in the URL, and the register
 * page brings it back the same way, so the register page and the
 * `/login?registered=1` URL stay as they are. After a successful login the
 * guest goes to it only when it is a same-app path starting with a single `/`;
 * anything else, including an absolute or protocol-relative URL, goes to
 * `/search`. This prevents an open redirect. A return URL that leads back to
 * this login page goes to `/search` too, so a signed-in guest never lands on
 * it.
 *
 * History: the navigation after a successful login replaces the login entry,
 * so Back never returns a signed-in guest to this form. A guest who is already
 * signed in when the page opens, as after Back to an older login entry, sees
 * nothing of it: once the navigation that opened it ends, the page moves on
 * to the return URL in place of that entry. The one exception is the
 * register page's fresh hand-off, `registered=1` with an email waiting, which
 * shows the notice and the pre-filled form even to a signed-in guest who has
 * just created another account.
 *
 * Focus: the submit button stays natively disabled while the login is
 * pending, which drops its focus to the document body. After a failure, once
 * the error has rendered, focus that was dropped goes to the first field the
 * error marks invalid, or back to the submit button, unless the guest or the
 * page moved focus while the login was pending or focus has left the page.
 * After a success, it goes
 * to the heading of the page the guest lands on as soon as that heading
 * renders, unless focus has moved to another element or out of the page, or
 * the guest has navigated on, by then.
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

  /** Renders this page synchronously, to replace the alert region of a shown error. */
  private readonly changeDetector = inject(ChangeDetectorRef);

  /** Tells which element has focus, at submit and once a failed login's answer has rendered. */
  private readonly document = inject(DOCUMENT);

  /** Registers the focus restore after the next render, outside an injection context. */
  private readonly injector = inject(Injector);

  /**
   * Registers the focus move onto the page a successful login lands on: the
   * application's root injector, because that navigation destroys this page
   * and its own injector before the new page renders.
   */
  private readonly appInjector = inject(ApplicationRef).injector;

  /** The email input, a target when the error marks that field invalid. */
  @ViewChild('emailInput') private emailInput?: ElementRef<HTMLInputElement>;

  /** The password input, a target when the error marks that field invalid. */
  @ViewChild('passwordInput') private passwordInput?: ElementRef<HTMLInputElement>;

  /** The "Log in" button, which the pending state disables. */
  @ViewChild('submitButton') private submitButton?: ElementRef<HTMLButtonElement>;

  /**
   * The credentials as the inputs report them; a `type="email"` input already
   * drops surrounding whitespace. The group declares no validators of its own;
   * the template's `required` attributes add Angular's required validator,
   * which `submit` does not check.
   */
  protected readonly form = this.fb.group({ email: '', password: '' });

  /** Whether the login request is in flight; disables the submit button. */
  protected readonly pending = signal(false);

  /**
   * The inline error of the last failed attempt, or `null` when there is none.
   * It stays while the next attempt is pending, until that attempt's answer
   * replaces it or, on success, clears it. Editing a field drops that field's
   * messages, and the whole error once no field message is left; an error
   * without field messages, such as the 401, stays.
   */
  protected readonly error = signal<UiError | null>(null);

  /** Prefix of the ids the inline error gives its field messages. */
  protected readonly errorIdPrefix = 'login-error';

  /**
   * The ids of the server's messages per rejected field (`email`, `password`),
   * which the inputs reference through `aria-describedby` and mark with
   * `aria-invalid`; empty while there is no error. A field's entry lasts until
   * the guest edits that field or the next attempt's answer replaces the
   * error. The 401 carries no field errors and so marks no field.
   */
  protected readonly fieldErrors = computed(() => fieldErrorIds(this.error(), this.errorIdPrefix));

  /**
   * The notice the query asks for, shown in the visible notice box, or `null`
   * for none. It is set as soon as the query is read, which for a new page is
   * before its first render, so the box is in the page's first frame.
   */
  protected readonly notice = signal<string | null>(null);

  /**
   * The text of the always-mounted live region that announces the notice, or
   * `null` while it is empty. A new notice is cleared first and set after
   * `NOTICE_ANNOUNCE_DELAY_MS`, so it is announced; an unchanged one stays put.
   */
  protected readonly announcement = signal<string | null>(null);

  /**
   * The return URL as given, checked only where it is used: the query's
   * `returnUrl`, or else the one the register page handed over in the router
   * state of the navigation that showed this query; `null` when there is
   * neither.
   */
  private readonly returnUrl = signal<string | null>(null);

  /**
   * The router state of the "Create an account" link: the return URL, which
   * the register page hands back on its way here, so a guest who registers
   * first still continues where they were going; `undefined` when there is no
   * return URL, so the link carries no state.
   */
  protected readonly registerLinkState = computed(() => {
    const returnUrl = this.returnUrl();
    return returnUrl === null ? undefined : { returnUrl };
  });

  /**
   * Whether the guest was already signed in when the page opened, as after
   * Back to an earlier login entry. The page then renders nothing and moves
   * on to the return URL in place of this history entry. A fresh hand-off
   * from the register page is the exception: `registered=1` with an email
   * still waiting in `AuthService.pendingLoginEmail`, as when a signed-in
   * guest reaches the register page through Back and creates another account.
   * That page shows its notice and pre-filled form, so the new account can log
   * in. A later visit to a `/login?registered=1` entry finds no email waiting,
   * because this page clears it when it reads it, and moves on as usual.
   * Decided once, before the constructor reads and clears that email.
   */
  protected readonly redirecting =
    this.auth.isAuthenticated() &&
    !(this.route.snapshot.queryParamMap.get('registered') === '1' && this.auth.pendingLoginEmail() !== null);

  constructor() {
    this.route.queryParamMap
      .pipe(
        map((params) => {
          // Read per emission: the router reuses this page when only the query
          // changes, and each navigation carries its own state.
          this.returnUrl.set(params.get('returnUrl') ?? handedOverReturnUrl(this.router));
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
        tap((message) => this.notice.set(message)),
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
      .subscribe((message) => this.announcement.set(message));

    // Editing a field the server rejected ends that field's error, which
    // also removes its aria-invalid mark and description.
    for (const [field, control] of Object.entries(this.form.controls)) {
      control.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
        this.error.update((shown) => (shown === null ? null : withoutFieldErrors(shown, field)));
      });
    }

    if (this.redirecting) {
      // The page is created while the navigation that opened it activates its
      // route; navigating from here would cancel that navigation halfway, so
      // the redirect waits for it to end.
      this.router.events
        .pipe(
          filter((event) => event instanceof NavigationEnd),
          take(1),
          takeUntilDestroyed(),
        )
        .subscribe(() => {
          void this.router.navigateByUrl(safeReturnUrl(this.returnUrl(), this.router), { replaceUrl: true });
        });
    }
  }

  /**
   * Sends the form controls' current values unchanged, whatever their
   * validity. A submit while a login is already pending is ignored. On
   * success, `AuthService` has stored the session, the error is cleared and
   * the guest continues to the safe return URL; on failure the error replaces
   * the previous one inline.
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
    const { email, password } = this.form.getRawValue();

    this.auth
      .login(email, password)
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
          // Replacing the login entry keeps Back from offering this form to a
          // guest who is now signed in.
          void this.router
            .navigateByUrl(safeReturnUrl(this.returnUrl(), this.router), { replaceUrl: true })
            .then((navigated) => {
              if (navigated) {
                focusLandingPageAfterRender(this.document, this.appInjector, this.router);
              }
            });
        },
        error: (err: unknown) => {
          this.showError(
            err instanceof HttpErrorResponse && err.status === 401
              ? { ...toUiError(err), title: WRONG_CREDENTIALS_TITLE, detail: null, fieldErrors: [] }
              : toUiError(err),
          );
          this.restoreFocusAfterError(trigger, focusMoves);
        },
      });
  }

  /**
   * Shows a failed attempt's error in a new alert region. The inline error
   * updates an existing region in place, and assistive technology may not
   * announce an alert whose text did not change, such as a repeated 401. So
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
   * Moves focus once a failed login's error has rendered and the answer has
   * re-enabled the submit button. Disabling the focused button while the
   * request is pending drops focus to the document body, and only that drop
   * is undone: focus moves only when nothing took focus while the request was
   * pending, the document still has focus, and focus is on the body, on
   * nothing, or still on that button. Anything else keeps it: the field Enter
   * was pressed in, an element the guest or the page focused meanwhile, even
   * one focus has since left, and a place outside the document, such as the
   * browser's own controls a Tab past the page's last element reaches, where
   * the body is active but the document has no focus. It goes to the first
   * input, in form order, that the error marks invalid, and otherwise back to
   * `trigger`, or to the submit button when `trigger` has left the document;
   * only to an element that is still in the document and enabled. The page's
   * own injector registers it, so a page destroyed before that render moves
   * nothing.
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
   * or `null` when it marks none, as with every 401.
   */
  private firstInvalidInput(): HTMLInputElement | null {
    const invalid = this.fieldErrors();
    const inputs: readonly (readonly [string, ElementRef<HTMLInputElement> | undefined])[] = [
      ['email', this.emailInput],
      ['password', this.passwordInput],
    ];
    return inputs.find(([field, input]) => input !== undefined && invalid.has(field))?.[1]?.nativeElement ?? null;
  }
}

/**
 * Returns `value` when it is a same-app path starting with a single `/` that
 * leads somewhere other than this login page, and `/search` otherwise. A
 * leading `//` or `/\` is refused, because browsers read both as
 * protocol-relative URLs to another host. A path whose primary route, as the
 * router parses it, is `login`, such as `/login`, `/login?reason=x`,
 * `/login;a=1` or `/%6Cogin`, is refused too: the router would reuse this page
 * for a guest who is now signed in, showing them the login form again, or
 * nothing at all when the page opened signed in. A path the
 * router cannot parse, such as one with a malformed percent escape, is
 * returned unchanged: the router reads it as `/`, whose redirect lands on
 * `/search`.
 *
 * @param value - The return URL as given, or `null` when there is none.
 * @param router - Parses `value` the way a navigation to it would.
 */
function safeReturnUrl(value: string | null, router: Router): string {
  if (value === null || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return DEFAULT_RETURN_URL;
  }
  const primary = router.parseUrl(value).root.children[PRIMARY_OUTLET];
  const paths = primary?.segments.map((segment) => segment.path) ?? [];
  return paths.length === 1 && paths[0] === LOGIN_PATH ? DEFAULT_RETURN_URL : value;
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
 * The `returnUrl` that the register page handed over in the router state of
 * the navigation in progress, which history traversal and a reload restore
 * too, or `null` when that state carries none or a value that is not a string.
 */
function handedOverReturnUrl(router: Router): string | null {
  const value: unknown = router.getCurrentNavigation()?.extras.state?.['returnUrl'];
  return typeof value === 'string' ? value : null;
}
