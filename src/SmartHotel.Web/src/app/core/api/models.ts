/**
 * Request and response types of the SmartHotel API, as the SPA's API clients
 * send and receive them.
 *
 * Every type here matches the API's interface specification and the C# records
 * that serialize it (`AuthContracts.cs`, `HotelContracts.cs` and
 * `BookingContracts.cs` in `SmartHotel.Api`). Wire formats:
 * - JSON with camelCase property names;
 * - dates as exact `yyyy-MM-dd` strings;
 * - timestamps as ISO 8601 UTC strings ending in `Z`;
 * - money as JSON numbers in USD with two decimals;
 * - enums as their names, as strings.
 *
 * Dates and timestamps stay `string`, never `Date`: `HttpClient` does not
 * revive dates, and the pages show the raw `yyyy-MM-dd` values.
 *
 * The server is the only authority on validation. The bounds documented below
 * are the server's; the client does not repeat them, and the server's 400
 * responses reach the user inline.
 *
 * The module holds types only and compiles to an empty JavaScript module.
 */

/**
 * Body of `POST /api/auth/register`. The server validates every member and
 * answers 400 with field errors when one is invalid, or 409 when the email is
 * already registered.
 */
export interface RegisterRequest {
  /**
   * The guest's email. After trimming it must be printable ASCII, a valid
   * address and at most 254 characters. Uniqueness ignores letter case.
   */
  email: string;
  /** The password, 8 to 128 characters. It is never trimmed. */
  password: string;
  /** The guest's full name. After trimming: not blank, at most 200 characters. */
  fullName: string;
}

/**
 * Body of a successful registration (201). The API never returns a password
 * hash or any other guest data.
 */
export interface RegisterResponse {
  /** The new guest's id. */
  guestId: number;
}

/**
 * Body of `POST /api/auth/login`. Any mismatch, including an unknown email,
 * gives the same 401 whether or not the account exists.
 */
export interface LoginRequest {
  /** The email as entered; the server folds its letter case before the lookup. */
  email: string;
  /** The password as entered. */
  password: string;
}

/** Body of a successful login (200). */
export interface LoginResponse {
  /** The signed JWT bearer token, valid for 60 minutes. */
  token: string;
  /**
   * When the token expires: a UTC ISO 8601 timestamp in whole seconds,
   * `yyyy-MM-ddTHH:mm:ssZ`, equal to the token's `exp` claim.
   */
  expiresAt: string;
}

/** Query of `GET /api/hotels`. */
export interface HotelSearchQuery {
  /** The city to search; required, not blank, at most 100 characters. */
  city: string;
  /** The first night of the stay, `yyyy-MM-dd`, not earlier than today (UTC). */
  checkIn: string;
  /**
   * The departure day, `yyyy-MM-dd`, 1 to 30 nights after `checkIn`. It is the
   * exclusive end of the stay and is not occupied.
   */
  checkOut: string;
  /** The party size, an integer of at least 1. */
  guests: number;
}

/** Query of `GET /api/hotels/{id}`: the stay the room types are priced for. */
export interface StayQuery {
  /** The first night of the stay, `yyyy-MM-dd`, not earlier than today (UTC). */
  checkIn: string;
  /**
   * The departure day, `yyyy-MM-dd`, 1 to 30 nights after `checkIn`. It is the
   * exclusive end of the stay and is not occupied.
   */
  checkOut: string;
  /** The party size, an integer of at least 1. */
  guests: number;
}

/**
 * One element of the `GET /api/hotels` array. Results are ordered by
 * `lowestNightlyPrice`, then by name; `[]` means no hotel qualifies, an
 * unknown city included.
 */
export interface HotelSummary {
  /** The hotel's id, the `{id}` of `GET /api/hotels/{id}`. */
  id: number;
  /** The hotel's display name. */
  name: string;
  /** The city as stored, such as `New York`. */
  city: string;
  /** The hotel's street address. */
  address: string;
  /**
   * The lowest nightly price in USD among the hotel's room types that can host
   * the party and have a free room for the whole stay.
   */
  lowestNightlyPrice: number;
}

/** One room type of a {@link HotelDetail}, priced for the requested stay. */
export interface RoomTypeAvailability {
  /** The room type's id, the `roomTypeId` of `POST /api/bookings`. */
  id: number;
  /** The room type's display name, such as `Standard`. */
  name: string;
  /** The largest party one room of this type can host, at least 1. */
  maxGuests: number;
  /** The price per night in USD. */
  nightlyPrice: number;
  /**
   * The number of rooms of this type with no `Confirmed` booking that overlaps
   * the stay. It counts physical rooms whatever `guests` is, so one booking
   * lowers it by exactly one and its cancellation restores it.
   */
  availableRooms: number;
  /**
   * The price of the whole stay in USD: the nightly price multiplied by the
   * nights, computed on the server. It equals the total recorded on a booking
   * of this room type, its `totalPrice`, and the client never computes it.
   */
  stayTotal: number;
}

/** Body of `GET /api/hotels/{id}` (200). */
export interface HotelDetail {
  /** The hotel's id. */
  id: number;
  /** The hotel's display name. */
  name: string;
  /** The city as stored, such as `New York`. */
  city: string;
  /** The hotel's street address. */
  address: string;
  /** The description shown on the hotel detail page. */
  description: string;
  /** The validated check-in day, `yyyy-MM-dd`, echoed from the query. */
  checkIn: string;
  /** The validated check-out day, `yyyy-MM-dd`, echoed from the query. */
  checkOut: string;
  /** The number of nights from `checkIn` to `checkOut`, 1 to 30. */
  nights: number;
  /** The effective guest count, 1 when the query omitted it. */
  guests: number;
  /** Every room type of the hotel, ordered by nightly price. */
  roomTypes: RoomTypeAvailability[];
}

/**
 * Body of `POST /api/bookings`. It has no price field: the server computes the
 * total from the room type's nightly price and the number of nights.
 */
export interface CreateBookingRequest {
  /** The id of an existing hotel, at least 1; an unknown hotel gives 404. */
  hotelId: number;
  /**
   * The id of a room type of that hotel, at least 1; an unknown room type, or
   * one of another hotel, gives 404.
   */
  roomTypeId: number;
  /** The first night of the stay, `yyyy-MM-dd`, not earlier than today (UTC). */
  checkIn: string;
  /**
   * The departure day, `yyyy-MM-dd`, 1 to 30 nights after `checkIn`. It is the
   * exclusive end of the stay and is not occupied.
   */
  checkOut: string;
  /**
   * The party size, required and at least 1; above the room type's
   * `maxGuests` it gives 400 keyed `guests`.
   */
  guests: number;
}

/** A booking's status, serialized by the API as the enum member's name. */
export type BookingStatus = 'Confirmed' | 'Cancelled';

/**
 * A booking as the API returns it: the 201 body of `POST /api/bookings` and
 * each element of `GET /api/bookings`, which lists the caller's bookings
 * newest first.
 */
export interface Booking {
  /** The booking's id, the `{id}` of `DELETE /api/bookings/{id}`. */
  id: number;
  /** The id of the hotel the room belongs to. */
  hotelId: number;
  /** The hotel's display name. */
  hotelName: string;
  /** The id of the booked room type. */
  roomTypeId: number;
  /** The room type's display name, such as `Standard`. */
  roomTypeName: string;
  /** The room the server assigned, unique within the hotel, such as `"101"`. */
  roomNumber: string;
  /** The first night of the stay, `yyyy-MM-dd`. */
  checkIn: string;
  /** The departure day, `yyyy-MM-dd`; it is not occupied. */
  checkOut: string;
  /** The number of nights, 1 to 30. */
  nights: number;
  /** The number of guests. */
  guests: number;
  /**
   * The total in USD with two decimals: the nightly price multiplied by the
   * nights, computed on the server when the booking was created.
   */
  totalPrice: number;
  /** `Confirmed` on creation; `Cancelled` once cancelled. */
  status: BookingStatus;
  /** When the booking was created, a UTC ISO 8601 timestamp ending in `Z`. */
  createdAt: string;
}

/**
 * The `application/problem+json` body of every API error. Every 4xx and 5xx
 * response carries `type`, `title`, `status`, `detail` and `traceId`, and a
 * 400 also carries `errors`.
 *
 * All members are optional because the type describes untrusted input:
 * `core/http/problem-details.ts` narrows the `unknown` error body itself
 * instead of trusting this shape.
 */
export interface ProblemDetails {
  /** A URI identifying the problem type, such as an RFC 9110 section link. */
  type?: string;
  /** A short, human-readable summary of the problem. */
  title?: string;
  /** The HTTP status code the server set for the problem. */
  status?: number;
  /** A human-readable explanation of this occurrence of the problem. */
  detail?: string;
  /** The identifier that correlates the response with the server's logs. */
  traceId?: string;
  /**
   * Validation messages on a 400, keyed by the camelCase request field, or by
   * the JSON path when the body itself failed to bind. It can be empty.
   */
  errors?: Record<string, string[]>;
}
