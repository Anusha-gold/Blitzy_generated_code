import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { NgFor, NgIf } from '@angular/common';
import type { UiError } from '../../core/http/problem-details';

/**
 * Renders one inline error as an ARIA alert (`role="alert"`,
 * `data-testid="inline-error"`): the ProblemDetails title, then the detail
 * when there is one, then every field error message.
 *
 * Renders nothing while `error` is `null`, so no empty alert region exists on
 * a page without an error. Page-specific actions, such as the "Log in again"
 * link after a 401, are projected through `<ng-content>` inside the alert and
 * so appear only alongside an error.
 *
 * Server-supplied text is rendered by interpolation only, never as HTML. The
 * component maps no status to text: it shows the {@link UiError} it receives.
 */
@Component({
  selector: 'app-error-message',
  standalone: true,
  imports: [NgIf, NgFor],
  templateUrl: './error-message.component.html',
  styleUrl: './error-message.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ErrorMessageComponent {
  /** The error to show; `null` renders nothing. */
  @Input() error: UiError | null = null;
}
