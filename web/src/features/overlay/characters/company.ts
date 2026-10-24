/**
 * What a character senses beyond its reading (as the recorder's CharacterCompany.cs): where it is, the mouse, clicks on
 * it, the nearest other character on its line, the weather and the time of day; and the characters of one screen as
 * they were last drawn, so two can meet.
 */

/** The weather where the user is, for the characters (kind: clear, clouds, fog, rain, snow or storm; °C; m/s). */
export interface CharacterWeather {
  id: number;
  kind: string;
  temperature: number;
  wind: number;
  day: boolean;
}

/** Another character near it on its line, as it was last drawn. */
export interface CharacterFriend {
  id: string;
  name: string;
  x: number;
  size: number;
  /** It has nothing else to do (nothing bad, no moment, not meeting another, not watching the mouse). */
  free: boolean;
  meetingWith: string | null;
}

export interface CharacterSenses {
  /** Who it is among the characters of its screen (its panel's id and its own). */
  self: string;
  /** Where its feet were last drawn, and how tall it is drawn (the cursor's and the others' pixels). */
  x: number;
  y: number;
  size: number;
  /** Where the mouse pointer is (null: not known). */
  cursor: { x: number; y: number } | null;
  /** How many times it has been clicked. */
  touches: number;
  friend: CharacterFriend | null;
  weather: CharacterWeather | null;
  /** The local time of day, 0–24 (null: not known), and the day (a number, one more each day). */
  hour: number | null;
  day: number;
  /** It plays along a line (on top of or inside its panel, its screen's bottom or top, a spot). */
  line: boolean;
}

/** A character as it was last drawn, for the others of its screen. */
export interface CharacterSpot {
  id: string;
  name: string;
  x: number;
  y: number;
  size: number;
  free: boolean;
  meetingWith: string | null;
  line: boolean;
}

/** Whether a place is along a line (on top of or inside its panel, its screen's bottom or top, a spot). */
export const isLine = (place: string) => place === 'top' || place === 'inside' || place === 'screen' || place === 'spot';

/** The nearest of `others` on `self`'s line (feet within a third of the taller one's height up or down), or null. */
export function friendOf(self: CharacterSpot, others: Iterable<CharacterSpot>): CharacterFriend | null {
  if (!self.line) return null;
  let best: CharacterSpot | null = null;
  for (const o of others) {
    if (o.id === self.id || !o.line || Math.abs(o.y - self.y) > 0.35 * Math.max(self.size, o.size)) continue;
    if (!best || Math.abs(o.x - self.x) < Math.abs(best.x - self.x)) best = o;
  }
  return best ? { id: best.id, name: best.name, x: best.x, size: best.size, free: best.free, meetingWith: best.meetingWith } : null;
}

/** The local time of day (0–24) and day, for good mornings and late nights. */
export function clockNow(): { hour: number; day: number } {
  const d = new Date();
  return { hour: d.getHours() + d.getMinutes() / 60 + d.getSeconds() / 3600, day: Math.floor((d.getTime() - d.getTimezoneOffset() * 60_000) / 86_400_000) };
}

/**
 * The characters of one screen in the editor as they were last drawn, with the mouse over the screen and the weather:
 * what each senses comes from here.
 */
export class Company {
  private readonly spots = new Map<string, CharacterSpot>();
  /** The mouse over the screen, in its pixels (null: not over it). */
  cursor: { x: number; y: number } | null = null;
  weather: CharacterWeather | null = null;

  set(spot: CharacterSpot) {
    this.spots.set(spot.id, spot);
  }

  remove(id: string) {
    this.spots.delete(id);
  }

  friendOf(self: string): CharacterFriend | null {
    const me = this.spots.get(self);
    return me ? friendOf(me, this.spots.values()) : null;
  }
}
