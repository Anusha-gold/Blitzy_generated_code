/**
 * Maps a failed `HttpClient` request to the single shape every inline error
 * renders from.
 *
 * The API answers every 4xx and 5xx status with an `application/problem+json`
 * body carrying `type`, `title`, `status`, `detail` and `traceId`; a 400 also
 * carries `errors`, an object mapping each invalid field to its messages. Only
 * a 4xx response whose `Content-Type` declares that media type is read as a
 * problem, so a JSON error from anything else, such as a proxy, gets the
 * generic text. Only `title`, `detail` and `errors` are read, and a value that
 * is empty or only whitespace counts as absent. The response status, not the
 * body's `status`, is authoritative, and `type` and `traceId` are never shown.
 * A 5xx body is never read, whatever its media type: it reports a fault of the
 * server, not of the request, and text from a misconfigured server or proxy
 * could expose its internals, so every 5xx gets the generic text.
 *
 * Status `0` covers every request that got no response: a network failure, a
 * CORS rejection, the service down, or a request that passed its
 * {@link REQUEST_DEADLINE_MS} deadline. `AuthApiService`, `HotelsApiService`
 * and `BookingsApiService` bound each of their requests with
 * {@link withRequestDeadline}, which turns an expired wait into such a status-0
 * error, so a peer that never answers ends in the same inline error.
 *
 * The error channel of an observable is untyped, so a request can also fail
 * with a value that is not an `HttpErrorResponse`, such as an exception thrown
 * synchronously while the request was being sent, which `loadingInterceptor`
 * and `HttpClient` forward as an error notification. No HTTP response was
 * received for it, so it maps to status `0` as well, and nothing of it, its
 * message and stack included, is read or shown.
 *
 * A 2xx status reaches the mapping only for a success response the app cannot
 * use: one whose client rejected its status, or its body, such as a body that
 * failed its shape check under {@link withResponseBody}, or JSON that
 * `HttpClient` could not parse. Each arrives as an `HttpErrorResponse`
 * carrying the response's own status, so such a response ends in an inline
 * error too, never in a page reading a missing field.
 *
 * Nothing here logs: an error body can echo user input. The result is plain
 * text that consumers render through interpolation only, never as HTML.
 */
import { HttpErrorResponse } from '@angular/common/http';
import type { HttpResponse } from '@angular/common/http';
import { map, throwError, timeout } from 'rxjs';
import type { MonoTypeOperatorFunction, OperatorFunction } from 'rxjs';

const UNREACHABLE_TITLE = 'The service is unreachable.';
const UNEXPECTED_RESPONSE_TITLE = 'The service sent an unexpected response.';
const PROBLEM_MEDIA_TYPE = 'application/problem+json';
const DEADLINE_STATUS_TEXT = 'Request deadline exceeded';
const UNEXPECTED_BODY_STATUS_TEXT = 'Unexpected response body';
/** A JSON path naming one top-level member, such as `$.email`; group 1 is the member. */
const TOP_LEVEL_JSON_PATH = /^\$\.([A-Za-z_][A-Za-z0-9_]*)$/;
/**
 * The label the guest sees for each request field the API names in `errors`:
 * the form `<label>` text on the search, login and register pages, and the
 * table headers for the booked hotel and room type. One map serves every page,
 * because a field name means the same input wherever it is sent.
 */
const FIELD_LABELS: ReadonlyMap<string, string> = new Map<string, string>([
  ['city', 'City'],
  ['checkIn', 'Check-in'],
  ['checkOut', 'Check-out'],
  ['guests', 'Guests'],
  ['email', 'Email'],
  ['password', 'Password'],
  ['fullName', 'Full name'],
  ['hotelId', 'Hotel'],
  ['roomTypeId', 'Room type'],
]);

/**
 * Time, in milliseconds, allowed for a request bounded by
 * {@link withRequestDeadline} to deliver its response, body included, before
 * the operator aborts it: 30 seconds. It is a failure bound that keeps a page
 * from waiting indefinitely, not the search performance target.
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
 * Checks the body of a successful `HttpClient` response at run time and emits
 * it as `T`.
 *
 * `HttpClient`'s type argument is never checked, so a 2xx body such as `null`,
 * `{}` or a field of the wrong type would otherwise reach the page as `T` and
 * fail where it is read. Applied to a request sent with `observe: 'response'`,
 * the operator emits the response's body when `isBody` accepts it. Otherwise it
 * fails with an `HttpErrorResponse` carrying the response's own 2xx status,
 * URL and headers, statusText "Unexpected response body" and no body, which
 * {@link toUiError} maps to "The service sent an unexpected response.". Only
 * a rejected body is converted: errors from upstream, and an exception that
 * `isBody` throws, pass through unchanged, whatever their type.
 *
 * The operator never retries and never logs: the body can echo user input.
 *
 * @param isBody - Whether a parsed body has the shape the caller reads.
 * @returns An operator from the full response to its accepted body.
 */
export function withResponseBody<T>(
  isBody: (body: unknown) => body is T,
): OperatorFunction<HttpResponse<unknown>, T> {
  return map((response) => {
    const body = response.body;
    if (isBody(body)) {
      return body;
    }
    throw new HttpErrorResponse({
      status: response.status,
      statusText: UNEXPECTED_BODY_STATUS_TEXT,
      url: response.url ?? undefined,
      headers: response.headers,
    });
  });
}

/**
 * The single shape every inline error renders from. `ErrorMessageComponent`
 * shows `title`, then `detail` when it is not `null`, then each field message.
 */
export interface UiError {
  /**
   * HTTP status of the failed response; `0` when no HTTP response was
   * received: the service was unreachable, the request passed its deadline,
   * or the request failed with a value that is not an `HttpErrorResponse`.
   */
  status: number;
  /** Headline of the error; never empty or only whitespace. */
  title: string;
  /**
   * Explanation of the error from a 4xx problem body, or `null` when that body
   * carried none or only whitespace, and for every other failure.
   */
  detail: string | null;
  /**
   * Validation messages per request field, in the order and with the field-name
   * casing the server sent. Blank messages are dropped, and a field left with
   * no message is omitted. Empty when the response reported no field errors,
   * and for every failure but a 4xx problem.
   */
  fieldErrors: { field: string; messages: string[] }[];
}

/**
 * Converts the value a failed `HttpClient` request delivered on its error
 * channel into a {@link UiError}.
 *
 * The rules apply in this order, and the first that matches decides:
 * 1. A value that is not an `HttpErrorResponse`, such as an exception thrown
 *    synchronously while the request was being sent, means no HTTP response
 *    was received. It gives status `0` and the title "The service is
 *    unreachable." with no detail and no field errors. Nothing of the value,
 *    its message and stack included, is read.
 * 2. Status `0` (network failure, CORS rejection, the service down, or a
 *    request that passed its deadline under {@link withRequestDeadline}) gives
 *    the same title with no detail and no field errors.
 * 3. A 2xx status (a success response whose client rejected its status or its
 *    body, such as a body that failed its check under {@link withResponseBody},
 *    or JSON that `HttpClient` could not parse) gives the title "The service
 *    sent an unexpected response." with no detail and no field errors.
 * 4. A 4xx response whose `Content-Type` media type, without parameters and
 *    ignoring case, is `application/problem+json` and whose body is a JSON
 *    object is read as ProblemDetails: a string `title` with non-whitespace
 *    text is used, otherwise a generic "Request failed (status)." title; a
 *    string `detail` with non-whitespace text is used, otherwise `null`; and
 *    each `errors` entry whose value is an array keeps its string messages with
 *    non-whitespace text, dropping entries left with none.
 * 5. Any other response gives the generic title with no detail and no field
 *    errors: a 4xx with another or no media type, or with a body that is
 *    text, an array, `null` or absent, and every 5xx or other status whatever
 *    its media type and body, so a server fault never shows the server's text.
 *
 * Text that is kept is returned exactly as the server sent it, untrimmed. The
 * status is the response's own, and `0` when no response was received. The
 * function is pure.
 *
 * @param error - Any value the failed request's error channel delivered:
 *   normally the `HttpErrorResponse` that `HttpClient` emitted, but the
 *   channel is untyped, so every other value is accepted too.
 * @returns The error to render inline.
 */
export function toUiError(error: unknown): UiError {
  if (!(error instanceof HttpErrorResponse) || error.status === 0) {
    return { status: 0, title: UNREACHABLE_TITLE, detail: null, fieldErrors: [] };
  }
  if (error.status >= 200 && error.status < 300) {
    return { status: error.status, title: UNEXPECTED_RESPONSE_TITLE, detail: null, fieldErrors: [] };
  }

  const body: unknown = error.error;
  if (error.status < 400 || error.status >= 500 || !isProblemResponse(error) || !isRecord(body)) {
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

/**
 * Returns the label the guest sees for the request field a server error key
 * names, such as `Check-out` for `checkOut`, so a field error can be
 * identified by its visible label instead of the API's field name. A key that
 * is a top-level JSON path member, such as `$.email`, names that member, as in
 * {@link fieldErrorIds}. Names are compared with case. A key that names no
 * labelled field, such as the body-level `$`, a deeper path or any other
 * name, gives `null`, and its messages then stand on their own. The function
 * is pure.
 *
 * @param key - A field key from {@link UiError.fieldErrors}, exactly as the
 * server sent it.
 * @returns The visible label, or `null` when the key names no labelled field.
 */
export function fieldLabel(key: string): string | null {
  return FIELD_LABELS.get(fieldNameOf(key)) ?? null;
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

/**
 * Returns the error a form shows once the guest has edited one of its fields:
 * `error` without the {@link UiError.fieldErrors} entries that name `field`.
 * Keys are matched as {@link fieldErrorIds} matches them, so a top-level JSON
 * path key such as `$.email` names `email`, and every other key must equal
 * `field` exactly, with case.
 *
 * - When no entry names `field`, `error` itself is returned, so an error that
 *   carries no field errors, such as a 401 or 409, stays as it is.
 * - When the entries removed were its last field errors, the result is
 *   `null`: every field the server rejected has been edited, so the whole
 *   error, title and detail included, is cleared.
 * - Otherwise the result is a copy holding the remaining entries in their
 *   order.
 *
 * The function is pure.
 *
 * @param error - The error being shown.
 * @param field - The name of the edited form field, for example `email`.
 * @returns The error to show from now on, or `null` to show none.
 */
export function withoutFieldErrors(error: UiError, field: string): UiError | null {
  const remaining = error.fieldErrors.filter((fe) => fieldNameOf(fe.field) !== field);
  if (remaining.length === error.fieldErrors.length) {
    return error;
  }
  return remaining.length === 0 ? null : { ...error, fieldErrors: remaining };
}
