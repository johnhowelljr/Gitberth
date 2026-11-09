// Rendered on the server for every request, so each page load shows a new time.
export const dynamic = "force-dynamic";

export default function Page() {
  return (
    <main>
      <h1>Hello from Next.js</h1>
      <p>Rendered on the server at {new Date().toISOString()}.</p>
    </main>
  );
}
