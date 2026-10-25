import { admin } from "./nl/admin";
import { booking } from "./nl/booking";
import { operations } from "./nl/operations";
import { rooms } from "./nl/rooms";
import { shell } from "./nl/shell";

/** Dutch catalog, keyed by the English source text. Keep placeholders such as {name} identical to the source. */
export const nlParts = { shell, booking, rooms, admin, operations };

export const nl: Record<string, string> = Object.assign({}, ...Object.values(nlParts));
