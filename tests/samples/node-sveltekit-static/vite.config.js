import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

// Every page is prerendered into build/ (see src/routes/+layout.js).
export default defineConfig({
	plugins: [sveltekit({ adapter: adapter() })]
});
