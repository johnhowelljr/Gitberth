<script>
	/** @type {import('./$types').PageProps} */
	let { data } = $props();
</script>

<h1>SvelteKit on Gitberth</h1>
<p>Rendered on the server at {data.renderedAt} by Node {data.node}.</p>
