// Runs on the server for every request, so each page load shows a new time.
/** @type {import('./$types').PageServerLoad} */
export function load() {
	return {
		renderedAt: new Date().toISOString(),
		node: process.version
	};
}
