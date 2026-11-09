import adapter from '@sveltejs/adapter-node';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

// SvelteKit 3 takes its options here; svelte.config.js is gone.
export default defineConfig({
	plugins: [sveltekit({ adapter: adapter() })]
});
