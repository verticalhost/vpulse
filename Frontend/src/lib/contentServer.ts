// The backend serves media and OAuth callbacks from the first free port in a small range, so the
// port is only known once it has told us. Everything that builds a media URL goes through here.

const DEFAULT_PORT = 2322;

let port = DEFAULT_PORT;

export function setContentServerPort(value: number) {
  if (Number.isInteger(value) && value > 0) {
    port = value;
  }
}

export function contentServerOrigin(): string {
  return `http://localhost:${port}`;
}

/** `path` starts with a slash, e.g. `/api/content`. */
export function contentServerUrl(path: string): string {
  return `${contentServerOrigin()}${path}`;
}
