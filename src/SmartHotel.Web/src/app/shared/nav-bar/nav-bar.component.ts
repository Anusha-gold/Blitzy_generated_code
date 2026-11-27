import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { NgIf } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';

/**
 * The app-wide navigation bar, rendered by the shell above every page.
 *
 * Always visible: the brand link "SmartHotel" and "Search", both to `/search`,
 * and "My bookings" to `/bookings`. "My bookings" stays visible while signed
 * out, so the guard's redirect to `/login?returnUrl=%2Fbookings` can be reached
 * by a click.
 *
 * Signed out, it adds "Log in" and "Register". Signed in, it shows the guest's
 * email and "Log out" instead.
 *
 * Logging out is client-side only: {@link AuthService.logout} discards the
 * in-memory session and returns to `/search` without calling the API, so the
 * discarded token stays valid on the server until it expires.
 *
 * The template reads {@link AuthService.session}, a signal, so OnPush change
 * detection re-renders the bar whenever the session changes. It therefore
 * shows "Log in" as soon as `authInterceptor` clears the session after a
 * server 401, or `AuthService` clears it when the token's `expiresAt` passes,
 * even on a public page that sends no request, with no subscription of its own.
 *
 * Focus: when the signed-in controls are removed while "Log out" has focus,
 * on logout, at token expiry or after a server 401, focus moves to the "Log
 * in" link that replaces it, so it never drops to the page body.
 */
@Component({
  selector: 'app-nav-bar',
  standalone: true,
  imports: [NgIf, RouterLink],
  templateUrl: './nav-bar.component.html',
  styleUrl: './nav-bar.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NavBarComponent {
  /** The guest's session, read by the template, and its logout action. */
  protected readonly auth = inject(AuthService);

  /**
   * Whether "Log out" holds focus. The button's removal leaves it `true`, so
   * the "Log in" link rendered in its place takes the focus over. The template
   * never reads it. Angular calls the `logInLink` setter during change
   * detection, while the bar's view is the active reactive consumer, so the
   * setter reads and resets the flag inside `untracked`: a write there would
   * otherwise throw NG0600, and the read would make the view depend on the flag.
   */
  private readonly logOutHasFocus = signal(false);

  /**
   * Receives the "Log in" link each time the signed-out controls render, and
   * `undefined` when they are removed. Angular sets it in the change-detection
   * pass that replaced the signed-in controls, after the link is in the page,
   * so when "Log out" held focus until its removal, the link takes it.
   */
  @ViewChild('logInLink')
  private set logInLink(link: ElementRef<HTMLAnchorElement> | undefined) {
    if (link === undefined) {
      return;
    }
    untracked(() => {
      if (this.logOutHasFocus()) {
        this.logOutHasFocus.set(false);
        link.nativeElement.focus();
      }
    });
  }

  /** Records that "Log out" has received focus, from the keyboard, a click or a script. */
  protected logOutFocused(): void {
    this.logOutHasFocus.set(true);
  }

  /**
   * Records that "Log out" has lost focus, unless the loss is its own removal.
   *
   * Chromium fires `blur` on a focused button that is being removed while it is
   * still in the document, so the check waits for a microtask: it runs after
   * the change-detection pass that removed the button and called the
   * `logInLink` setter. A button still in the document then lost focus to the
   * guest, by Tab or a click elsewhere. One that is still the document's active
   * element lost it only to a window blur, so the flag stays set and a removal
   * while the guest is in another window still hands focus to "Log in".
   * Firefox fires no `blur` on removal, so the flag stays set there too.
   *
   * @param button - The "Log out" button that fired `blur`.
   */
  protected logOutBlurred(button: HTMLButtonElement): void {
    queueMicrotask(() => {
      if (button.isConnected && button.ownerDocument.activeElement !== button) {
        this.logOutHasFocus.set(false);
      }
    });
  }
}
