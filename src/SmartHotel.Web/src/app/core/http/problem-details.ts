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
 * Status `0` covers every request that got no response: a network failure, a
 * CORS rejection, the service down, or a request that passed its
 * {@link REQUEST_DEADLINE_MS} deadline. `HotelsApiService` and
 * `BookingsApiService` bound each of their requests with
 * {@link withRequestDeadline}, which turns an expired wait into such a status-0
 * error, so a peer that never answers ends in the same inline error.
 *
 * Nothing here logs: an error body can echo user input. The result is plain
 * text that consumers render through interpolation only, never as HTML.
 */
import { HttpErrorResponse } from '@angular/common/http';
import { throwError, timeout } from 'rxjs';
import type { MonoTypeOperatorFunction } from 'rxjs';

const UNREACHABLE_TITLE = 'The service is unreachable.';
const PROBLEM_MEDIA_TYPE = 'application/problem+json';
const DEADLINE_STATUS_TEXT = 'Request deadline exceeded';
/** A JSON path naming one top-level member, such as `$.email`; group 1 is the member. */
const TOP_LEVEL_JSON_PATH = /^\$\.([A-Za-z_][A-Za-z0-9_]*)$/;

/**
 * How long, in milliseconds, a request bounded by {@link withRequestDeadline}
 * may wait for its response before the operator aborts it: 30 seconds. That is
 * far above the API's one-second search latency target, so only a peer that
 * has stopped answering reaches it.
 */
export const REQUEST_DEADLINE_MS = 30_000;

/**
 * Bounds an `HttpClient` request by {@link REQUEST_DEADLINE_MS}.
 *
 * When the response has not arrived within the deadline, the operator
 * unsubscribes from the request, which aborts it and lets every interceptor's
 * `finalize` run, so the loading indicator is lowered. It then fails with an
 * `HttpErrorResponse` of status `0`, statusText "Request deadline exceeded"
 * and no body, which {@link toUiError} maps to "The service is unreachable.".
 * A response that arrives in time passes through unchanged, and so does any
 * other error.
 *
 * The operator never retries. Aborting stops only the wait: the server may
 * still have received and applied the request.
 *
 * @param url - The request's URL, recorded on the deadline error.
 * @returns An operator that mirrors the request until its deadline.
 */
export function withRequestDeadline<T>(url: string): MonoTypeOperatorFunction<T> {
  return timeout({
    first: REQUEST_DEADLINE_MS,
    with: () => throwError(() => new HttpErrorResponse({ status: 0, statusText: DEADLINE_STATUS_TEXT, url })),
  });
}

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
 * 1. Status `0` (network failure, CORS rejection, the service down, or a
 *    request that passed its deadline under {@link withRequestDeadline}) gives
 *    the title "The service is unreachable." with no detail and no field
 *    errors.
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

/**
 * Returns the element id of one field-error message: `idPrefix`, the field's
 * index in {@link UiError.fieldErrors} and the message's index within it,
 * joined by `-`. The id is built from indexes, never from the field name, so a
 * server key containing white space or any other character stays a valid id
 * reference. `ErrorMessageComponent` gives each message this id, and form
 * inputs reference it through {@link fieldErrorIds}.
 *
 * @param idPrefix - The page's prefix, unique within the document.
 * @param fieldIndex - The field's index in `fieldErrors`.
 * @param messageIndex - The message's index in that field's `messages`.
 * @returns The id, for example `search-error-0-1`.
 */
export function fieldErrorId(idPrefix: string, fieldIndex: number, messageIndex: number): string {
  return `${idPrefix}-${fieldIndex}-${messageIndex}`;
}

/**
 * Maps each field named in the error's {@link UiError.fieldErrors} to the
 * space-separated ids, from {@link fieldErrorId}, of its messages, so a form
 * input can mark itself invalid and reference its messages through
 * `aria-describedby`. A server key that is a top-level JSON path member, `$.`
 * followed by a member name such as `$.email` (the form the API uses for a
 * body field of the wrong JSON type), names that member, `email`; every other
 * key is the field name exactly as the server sent it. Names are compared with
 * case, so body-level keys such as `$` or `request` and deeper paths such as
 * `$.items[0].name` match no form input and stay in the summary only. A field
 * reported more than once, under either form, gets the ids of all its
 * messages in order, and a field with no message is left out. The map is
 * empty for `null`, so the associations end as soon as a page clears its
 * error. The function is pure.
 *
 * @param error - The error being shown, or `null` when there is none.
 * @param idPrefix - The prefix the same error's `ErrorMessageComponent` uses.
 * @returns The ids per field name; `get` returns `undefined` for a field
 * without an error.
 */
export function fieldErrorIds(error: UiError | null, idPrefix: string): ReadonlyMap<string, string> {
  const ids = new Map<string, string>();
  if (error === null) {
    return ids;
  }

  error.fieldErrors.forEach((fe, fieldIndex) => {
    if (fe.messages.length === 0) {
      return;
    }
    const messageIds = fe.messages.map((_message, messageIndex) => fieldErrorId(idPrefix, fieldIndex, messageIndex));
    const field = fieldNameOf(fe.field);
    const existing = ids.get(field);
    ids.set(field, existing === undefined ? messageIds.join(' ') : [existing, ...messageIds].join(' '));
  });
  return ids;
}

/** The member a top-level JSON path key (`$.email`) names; any other key unchanged. */
function fieldNameOf(key: string): string {
  const match = TOP_LEVEL_JSON_PATH.exec(key);
  return match === null ? key : match[1];
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
