const CLOSE_BAD_FRAME = 4400;

function safeClose(ws: WebSocket, code: number, reason: string): void {
  try {
    ws.close(code, reason);
  } catch {
    /* already closed */
  }
}

/**
 * Forwards binary messages between two accepted sockets without parsing them.
 * Either side closing closes the other with 1000; a text frame closes both with 4400.
 */
export function pipe(a: WebSocket, b: WebSocket): void {
  const wire = (from: WebSocket, to: WebSocket) => {
    from.addEventListener("message", (ev) => {
      if (typeof ev.data === "string") {
        safeClose(from, CLOSE_BAD_FRAME, "binary only");
        safeClose(to, CLOSE_BAD_FRAME, "binary only");
        return;
      }
      try {
        to.send(ev.data);
      } catch {
        safeClose(from, 1000, "peer closed");
      }
    });
    const onEnd = () => safeClose(to, 1000, "peer closed");
    from.addEventListener("close", onEnd);
    from.addEventListener("error", onEnd);
  };
  // Server-side sockets deliver Blobs by default; Blobs would be re-sent as the text "[object Blob]".
  a.binaryType = "arraybuffer";
  b.binaryType = "arraybuffer";
  wire(a, b);
  wire(b, a);
}
