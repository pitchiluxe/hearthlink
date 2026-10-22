import { DurableObject } from "cloudflare:workers";

// Placeholder; implemented in Task 2.
export class HostRoom extends DurableObject {
  async fetch(_request: Request): Promise<Response> {
    return new Response("not implemented", { status: 501 });
  }
}
