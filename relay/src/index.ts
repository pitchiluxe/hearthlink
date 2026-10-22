import type { Env } from "./env";

export { HostRoom } from "./host-room";
export { PairingIndex } from "./pairing-index";

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/v1/presence") {
      if (request.headers.get("Upgrade") !== "websocket") {
        return new Response("expected websocket", { status: 426 });
      }
      const id = url.searchParams.get("id");
      if (!id) return new Response("missing id", { status: 400 });
      return env.HOSTS.get(env.HOSTS.idFromName(id)).fetch(request);
    }
    return new Response("not found", { status: 404 });
  },
} satisfies ExportedHandler<Env>;
