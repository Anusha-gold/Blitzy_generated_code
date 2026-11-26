/**
 * Maps a failed `HttpClient` response to the single shape every inline error
 * renders from.
 *
 * The API answers every 4xx and 5xx status with an `application/problem+json`
 * body carrying `type`, `title`, `status`, `detail` and `traceId`; a 400 also
 * carries `errors`, an object mapping each invalid field to its messages. Only
 * a response whose `Content-Type` declares that media type is read as a
 * problem, so a JSON error from anything else, such as a proxy, gets the
 * generic text. Only `title`, `detail` and `errors` are read, and a value that
 * is empty or only whitespace counts as absent. The response status, not the
 * body's `status`, is authoritative, and `type` and `traceId` are never shown.
 *
 * Nothing here logs: an error body can echo user input. The result is plain
 * text that consumers render through interpolation only, never as HTML.
 */
import { HttpErrorResponse } from '@angular/common/http';

const UNREACHABLE_TITLE = 'The service is unreachable.';
const PROBLEM_MEDIA_TYPE = 'application/problem+json';

/**
 * The single shape every inline error renders from. `ErrorMessageComponent`
 * shows `title`, then `detail` when it is not `null`, then each field message.
 */
export interface UiError {
  /** HTTP status of the failed response; `0` when the service was unreachable. */
  status: number;
  /** Headline of the error; never empty or only whitespace. */
  title: string;
  /**
   * Explanation of the error, or `null` when the response carried none or only
   * whitespace.
   */
  detail: string | null;
  /**
   * Validation messages per request field, in the order and with the field-name
   * casing the server sent. Blank messages are dropped, and a field left with
   * no message is omitted. Empty when the response reported no field errors.
   */
  fieldErrors: { field: string; messages: string[] }[];
}

/**
 * Converts a failed `HttpClient` response into a {@link UiError}.
 *
 * The rules apply in this order, and the first that matches decides:
 * 1. Status `0` (network failure, CORS rejection or the service down) gives the
 *    title "The service is unreachable." with no detail and no field errors.
 * 2. A response whose `Content-Type` media type, without parameters and
 *    ignoring case, is `application/problem+json` and whose body is a JSON
 *    object is read as ProblemDetails: a string `title` with non-whitespace
 *    text is used, otherwise a generic "Request failed (status)." title; a
 *    string `detail` with non-whitespace text is used, otherwise `null`; and
 *    each `errors` entry whose value is an array keeps its string messages with
 *    non-whitespace text, dropping entries left with none.
 * 3. Any other response (another or no media type, or a body that is text, an
 *    array, `null` or absent) gives the generic title with no detail and no
 *    field errors.
 *
 * Text that is kept is returned exactly as the server sent it, untrimmed. The
 * status is always the response's own. The function is pure.
 *
 * @param error - The error `HttpClient` emitted for the failed request.
 * @returns The error to render inline.
 */
export function toUiError(error: HttpErrorResponse): UiError {
  if (error.status === 0) {
    return { status: 0, title: UNREACHABLE_TITLE, detail: null, fieldErrors: [] };
  }

  const body: unknown = error.error;
  if (!isProblemResponse(error) || !isRecord(body)) {
    return { status: error.status, title: genericTitle(error.status), detail: null, fieldErrors: [] };
  }

  const title = body['title'];
  const detail = body['detail'];
  return {
    status: error.status,
    title: hasText(title) ? title : genericTitle(error.status),
    detail: hasText(detail) ? detail : null,
    fieldErrors: toFieldErrors(body['errors']),
  };
}

function genericTitle(status: number): string {
  return `Request failed (${status}).`;
}

/**
 * Whether the response declares the `application/problem+json` media type,
 * compared without its parameters (such as `charset`) and ignoring case.
 * `Content-Type` is a CORS-safelisted response header, so a cross-origin
 * response exposes it without `Access-Control-Expose-Headers`.
 */
function isProblemResponse(error: HttpErrorResponse): boolean {
  const contentType = error.headers.get('Content-Type');
  if (contentType === null) {
    return false;
  }
  const mediaType = contentType.split(';', 1)[0].trim().toLowerCase();
  return mediaType === PROBLEM_MEDIA_TYPE;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Whether the value is a string with at least one non-whitespace character. */
function hasText(value: unknown): value is string {
  return typeof value === 'string' && value.trim() !== '';
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
    const messages = entries.filter(hasText);
    if (messages.length > 0) {
      fieldErrors.push({ field, messages });
    }
  }
  return fieldErrors;
}
