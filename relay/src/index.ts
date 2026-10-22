export { HostRoom } from "./host-room";
export { PairingIndex } from "./pairing-index";

export default {
  async fetch(_request: Request): Promise<Response> {
    return new Response("not found", { status: 404 });
  },
} satisfies ExportedHandler;
