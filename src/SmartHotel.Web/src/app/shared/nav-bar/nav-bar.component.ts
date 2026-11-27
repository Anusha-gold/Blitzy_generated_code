import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
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
 * server 401, with no subscription of its own.
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
}
