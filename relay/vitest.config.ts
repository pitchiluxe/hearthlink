import { defineConfig } from "vitest/config";
import { cloudflareTest } from "@cloudflare/vitest-pool-workers";

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.toml" },
      // The locally bundled workerd can lag behind the production compatibility_date.
      miniflare: { compatibilityDate: "2026-08-22" },
    }),
  ],
});
