import { ChangeDetectionStrategy, Component, Input, signal } from '@angular/core';
import { NgFor, NgIf } from '@angular/common';
import { fieldErrorId, fieldLabel, type UiError } from '../../core/http/problem-details';

/**
 * Renders one inline error in a styled wrapper (`data-testid="inline-error"`)
 * whose ARIA alert region (`role="alert"`) holds only its text: the
 * ProblemDetails title, then the detail when there is one, then every field
 * error message. Assistive technology therefore announces status text only.
 *
 * Renders nothing while `error` is `null`, so no empty alert region exists on
 * a page without an error. Page-specific actions, such as the "Log in again"
 * link after a 401, are projected through `<ng-content>` after the alert
 * region, inside the same wrapper and test hook, and so appear only alongside
 * an error.
 *
 * Server-supplied text is rendered by interpolation only, never as HTML. The
 * component maps no status to text: it shows the {@link UiError} it receives.
 *
 * Each field error reads `Label: message`, where the label is the one the
 * guest sees for that field, from `fieldLabel`, so the list names each field
 * as the form does. A key with no such label, such as the body-level `$`,
 * shows its message alone; the server's field name is never shown. With a
 * `fieldErrorIdPrefix`, the message text alone carries the id from
 * `fieldErrorId`, so the rejected input can name it in `aria-describedby`
 * through `fieldErrorIds` without repeating its own label; without one, the
 * messages carry no ids.
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
  @Input() set error(value: UiError | null) {
    this.shownError.set(value);
  }

  /**
   * Prefix for the ids of the field-error messages, so form inputs can
   * reference them through `aria-describedby`; `null` gives them no ids.
   */
  @Input() set fieldErrorIdPrefix(value: string | null) {
    this.idPrefix.set(value);
  }

  /**
   * The error last bound to `error`, which the template shows; `null` until
   * one is bound.
   */
  protected readonly shownError = signal<UiError | null>(null);

  /** The prefix last bound to `fieldErrorIdPrefix`; `null` until one is bound. */
  private readonly idPrefix = signal<string | null>(null);

  /** The visible label for a field-error key, or `null` when it has none. */
  protected readonly fieldLabel = fieldLabel;

  /**
   * The id of one field-error message, or `null` when no prefix is set.
   *
   * @param fieldIndex - The field's index in `error.fieldErrors`.
   * @param messageIndex - The message's index in that field's `messages`.
   */
  protected messageId(fieldIndex: number, messageIndex: number): string | null {
    const prefix = this.idPrefix();
    return prefix === null ? null : fieldErrorId(prefix, fieldIndex, messageIndex);
  }
}
