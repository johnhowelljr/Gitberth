// @ts-check
import { defineConfig } from 'astro/config';
import vercel from '@astrojs/vercel';

// https://astro.build/config
export default defineConfig({
  output: 'server',
  // Vercel-only options; Gitberth's build drops them along with the adapter.
  adapter: vercel({
    webAnalytics: { enabled: true },
    maxDuration: 10,
  }),
});
