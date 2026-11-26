/**
 * Maps a failed `HttpClient` response to the single shape every inline error
 * renders from.
 *
 * The API answers every 4xx and 5xx status with an `application/problem+json`
 * body carrying `type`, `title`, `status`, `detail` and `traceId`; a 400 also
 * carries `errors`, an object mapping each invalid field to its messages. Only
 * `title`, `detail` and `errors` are read. The response status, not the body's
 * `status`, is authoritative, and `type` and `traceId` are never shown.
 *
 * Nothing here logs: an error body can echo user input. The result is plain
 * text that consumers render through interpolation only, never as HTML.
 */
import { HttpErrorResponse } from '@angular/common/http';

const UNREACHABLE_TITLE = 'The service is unreachable.';

/**
 * The single shape every inline error renders from. `ErrorMessageComponent`
 * shows `title`, then `detail` when it is not `null`, then each field message.
 */
export interface UiError {
  /** HTTP status of the failed response; `0` when the service was unreachable. */
  status: number;
  /** Headline of the error; never empty. */
  title: string;
  /** Explanation of the error, or `null` when the response carried none. */
  detail: string | null;
  /**
   * Validation messages per request field, in the order and with the field-name
   * casing the server sent. Empty when the response reported no field errors.
   */
  fieldErrors: { field: string; messages: string[] }[];
}

/**
 * Converts a failed `HttpClient` response into a {@link UiError}.
 *
 * The rules apply in this order, and the first that matches decides:
 * 1. Status `0` (network failure, CORS rejection or the service down) gives the
 *    title "The service is unreachable." with no detail and no field errors.
 * 2. A JSON object body is read as ProblemDetails: a non-empty string `title`
 *    is used, otherwise a generic "Request failed (status)." title; a string
 *    `detail` is used, otherwise `null`; and each `errors` entry whose value is
 *    an array keeps its string messages, dropping entries left with none.
 * 3. Any other body (text, `null` or absent) gives the generic title with no
 *    detail and no field errors.
 *
 * The status is always the response's own. The function is pure.
 *
 * @param error - The error `HttpClient` emitted for the failed request.
 * @returns The error to render inline.
 */
export function toUiError(error: HttpErrorResponse): UiError {
  if (error.status === 0) {
    return { status: 0, title: UNREACHABLE_TITLE, detail: null, fieldErrors: [] };
  }

  const body: unknown = error.error;
  if (!isRecord(body)) {
    return { status: error.status, title: genericTitle(error.status), detail: null, fieldErrors: [] };
  }

  const title = body['title'];
  const detail = body['detail'];
  return {
    status: error.status,
    title: typeof title === 'string' && title !== '' ? title : genericTitle(error.status),
    detail: typeof detail === 'string' ? detail : null,
    fieldErrors: toFieldErrors(body['errors']),
  };
}

function genericTitle(status: number): string {
  return `Request failed (${status}).`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function toFieldErrors(errors: unknown): UiError['fieldErrors'] {
  if (!isRecord(errors)) {
    return [];
  }

  const fieldErrors: UiError['fieldErrors'] = [];
  for (const [field, value] of Object.entries(errors)) {
    if (!Array.isArray(value)) {
      continue;
    }
    const entries: readonly unknown[] = value;
    const messages = entries.filter((message): message is string => typeof message === 'string');
    if (messages.length > 0) {
      fieldErrors.push({ field, messages });
    }
  }
  return fieldErrors;
}
