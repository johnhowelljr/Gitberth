<script>
	/** @type {import('./$types').PageProps} */
	let { data } = $props();
</script>

<h1>Hello from SvelteKit</h1>
<p>Rendered on the server at {data.renderedAt}.</p>
