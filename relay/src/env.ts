import type { HostRoom } from "./host-room";
import type { PairingIndex } from "./pairing-index";

export interface Env {
  HOSTS: DurableObjectNamespace<HostRoom>;
  INDEX: DurableObjectNamespace<PairingIndex>;
}
